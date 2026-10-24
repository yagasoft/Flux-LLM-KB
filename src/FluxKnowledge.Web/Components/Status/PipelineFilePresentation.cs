using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;

namespace FluxKnowledge.Web.Components.Status;

public static class PipelineFilePresentation
{
    public static IReadOnlyList<PipelineFileCategoryCount> Categories(SourcePipelineProgress source) =>
    [
        new("Published", "Published — ready to query", source.PublishedFiles),
        new("Pending", "Pending", source.PendingFiles),
        new("Blocked", "Blocked", source.BlockedFiles),
        new("Failed", "Failed", source.FailedFiles),
        new("Deferred", "Deferred", source.DeferredFiles),
        new("MetadataOnly", "Metadata-only", source.MetadataOnlyFiles),
        new("NoText", "No extractable text", source.CompletedWithoutTextFiles),
        new("Unconfirmed", "Unconfirmed revisions", source.ProvisionalFiles)
    ];

    public static string ShortenPath(string path, int maximumLength = 64)
    {
        var value = path.Replace('\\', '/');
        if (value.Length <= maximumLength) return value;
        var last = value.LastIndexOf('/');
        if (last < 0) return value; // Preserve a filename, even when it alone exceeds the target.
        var filename = value[(last + 1)..];
        var budget = maximumLength - filename.Length - 3;
        if (budget < 3)
        {
            var rootEnd = value.IndexOf('/', value.StartsWith("//", StringComparison.Ordinal) ? 2 : 0);
            if (value.StartsWith("//", StringComparison.Ordinal) && rootEnd >= 0) rootEnd = value.IndexOf('/', rootEnd + 1);
            return value[..(rootEnd >= 0 ? rootEnd : last)] + "/…/" + filename;
        }
        var prefix = value[..Math.Min(budget, last)];
        var boundary = prefix.LastIndexOf('/');
        if (boundary >= 2) prefix = prefix[..boundary];
        return prefix.TrimEnd('/') + "/…/" + filename;
    }

    internal static PipelineJobClassification Classify(PipelineJobActivityRow row) =>
        PipelineActivityClassifier.Classify(new((PublicJobState)row.PublicState, row.IsHistorical,
            row.SourceAdmitted, row.HasWorkerLease, row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu,
            row.IsDue, row.Reason, row.HasWaitingGpu, row.HasPendingDispatch));

    internal static PipelineJobActivityRow? SelectJob(IEnumerable<PipelineJobActivityRow> jobs) => jobs
        .Where(j => !j.IsHistorical)
        .OrderBy(j => Classify(j).State switch
        {
            PipelineWorkState.Running => 0,
            PipelineWorkState.Attention => 1,
            PipelineWorkState.Waiting => 2,
            _ => 3
        }).ThenBy(j => j.Stage).ThenBy(j => j.Id).FirstOrDefault();

    internal static string WorkState(PipelineJobActivityRow row)
    {
        var classified = Classify(row);
        if (classified.State == PipelineWorkState.Running) return "Running";
        if (classified.State == PipelineWorkState.Attention) return "Needs attention";
        if (classified.State == PipelineWorkState.Historical) return "Historical";
        if (!row.SourceAdmitted || row.HasWaitingGpu) return "Waiting for dependency";
        if (row.Reason is "embedding-gpu-retry" or "worker-retry" or "transient-failure") return "Retrying";
        if (row.Reason == "embedding-batch-persisted") return "Queued continuation";
        return classified.State == PipelineWorkState.Waiting ? "Waiting for dependency" : "Queued";
    }

    public static string NextAction(string? reason) => reason switch
    {
        "source-content-not-available-locally" => "Check that this file is available locally, then inspect source status.",
        "source-revision-binding-unconfirmed" => "Inspect source reconciliation; no winning revision is confirmed.",
        "repository-source-blocked" => "Review the source policy and recorded refusal.",
        "repository-source-deferred" => "Review the source capability and recorded deferral.",
        _ when reason?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true ||
               reason?.Contains("access-denied", StringComparison.OrdinalIgnoreCase) == true => "Inspect source access and the recorded error.",
        _ when reason?.Contains("unsupported", StringComparison.OrdinalIgnoreCase) == true => "Review the required format capability.",
        _ when reason?.Contains("uncertain", StringComparison.OrdinalIgnoreCase) == true => "Review execution and native cleanup evidence before recovery.",
        _ => "Inspect the file's source and stage details."
    };
}
