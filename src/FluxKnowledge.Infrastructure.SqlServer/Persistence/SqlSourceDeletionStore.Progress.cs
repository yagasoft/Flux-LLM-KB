using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    private const int MembershipVectorPageSize = 1000;
    private const int MembershipDeleteBatchSize = 16000;
    private const int PayloadBatchSize = 4096;
    // Amortise the full historical proof without changing initial withdrawal.
    private const int PurgePayloadBatchSize = 16000;

    private static Task<SourceDeletionOperationEntity?> ReadOperationForUpdateAsync(
        FluxKnowledgeDbContext context, Guid operationId, CancellationToken ct) =>
        context.SourceDeletionOperations.FromSqlInterpolated($"""
            SELECT * FROM [SourceDeletionOperations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {operationId}
            """).SingleOrDefaultAsync(ct);

    private SourceDeletionRunResult? ReadPreviousOutcome(SourceDeletionOperationEntity? operation, SourceDeletionWorkItem work)
    {
        if (operation is null || operation.SourceRootId != work.SourceRootId) return LostOwnership(work);
        if (operation.State == 3 || operation.LastCommittedLeaseId == work.LeaseId)
        {
            var continueImmediately = operation.State != 3 && operation.State != 4 && operation.ReasonCode is null;
            return new(operation.State == 3, operation.Phase, operation.ReasonCode, continueImmediately,
                continueImmediately && operation.Phase == "rebuild-index");
        }
        return OwnsActiveLease(operation, work, timeProvider.GetUtcNow()) ? null : LostOwnership(work);
    }

    private static SourceDeletionRunResult LostOwnership(SourceDeletionWorkItem work) =>
        new(false, work.Phase, "source-delete-lease-lost");

    private static bool IsAfterCutover(string phase) => phase is "cleanup-memberships" or "purge-records" or "cleanup-files";

    // Every caller holds the operation's UPDLOCK until this transaction commits.
    private async Task<SourceDeletionRunResult> CommitUnitAsync(FluxKnowledgeDbContext context,
        IDbContextTransaction transaction, SourceDeletionOperationEntity operation, string phase, CancellationToken ct,
        string? reason = null, bool progressed = true, int nextState = 0, bool continueImmediately = true)
    {
        var now = timeProvider.GetUtcNow();
        if (operation.State != 1 || operation.LeaseId is null ||
            operation.LeaseExpiresAtUtc is not { } expires || expires <= now)
            throw new SourceDeletionLeaseLostException();
        operation.LastCommittedLeaseId = operation.LeaseId;
        operation.ResumePhase = nextState == 4 ? operation.Phase : null;
        operation.State = nextState;
        operation.Phase = phase;
        operation.ReasonCode = reason;
        operation.UpdatedAtUtc = now;
        if (progressed) operation.LastProgressAtUtc = now;
        ReleaseLease(operation);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        if (expires <= timeProvider.GetUtcNow()) throw new SourceDeletionLeaseLostException();
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(nextState == 3, phase, reason, nextState == 0 && continueImmediately,
            nextState == 0 && continueImmediately && phase == "rebuild-index");
    }

    private static IQueryable<VectorEntity> OwnedVectors(FluxKnowledgeDbContext context, Guid rootId) =>
        from vector in context.Vectors
        join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
        join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
        join record in context.PipelineRecords on artifact.PipelineRecordId equals record.Id
        join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
        where revision.SourceRootId == rootId
        select vector;

    private static async Task<bool> HasOwnedMembershipsAsync(FluxKnowledgeDbContext context, Guid rootId, CancellationToken ct)
    {
        var ids=OwnedVectors(context,rootId).Select(value => value.VectorId);
        return await context.IndexGenerationVectors.AnyAsync(member => ids.Contains(member.VectorId),ct) ||
            await context.IndexSegmentVectors.AnyAsync(member => ids.Contains(member.VectorId),ct) ||
            await context.CurrentIndexMembers.AnyAsync(member => ids.Contains(member.VectorId),ct);
    }

    private async Task<SourceDeletionRunResult> DeleteMembershipUnitAsync(FluxKnowledgeDbContext context,
        IDbContextTransaction transaction, SourceDeletionOperationEntity operation, CancellationToken ct)
    {
        var cursor = operation.MembershipVectorCursor ?? 0;
        var page = await OwnedVectors(context, operation.SourceRootId).Where(value => value.VectorId > cursor)
            .OrderBy(value => value.VectorId).Select(value => new { value.VectorId, value.IsDeleted })
            .Take(MembershipVectorPageSize).ToArrayAsync(ct).ConfigureAwait(false);
        if (page.Length == 0)
        {
            if (await context.SourceDeletionCleanupItems.AnyAsync(value => value.SourceDeletionOperationId == operation.Id &&
                (value.StorageKind == 3 || value.StorageKind == 4) && value.State != 1, ct))
                return await CommitUnitAsync(context, transaction, operation, operation.Phase, ct,
                    "source-delete-segment-retirement-pending", progressed: false, continueImmediately: false);
            if (await HasOwnedMembershipsAsync(context, operation.SourceRootId, ct).ConfigureAwait(false))
                return await RefuseAsync(context, transaction, operation, "source-delete-membership-changed", ct).ConfigureAwait(false);
            return await CommitUnitAsync(context, transaction, operation, "purge-records", ct).ConfigureAwait(false);
        }
        if (page.Any(vector => !vector.IsDeleted))
            return await RefuseAsync(context, transaction, operation, "source-delete-vector-not-withdrawn", ct).ConfigureAwait(false);
        var ids = page.Select(vector => vector.VectorId).ToArray();
        if (await context.IndexSegmentVectors.AnyAsync(value => ids.Contains(value.VectorId), ct) ||
            await context.CurrentIndexMembers.AnyAsync(value => ids.Contains(value.VectorId), ct))
            return await CommitUnitAsync(context, transaction, operation, operation.Phase, ct,
                "source-delete-segment-retirement-pending", progressed: false, continueImmediately: false);
        var activeGeneration = await context.IndexState.Where(value => value.Id == 1)
            .Select(value => value.ActiveIndexGenerationId).SingleAsync(ct).ConfigureAwait(false);
        // Keep the exact page proof fresh in this transaction, but read each
        // generation's status once rather than once per historical membership.
        // Fixed parameter types also preserve the plan across short/long ID values.
        var vectorPage = new SqlParameter("@vectorPage",SqlDbType.NVarChar,-1) {Value=JsonSerializer.Serialize(ids)};
        var activeGenerationParameter = new SqlParameter("@activeGeneration",SqlDbType.UniqueIdentifier)
            {Value=(object?)activeGeneration ?? DBNull.Value};
        var protectedMembership = await context.Database.SqlQueryRaw<bool>("""
            -- source-delete-membership-generation-guard
            DECLARE @ReferencedGenerations TABLE ([Id] uniqueidentifier NOT NULL PRIMARY KEY);
            INSERT @ReferencedGenerations ([Id])
            SELECT DISTINCT [member].[GenerationId]
            FROM OPENJSON(@vectorPage) WITH ([Value] bigint '$') AS [requested]
            INNER JOIN [IndexGenerationVectors] AS [member] WITH (FORCESEEK) ON [member].[VectorId]=[requested].[Value];
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM @ReferencedGenerations AS [referenced]
                INNER JOIN [IndexGenerations] AS [generation] ON [generation].[Id]=[referenced].[Id]
                WHERE [generation].[RetiredAtUtc] IS NULL OR [generation].[Id]=@activeGeneration
            ) THEN 1 ELSE 0 END AS bit) AS [Value]
            OPTION (USE HINT('DISABLE_OPTIMIZER_ROWGOAL'))
            """,vectorPage,activeGenerationParameter).ToArrayAsync(ct).ConfigureAwait(false);
        if (protectedMembership.Single())
            return await RefuseAsync(context, transaction, operation, "source-delete-membership-generation-not-retired", ct).ConfigureAwait(false);

        // Bound both vectors inspected and rows removed. DELETE TOP can choose
        // any row order; the cursor advances only through the cleared prefix.
        var deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE TOP ({MembershipDeleteBatchSize}) FROM [IndexGenerationVectors]
            WHERE [VectorId] IN (SELECT [Value] FROM OPENJSON({JsonSerializer.Serialize(ids)}) WITH ([Value] bigint '$'))
            """, ct).ConfigureAwait(false);
        operation.MembershipRowsDeleted = checked(operation.MembershipRowsDeleted + deleted);
        var firstRemaining = await context.IndexGenerationVectors.Where(member => ids.Contains(member.VectorId))
            .Select(member => (long?)member.VectorId).MinAsync(ct).ConfigureAwait(false);
        var cleared = firstRemaining is null ? ids[^1] : ids.Where(id => id < firstRemaining.Value).DefaultIfEmpty(cursor).Max();
        if (cleared > cursor) operation.MembershipVectorCursor = cleared;
        return await CommitUnitAsync(context, transaction, operation, operation.Phase, ct).ConfigureAwait(false);
    }

    private static async ValueTask<T> WithSqlFailureAsync<T>(Func<ValueTask<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (SqlException exception) { throw SqlFailure(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql) { throw SqlFailure(sql); }
    }

    private static async Task<bool> HasForeignBatchTaskAsync(FluxKnowledgeDbContext context,
        Guid[] miniTaskIds,Guid[] batchIds,CancellationToken ct)
    {
        // Repeated JSON anti-membership scans become quadratic in execution
        // history. Materialise both complete sets once while the caller retains
        // its serializable ownership/publication fence, including foreign tasks.
        var tasks=new SqlParameter("@ownedTasks",SqlDbType.NVarChar,-1){Value=JsonSerializer.Serialize(miniTaskIds)};
        var batches=new SqlParameter("@ownedBatches",SqlDbType.NVarChar,-1){Value=JsonSerializer.Serialize(batchIds)};
        var result=await context.Database.SqlQueryRaw<bool>("""
            -- source-delete-shared-batch-guard
            DECLARE @Tasks TABLE ([Id] uniqueidentifier NOT NULL PRIMARY KEY);
            DECLARE @Batches TABLE ([Id] uniqueidentifier NOT NULL PRIMARY KEY);
            INSERT @Tasks ([Id]) SELECT [Value] FROM OPENJSON(@ownedTasks) WITH ([Value] uniqueidentifier '$');
            INSERT @Batches ([Id]) SELECT [Value] FROM OPENJSON(@ownedBatches) WITH ([Value] uniqueidentifier '$');
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM [GpuMiniTasks] AS [task]
                INNER JOIN @Batches AS [batch] ON [batch].[Id]=[task].[BatchId]
                LEFT JOIN @Tasks AS [owned] ON [owned].[Id]=[task].[Id]
                WHERE [owned].[Id] IS NULL
            ) THEN 1 ELSE 0 END AS bit) AS [Value]
            OPTION (USE HINT('DISABLE_OPTIMIZER_ROWGOAL'))
            """,tasks,batches).ToArrayAsync(ct).ConfigureAwait(false);
        return result.Single();
    }

    private static SourceDeletionFailureException SqlFailure(SqlException exception) => new(exception.Number switch
    {
        -2 => "source-delete-sql-timeout",
        1205 => "source-delete-sql-deadlock",
        _ => "source-delete-sql-failed"
    }, exception);

    private sealed class SourceDeletionLeaseLostException : Exception;
}
