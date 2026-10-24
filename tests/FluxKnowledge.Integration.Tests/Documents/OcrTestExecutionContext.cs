using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Integration.Tests.Documents;

internal static class OcrTestExecutionContext
{
    internal static DocumentOcrExecutionContext Create(TimeSpan? duration = null, IDocumentOcrExecutionObserver? observer = null)
    {
        var now = DateTimeOffset.UtcNow;
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), PaddleOcrVlmRuntimeContract.CapacitySlotKey,
            PaddleOcrVlmRuntimeContract.ExecutorKey, 1, Guid.NewGuid());
        return new(new(handle, Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), new(Environment.ProcessId, now.AddMinutes(-1), new string('a', 64))),
            now, now.Add(duration ?? TimeSpan.FromMinutes(20))), observer ?? new Observer());
    }

    internal sealed class Observer : IDocumentOcrExecutionObserver
    {
        internal DocumentOcrProcessIdentity? Process { get; private set; }
        internal DocumentOcrCleanupProof? Proof { get; private set; }
        public ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
            DocumentOcrProcessIdentity process, CancellationToken cancellationToken)
        {
            Process = process;
            return ValueTask.FromResult(new DocumentOcrLifecycleResult(DocumentOcrLifecycleDisposition.Applied, "synthetic-start", lease));
        }
        public ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
            DocumentOcrCleanupProof proof, CancellationToken cancellationToken)
        {
            Proof = proof;
            return ValueTask.FromResult(new DocumentOcrLifecycleResult(DocumentOcrLifecycleDisposition.Applied, "synthetic-cleanup", lease));
        }
    }
}
