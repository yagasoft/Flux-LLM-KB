using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class CorpusRebuildPlanTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Plan_captures_exact_current_inputs_and_derived_files_without_mutating_published_work()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Plan retains this source.");
        await using var context = environment.Factory.CreateDbContext();
        var canonical = await context.Artifacts.AsNoTracking().SingleAsync(value => value.Stage == (int)PipelineStage.CanonicalIndex);
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        var jobs = await context.Jobs.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.PublicState, value.RowVersion }).ToArrayAsync();
        var plan = await new SqlCorpusRebuildStore(environment.Factory).ReadPlanAsync(Guid.NewGuid(), new("new-embedding-profile", 1024),
            new string('b', 64), CancellationToken.None);
        var input = Assert.Single(plan.Inputs);
        Assert.Equal(canonical.Id, input.CanonicalArtifactId);
        Assert.Equal(canonical.ContentHash, input.ContentHash);
        Assert.NotEqual(state.CorpusEpoch, plan.TargetEpoch);
        Assert.Equal(state.CorpusEpoch, plan.PreviousStamp.CorpusEpoch);
        Assert.Equal(state.CorpusVersion, plan.PreviousStamp.CorpusVersion);
        Assert.Equal(64, plan.ManifestHash.Length);
        Assert.NotEmpty(plan.Generations);
        Assert.All(plan.ProjectionArtifacts, artifact => Assert.True(artifact.Stage is (int)PipelineStage.Embed or (int)PipelineStage.Publish));
        Assert.Equal(await context.TextChunks.LongCountAsync(), plan.ChunkCount);
        Assert.Equal(await context.Vectors.LongCountAsync(), plan.VectorCount);
        Assert.Equal(state.CorpusEpoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
        var unchanged = await context.Jobs.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.PublicState, value.RowVersion }).ToArrayAsync();
        Assert.Equal(jobs.Select(value => (value.Id, value.PublicState, Convert.ToHexString(value.RowVersion))),
            unchanged.Select(value => (value.Id, value.PublicState, Convert.ToHexString(value.RowVersion))));
    }

    [NativeSqlServerTheory]
    [InlineData(PublicJobState.WorkerQueued)]
    [InlineData(PublicJobState.WorkerProcessing)]
    [InlineData(PublicJobState.GpuQueued)]
    [InlineData(PublicJobState.GpuProcessing)]
    [InlineData(PublicJobState.Failed)]
    public async Task Plan_refuses_unsettled_or_retryable_projection_work(PublicJobState state)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unsettled source.");
        await using var context = environment.Factory.CreateDbContext();
        await context.Jobs.Where(value => value.Stage == (int)PipelineStage.Embed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PublicState, (int)state));
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await new SqlCorpusRebuildStore(environment.Factory).ReadPlanAsync(Guid.NewGuid(), new("new-profile", 1024), new string('b', 64), CancellationToken.None));
        Assert.Equal("corpus-rebuild-projection-work-unsettled", error.Message);
        Assert.NotEmpty(await context.TextChunks.ToArrayAsync());
        Assert.NotEmpty(await context.Vectors.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Plan_includes_published_paused_inputs_and_refuses_suppressed_inputs_without_changing_root_state()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted control.");
        var retainedRecordId = await environment.AddRetainedAndPumpAsync("Published retained source.");
        await using var context = environment.Factory.CreateDbContext();
        var retained = await context.SourceRevisions.SingleAsync();
        var root = await context.SourceRootConfigurations.SingleAsync();
        root.State = (int)SourceRootState.Paused;
        await context.SaveChangesAsync();
        var plan = await new SqlCorpusRebuildStore(environment.Factory).ReadPlanAsync(Guid.NewGuid(), new("new-profile", 1024), new string('b', 64), CancellationToken.None);
        Assert.Contains(plan.Inputs, input => input.PipelineRecordId == retainedRecordId);
        Assert.Equal((int)SourceRootState.Paused, (await context.SourceRootConfigurations.AsNoTracking().SingleAsync()).State);
        retained.SuppressedAtUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await new SqlCorpusRebuildStore(environment.Factory).ReadPlanAsync(Guid.NewGuid(), new("new-profile", 1024), new string('b', 64), CancellationToken.None));
        Assert.Equal("corpus-rebuild-excluded-resumable-input", error.Message);
        Assert.Equal((int)SourceRootState.Paused, (await context.SourceRootConfigurations.AsNoTracking().SingleAsync()).State);
        Assert.NotEmpty(await context.TextChunks.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Plan_refuses_to_erase_a_canonical_input_outside_its_published_worklist()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Excluded but resumable source.");
        await using var context = environment.Factory.CreateDbContext();
        await context.PipelineRecords.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.CompletionCriteriaMet, false));
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await new SqlCorpusRebuildStore(environment.Factory).ReadPlanAsync(Guid.NewGuid(), new("new-profile", 1024), new string('b', 64), CancellationToken.None));
        Assert.Equal("corpus-rebuild-excluded-resumable-input", error.Message);
        Assert.NotEmpty(await context.TextChunks.ToArrayAsync());
    }
}
