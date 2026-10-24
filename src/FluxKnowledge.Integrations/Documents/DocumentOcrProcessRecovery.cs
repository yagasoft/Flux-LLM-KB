using System.ComponentModel;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Integrations.Windows;

namespace FluxKnowledge.Integrations.Documents;

public sealed class DocumentOcrProcessRecovery(IGpuInteractiveOwnerProbe? ownerProbe = null,
    TimeProvider? timeProvider = null) : IDocumentOcrProcessRecovery
{
    private readonly IGpuInteractiveOwnerProbe _probe = ownerProbe ?? new WindowsInteractiveGpuOwnerProbe();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<DocumentOcrRecoveryObservation> InspectAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity? process, bool hasStoredResult, bool cleanupConfirmed, CancellationToken cancellationToken)
    {
        lease.Validate();
        if (lease.Supervisor.Process.MachineFingerprint != _probe.Current.MachineFingerprint)
            return new(null, "document-ocr-foreign-owner", cleanupConfirmed && !hasStoredResult && _clock.GetUtcNow() < lease.DeadlineAtUtc);
        var supervisor = _probe.Observe(lease.Supervisor.Process);
        // Cleanup precedes the original executor's bounded result read/write. An empty job is
        // physical evidence, not permission to replace a healthy supervisor's pending result.
        if (cleanupConfirmed)
            return !hasStoredResult && _clock.GetUtcNow() < lease.DeadlineAtUtc && supervisor != GpuInteractiveOwnerObservation.Exited
                ? new(null, "document-ocr-result-write-pending", true)
                : new(null, "document-ocr-cleanup-already-confirmed");
        if (supervisor == GpuInteractiveOwnerObservation.Unknown) return new(null, "document-ocr-owner-unverifiable");
        if (process is null)
            return supervisor == GpuInteractiveOwnerObservation.Alive && _clock.GetUtcNow() < lease.DeadlineAtUtc && !hasStoredResult
                ? new(null, "document-ocr-owner-running", true)
                : new(new(lease.ExecutionInstanceId, DocumentOcrCleanupKind.VerifiedUnstarted, null, _clock.GetUtcNow()), "document-ocr-unstarted-cleanup");
        if (process.MachineFingerprint != lease.Supervisor.Process.MachineFingerprint)
            return new(null, "document-ocr-owner-unverifiable");
        try
        {
            using var job = OwnedOcrJob.TryOpen(lease.ExecutionInstanceId);
            if (job is null) return new(null, "document-ocr-job-missing-maintenance-required");
            _ = job.ActiveProcesses; // Verify the opened job remains queryable, including an empty one.
            if (supervisor == GpuInteractiveOwnerObservation.Alive &&
                _clock.GetUtcNow() < lease.DeadlineAtUtc && !hasStoredResult)
                return new(null, "document-ocr-owner-running", true);
            if (!await job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                return new(null, "document-ocr-native-cleanup-unproven");
            return new(new(lease.ExecutionInstanceId, DocumentOcrCleanupKind.OwnedJobEmpty, process, _clock.GetUtcNow()), "document-ocr-owned-job-empty");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(null, "document-ocr-job-unverifiable-maintenance-required");
        }
    }
}
