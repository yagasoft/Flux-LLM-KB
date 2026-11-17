using System.Text.Json;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static partial class SqlDefaultRetainedPolicy
{
    private static bool IsLegacyTextIdentity(SourceActivityEntity activity) =>
        activity.ActivityKind == (int)SourceActivityKind.TextExtraction &&
        activity.ExecutionClass == (int)ExecutionClass.InProcess &&
        activity.ProcessorVersion == "phase-3a-v1" &&
        activity.DescriptorFingerprint == SourceActivityEntity.LegacyDescriptorFingerprint;

    private static bool MatchesLegacyTextPredecessor(SourceActivityEntity activity) =>
        IsLegacyTextIdentity(activity) && activity.AttemptCount == 0 && activity.LastAttemptAtUtc is null &&
        activity.ResultingPipelineRecordId is null && activity.ResultingPipelineRecordRevision is null &&
        activity.RequiredCapability is null &&
        (activity.State == (int)SourceActivityState.Pending && activity.Reason is null ||
         activity.State == (int)SourceActivityState.CancelledSuperseded && TryReadLegacyCancellation(activity, out _));

    private static async ValueTask<bool> ValidateLegacyTextPredecessorAsync(
        FluxKnowledgeDbContext db, SourceRevisionEntity revision, SourceActivityEntity predecessor, CancellationToken token)
    {
        if (!IsLegacyTextIdentity(predecessor)) return true;
        if (!MatchesLegacyTextPredecessor(predecessor) ||
            await db.SourceProcessorBranches.AnyAsync(value => value.SourceActivityId == predecessor.Id, token).ConfigureAwait(false)) return false;

        if (predecessor.State == (int)SourceActivityState.Pending)
        {
            // This route repairs only genuinely unbound work. An older policy owner
            // must own the sole successor relation and cancel its idle text sibling.
            // Apply repeats these absence reads under its serializable publication fence.
            return !await db.PipelineRecords.AnyAsync(value => value.SourceRevisionId == revision.Id, token).ConfigureAwait(false) &&
                !await db.SourceProcessorBranches.AnyAsync(value => value.SourceRevisionId == revision.Id, token).ConfigureAwait(false) &&
                !await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == predecessor.Id ||
                    value.SuccessorActivityId == predecessor.Id, token).ConfigureAwait(false) &&
                !await db.SourceActivities.AnyAsync(value => value.Id != predecessor.Id && value.SourceRevisionId == revision.Id &&
                    (value.State != (int)SourceActivityState.CancelledSuperseded || value.ProcessorVersion == RetainedDefaultPolicy.Version ||
                     value.ProcessorVersion == RetainedDefaultPolicy.UnsupportedBinary.ProcessorVersion), token).ConfigureAwait(false);
        }

        if (!TryReadLegacyCancellation(predecessor, out var successorId)) return false;
        var relation = await db.SourceActivityRelations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PredecessorActivityId == predecessor.Id, token).ConfigureAwait(false);
        if (relation is null || relation.SuccessorActivityId != successorId ||
            relation.RelationshipKind != RetainedDefaultPolicy.RelationshipKind || relation.ReasonCode != RetainedDefaultPolicy.Version) return false;
        var successor = await db.SourceActivities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == successorId, token).ConfigureAwait(false);
        return successor is not null && successor.SourceRevisionId == revision.Id &&
            successor.InputFingerprint == revision.ContentSha256 && successor.ActivityKind == (int)SourceActivityKind.TextExtraction &&
            successor.ExecutionClass == (int)ExecutionClass.InProcess && successor.ProcessorVersion == RetainedDefaultPolicy.Version &&
            successor.DescriptorFingerprint == RetainedDefaultPolicy.TextFingerprint && TryReadTextAdmission(successor, revision, out _);
    }

    private static bool TryReadLegacyCancellation(SourceActivityEntity activity, out Guid successorId)
    {
        successorId = default;
        if (activity.Reason != RetainedDefaultPolicy.RelationshipKind || activity.AttemptEvidenceJson is null) return false;
        try
        {
            using var document = JsonDocument.Parse(activity.AttemptEvidenceJson);
            var proof = document.RootElement;
            return proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty("policy", out var policy) &&
                policy.ValueKind == JsonValueKind.String && policy.GetString() == RetainedDefaultPolicy.Version &&
                proof.TryGetProperty("supersededByActivityId", out var successor) && successor.ValueKind == JsonValueKind.String &&
                successor.TryGetGuid(out successorId) && successorId != Guid.Empty;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadTextAdmission(SourceActivityEntity activity, SourceRevisionEntity revision, out long configurationRevision)
    {
        configurationRevision = 0;
        if (activity.AttemptEvidenceJson is null) return false;
        try
        {
            using var document = JsonDocument.Parse(activity.AttemptEvidenceJson);
            var proof = document.RootElement;
            return proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty("policy", out var policy) &&
                policy.ValueKind == JsonValueKind.String && policy.GetString() == RetainedDefaultPolicy.Version &&
                proof.TryGetProperty("configurationRevision", out var version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt64(out configurationRevision) && configurationRevision > 0 &&
                proof.TryGetProperty("inputSha256", out var hash) && hash.ValueKind == JsonValueKind.String && hash.GetString() == revision.ContentSha256 &&
                proof.TryGetProperty("verifiedByteLength", out var length) && length.ValueKind == JsonValueKind.Number &&
                length.TryGetInt64(out var byteLength) && byteLength == revision.ByteLength;
        }
        catch (JsonException) { return false; }
    }
}
