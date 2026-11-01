using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentedIndexStore
{
    internal sealed record RootRefreshProof(Guid CandidateId, byte[] RowVersion);

    private async Task<RootRefreshProof?> ReadRootRefreshProofAsync(Guid id, CancellationToken token)
    {
        IndexPublicationCandidateEntity expected;
        await using (var context = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            expected = await LockCandidateAsync(context, id, token);
            // Completed replay legitimately has no staging. All other admission
            // checks still run under the final publication fence.
            if (expected.State != 1 || !IsRootRefresh(expected)) return null;
            if (expected.BuildPurpose != 0 || !expected.WorklistComplete) throw InvalidRootProof();
            await transaction.CommitAsync(token);
        }
        foreach (var phase in new[] { 0, 1 })
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long last = 0, count = 0;
            while (true)
            {
                MemberRow[] page;
                await using (var context = await contexts.CreateDbContextAsync(token))
                await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
                {
                    var current = await LockCandidateAsync(context, id, token);
                    if (current.State == 2) return null; // Concurrent committed replay needs no deleted staging.
                    RequireRootProof(current, new(id, expected.RowVersion));
                    page = await context.IndexCandidateMembers.AsNoTracking()
                        .Where(value => value.CandidateId == id && value.Phase == phase && value.VectorId > last)
                        .OrderBy(value => value.VectorId).Take(MetadataPageSize).Select(value => new MemberRow
                        {
                            VectorId = value.VectorId, PayloadChecksum = value.PayloadChecksum, SourceIdentityId = value.SourceIdentityId,
                            OwnerSourceRevisionId = value.OwnerSourceRevisionId, SourceRootId = value.SourceRootId
                        }).ToArrayAsync(token);
                    await transaction.CommitAsync(token);
                }
                if (page.Length == 0) break;
                AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if ((count, digest) != (phase == 0 ? (expected.BeforeCount, expected.BeforeChecksum) : (expected.AfterCount, expected.AfterChecksum)))
                throw InvalidRootProof();
        }
        return new(id, expected.RowVersion.ToArray());
    }

    private static InvalidOperationException InvalidRootProof() => new("segmented-refresh-proof-invalid");

    private static void RequireRootProof(IndexPublicationCandidateEntity candidate, RootRefreshProof proof)
    {
        if (candidate.Id != proof.CandidateId || candidate.BuildPurpose != 0 || candidate.State != 1 ||
            !candidate.WorklistComplete || !IsRootRefresh(candidate) || !candidate.RowVersion.SequenceEqual(proof.RowVersion))
            throw new PublicationSnapshotConflictException("The sealed root proof changed before activation.");
    }

    private static async Task<bool> RootMembershipMatchesAsync(FluxKnowledgeDbContext context,
        IndexPublicationCandidateEntity candidate, int phase, CancellationToken token)
    {
        var query = phase == 0 ? QueryRootCurrentWindow(context, candidate, 0, long.MaxValue, composable: true)
            : QueryRootEligibleWindow(context, candidate.AffectedScopesJson, 0, long.MaxValue, composable: true);
        await using var canonical = query.CreateDbCommand();
        // Only application-owned SQL is composed. Caller values remain the
        // provider's parameters; no SQL literals are built from source data.
        var parameters = canonical.Parameters.Cast<DbParameter>().Select(value => (object)((ICloneable)value).Clone()).ToList();
        parameters.Add(new SqlParameter("@rootProofCandidate", SqlDbType.UniqueIdentifier) { Value = candidate.Id });
        parameters.Add(new SqlParameter("@rootProofPhase", SqlDbType.Int) { Value = phase });
        var sql = "WITH [canonical] AS (" + canonical.CommandText + """
            ), [staged] AS (
                SELECT [VectorId], [PayloadChecksum], [SourceIdentityId], [OwnerSourceRevisionId], [SourceRootId]
                FROM [IndexCandidateMembers] WHERE [CandidateId]=@rootProofCandidate AND [Phase]=@rootProofPhase
            )
            SELECT COUNT_BIG([canonical].[VectorId]) AS [CanonicalCount],
                COUNT_BIG(CASE WHEN [canonical].[VectorId] IS NULL OR [staged].[VectorId] IS NULL
                    OR DATALENGTH([canonical].[PayloadChecksum]) <> DATALENGTH([staged].[PayloadChecksum])
                    OR CONVERT(varbinary(64),[canonical].[PayloadChecksum]) <> CONVERT(varbinary(64),[staged].[PayloadChecksum])
                    OR [canonical].[SourceIdentityId] <> [staged].[SourceIdentityId]
                    OR [canonical].[OwnerSourceRevisionId] <> [staged].[OwnerSourceRevisionId]
                    OR ([canonical].[OwnerSourceRevisionId] IS NULL AND [staged].[OwnerSourceRevisionId] IS NOT NULL)
                    OR ([canonical].[OwnerSourceRevisionId] IS NOT NULL AND [staged].[OwnerSourceRevisionId] IS NULL)
                    OR [canonical].[SourceRootId] <> [staged].[SourceRootId]
                    OR ([canonical].[SourceRootId] IS NULL AND [staged].[SourceRootId] IS NOT NULL)
                    OR ([canonical].[SourceRootId] IS NOT NULL AND [staged].[SourceRootId] IS NULL)
                    THEN 1 END) AS [Differences]
            FROM [canonical] FULL OUTER JOIN [staged] ON [canonical].[VectorId]=[staged].[VectorId]
            OPTION (FORCE ORDER, MAXDOP 1)
            """;
        var result = (await context.Database.SqlQueryRaw<RootMembershipComparison>(sql, parameters.ToArray()).ToArrayAsync(token)).Single();
        return result.Differences == 0 && result.CanonicalCount == (phase == 0 ? candidate.BeforeCount : candidate.AfterCount);
    }

    private sealed class RootMembershipComparison
    {
        public long CanonicalCount { get; init; }
        public long Differences { get; init; }
    }
}
