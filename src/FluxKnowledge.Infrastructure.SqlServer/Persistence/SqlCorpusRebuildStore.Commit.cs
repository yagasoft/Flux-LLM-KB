using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlCorpusRebuildStore
{
    public async ValueTask<CorpusRebuildReceipt> CommitAsync(CorpusRebuildPlan plan, string sharedSlotKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OperationId == Guid.Empty || plan.TargetEpoch == Guid.Empty || plan.TargetEpoch == plan.PreviousStamp.CorpusEpoch ||
            plan.ManifestHash != Hash(JsonSerializer.Serialize(plan with { ManifestHash = "" })) ||
            plan.Inputs.Any(input => input.EmbeddingJobId == Guid.Empty || input.DispatchMessageId == Guid.Empty) ||
            plan.Inputs.Select(input => input.PipelineRecordId).Distinct().Count() != plan.Inputs.Count ||
            plan.Inputs.Select(input => input.EmbeddingJobId).Distinct().Count() != plan.Inputs.Count ||
            plan.Inputs.Select(input => input.DispatchMessageId).Distinct().Count() != plan.Inputs.Count)
            throw new CorpusRebuildRefusalException("corpus-rebuild-plan-invalid");
        var receipt = await WithMaintenanceTransactionAsync(sharedSlotKey, async (context, token) =>
        {
            var connection = context.Database.GetDbConnection();
            if (!string.Equals(connection.Database, plan.DatabaseName, StringComparison.Ordinal) ||
                !string.Equals(connection.DataSource, plan.DatabaseServer, StringComparison.Ordinal))
                throw new CorpusRebuildRefusalException("corpus-rebuild-database-mismatch");
            var existing = await context.CorpusRebuildOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == plan.OperationId, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.TargetEpoch != plan.TargetEpoch || existing.ManifestHash != plan.ManifestHash ||
                    existing.ManifestJson != JsonSerializer.Serialize(plan))
                    throw new CorpusRebuildRefusalException("corpus-rebuild-operation-conflict");
                return new CorpusRebuildReceipt(existing.Id, existing.TargetEpoch, existing.ManifestHash, true);
            }
            if (await context.CorpusQueryLeases.AnyAsync(token).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-query-drain-required");
            CorpusRebuildPlan? superseded = null;
            CorpusRebuildSupersession? supersession = null;
            if (plan.Supersession is { } replacement)
            {
                superseded = await ReadReplacementParentAsync(context, plan.OperationId, replacement.OperationId,
                    replacement.TargetEpoch, replacement.ManifestHash, token).ConfigureAwait(false);
                supersession = await CaptureSupersessionAsync(context, superseded, replacement.RuntimeKey,
                    replacement.SettingsFingerprint, token).ConfigureAwait(false);
            }
            var captured = await CapturePlanAsync(context, plan.OperationId, plan.TargetEpoch, plan.Profile,
                plan.PassagePolicyFingerprint, token, plan.Inputs.ToDictionary(input => input.PipelineRecordId),
                superseded, supersession).ConfigureAwait(false);
            if (captured.ManifestHash != plan.ManifestHash)
                throw new CorpusRebuildRefusalException("corpus-rebuild-manifest-changed");
            var jobIds = plan.Inputs.Select(input => input.EmbeddingJobId).ToArray();
            var dispatchIds = plan.Inputs.Select(input => input.DispatchMessageId).ToArray();
            if (await context.Jobs.AnyAsync(job => jobIds.Contains(job.Id), token).ConfigureAwait(false) ||
                await context.OutboxMessages.AnyAsync(message => dispatchIds.Contains(message.Id), token).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-reserved-identity-used");
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            context.CorpusRebuildOperations.Add(new CorpusRebuildOperationEntity
            {
                Id = plan.OperationId, TargetEpoch = plan.TargetEpoch, ManifestHash = plan.ManifestHash,
                ManifestJson = JsonSerializer.Serialize(plan), CreatedAtUtc = now, SupersedesOperationId = superseded?.OperationId
            });
            context.CorpusRebuildWorkItems.AddRange(plan.Inputs.Select(input => new CorpusRebuildWorkItemEntity
            {
                OperationId = plan.OperationId, PipelineRecordId = input.PipelineRecordId, SourceRevision = input.SourceRevision,
                CanonicalArtifactId = input.CanonicalArtifactId, EmbeddingJobId = input.EmbeddingJobId,
                DispatchMessageId = input.DispatchMessageId
            }));
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            if (supersession is not null)
                await CommitSupersessionAsync(context, plan.OperationId, supersession, now, token).ConfigureAwait(false);
            var state = await context.IndexState.SingleAsync(value => value.Id == 1, token).ConfigureAwait(false);
            state.ActiveIndexGenerationId = null;
            state.EmptyCatalogueValidatedAtUtc = null;
            state.CorpusEpoch = plan.TargetEpoch;
            state.CorpusVersion = 0;
            state.CorpusRebuildOperationId = plan.OperationId;
            state.UpdatedAtUtc = now;
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            await context.EmbeddingGpuRequests.Where(request => !context.CorpusRebuildSupersededJobs.Any(receipt => receipt.JobId == request.ParentJobId))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);
            await context.IndexGenerationVectors.ExecuteDeleteAsync(token).ConfigureAwait(false);
            await context.Vectors.ExecuteDeleteAsync(token).ConfigureAwait(false);
            await context.IndexGenerations.ExecuteDeleteAsync(token).ConfigureAwait(false);
            await context.TextChunks.ExecuteDeleteAsync(token).ConfigureAwait(false);
            var artifactIds = plan.ProjectionArtifacts.Select(artifact => artifact.Id).ToArray();
            await context.Artifacts.Where(artifact => artifactIds.Contains(artifact.Id)).ExecuteDeleteAsync(token).ConfigureAwait(false);
            if (afterProjectionReset is not null) await afterProjectionReset(token).ConfigureAwait(false);
            return new CorpusRebuildReceipt(plan.OperationId, plan.TargetEpoch, plan.ManifestHash, false);
        }, ct).ConfigureAwait(false);
        if (afterCommit is not null) await afterCommit(ct).ConfigureAwait(false);
        return receipt;
    }
}
