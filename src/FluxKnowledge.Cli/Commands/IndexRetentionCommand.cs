using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Trusted-local preparation/staging only; destructive work uses corpus native preview/commit.</summary>
public static class IndexRetentionCommand
{
    private sealed record Targets(Guid[] GenerationIds, Guid[] ProtectedGenerationIds);
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            if (args.Length != 7 || args[0] is not ("plan" or "stage")) return await UsageAsync(error).ConfigureAwait(false);
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(connection)) throw new NativeOperationException("index-retention-database-unavailable");
            var layout = LiveRootLayout.Production;
            var safety = new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance);
            var files = new DerivedIndexFileSystem(new(layout.IndexRoot));
            var operation = new SqlIndexRetentionOperations(new Factory(connection), new(layout.IndexRoot, layout.RecoveryRoot,
                path => files.TryCanonicalIntendedGenerationPath(path, out var canonical) ? canonical : null, files.IsValidDirectory, safety.ValidateBeforeIo));
            if (args[0] == "plan" && args[1] == "--manifest-id" && Guid.TryParse(args[2], out var id) && args[3] == "--targets" && args[5] == "--output")
            {
                using var input = File.OpenRead(args[4]);
                var targets = await JsonSerializer.DeserializeAsync<Targets>(input, cancellationToken: ct).ConfigureAwait(false)
                    ?? throw new NativeOperationException("index-retention-plan-invalid");
                var manifest = await operation.PrepareAsync(id, targets.GenerationIds, targets.ProtectedGenerationIds, args[6], ct).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { manifest.ManifestId, ManifestHash = SqlIndexRetentionOperations.ManifestHash(manifest),
                    Generations = manifest.Entries.Count, Memberships = manifest.Entries.Sum(value => value.MemberCount), manifest.MaximumMembersPerUnit,
                    ReadOnly = true, SnapshotStaged = false })).ConfigureAwait(false);
                return 0;
            }
            if (args[0] == "stage" && args[1] == "--manifest" && args[3] == "--manifest-hash" && args[5] == "--plan-directory")
            {
                using var input = File.OpenRead(args[2]);
                var manifest = await JsonSerializer.DeserializeAsync<IndexRetentionManifest>(input, cancellationToken: ct).ConfigureAwait(false)
                    ?? throw new NativeOperationException("index-retention-manifest-invalid");
                if (SqlIndexRetentionOperations.ManifestHash(manifest) != args[4]) throw new NativeOperationException("index-retention-manifest-changed");
                await operation.StageAsync(manifest, args[6], ct).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { manifest.ManifestId, SnapshotStaged = true, Deleted = false })).ConfigureAwait(false);
                return 0;
            }
            return await UsageAsync(error).ConfigureAwait(false);
        }
        catch (NativeOperationException exception) { await error.WriteLineAsync(exception.ReasonCode).ConfigureAwait(false); return 1; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Data.SqlClient.SqlException)
        { await error.WriteLineAsync("index-retention-operator-unavailable").ConfigureAwait(false); return 1; }
    }
    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync("Usage: index-retention plan --manifest-id <guid> --targets <private.json> --output <private-directory> | stage --manifest <private.json> --manifest-hash <sha256> --plan-directory <private-directory>").ConfigureAwait(false);
        return 2;
    }
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
}
