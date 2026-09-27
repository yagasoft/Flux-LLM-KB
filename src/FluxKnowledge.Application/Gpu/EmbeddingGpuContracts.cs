using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;

namespace FluxKnowledge.Application.Gpu;

public sealed record EmbeddingGpuRuntime(string RuntimeKey, string SettingsFingerprint,
    EmbeddingProfile Profile, long EstimatedBytes);

public interface IEmbeddingGpuHandoff
{
    EmbeddingProfile Profile { get; }
    ValueTask QueueAsync(StageWorkItem work, EmbeddingWorkBatch batch, CancellationToken cancellationToken);
}

public sealed record EmbeddingGpuExecutionWork(Guid MiniTaskId, Guid ClaimOperationId, Guid ParentJobId, Guid PipelineRecordId,
    long SourceRevision, EmbeddingWorkBatch Batch);

public sealed record EmbeddingGpuCompletion(Guid MiniTaskId, byte[]? ResultDigest, DateTimeOffset CleanupConfirmedAtUtc);

public sealed record EmbeddingGpuRecoveryWork(Guid MiniTaskId, GpuExecutorBatchHandle Handle, Guid? ExecutorInstanceId,
    Guid? ClaimOperationId, GpuInteractiveOwnerIdentity? Owner, bool NativeCleanupConfirmed);

public interface IEmbeddingGpuInference
{
    ValueTask<GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>> EmbedBatchAsync(GpuOwnedWorkContext ownership,
        IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IEmbeddingGpuRequestStore : IEmbeddingGpuHandoff
{
    /// <summary>Exact-operation replay recovers a lost claim response. The adapter must still run one native callback per dispatch.</summary>
    ValueTask<EmbeddingGpuExecutionWork?> ClaimExecutionAsync(GpuExecutorBatchHandle handle,
        Guid executorInstance, Guid claimOperation, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken);
    ValueTask CommitAsync(GpuExecutorBatchHandle handle, Guid executorInstance,
        EmbeddingGpuExecutionWork work, IReadOnlyList<EmbeddingResult> results, CancellationToken cancellationToken);
    ValueTask RecordNativeCleanupAsync(GpuExecutorBatchHandle handle, Guid executorInstance, Guid claimOperation,
        CancellationToken cancellationToken);
    ValueTask<EmbeddingGpuCompletion?> ReadPendingCompletionAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken);
    ValueTask<bool> RequeueSettledAsync(GpuExecutorBatchHandle handle, Guid miniTaskId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<EmbeddingGpuRecoveryWork>> ReadRecoveryAsync(CancellationToken cancellationToken);
    /// <summary>CAS proof that native work never started: only an exact still-unbound acknowledged/uncertain request can be closed.</summary>
    ValueTask<bool> ConfirmUnstartedCleanupAsync(EmbeddingGpuRecoveryWork work, Guid executorInstance,
        Guid claimOperation, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken);
    /// <summary>Trusted caller must have observed this exact process incarnation Exited; Alive/Unknown are not proof.</summary>
    ValueTask<bool> ConfirmExitedCleanupAsync(EmbeddingGpuRecoveryWork work, CancellationToken cancellationToken);
}
