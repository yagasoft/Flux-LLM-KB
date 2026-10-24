using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineFilePresentationTests
{
    [Fact]
    public void Overlapping_jobs_choose_confirmed_execution_and_preserve_other_jobs()
    {
        var pending = Job(PipelineStage.Embed, "embedding-gpu-retry");
        var running = Job(PipelineStage.Embed);
        running.HasActiveGpu = true;
        running.PublicState = 3;
        var selected = PipelineFilePresentation.SelectJob([pending, running]);
        Assert.Same(running, selected);
        Assert.Equal("Running", PipelineFilePresentation.WorkState(running));
        Assert.Equal("Retrying", PipelineFilePresentation.WorkState(pending));
    }

    [Fact]
    public void Normal_batch_continuation_is_not_a_retry_and_age_does_not_prove_retry()
    {
        var continuation = Job(PipelineStage.Embed, "embedding-batch-persisted");
        Assert.Equal("Queued continuation", PipelineFilePresentation.WorkState(continuation));
        Assert.Equal("Queued", PipelineFilePresentation.WorkState(Job(PipelineStage.Embed)));
        var held = Job(PipelineStage.Embed, "embedding-batch-persisted");
        held.SourceAdmitted = false;
        Assert.Equal("Waiting for dependency", PipelineFilePresentation.WorkState(held));
        var uncertain = Job(PipelineStage.Embed, "embedding-batch-persisted");
        uncertain.HasUncertainGpu = true;
        Assert.Equal("Needs attention", PipelineFilePresentation.WorkState(uncertain));
    }

    [Theory]
    [InlineData("D:/test/sub1/a/really/long/path/to/another/folder/file.ext")]
    [InlineData("D:\\test\\sub1\\a\\really\\long\\path\\to\\another\\folder\\file.ext")]
    public void Path_keeps_root_and_filename_when_shortening_the_middle(string path)
    {
        var shortened = PipelineFilePresentation.ShortenPath(path, 38);
        Assert.StartsWith("D:/test/", shortened);
        Assert.EndsWith("/file.ext", shortened);
        Assert.Contains("…", shortened);
        Assert.True(shortened.Length <= 38);
    }

    [Fact]
    public void Long_filename_is_kept_with_its_root_even_when_it_exceeds_the_short_path_target()
    {
        var filename = new string('x', 100) + ".txt";
        var path = PipelineFilePresentation.ShortenPath("D:/test/deep/folder/" + filename);
        Assert.StartsWith("D:/", path);
        Assert.EndsWith(filename, path);
        var network = PipelineFilePresentation.ShortenPath("//server/share/deep/folder/" + filename);
        Assert.StartsWith("//server/share/", network);
        Assert.EndsWith(filename, network);
    }

    [Fact]
    public void Category_counts_reconcile_including_unknown_revision_and_nontext_outcomes()
    {
        var source = new SourcePipelineProgress(Guid.NewGuid(), "source", "D:/source", "Enabled",
            36, 8, 7, 6, 5, 4, 3, 2, null, null, null, false, null, ProvisionalFiles: 1);
        var categories = PipelineFilePresentation.Categories(source);
        Assert.Equal(source.KnownFiles, categories.Sum(c => c.Count));
        Assert.Equal(8, Assert.Single(categories, c => c.Category == "Published").Count);
        Assert.Equal(1, Assert.Single(categories, c => c.Category == "Unconfirmed").Count);
        Assert.Contains(categories, c => c.Label == "Published — ready to query");
    }

    private static PipelineJobActivityRow Job(PipelineStage stage, string? reason = null) => new()
    {
        Id = Guid.NewGuid(), Stage = (int)stage, PublicState = 0, SourceAdmitted = true,
        HasPendingDispatch = true, IsDue = true, Reason = reason
    };
}
