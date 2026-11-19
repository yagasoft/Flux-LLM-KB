using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Preserves a guided branch's confirmed configuration through worker and derived-input hand-off.</summary>
internal static class SqlGuidedRetainedBinding
{
    internal const string Fingerprint = "guided-retained-recovery-v1";

    internal static bool IsMarked(SourceActivityEntity activity) => activity.DescriptorFingerprint == Fingerprint;

    internal static string Evidence(long configurationRevision) => JsonSerializer.Serialize(new { configurationRevision });

    internal static async Task<bool> IsCurrentAsync(FluxKnowledgeDbContext db, SourceActivityEntity activity, CancellationToken token)
    {
        if (activity.AttemptEvidenceJson is null) return false;
        long configurationRevision;
        try
        {
            using var evidence = JsonDocument.Parse(activity.AttemptEvidenceJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object ||
                !evidence.RootElement.TryGetProperty("configurationRevision", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt64(out configurationRevision) || configurationRevision <= 0) return false;
        }
        catch (JsonException) { return false; }

        var revision = await db.SourceRevisions.SingleOrDefaultAsync(value => value.Id == activity.SourceRevisionId &&
            value.SuppressedAtUtc == null && value.OriginKind == 0 && value.ContentSha256 == activity.InputFingerprint, token).ConfigureAwait(false);
        if (revision is null) return false;
        var root = await db.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == revision.SourceRootId &&
            value.State == (int)SourceRootState.Enabled && value.ConfigurationRevision == configurationRevision && !value.FollowLinks, token).ConfigureAwait(false);
        return root is not null && SqlDocumentInputRecovery.IsInScope(root, revision);
    }

    // Read the branch-scoped configuration via a completed child. Existing unrelated
    // revisions and the previous owners of the same physical input keep their own authority.
    internal static readonly string DerivedInputFence = $$"""
        AND ([swInput].[OriginKind] = 0 OR NOT EXISTS (
            SELECT 1 FROM [SourceProcessorBranchMembers] AS [guidedMember]
            INNER JOIN [SourceProcessorBranches] AS [guidedBranch] ON [guidedBranch].[Id] = [guidedMember].[BranchId]
            INNER JOIN [SourceActivities] AS [guidedOwner] ON [guidedOwner].[Id] = [guidedBranch].[SourceActivityId]
            WHERE [guidedOwner].[DescriptorFingerprint] = N'{{Fingerprint}}'
              AND ([guidedMember].[ChildSourceRevisionId] = [swInput].[Id] OR
                   ([swParent].[OriginKind] = 1 AND [guidedMember].[ChildSourceRevisionId] = [swParent].[Id]))
              AND ([swRoot].[State] <> {{(int)SourceRootState.Enabled}} OR [swRoot].[ConfigurationRevision] <> COALESCE(
                   TRY_CONVERT(bigint, JSON_VALUE(CASE WHEN ISJSON([guidedOwner].[AttemptEvidenceJson]) = 1
                           THEN [guidedOwner].[AttemptEvidenceJson] END, '$.configurationRevision')), -1))
        ))
        """;
}
