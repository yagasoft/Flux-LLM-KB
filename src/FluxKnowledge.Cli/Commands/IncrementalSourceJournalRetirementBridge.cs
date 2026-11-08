using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Model-free, stopped-updater operation; absent from standalone CLI routes.</summary>
internal static class IncrementalSourceJournalRetirementBridge
{
    internal static string DetectionPolicy => SourcePolicyDefaults.DetectionPolicy;
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    internal static object BeginProduction(string bindingsJson, Action assertQuiescent) => Begin(
        LiveRootLayout.Production.RuntimeRoot, Path.Combine(LiveRootLayout.Production.ConfigRoot, "appsettings.Production.json"),
        Path.Combine(LiveRootLayout.Production.RuntimeRoot, "deployment-validation-hold.json"), bindingsJson, assertQuiescent,
        @"Global\FluxKnowledge.IncrementalIisUpdate.v1");

    internal static object BeginForIsolatedTests(string runtime, string configuration, string hold, string bindingsJson,
        Action assertQuiescent, string mutexName)
    {
        foreach (var path in new[] { runtime, configuration, hold })
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Equals(LiveRootLayout.Production.Root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(LiveRootLayout.Production.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("source-journal-retirement-test-root-is-production");
        }
        if (!mutexName.StartsWith(@"Local\FluxKnowledge.SourceJournalRetirementTest.", StringComparison.Ordinal))
            throw new InvalidOperationException("source-journal-retirement-test-mutex-invalid");
        return Begin(runtime, configuration, hold, bindingsJson, assertQuiescent, mutexName);
    }

    private static Capability Begin(string runtime, string configuration, string hold, string json, Action assertQuiescent, string mutexName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(assertQuiescent);
        var bindings = JsonSerializer.Deserialize<Bindings>(json, Json) ?? throw Invalid("bindings-invalid");
        if (bindings.ReleaseId.Length is < 1 or > 256 || bindings.DatabaseIncarnation == Guid.Empty ||
            bindings.MetadataPolicy is not null && bindings.MetadataPolicy != SourcePolicyDefaults.DetectionPolicy ||
            new[] { bindings.ConfigurationSha256, bindings.HoldSha256, bindings.RuntimeIdentityFingerprint,
                bindings.OperatorEntrySha256, bindings.StoppedWritersEvidenceSha256 }.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw Invalid("bindings-invalid");
        var mutex = new Mutex(false, mutexName);
        try
        {
            try { if (!mutex.WaitOne(0)) throw Invalid("updater-owned"); }
            catch (AbandonedMutexException) { /* Fresh stopped-owner proof is still required. */ }
        }
        catch { mutex.Dispose(); throw; }
        var capability = new Capability(mutex, assertQuiescent, bindings, runtime);
        try
        {
            assertQuiescent();
            capability.Pin(typeof(IncrementalSourceJournalRetirementBridge).Assembly.Location, bindings.OperatorEntrySha256, 64 * 1024 * 1024);
            using (var settings = JsonDocument.Parse(capability.Pin(configuration, bindings.ConfigurationSha256, 2 * 1024 * 1024)))
            {
                var journal = settings.RootElement.GetProperty("SourceJournal");
                if ((bindings.MetadataPolicy is null || bindings.RequireFeatureOff) &&
                    (journal.GetProperty("Enabled").ValueKind != JsonValueKind.False || journal.GetProperty("SealingEnabled").ValueKind != JsonValueKind.False))
                    throw Invalid("feature-off-required");
                capability.JournalEnabled = journal.GetProperty("Enabled").GetBoolean();
                var sql = new SqlConnectionStringBuilder(settings.RootElement.GetProperty("ConnectionStrings").GetProperty("FluxKnowledge").GetString())
                { ConnectRetryCount = 0, ConnectTimeout = 5, Pooling = false, Enlist = false };
                if (string.IsNullOrWhiteSpace(sql.DataSource) || string.IsNullOrWhiteSpace(sql.InitialCatalog) ||
                    new[] { "master", "model", "msdb", "tempdb" }.Contains(sql.InitialCatalog, StringComparer.OrdinalIgnoreCase)) throw Invalid("database-target-invalid");
                capability.Factory = new Factory(sql.ConnectionString);
            }
            using (var deny = JsonDocument.Parse(capability.Pin(hold, bindings.HoldSha256, 4096)))
                if (deny.RootElement.ValueKind != JsonValueKind.String || deny.RootElement.GetString() != bindings.ReleaseId) throw Invalid("hold-owner-changed");
            capability.Held.Add(PhysicalFileIdentity.OpenDirectoryLease(runtime));
            if (PhysicalFileIdentity.GetDirectory(runtime).IdentityFingerprint != bindings.RuntimeIdentityFingerprint) throw Invalid("runtime-changed");
            var evidence = capability.Pin(bindings.StoppedWritersEvidencePath, bindings.StoppedWritersEvidenceSha256, 65536);
            capability.Proof = JsonSerializer.Deserialize<StoppedWriters>(evidence, Json) ?? throw Invalid("stopped-proof-invalid");
            capability.RequireStoppedEvidence();
            return capability;
        }
        catch { capability.Dispose(); throw; }
    }

    // Keep PowerShell's mutex and live stop callback on the owning runspace thread.
    internal static string Run(object value, int timeoutSeconds)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (value is not Capability capability) throw Invalid("capability-unrecognised");
        capability.RequireThread();
        if (capability.Disposed || capability.Consumed) throw Invalid("capability-consumed");
        if (timeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        capability.Consumed = true;
        capability.AssertQuiescent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var stoppedChecks = new BlockingCollection<TaskCompletionSource>();
        async ValueTask RequireStoppedAsync(CancellationToken ct)
        {
            await capability.RequireDatabaseAsync(ct).ConfigureAwait(false);
            var check = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stoppedChecks.Add(check, ct);
            await check.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        var store = new SqlSourceVerificationStore(capability.Factory, TimeProvider.System, new SourceJournalOptions { Enabled = capability.JournalEnabled });
        var retirement = capability.MetadataPolicy is not null ? MetadataAsync() : FeatureOffAsync();
        // The transaction awaits both current live stop checks. Execute the PowerShell
        // callback on its owning runspace thread, including the final pre-commit guard.
        while (!retirement.IsCompleted)
            if (stoppedChecks.TryTake(out var check, 50))
            {
                try { capability.AssertQuiescent(); check.SetResult(); }
                catch (Exception error) { check.SetException(error); }
            }
        var result = retirement.GetAwaiter().GetResult();
        capability.AssertQuiescent();
        return JsonSerializer.Serialize(result);

        async Task<object> MetadataAsync() => await store.RetireForMetadataPolicyAsync(
            new WindowsSourceVerificationReaderSlot(capability.Runtime), RequireStoppedAsync, timeout.Token).ConfigureAwait(false);
        async Task<object> FeatureOffAsync() => await store.RetireForFeatureOffAsync(
            new WindowsSourceVerificationReaderSlot(capability.Runtime), RequireStoppedAsync, timeout.Token).ConfigureAwait(false);
    }

    private static InvalidOperationException Invalid(string reason) => new("source-journal-retirement-" + reason);
    private sealed record Bindings(string ReleaseId, string ConfigurationSha256, string HoldSha256, string RuntimeIdentityFingerprint,
        Guid DatabaseIncarnation, string OperatorEntrySha256, string StoppedWritersEvidencePath, string StoppedWritersEvidenceSha256,
        string? MetadataPolicy = null, bool RequireFeatureOff = false);
    private sealed record StoppedWriters(int Version, string ReleaseId, Guid DatabaseIncarnation, string RuntimeRoot,
        string RuntimeIdentityFingerprint, string ConfigurationSha256, bool WritersStopped, int[] StoppedProcessIds, DateTimeOffset ObservedAtUtc);
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class Capability(Mutex mutex, Action assertQuiescent, Bindings bindings, string runtime) : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        internal string Runtime { get; } = Path.GetFullPath(runtime);
        internal string? MetadataPolicy { get; } = bindings.MetadataPolicy;
        internal bool JournalEnabled { get; set; }
        internal Action AssertQuiescent { get; } = assertQuiescent;
        internal List<IDisposable> Held { get; } = [];
        internal IDbContextFactory<FluxKnowledgeDbContext> Factory { get; set; } = null!;
        internal StoppedWriters Proof { get; set; } = null!;
        internal bool Consumed { get; set; }
        internal bool Disposed { get; private set; }
        internal void RequireThread()
        { if (Environment.CurrentManagedThreadId != _thread) throw Invalid("updater-thread-changed"); }
        internal FileStream Pin(string path, string hash, int maximum)
        {
            var parents = new Stack<string>();
            for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null; parent = Path.GetDirectoryName(parent)) parents.Push(parent);
            foreach (var parent in parents) Held.Add(PhysicalFileIdentity.OpenDirectoryLease(parent));
            var file = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(path), FileAccess.Read);
            Held.Add(file);
            if (file.Length is < 1 || file.Length > maximum || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), hash, StringComparison.OrdinalIgnoreCase))
                throw Invalid("file-binding-changed");
            file.Position = 0; return file;
        }
        internal void RequireStoppedEvidence()
        {
            var now = DateTimeOffset.UtcNow;
            if (Proof.Version != 1 || !Proof.WritersStopped || Proof.ReleaseId != bindings.ReleaseId || Proof.DatabaseIncarnation != bindings.DatabaseIncarnation ||
                !string.Equals(Path.GetFullPath(Proof.RuntimeRoot), Runtime, StringComparison.OrdinalIgnoreCase) ||
                Proof.RuntimeIdentityFingerprint != bindings.RuntimeIdentityFingerprint || Proof.ConfigurationSha256 != bindings.ConfigurationSha256 ||
                Proof.ObservedAtUtc > now.AddSeconds(5) || Proof.ObservedAtUtc < now.AddMinutes(-15) ||
                Proof.StoppedProcessIds.Length > 256 || Proof.StoppedProcessIds.Any(id => id <= 0) ||
                Proof.StoppedProcessIds.Distinct().Count() != Proof.StoppedProcessIds.Length) throw Invalid("stopped-proof-invalid");
            foreach (var id in Proof.StoppedProcessIds)
            {
                try { using var process = Process.GetProcessById(id); if (!process.HasExited) throw Invalid("stopped-process-live"); }
                catch (ArgumentException) { /* Absent PID is terminal. Reuse refuses conservatively. */ }
            }
        }
        internal async ValueTask RequireDatabaseAsync(CancellationToken ct)
        {
            RequireStoppedEvidence();
            await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();";
            command.CommandTimeout = 5;
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not Guid observed || observed != bindings.DatabaseIncarnation) throw Invalid("database-changed");
        }
        public void Dispose()
        {
            RequireThread(); if (Disposed) return; Disposed = true;
            try { for (var index = Held.Count - 1; index >= 0; index--) Held[index].Dispose(); }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
