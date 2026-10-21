using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Explicit, model-free projection conversion, repair and current-SQL rollback.</summary>
public static class IndexProjectionCommand
{
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default)
    {
        if (!IsValid(args)) return await UsageAsync(error);
        try
        {
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("index-projection-database-unavailable");
            var binding = new SqlConnectionStringBuilder(connection) { ConnectRetryCount = 0, ConnectTimeout = 5 };
            if (string.IsNullOrWhiteSpace(binding.DataSource) || string.IsNullOrWhiteSpace(binding.InitialCatalog) ||
                new[] { "master", "model", "msdb", "tempdb" }.Contains(binding.InitialCatalog, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-database-target-invalid");
            var options = UsearchIndexOptions.FromConfiguredRoot(Environment.GetEnvironmentVariable("Usearch__RootPath"));
            return await ExecuteAsync(args, new Factory(binding.ConnectionString), options, output, error, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or SqlException)
        {
            await error.WriteLineAsync("index-projection-operator-unavailable");
            return 1;
        }
    }

    internal static async Task<int> ExecuteAsync(string[] args, IDbContextFactory<FluxKnowledgeDbContext> factory,
        UsearchIndexOptions options, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        if (!IsValid(args)) return await UsageAsync(error);
        var operationRecorded = false;
        try
        {
            // Production conversion must be driven by the coordinated updater's
            // writer-drain and uncertain-start protocol, not this standalone path.
            if (args[0] != "plan" && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath)),
                LiveRootLayout.Production.IndexRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-production-requires-coordinated-updater");
            var paths = new WindowsIndexSegmentReadLeaseFactory(options.RootPath);
            using var held = paths.AcquireDirectory(options.RootPath);
            var physical = PhysicalFileIdentity.GetDirectory(options.RootPath);
            await using var db = await factory.CreateDbContextAsync(token);
            var connection = db.Database.GetDbConnection();
            var budgets = new IndexSegmentBuildOptions();
            var store = new SqlSegmentedIndexStore(factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
            if (args[0] == "plan")
            {
                var plan = await store.ReadRebuildPlanAsync(Guid.Parse(args[2]), int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), token);
                await RequireProjectionRootAsync(db, plan, options.RootPath, token);
                var envelope = new OperatorPlan(1, connection.DataSource, connection.Database, options.RootPath,
                    physical.IdentityFingerprint, budgets, plan);
                await output.WriteLineAsync(JsonSerializer.Serialize(envelope));
                return 0;
            }
            var captured = await ReadPlanAsync(args[2], args[4], token);
            await ValidateCapturedAsync(captured, db, store, options, physical.IdentityFingerprint, token);
            if (args[0] == "abandon")
            {
                await store.AbandonRebuildAsync(captured.Plan.OperationId, token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { captured.Plan.OperationId, AbandonmentHandled = true }));
                return 0;
            }
            var rebuild = new UsearchProjectionRebuilder(options, paths, store, store,
                new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System),
                new UsearchLegacyProjectionBuilder(options, new WindowsIndexGenerationReadLeaseFactory(options.RootPath)), budgets);
            var recorded = await rebuild.RunAsync(captured.Plan, token);
            operationRecorded = true;
            await output.WriteLineAsync(JsonSerializer.Serialize(new { captured.Plan.OperationId, RecordedGeneration = recorded }));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or SqlException)
        {
            var reason = operationRecorded ? "index-projection-operation-recorded-result-delivery-failed-resume-same-plan" :
                exception is IndexStorageCapacityException ? "index-storage-capacity-deferred" : exception is InvalidOperationException &&
                (exception.Message.StartsWith("index-rebuild-", StringComparison.Ordinal) || exception.Message.StartsWith("index-projection-", StringComparison.Ordinal)) &&
                exception.Message.Length <= 160 ? exception.Message : "index-projection-operator-unavailable";
            await error.WriteLineAsync(reason);
            return 1;
        }
    }

    internal static async Task ValidateCapturedAsync(OperatorPlan captured, FluxKnowledgeDbContext db, SqlSegmentedIndexStore store,
        UsearchIndexOptions options, string rootFingerprint, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (captured.Version != 1 || captured.Plan is null || captured.Budgets != new IndexSegmentBuildOptions() ||
            !string.Equals(captured.Server, connection.DataSource, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(captured.Database, connection.Database, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(captured.IndexRoot, options.RootPath, StringComparison.OrdinalIgnoreCase) ||
            captured.IndexRootFingerprint != rootFingerprint ||
            captured.Plan != await store.ReadRebuildPlanAsync(captured.Plan.OperationId, captured.Plan.TargetFormat, token))
            throw new InvalidOperationException("index-projection-plan-binding-changed");
        await RequireProjectionRootAsync(db, captured.Plan, options.RootPath, token);
    }

    private static async Task RequireProjectionRootAsync(FluxKnowledgeDbContext db, IndexProjectionRebuildPlan plan,
        string root, CancellationToken token)
    {
        var generation = await db.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == plan.BaseGenerationId, token);
        var parent = Path.Combine(root, plan.SourceFormat == 1 ? "generations" : "manifests");
        if (generation.FormatVersion != plan.SourceFormat || !Path.IsPathFullyQualified(generation.IndexPath) ||
            !string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(generation.IndexPath))),
                parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("index-projection-sql-root-binding-changed");
    }

    internal static async Task<OperatorPlan> ReadPlanAsync(string path, string expectedHash, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        var held = new List<IDisposable>();
        try
        {
            var ancestors = new Stack<string>();
            for (var parent = Path.GetDirectoryName(full); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
            foreach (var parent in ancestors) held.Add(PhysicalFileIdentity.OpenDirectoryLease(parent));
            var handle = PhysicalFileIdentity.OpenReadNoFollow(full);
            await using var input = new FileStream(handle, FileAccess.Read);
            if (input.Length is < 1 or > 65536 ||
                !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-projection-plan-hash-invalid");
            input.Position = 0;
            return await JsonSerializer.DeserializeAsync<OperatorPlan>(input, cancellationToken: token)
                ?? throw new InvalidOperationException("index-projection-plan-invalid");
        }
        finally { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
    }

    private static bool IsValid(string[] args) => args.Length == 5 &&
        (args[0] == "plan" && args[1] == "--operation-id" && Guid.TryParse(args[2], out var id) && id != Guid.Empty &&
            args[3] == "--target-format" && args[4] is "1" or "2" ||
         args[0] is "run" or "abandon" && args[1] == "--plan" && !string.IsNullOrWhiteSpace(args[2]) &&
            args[3] == "--sha256" && args[4].Length == 64 && args[4].All(Uri.IsHexDigit));

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync("Usage: index-projection plan --operation-id <guid> --target-format <1|2> | run|abandon --plan <private.json> --sha256 <sha256>. Requires ConnectionStrings__FluxKnowledge and Usearch__RootPath.");
        return 2;
    }

    internal sealed record OperatorPlan(int Version, string Server, string Database, string IndexRoot,
        string IndexRootFingerprint, IndexSegmentBuildOptions Budgets, IndexProjectionRebuildPlan Plan);

    internal sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
}
