using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Trusted-local operator interface for the reviewed, disposable corpus projection rebuild.</summary>
public static class CorpusRebuildCommand
{
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        if (!IsValid(args)) return await UsageAsync(error).ConfigureAwait(false);
        try
        {
            var stores = new BgeGpuModelStores(
                Store(["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]),
                Store(["bundles", "bge-reranker-v2-m3-onnx", BgeOfflineModels.RerankerRevision, BgeOfflineModels.RerankerExport]),
                Store(["bundles", "bge-reranker-v2-m3", BgeOfflineModels.RerankerRevision]),
                Store(["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
            if (args[0] == "verify-models")
            {
                await BgeOfflineModels.VerifyGpuFilesAsync(stores, ct).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { Verified = true, Offline = true,
                    BgeOfflineModels.EmbeddingFingerprint, BgeOfflineModels.RerankerFingerprint })).ConfigureAwait(false);
                return 0;
            }
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException();
            using var tokenizer = new BgePassageTokenizer(stores);
            var safety = new LiveRootStorageSafety(LiveRootLayout.Production, FileSystemLiveRootPathInspector.Instance);
            return await ExecuteAsync(args, new Factory(connectionString), new(tokenizer),
                new UsearchGenerationValidator(safety), output, error, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SqlException or ArgumentException)
        {
            await error.WriteLineAsync("corpus-rebuild-operator-unavailable").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> ExecuteAsync(string[] args, IDbContextFactory<FluxKnowledgeDbContext> factory,
        PassageBuilder builder, IIndexGenerationVerifier verifier, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        if (!IsValid(args)) return await UsageAsync(error).ConfigureAwait(false);
        try
        {
            var store = new SqlCorpusRebuildStore(factory);
            var operationId = args[0] == "commit" ? Guid.Empty : Guid.Parse(args[2]);
            object result;
            switch (args[0])
            {
                case "plan":
                    result = await store.ReadPlanAsync(operationId, new(BgeOfflineModels.EmbeddingFingerprint, 1024),
                        builder.PolicyFingerprint, ct).ConfigureAwait(false);
                    break;
                case "commit":
                    using (var stream = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length is < 1 or > 16 * 1024 * 1024) throw new CorpusRebuildRefusalException("corpus-rebuild-manifest-invalid");
                        var plan = await JsonSerializer.DeserializeAsync<CorpusRebuildPlan>(stream, cancellationToken: ct).ConfigureAwait(false)
                            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-manifest-invalid");
                        if (plan.Profile != new EmbeddingProfile(BgeOfflineModels.EmbeddingFingerprint, 1024) ||
                            plan.PassagePolicyFingerprint != builder.PolicyFingerprint)
                            throw new CorpusRebuildRefusalException("corpus-rebuild-profile-changed");
                        result = await store.CommitAsync(plan, PaddleOcrVlmRuntimeContract.CapacitySlotKey, ct).ConfigureAwait(false);
                    }
                    break;
                case "prepare":
                    await using (var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
                    {
                        var operation = await context.CorpusRebuildOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == operationId, ct).ConfigureAwait(false)
                            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-operation-not-found");
                        var plan = JsonSerializer.Deserialize<CorpusRebuildPlan>(operation.ManifestJson)
                            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-receipt-invalid");
                        if (operation.CompletedAtUtc is null)
                            foreach (var input in plan.Inputs)
                                _ = await store.PrepareAsync(operationId, input.PipelineRecordId, builder, ct).ConfigureAwait(false);
                        result = new { OperationId = operationId, Prepared = true, ItemCount = plan.Inputs.Count };
                    }
                    break;
                case "finish":
                    result = await store.FinishAsync(operationId, PaddleOcrVlmRuntimeContract.CapacitySlotKey, verifier, ct).ConfigureAwait(false);
                    break;
                default:
                    await using (var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
                    {
                        var operation = await context.CorpusRebuildOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == operationId, ct).ConfigureAwait(false);
                        var items = context.CorpusRebuildWorkItems.Where(value => value.OperationId == operationId);
                        var jobIds = items.Select(value => value.EmbeddingJobId);
                        result = new
                        {
                            OperationId = operationId, Committed = operation is not null, Completed = operation?.CompletedAtUtc is not null,
                            operation?.ManifestHash, operation?.TargetEpoch,
                            PendingItems = await items.CountAsync(value => value.State == 0, ct).ConfigureAwait(false),
                            RunningItems = await items.CountAsync(value => value.State == 1, ct).ConfigureAwait(false),
                            CompletedItems = await items.CountAsync(value => value.State == 2, ct).ConfigureAwait(false),
                            FailedEmbeddingJobs = await context.Jobs.CountAsync(value => jobIds.Contains(value.Id) && value.PublicState == (int)FluxKnowledge.Domain.Jobs.PublicJobState.Failed, ct).ConfigureAwait(false),
                            FailedPublishJobs = await context.Jobs.CountAsync(value => value.Stage == (int)FluxKnowledge.Domain.Pipeline.PipelineStage.Publish &&
                                value.PublicState == (int)FluxKnowledge.Domain.Jobs.PublicJobState.Failed && items.Any(item =>
                                    item.State == 1 && item.PipelineRecordId == value.PipelineRecordId && item.SourceRevision == value.SourceRevision), ct).ConfigureAwait(false)
                        };
                    }
                    break;
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            return 0;
        }
        catch (CorpusRebuildRefusalException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SqlException or JsonException or ArgumentException)
        {
            await error.WriteLineAsync("corpus-rebuild-operator-unavailable").ConfigureAwait(false);
            return 1;
        }
    }

    private static bool IsValid(string[] args) => args is ["verify-models"] || args.Length == 3 &&
        (args[0] == "commit" ? args[1] == "--manifest" && !string.IsNullOrWhiteSpace(args[2]) :
         args[0] is "plan" or "prepare" or "status" or "finish" && args[1] == "--operation" &&
         Guid.TryParseExact(args[2], "D", out var id) && id != Guid.Empty);

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync("Usage: FluxKnowledge.Cli corpus-rebuild <plan|prepare|status|finish> --operation <guid> | commit --manifest <path> | verify-models").ConfigureAwait(false);
        return 2;
    }

    private static LocalModelStore Store(string[] components) => new(() => WindowsModelVerificationFiles.OpenProductionBundle(components));

    private sealed class Factory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
