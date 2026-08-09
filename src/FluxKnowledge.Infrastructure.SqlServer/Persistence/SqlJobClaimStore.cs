using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlJobClaimStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : IJobClaimStore
{
    public async ValueTask<Job?> ClaimWorkerAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(
                leaseOwner,
                nowUtc,
                leaseExpiresAtUtc,
                PublicJobState.WorkerQueued,
                PublicJobState.WorkerProcessing,
                dispatchMessage: null,
                cancellationToken)
            .ConfigureAwait(false);
        return claim is null ? null : RestoreClaimedJob(claim);
    }

    public async ValueTask<Job?> ClaimGpuAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(
                leaseOwner,
                nowUtc,
                leaseExpiresAtUtc,
                PublicJobState.GpuQueued,
                PublicJobState.GpuProcessing,
                dispatchMessage: null,
                cancellationToken)
            .ConfigureAwait(false);
        return claim is null ? null : RestoreClaimedJob(claim);
    }

    public ValueTask<ClaimedJob?> ClaimNextDueAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLease(leaseOwner, leaseDuration);
        return ClaimAsync(
            leaseOwner,
            nowUtc,
            nowUtc.Add(leaseDuration),
            PublicJobState.WorkerQueued,
            PublicJobState.WorkerProcessing,
            dispatchMessage: null,
            cancellationToken);
    }

    public ValueTask<ClaimedJob?> ClaimForDispatchAsync(
        ClaimedDispatchMessage dispatchMessage,
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatchMessage);
        ValidateLease(leaseOwner, leaseDuration);
        return ClaimAsync(
            leaseOwner,
            nowUtc,
            nowUtc.Add(leaseDuration),
            PublicJobState.WorkerQueued,
            PublicJobState.WorkerProcessing,
            dispatchMessage,
            cancellationToken);
    }

    private async ValueTask<ClaimedJob?> ClaimAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        PublicJobState queuedState,
        PublicJobState processingState,
        ClaimedDispatchMessage? dispatchMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseExpiresAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseExpiresAtUtc),
                "Lease expiry must be later than the claim time.");
        }

        var dispatchPredicate = dispatchMessage is null
            ? string.Empty
            : """
                AND [PipelineRecordId] = @pipelineRecordId
                AND [SourceRevision] = @sourceRevision
                AND [Stage] = @stage
                AND [Operation] = @operation
              """;
        var sql =
            $$"""
             DECLARE @claimed TABLE
             (
                 [Id] uniqueidentifier, [PipelineRecordId] uniqueidentifier, [SourceRevision] bigint,
                 [Stage] int, [Operation] nvarchar(128), [PublicState] int, [DueAtUtc] datetimeoffset(7),
                 [AttemptCount] int, [LeaseOwner] nvarchar(256), [LeaseExpiresAtUtc] datetimeoffset(7), [LeaseGeneration] bigint
             );
             DECLARE @claimedActivities TABLE ([Id] uniqueidentifier, [SourceRevisionId] uniqueidentifier);
             ;WITH [candidate] AS
             (
                 SELECT TOP (1)
                     [Id], [PipelineRecordId], [SourceRevision], [Stage], [Operation],
                     [PublicState], [DueAtUtc], [AttemptCount], [LeaseOwner],
                     [LeaseExpiresAtUtc], [LeaseGeneration], [Reason], [ErrorDetails],
                     [RowVersion]
                 FROM [Jobs] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                 WHERE [DueAtUtc] <= @nowUtc
                   AND
                   (
                       ([PublicState] = @queuedState AND
                           ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= @nowUtc))
                       OR
                       ([PublicState] = @processingState AND [LeaseExpiresAtUtc] <= @nowUtc)
                   )
                    {{dispatchPredicate}}
                 ORDER BY [DueAtUtc], [Id]
             )
             UPDATE [candidate]
             SET [PublicState] = @processingState,
                 [LeaseOwner] = @leaseOwner,
                 [LeaseExpiresAtUtc] = @leaseExpiresAtUtc,
                 [LeaseGeneration] = [LeaseGeneration] + 1,
                 [AttemptCount] = [AttemptCount] + 1,
                 [Reason] = NULL,
                 [ErrorDetails] = NULL
             OUTPUT
                 inserted.[Id],
                 inserted.[PipelineRecordId],
                 inserted.[SourceRevision],
                 inserted.[Stage],
                 inserted.[Operation],
                 inserted.[PublicState],
                 inserted.[DueAtUtc],
                 inserted.[AttemptCount],
                 inserted.[LeaseOwner],
                 inserted.[LeaseExpiresAtUtc],
                 inserted.[LeaseGeneration]
             INTO @claimed;

             UPDATE [activity]
             SET [State] = @sourceRunning,
                 [AttemptCount] = [activity].[AttemptCount] + 1,
                 [LastAttemptAtUtc] = @nowUtc,
                 [AttemptEvidenceJson] = CONCAT('{"claimLeaseGeneration":', CONVERT(nvarchar(32), [claimed].[LeaseGeneration]), '}'),
                 [UpdatedAtUtc] = @nowUtc
             OUTPUT inserted.[Id], inserted.[SourceRevisionId] INTO @claimedActivities
             FROM [SourceActivities] AS [activity]
             INNER JOIN @claimed AS [claimed]
                 ON [activity].[ResultingPipelineRecordId] = [claimed].[PipelineRecordId]
                AND [activity].[ResultingPipelineRecordRevision] = [claimed].[SourceRevision]
             WHERE [claimed].[Operation] = @extractOperation
               AND [claimed].[Stage] = @extractStage
               AND
               (
                   ([activity].[ExecutionClass] = @sourceInProcess AND
                    [activity].[ActivityKind] IN (@sourceTextExtraction, @sourceMetadataExtraction) AND
                    [activity].[State] IN (@sourcePending, @sourceRunning, @sourceFailedRetryable))
                   OR
                   ([activity].[ExecutionClass] = @sourceDeferredCapability AND
                    [activity].[ActivityKind] = @sourceTextExtraction AND
                    [activity].[State] IN (@sourceDeferredUnsupported, @sourceRunning, @sourceFailedRetryable))
               );

             INSERT INTO [AuditEvents] ([PipelineRecordId], [SourceRootId], [SourceRevisionId], [SourceActivityId], [CorrelationId], [EventFamily], [Severity], [EventType], [Actor], [DetailsJson], [OccurredAtUtc])
             SELECT [claimed].[PipelineRecordId], [revision].[SourceRootId], [activity].[SourceRevisionId], [activity].[Id],
                 CONCAT(N'source:', CONVERT(nvarchar(36), [activity].[SourceRevisionId])), N'activity', N'information', N'activity.claimed', N'pipeline-worker', N'{}', @nowUtc
             FROM [SourceActivities] [activity]
             INNER JOIN @claimedActivities [claimedActivity] ON [claimedActivity].[Id] = [activity].[Id]
             INNER JOIN [SourceRevisions] [revision] ON [revision].[Id] = [activity].[SourceRevisionId]
             CROSS JOIN @claimed [claimed];

             SELECT [Id], [PipelineRecordId], [SourceRevision], [Stage], [Operation], [PublicState], [DueAtUtc],
                 [AttemptCount], [LeaseOwner], [LeaseExpiresAtUtc], [LeaseGeneration]
             FROM @claimed;
             """;

        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        AddParameter(command, "@nowUtc", SqlDbType.DateTimeOffset, nowUtc);
        AddParameter(command, "@leaseExpiresAtUtc", SqlDbType.DateTimeOffset, leaseExpiresAtUtc);
        AddParameter(command, "@leaseOwner", SqlDbType.NVarChar, leaseOwner, 256);
        AddParameter(command, "@queuedState", SqlDbType.Int, (int)queuedState);
        AddParameter(command, "@processingState", SqlDbType.Int, (int)processingState);
        AddParameter(command, "@extractOperation", SqlDbType.NVarChar, PipelineOperations.ExtractUtf8, 128);
        AddParameter(command, "@extractStage", SqlDbType.Int, (int)PipelineStage.Extract);
        AddParameter(command, "@sourcePending", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityState.Pending);
        AddParameter(command, "@sourceRunning", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityState.Running);
        AddParameter(command, "@sourceFailedRetryable", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityState.FailedRetryable);
        AddParameter(command, "@sourceDeferredUnsupported", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityState.DeferredUnsupported);
        AddParameter(command, "@sourceInProcess", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.ExecutionClass.InProcess);
        AddParameter(command, "@sourceDeferredCapability", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.ExecutionClass.DeferredCapability);
        AddParameter(command, "@sourceTextExtraction", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityKind.TextExtraction);
        AddParameter(command, "@sourceMetadataExtraction", SqlDbType.Int, (int)FluxKnowledge.Domain.Sources.SourceActivityKind.MetadataExtraction);
        if (dispatchMessage is not null)
        {
            AddParameter(
                command,
                "@pipelineRecordId",
                SqlDbType.UniqueIdentifier,
                dispatchMessage.PipelineRecordId.Value);
            AddParameter(
                command,
                "@sourceRevision",
                SqlDbType.BigInt,
                dispatchMessage.SourceRevision);
            AddParameter(command, "@stage", SqlDbType.Int, (int)dispatchMessage.Stage);
            AddParameter(command, "@operation", SqlDbType.NVarChar, dispatchMessage.Operation, 128);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var claimed = new ClaimedJob(
            new JobId(reader.GetGuid(0)),
            new PipelineRecordId(reader.GetGuid(1)),
            reader.GetInt64(2),
            (PipelineStage)reader.GetInt32(3),
            reader.GetString(4),
            (PublicJobState)reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    private static Job RestoreClaimedJob(ClaimedJob claim)
    {
        var worker = claim.PublicState == PublicJobState.WorkerProcessing;
        var job = worker
            ? Job.CreateQueued(
                claim.JobId,
                claim.PipelineRecordId,
                claim.Stage,
                claim.Operation,
                claim.DueAtUtc)
            : Job.CreateGpuQueued(
                claim.JobId,
                claim.PipelineRecordId,
                claim.Stage,
                claim.Operation,
                claim.DueAtUtc);
        for (var generation = 1L; generation <= claim.LeaseGeneration; generation++)
        {
            job = worker
                ? job.ClaimWorker(claim.LeaseOwner, claim.LeaseExpiresAtUtc)
                : job.ClaimGpu(claim.LeaseOwner, claim.LeaseExpiresAtUtc);
            if (generation < claim.LeaseGeneration)
            {
                job = job.ReturnForCapacity(claim.DueAtUtc);
            }
        }

        return job;
    }

    private static void ValidateLease(string leaseOwner, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be positive.");
        }
    }

    private static void AddParameter(
        SqlCommand command,
        string name,
        SqlDbType type,
        object value,
        int? size = null)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size is not null)
        {
            parameter.Size = size.Value;
        }

        parameter.Value = value;
    }
}
