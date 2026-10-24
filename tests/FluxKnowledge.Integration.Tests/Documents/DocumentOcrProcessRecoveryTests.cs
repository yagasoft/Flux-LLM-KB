using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Integrations.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class DocumentOcrProcessRecoveryTests
{
    [Theory]
    [InlineData(GpuInteractiveOwnerObservation.Alive)]
    [InlineData(GpuInteractiveOwnerObservation.Unknown)]
    public async Task Confirmed_cleanup_does_not_replace_a_live_or_unverifiable_supervisors_pending_result_before_deadline(GpuInteractiveOwnerObservation observation)
    {
        var lease = OcrTestExecutionContext.Create().Lease;
        var recovery = new DocumentOcrProcessRecovery(new Probe(lease.Supervisor.Process, observation));
        var state = await recovery.InspectAsync(lease, null, false, true, CancellationToken.None);
        Assert.True(state.OwnerRunning);
        Assert.Null(state.Proof);
        Assert.False((await recovery.InspectAsync(lease, null, true, true, CancellationToken.None)).OwnerRunning);
    }

    [Fact]
    public async Task Empty_job_before_first_cleanup_proof_preserves_live_supervisors_pending_result()
    {
        var lease = OcrTestExecutionContext.Create().Lease;
        using var job = OwnedOcrJob.Create(lease.ExecutionInstanceId);
        var recovery = new DocumentOcrProcessRecovery(new Probe(lease.Supervisor.Process, GpuInteractiveOwnerObservation.Alive));
        var state = await recovery.InspectAsync(lease, new(1234, DateTimeOffset.UtcNow,
            lease.Supervisor.Process.MachineFingerprint), false, false, CancellationToken.None);
        Assert.True(state.OwnerRunning);
        Assert.Null(state.Proof);
    }

    [Fact]
    public async Task Missing_named_job_is_cleanup_unproven_even_when_exact_supervisor_has_exited()
    {
        var lease = OcrTestExecutionContext.Create().Lease;
        var recovery = new DocumentOcrProcessRecovery(new Probe(lease.Supervisor.Process, GpuInteractiveOwnerObservation.Exited));
        var state = await recovery.InspectAsync(lease, new(1234, DateTimeOffset.UtcNow,
            lease.Supervisor.Process.MachineFingerprint), true, false, CancellationToken.None);
        Assert.False(state.OwnerRunning);
        Assert.Null(state.Proof);
        Assert.Equal("document-ocr-job-missing-maintenance-required", state.ReasonCode);
    }

    [Fact]
    public async Task Expired_unstarted_claim_can_be_cleaned_without_relaunching_a_process()
    {
        var lease = OcrTestExecutionContext.Create().Lease;
        var recovery = new DocumentOcrProcessRecovery(new Probe(lease.Supervisor.Process, GpuInteractiveOwnerObservation.Alive),
            new Clock(lease.DeadlineAtUtc));
        var state = await recovery.InspectAsync(lease, null, false, false, CancellationToken.None);
        Assert.False(state.OwnerRunning);
        Assert.Equal(DocumentOcrCleanupKind.VerifiedUnstarted, state.Proof?.Kind);
    }

    private sealed class Probe(GpuInteractiveOwnerIdentity current, GpuInteractiveOwnerObservation observation) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => current;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => observation;
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
