using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineFileSampleTests
{
    [Fact]
    public void File_stage_uses_current_binding_and_each_file_once_despite_overlapping_jobs()
    {
        var file = File();
        var current = Job(file, PipelineStage.Embed);
        current.HasActiveGpu = true;
        var previous = Job(file, PipelineStage.Publish);
        previous.IsHistorical = true;
        var overlap = Job(file, PipelineStage.Embed);
        overlap.PipelineRecordId = current.PipelineRecordId;
        var items = PipelineFileSample.Project([file], [previous, overlap, current], [], [], new Dictionary<Guid, string>());
        var item = Assert.Single(items);
        Assert.Equal("Embedding", item.File.Stage);
        Assert.Equal("Running", item.File.State);
        Assert.Equal(2, item.File.Jobs.Count);
        Assert.DoesNotContain(item.File.Jobs, j => j.Id == previous.Id);
    }

    [Fact]
    public void Ocr_requires_bound_demand_and_confirmed_execution_not_an_extract_label()
    {
        var file = File();
        var job = Job(file, PipelineStage.Extract);
        job.PublicState = 3;
        var demand = new PipelineFileOcrRow { PipelineRecordId = job.PipelineRecordId, SourceRevision = 1,
            HasPending = true, RequestedPages = 4 };
        var unowned = Assert.Single(PipelineFileSample.Project([file], [job], [], [demand], new Dictionary<Guid, string>()));
        Assert.Equal("Awaiting OCR", unowned.File.Stage);
        Assert.True(unowned.RequiresOcr);
        Assert.False(unowned.OcrRunning);
        job.HasActiveGpu = true;
        // Unrelated active GPU work is insufficient to call the bound OCR request running.
        var unrelated = Assert.Single(PipelineFileSample.Project([file], [job], [], [demand], new Dictionary<Guid, string>()));
        Assert.False(unrelated.OcrRunning);
        demand.HasActive = true;
        var running = Assert.Single(PipelineFileSample.Project([file], [job], [], [demand], new Dictionary<Guid, string>()));
        Assert.Equal("OCR running", running.File.Stage);
        Assert.True(running.OcrRunning);
        var undecided = Assert.Single(PipelineFileSample.Project([file], [job], [], [], new Dictionary<Guid, string>()));
        Assert.True(undecided.OcrDecisionUnconfirmed);
        Assert.False(undecided.RequiresOcr);
        var text = file with { Classification = "AcceptedUtf8Text" };
        var nonOcr = Assert.Single(PipelineFileSample.Project([text], [job], [], [], new Dictionary<Guid, string>()));
        Assert.False(nonOcr.OcrDecisionUnconfirmed);
    }

    [Fact]
    public void Confirmed_retained_processor_precedes_queued_jobs_and_unconfirmed_claims_are_attention()
    {
        var branch = Guid.NewGuid();
        var file = File() with { SelectedBranchId = branch };
        var queued = Job(file, PipelineStage.Embed);
        var running = Assert.Single(PipelineFileSample.Project([file], [queued],
            [new(branch, file.SourceRevisionId, true)], [], new Dictionary<Guid, string>()));
        Assert.Equal("Extracting", running.File.Stage);
        Assert.Equal("Running", running.File.State);
        Assert.Single(running.File.Jobs);
        var uncertain = Assert.Single(PipelineFileSample.Project([file], [],
            [new(branch, file.SourceRevisionId, false, true)], [], new Dictionary<Guid, string>()));
        Assert.Equal("Stage unconfirmed", uncertain.File.Stage);
        Assert.Equal("Needs attention", uncertain.File.State);
    }

    [Fact]
    public void Unconfirmed_file_does_not_claim_a_running_stage_and_nonpending_file_keeps_its_outcome()
    {
        var unknown = File() with { Category = "Unconfirmed" };
        var job = Job(unknown, PipelineStage.Embed);
        job.HasActiveGpu = true;
        var unknownItem = Assert.Single(PipelineFileSample.Project([unknown], [job], [], [], new Dictionary<Guid, string>()));
        Assert.Equal("Stage unconfirmed", unknownItem.File.Stage);
        Assert.Equal("Unconfirmed", unknownItem.File.State);
        var published = unknown with { Category = "Published" };
        var item = Assert.Single(PipelineFileSample.Project([published], [job], [], [], new Dictionary<Guid, string>()));
        Assert.Equal("Published", item.File.Category);
        Assert.Empty(item.File.Jobs);
    }

    private static SourceLogicalFileState File() => new(Guid.NewGuid(), Guid.NewGuid(), "D:/source/file.pdf",
        null, "Pending", null, null, null);
    private static PipelineJobActivityRow Job(SourceLogicalFileState file, PipelineStage stage) => new()
    {
        Id = Guid.NewGuid(), PipelineRecordId = Guid.NewGuid(), LogicalSourceRevisionId = file.SourceRevisionId,
        SourceRootId = file.SourceRootId, SourceRevision = 1, Stage = (int)stage, CurrentStage = (int)stage,
        PublicState = 0, SourceAdmitted = true, HasPendingDispatch = true, IsDue = true
    };
}
