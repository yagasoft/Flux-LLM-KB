using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Captures a reviewed projection reset; reading a plan never changes corpus data.</summary>
public sealed partial class SqlCorpusRebuildStore(IDbContextFactory<FluxKnowledgeDbContext> factory,
    TimeProvider? timeProvider = null, Func<CancellationToken, ValueTask>? afterProjectionReset = null,
    Func<CancellationToken, ValueTask>? afterCommit = null)
{
    public async ValueTask<CorpusRebuildPlan> ReadPlanAsync(Guid operationId, EmbeddingProfile profile,
        string passagePolicyFingerprint, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(profile.ModelFingerprint) ||
            profile.ModelFingerprint.Length > 256 || profile.Dimensions is < 1 or > 4096 ||
            passagePolicyFingerprint.Length != 64 || passagePolicyFingerprint.Any(value => !char.IsAsciiHexDigitLower(value)))
            throw new ArgumentException("corpus-rebuild-profile-invalid");
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var plan = await CapturePlanAsync(context, operationId, Guid.NewGuid(), profile, passagePolicyFingerprint, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return plan;
        }).ConfigureAwait(false);
    }

    private static async Task<CorpusRebuildPlan> CapturePlanAsync(FluxKnowledgeDbContext context, Guid operationId,
        Guid targetEpoch, EmbeddingProfile profile, string policy, CancellationToken ct,
        IReadOnlyDictionary<Guid, CorpusRebuildInput>? reservedInputs = null,
        CorpusRebuildPlan? superseded = null, CorpusRebuildSupersession? supersession = null)
    {
        var replacementJobIds = supersession?.JobIds.ToArray() ?? [];
        var unstartedRequestIds = supersession?.UnstartedRequestIds.ToArray() ?? [];
        if (superseded is null && await context.IndexState.AnyAsync(state => state.CorpusRebuildOperationId != null, ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-in-progress");
        if (await context.Jobs.AnyAsync(job => !job.PipelineRecord.IsDeleted && job.Stage >= (int)PipelineStage.CanonicalIndex &&
                job.PublicState != (int)PublicJobState.Completed &&
                !context.CorpusRebuildSupersededJobs.Any(receipt => receipt.JobId == job.Id) &&
                !replacementJobIds.Contains(job.Id), ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-projection-work-unsettled");
        if (await context.SourceRootConfigurations.AnyAsync(root => root.State == (int)SourceRootState.Deleting, ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-source-deletion-in-progress");
        if (await context.EmbeddingGpuRequests.AnyAsync(request => (request.State != 2 || !request.NativeCleanupConfirmed) &&
                !unstartedRequestIds.Contains(request.MiniTaskId), ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-embedding-request-unsettled");

        RebuildInputRow[] rows;
        if (superseded is not null)
        {
            var retained = new List<RebuildInputRow>(superseded.Inputs.Count);
            foreach (var input in superseded.Inputs)
                retained.Add(await ReadUnchangedInputAsync(context, input, ct).ConfigureAwait(false));
            rows = retained.OrderBy(row => row.PipelineRecordId).ToArray();
        }
        else rows = await context.Database.SqlQuery<RebuildInputRow>(SqlPublishedPassageSelection.Bind($"""
            SELECT [record].[Id] AS [PipelineRecordId], [record].[Revision] AS [SourceRevision],
                [artifact].[Id] AS [CanonicalArtifactId], [artifact].[ContentHash], [artifact].[SearchText], [artifact].[DocumentMetadataJson],
                [record].[SourceRevisionId] AS [RetainedInputId], [retained].[ContentSha256] AS [RetainedHash],
                [root].[Id] AS [RootId], [root].[State] AS [RootState], [root].[ConfigurationRevision] AS [RootRevision],
                [publication].[OwnerSourceRevisionId] AS [OwnerId], [owner].[ContentSha256] AS [OwnerHash],
                [publication].[DocumentInputSourceRevisionId] AS [PublishedInputId],
                [publication].[SourceProcessorBranchId] AS [BranchId],
                [publication].[ProcessorFingerprint], [publication].[PublishedAtUtc]
            FROM [Artifacts] AS [artifact]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            CROSS APPLY (SELECT [artifact].[SourceRevision] AS [SourceRevision]) AS [chunk]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
            """)).OrderBy(row => row.PipelineRecordId).ToArrayAsync(ct).ConfigureAwait(false);
        var selected = rows.Select(row => row.CanonicalArtifactId).ToArray();
        if (await context.Artifacts.AnyAsync(artifact => artifact.Stage == (int)PipelineStage.CanonicalIndex &&
                artifact.SourceRevision == artifact.PipelineRecord.Revision && !artifact.PipelineRecord.IsDeleted &&
                !selected.Contains(artifact.Id), ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-excluded-resumable-input");

        var inputs = new List<CorpusRebuildInput>(rows.Length);
        foreach (var row in rows)
        {
            if (!string.Equals(Hash(row.SearchText), row.ContentHash, StringComparison.Ordinal))
                throw new CorpusRebuildRefusalException("corpus-rebuild-canonical-hash-invalid");
            inputs.Add(new(row.PipelineRecordId, row.SourceRevision, row.CanonicalArtifactId, row.ContentHash,
                Hash(JsonSerializer.Serialize(row.DocumentMetadataJson)), BindingHash(row),
                reservedInputs?.GetValueOrDefault(row.PipelineRecordId)?.EmbeddingJobId ?? Guid.NewGuid(),
                reservedInputs?.GetValueOrDefault(row.PipelineRecordId)?.DispatchMessageId ?? Guid.NewGuid()));
        }
        var artifacts = await context.Artifacts.AsNoTracking().Where(artifact => artifact.Stage >= (int)PipelineStage.Embed)
            .OrderBy(artifact => artifact.Id).Select(artifact => new CorpusRebuildArtifact(artifact.Id, artifact.Stage,
                artifact.ContentHash, artifact.ContentType)).ToArrayAsync(ct).ConfigureAwait(false);
        if (artifacts.Any(artifact => artifact.Stage == (int)PipelineStage.Embed
                ? artifact.ContentType != EmbedDraftDefaults.ArtifactContentType
                : artifact.Stage != (int)PipelineStage.Publish || artifact.ContentType != "application/vnd.fluxknowledge.usearch-generation"))
            throw new CorpusRebuildRefusalException("corpus-rebuild-unrecognised-projection-artifact");
        var generations = await context.IndexGenerations.AsNoTracking().OrderBy(generation => generation.Id)
            .Select(generation => new CorpusRebuildGeneration(generation.Id, generation.IndexPath,
                generation.MetadataChecksum, generation.ModelFingerprint, generation.Dimensions, generation.VectorCount)).ToArrayAsync(ct).ConfigureAwait(false);
        var stamp = await SqlPublishedPassageSelection.ReadStampAsync(context, ct).ConfigureAwait(false);
        var plan = new CorpusRebuildPlan(operationId, context.Database.GetDbConnection().DataSource, context.Database.GetDbConnection().Database, stamp, targetEpoch,
            profile, policy, inputs, artifacts, generations,
            await context.EmbeddingGpuRequests.OrderBy(request => request.MiniTaskId).Select(request => request.MiniTaskId).ToArrayAsync(ct).ConfigureAwait(false),
            await context.TextChunks.LongCountAsync(ct).ConfigureAwait(false),
            await context.Vectors.LongCountAsync(ct).ConfigureAwait(false),
            await context.IndexGenerationVectors.LongCountAsync(ct).ConfigureAwait(false), "", supersession);
        return plan with { ManifestHash = Hash(JsonSerializer.Serialize(plan)) };
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string BindingHash(RebuildInputRow row) => Hash(JsonSerializer.Serialize(new
    {
        row.RetainedInputId, row.RetainedHash, row.RootId, row.RootState, row.RootRevision,
        row.OwnerId, row.OwnerHash, row.PublishedInputId, row.BranchId, row.ProcessorFingerprint, row.PublishedAtUtc
    }));

    private sealed class RebuildInputRow
    {
        public Guid PipelineRecordId { get; init; }
        public long SourceRevision { get; init; }
        public Guid CanonicalArtifactId { get; init; }
        public string ContentHash { get; init; } = "";
        public string SearchText { get; init; } = "";
        public string? DocumentMetadataJson { get; init; }
        public Guid? RetainedInputId { get; init; }
        public string? RetainedHash { get; init; }
        public Guid? RootId { get; init; }
        public int? RootState { get; init; }
        public long? RootRevision { get; init; }
        public Guid? OwnerId { get; init; }
        public string? OwnerHash { get; init; }
        public Guid? PublishedInputId { get; init; }
        public Guid? BranchId { get; init; }
        public string? ProcessorFingerprint { get; init; }
        public DateTimeOffset? PublishedAtUtc { get; init; }
    }
}
