using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>The updater's in-process bridge; deliberately absent from the standalone CLI routes.</summary>
internal static class IncrementalProjectionBridge
{
    internal static object BeginProduction(string bindingsJson, Action assertQuiescent) => Begin(
        LiveRootLayout.Production.IndexRoot,
        Path.Combine(LiveRootLayout.Production.ConfigRoot, "appsettings.Production.json"),
        Path.Combine(LiveRootLayout.Production.RuntimeRoot, "deployment-validation-hold.json"),
        bindingsJson, assertQuiescent, @"Global\FluxKnowledge.IncrementalIisUpdate.v1");

    internal static object BeginForIsolatedTests(string indexRoot, string configuration, string hold,
        string bindingsJson, Action assertQuiescent, string mutexName)
    {
        foreach (var path in new[] { indexRoot, configuration, hold })
            if (Path.GetFullPath(path).StartsWith(LiveRootLayout.Production.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-test-root-is-production");
        if (!mutexName.StartsWith(@"Local\FluxKnowledge.ProjectionBridgeTest.", StringComparison.Ordinal))
            throw new InvalidOperationException("index-projection-test-mutex-invalid");
        return Begin(indexRoot, configuration, hold, bindingsJson, assertQuiescent, mutexName);
    }

    private static Capability Begin(string indexRoot, string configuration, string hold, string bindingsJson,
        Action assertQuiescent, string mutexName)
    {
        ArgumentNullException.ThrowIfNull(assertQuiescent);
        var bindings = JsonSerializer.Deserialize<Bindings>(bindingsJson) ?? throw new InvalidOperationException("index-projection-bindings-invalid");
        foreach (var hash in new[] { bindings.PlanSha256, bindings.ConfigurationSha256, bindings.HoldSha256, bindings.OperatorEntrySha256 })
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidOperationException("index-projection-bindings-invalid");
        if (bindings.DatabaseIncarnation == Guid.Empty) throw new InvalidOperationException("index-projection-bindings-invalid");
        var mutex = new Mutex(false, mutexName);
        try
        {
            try { if (!mutex.WaitOne(0)) throw new InvalidOperationException("index-projection-updater-owned"); }
            catch (AbandonedMutexException) { /* The stopped-owner callback must still prove fresh quiescence. */ }
        }
        catch { mutex.Dispose(); throw; }
        var capability = new Capability(mutex, assertQuiescent, new(indexRoot), bindings);
        try
        {
            assertQuiescent();
            capability.Pin(typeof(IncrementalProjectionBridge).Assembly.Location, bindings.OperatorEntrySha256, 64 * 1024 * 1024, "operator");
            var settings = capability.Pin(configuration, bindings.ConfigurationSha256, 65536, "configuration");
            using var json = JsonDocument.Parse(settings);
            var connection = json.RootElement.GetProperty("ConnectionStrings").GetProperty("FluxKnowledge").GetString();
            var sql = new SqlConnectionStringBuilder(connection) { ConnectRetryCount = 0, ConnectTimeout = 5, Pooling = false, Enlist = false };
            if (string.IsNullOrWhiteSpace(sql.DataSource) || string.IsNullOrWhiteSpace(sql.InitialCatalog) ||
                new[] { "master", "model", "msdb", "tempdb" }.Contains(sql.InitialCatalog, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-database-target-invalid");
            capability.Factory = new IndexProjectionCommand.Factory(sql.ConnectionString);
            var deny = capability.Pin(hold, bindings.HoldSha256, 4096, "hold");
            using var policy = JsonDocument.Parse(deny);
            if (policy.RootElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(policy.RootElement.GetString()))
                throw new InvalidOperationException("index-projection-deny-all-hold-required");
            capability.Plan = IndexProjectionCommand.ReadPlanAsync(bindings.PlanPath, bindings.PlanSha256, CancellationToken.None).GetAwaiter().GetResult();
            capability.Held.Add(new WindowsIndexSegmentReadLeaseFactory(indexRoot).AcquireDirectory(indexRoot));
            capability.RootFingerprint = PhysicalFileIdentity.GetDirectory(indexRoot).IdentityFingerprint;
            return capability;
        }
        catch { capability.Dispose(); throw; }
    }

    // Blocking here is intentional: PowerShell's mutex and callbacks stay on the
    // owning runspace thread. No child process or abandoned task can outlive it.
    internal static string Run(object value, int timeoutSeconds)
    {
        if (value is not Capability capability) throw new InvalidOperationException("index-projection-capability-unrecognised");
        capability.RequireThread();
        if (capability.Consumed || capability.Disposed) throw new InvalidOperationException("index-projection-capability-consumed");
        if (timeoutSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        capability.Consumed = true;
        capability.AssertQuiescent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var result = RunCoreAsync(capability, timeout.Token).GetAwaiter().GetResult();
        capability.AssertQuiescent();
        return result;
    }

    private static async Task<string> RunCoreAsync(Capability capability, CancellationToken token)
    {
        await using var db = await capability.Factory.CreateDbContextAsync(token);
        await RequireDatabaseAsync(db, capability.Bindings.DatabaseIncarnation, token);
        var store = new SqlSegmentedIndexStore(capability.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        await IndexProjectionCommand.ValidateCapturedAsync(capability.Plan, db, store, capability.Options, capability.RootFingerprint, token);
        var paths = new WindowsIndexSegmentReadLeaseFactory(capability.Options.RootPath);
        var rebuild = new UsearchProjectionRebuilder(capability.Options, paths, store, store,
            new SqlDerivedIndexRecoveryStore(capability.Factory, TimeProvider.System),
            new UsearchLegacyProjectionBuilder(capability.Options, new WindowsIndexGenerationReadLeaseFactory(capability.Options.RootPath)), capability.Plan.Budgets);
        var recorded = await rebuild.RunAsync(capability.Plan.Plan, token);
        await RequireDatabaseAsync(db, capability.Bindings.DatabaseIncarnation, token);
        var state = await db.IndexState.AsNoTracking().SingleAsync(token);
        if (state.ActiveIndexGenerationId != recorded.Id || state.RequiredIndexFormat != recorded.FormatVersion)
            throw new InvalidOperationException("index-projection-recorded-result-is-not-current");
        return JsonSerializer.Serialize(new { capability.Plan.Plan.OperationId, RecordedGeneration = recorded });
    }

    private static async Task RequireDatabaseAsync(FluxKnowledgeDbContext db, Guid expected, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();";
        command.CommandTimeout = 5;
        if (await command.ExecuteScalarAsync(token) is not Guid observed || observed != expected)
            throw new InvalidOperationException("index-projection-database-incarnation-changed");
    }

    private sealed record Bindings(string PlanPath, string PlanSha256, string ConfigurationSha256,
        string HoldSha256, Guid DatabaseIncarnation, string OperatorEntrySha256);

    private sealed class Capability(Mutex mutex, Action assertQuiescent, UsearchIndexOptions options, Bindings bindings) : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        internal List<IDisposable> Held { get; } = [];
        internal Action AssertQuiescent { get; } = assertQuiescent;
        internal UsearchIndexOptions Options { get; } = options;
        internal Bindings Bindings { get; } = bindings;
        internal IDbContextFactory<FluxKnowledgeDbContext> Factory { get; set; } = null!;
        internal IndexProjectionCommand.OperatorPlan Plan { get; set; } = null!;
        internal string RootFingerprint { get; set; } = "";
        internal bool Consumed { get; set; }
        internal bool Disposed { get; private set; }
        internal void RequireThread()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("index-projection-updater-thread-changed");
        }
        internal FileStream Pin(string path, string expected, int maximumBytes, string role)
        {
            var ancestors = new Stack<string>();
            for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
            foreach (var parent in ancestors) Held.Add(PhysicalFileIdentity.OpenDirectoryLease(parent));
            var stream = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(path), FileAccess.Read);
            Held.Add(stream);
            if (stream.Length is < 1 || stream.Length > maximumBytes ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-" + role + "-binding-changed");
            stream.Position = 0; return stream;
        }
        public void Dispose()
        {
            RequireThread();
            if (Disposed) return;
            Disposed = true;
            try { for (var i = Held.Count - 1; i >= 0; i--) Held[i].Dispose(); }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
