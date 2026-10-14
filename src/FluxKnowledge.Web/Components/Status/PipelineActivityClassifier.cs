using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Web.Components.Status;

public sealed record PipelineJobEvidence(
    PublicJobState PublicState,
    bool IsHistorical = false,
    bool SourceAdmitted = true,
    bool HasWorkerLease = false,
    bool HasActiveGpu = false,
    bool HasReadyGpu = false,
    bool HasUncertainGpu = false,
    bool IsDue = true,
    string? Reason = null,
    bool HasWaitingGpu = false,
    bool HasPendingDispatch = true);

public sealed record PipelineJobClassification(PipelineWorkState State, string Reason);

public static class PipelineActivityClassifier
{
    public static PipelineJobClassification Classify(PipelineJobEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.HasActiveGpu || evidence.PublicState == PublicJobState.WorkerProcessing && evidence.HasWorkerLease)
            return new(PipelineWorkState.Running, evidence.IsHistorical ? "Processing a previous revision." : "Execution ownership confirmed.");
        if (evidence.IsHistorical)
            return new(PipelineWorkState.Historical, "Previous or withdrawn source binding; prior outcome retained.");
        if (evidence.PublicState == PublicJobState.Failed)
            return new(PipelineWorkState.Attention, evidence.Reason ?? "Pipeline job failed.");
        if (evidence.HasUncertainGpu && !evidence.HasReadyGpu && !evidence.HasWaitingGpu)
            return new(PipelineWorkState.Attention, "GPU outcome is uncertain; recovery has not been authorised.");
        if (evidence.Reason == "repository-source-blocked")
            return new(PipelineWorkState.Attention, "Source processing is blocked.");
        if (evidence.Reason == "repository-source-deferred")
            return new(PipelineWorkState.Waiting, "Source processing is deferred.");
        if (evidence.Reason == "source-content-not-available-locally")
            return new(PipelineWorkState.Waiting, "Source content is not available locally. No download was requested.");
        if (!evidence.SourceAdmitted)
            return new(PipelineWorkState.Waiting, "Source intake or a maintenance fence is holding this job.");
        if (evidence.PublicState == PublicJobState.WorkerProcessing)
            return new(PipelineWorkState.Attention, "Worker lease is not confirmed.");
        if (evidence.HasWaitingGpu)
            return new(PipelineWorkState.Waiting, "GPU work is deferred or waiting for executor acknowledgement.");
        if (evidence.PublicState is PublicJobState.GpuQueued or PublicJobState.GpuProcessing && !evidence.HasReadyGpu)
            return new(PipelineWorkState.Attention, "No compatible ready or active GPU task confirms this label.");
        if (!evidence.IsDue)
            return new(PipelineWorkState.Waiting, "Waiting until the recorded retry time.");
        if (evidence.PublicState == PublicJobState.WorkerQueued && !evidence.HasPendingDispatch)
            return new(PipelineWorkState.Attention, "No compatible pending dispatch confirms this queue label.");
        return new(PipelineWorkState.Queued, "Ready for a worker or GPU scheduler.");
    }
}
