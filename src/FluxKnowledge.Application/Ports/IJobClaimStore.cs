using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Ports;

public sealed record ClaimedJob(
    JobId JobId,
    PipelineRecordId PipelineRecordId,
    long SourceRevision,
    PipelineStage Stage,
    string Operation,
    PublicJobState PublicState,
    DateTimeOffset DueAtUtc,
    int AttemptCount,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc,
    long LeaseGeneration);

public interface IJobClaimStore
{
    ValueTask<Job?> ClaimWorkerAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Job?> ClaimGpuAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<ClaimedJob?> ClaimNextDueAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask<ClaimedJob?> ClaimForDispatchAsync(
        ClaimedDispatchMessage dispatchMessage,
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}
