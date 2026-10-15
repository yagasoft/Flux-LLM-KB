using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    private const int MembershipBatchSize = 1000;

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

    private static Task<bool> HasOwnedMembershipsAsync(FluxKnowledgeDbContext context, Guid rootId, CancellationToken ct) =>
        OwnedVectors(context, rootId).AnyAsync(vector => context.IndexGenerationVectors.Any(member => member.VectorId == vector.VectorId), ct);

    private async Task<SourceDeletionRunResult> DeleteMembershipUnitAsync(FluxKnowledgeDbContext context,
        IDbContextTransaction transaction, SourceDeletionOperationEntity operation, CancellationToken ct)
    {
        var cursor = operation.MembershipVectorCursor ?? 0;
        var vector = await OwnedVectors(context, operation.SourceRootId).Where(value => value.VectorId > cursor)
            .OrderBy(value => value.VectorId).Select(value => new { value.VectorId, value.IsDeleted })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (vector is null)
        {
            if (await HasOwnedMembershipsAsync(context, operation.SourceRootId, ct).ConfigureAwait(false))
                return await RefuseAsync(context, transaction, operation, "source-delete-membership-changed", ct).ConfigureAwait(false);
            return await CommitUnitAsync(context, transaction, operation, "purge-records", ct).ConfigureAwait(false);
        }
        if (!vector.IsDeleted)
            return await RefuseAsync(context, transaction, operation, "source-delete-vector-not-withdrawn", ct).ConfigureAwait(false);
        var activeGeneration = await context.IndexState.Where(value => value.Id == 1)
            .Select(value => value.ActiveIndexGenerationId).SingleAsync(ct).ConfigureAwait(false);
        if (await context.IndexGenerationVectors.Where(member => member.VectorId == vector.VectorId)
            .AnyAsync(member => member.Generation.RetiredAtUtc == null || member.GenerationId == activeGeneration, ct).ConfigureAwait(false))
            return await RefuseAsync(context, transaction, operation, "source-delete-membership-generation-not-retired", ct).ConfigureAwait(false);

        // The existing VectorId index serves one scalar equality. Work and lock
        // footprint are bounded independently of the vector's historical fan-out.
        var deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE TOP ({MembershipBatchSize}) FROM [IndexGenerationVectors] WHERE [VectorId] = {vector.VectorId}
            """, ct).ConfigureAwait(false);
        operation.MembershipRowsDeleted = checked(operation.MembershipRowsDeleted + deleted);
        if (!await context.IndexGenerationVectors.AnyAsync(member => member.VectorId == vector.VectorId, ct).ConfigureAwait(false))
            operation.MembershipVectorCursor = vector.VectorId;
        return await CommitUnitAsync(context, transaction, operation, operation.Phase, ct).ConfigureAwait(false);
    }

    private static async ValueTask<T> WithSqlFailureAsync<T>(Func<ValueTask<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (SqlException exception) { throw SqlFailure(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql) { throw SqlFailure(sql); }
    }

    private static SourceDeletionFailureException SqlFailure(SqlException exception) => new(exception.Number switch
    {
        -2 => "source-delete-sql-timeout",
        1205 => "source-delete-sql-deadlock",
        _ => "source-delete-sql-failed"
    }, exception);

    private sealed class SourceDeletionLeaseLostException : Exception;
}
