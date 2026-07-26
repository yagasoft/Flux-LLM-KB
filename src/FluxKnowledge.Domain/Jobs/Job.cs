using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Domain.Jobs;

public sealed record Job
{
    public JobId Id { get; private init; }

    public PipelineRecordId PipelineRecordId { get; private init; }

    public PipelineStage Stage { get; private init; }

    public string Operation { get; private init; }

    public PublicJobState PublicState { get; private init; }

    public DateTimeOffset DueAtUtc { get; private init; }

    public int AttemptCount { get; private init; }

    public string? LeaseOwner { get; private init; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private init; }

    public long LeaseGeneration { get; private init; }

    public string? Reason { get; private init; }

    public string? ErrorDetails { get; private init; }

    public bool IsPending => PublicState == PublicJobState.WorkerQueued;

    public static Job CreateQueued(
        JobId id,
        PipelineRecordId pipelineRecordId,
        PipelineStage stage,
        string operation,
        DateTimeOffset? dueAtUtc = null) =>
        Create(id, pipelineRecordId, stage, operation, PublicJobState.WorkerQueued, dueAtUtc);

    public static Job CreateGpuQueued(
        JobId id,
        PipelineRecordId pipelineRecordId,
        PipelineStage stage,
        string operation,
        DateTimeOffset? dueAtUtc = null) =>
        Create(id, pipelineRecordId, stage, operation, PublicJobState.GpuQueued, dueAtUtc);

    public Job ClaimWorker(string leaseOwner, DateTimeOffset leaseExpiresAtUtc) =>
        Claim(PublicJobState.WorkerQueued, PublicJobState.WorkerProcessing, leaseOwner, leaseExpiresAtUtc);

    public Job ClaimGpu(string leaseOwner, DateTimeOffset leaseExpiresAtUtc) =>
        Claim(PublicJobState.GpuQueued, PublicJobState.GpuProcessing, leaseOwner, leaseExpiresAtUtc);

    public Job Complete(long leaseGeneration)
    {
        EnsureProcessingLease(leaseGeneration);
        return this with
        {
            PublicState = PublicJobState.Completed,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null,
            Reason = null,
            ErrorDetails = null
        };
    }

    public Job Fail(long leaseGeneration, string reason, string? errorDetails = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureProcessingLease(leaseGeneration);
        return this with
        {
            PublicState = PublicJobState.Failed,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null,
            Reason = reason,
            ErrorDetails = errorDetails
        };
    }

    public Job ReturnForCapacity(DateTimeOffset dueAtUtc)
    {
        var queuedState = PublicState switch
        {
            PublicJobState.WorkerProcessing => PublicJobState.WorkerQueued,
            PublicJobState.GpuProcessing => PublicJobState.GpuQueued,
            PublicJobState.WorkerQueued => PublicJobState.WorkerQueued,
            PublicJobState.GpuQueued => PublicJobState.GpuQueued,
            _ => throw new DomainInvariantException("Only queued or processing jobs can be returned for capacity.")
        };

        return this with
        {
            PublicState = queuedState,
            DueAtUtc = dueAtUtc,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null
        };
    }

    private static Job Create(
        JobId id,
        PipelineRecordId pipelineRecordId,
        PipelineStage stage,
        string operation,
        PublicJobState publicState,
        DateTimeOffset? dueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(pipelineRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new Job(
            id,
            pipelineRecordId,
            stage,
            operation,
            publicState,
            dueAtUtc ?? DateTimeOffset.UtcNow,
            0,
            null,
            null,
            0,
            null,
            null);
    }

    private Job Claim(
        PublicJobState requiredState,
        PublicJobState processingState,
        string leaseOwner,
        DateTimeOffset leaseExpiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (PublicState != requiredState)
        {
            throw new DomainInvariantException($"Job must be {requiredState} before it can be claimed.");
        }

        return this with
        {
            PublicState = processingState,
            LeaseOwner = leaseOwner,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            LeaseGeneration = LeaseGeneration + 1,
            AttemptCount = AttemptCount + 1
        };
    }

    private void EnsureProcessingLease(long leaseGeneration)
    {
        if (PublicState is not (PublicJobState.WorkerProcessing or PublicJobState.GpuProcessing))
        {
            throw new DomainInvariantException("Only processing jobs can be completed or failed.");
        }

        if (LeaseGeneration != leaseGeneration)
        {
            throw new DomainInvariantException("The lease generation does not match the current claim.");
        }
    }

    private Job(
        JobId id,
        PipelineRecordId pipelineRecordId,
        PipelineStage stage,
        string operation,
        PublicJobState publicState,
        DateTimeOffset dueAtUtc,
        int attemptCount,
        string? leaseOwner,
        DateTimeOffset? leaseExpiresAtUtc,
        long leaseGeneration,
        string? reason,
        string? errorDetails)
    {
        Id = id;
        PipelineRecordId = pipelineRecordId;
        Stage = stage;
        Operation = operation;
        PublicState = publicState;
        DueAtUtc = dueAtUtc;
        AttemptCount = attemptCount;
        LeaseOwner = leaseOwner;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        LeaseGeneration = leaseGeneration;
        Reason = reason;
        ErrorDetails = errorDetails;
    }
}
