using System.Text.Json;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_below_or_above_the_frozen_key_ceiling_invalidates_root_capture(bool aboveCeiling)
    {
        var append = new SegmentedSelectionScaleTests.MetadataAppendObserver();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "The initial native keeper precedes the concurrent publication.", publish: false, segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, append)));
        var transition = new SqlStageTransitionStore(environment.Factory,
            sourceAdmission: new global::FluxKnowledge.Integration.Tests.Support.FixtureSourceAdmission(environment.Factory));
        var initial = await ClaimSegmentedPublishAsync(environment, false);
        await transition.TransitionAsync(initial, CancellationToken.None);
        (Guid Source, Guid Draft, long Vector)? pending = aboveCeiling ? null : await CreatePendingAsync();
        long ceiling;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var state = await db.IndexState.SingleAsync();
            state.CorpusVersion++;
            db.IndexProjectionChanges.Add(new()
            {
                CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3,
                ScopeId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            ceiling = await db.Vectors.MaxAsync(value => value.VectorId);
        }
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        append.Arm();
        var preparing = store.PrepareAsync(Guid.Empty, budget.Token).AsTask();
        (Guid Source, Guid Draft, long Vector) published;
        try
        {
            if (await Task.WhenAny(append.Entered.Task, preparing) == preparing) await preparing;
            await append.Entered.Task.WaitAsync(budget.Token);
            published = pending ?? await CreatePendingAsync();
            Assert.Equal(aboveCeiling, published.Vector > ceiling);
            await using var db = await environment.Factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, budget.Token);
            // Commit the canonical publication ahead of its rebuildable projection.
            // This disposable fault injection uses the normal publication predicate
            // transition and version log; no production binding is available.
            await SqlPipelineStore.PreviewDraftAsync(db, published.Draft, DateTimeOffset.UtcNow, budget.Token);
            await SqlPublishedPassageSelection.AdvanceVersionAsync(db, DateTimeOffset.UtcNow, budget.Token, [new(1, published.Source)]);
            await db.SaveChangesAsync(budget.Token);
            await transaction.CommitAsync(budget.Token);
        }
        finally { append.Release.TrySetResult(); }
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(() => preparing);
        await using var after = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(initial.IndexingOutput!.ActivateGeneration!.Id, await after.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        var retry = await store.PrepareAsync(Guid.Empty, budget.Token);
        Assert.True(await after.IndexCandidateMembers.AnyAsync(value => value.CandidateId == retry.Id && value.Phase == 1 && value.VectorId == published.Vector));
        await store.AbandonAsync(retry.Id, CancellationToken.None);

        async Task<(Guid Source, Guid Draft, long Vector)> CreatePendingAsync()
        {
            await environment.AddAndPumpAsync("The later canonical input must not be lost outside the frozen range.");
            var recordId = environment.LastReceipt!.PipelineRecordId.Value;
            await using var db = await environment.Factory.CreateDbContextAsync();
            var source = await db.PipelineRecords.Where(value => value.Id == recordId).Select(value => value.SourceIdentityId).SingleAsync();
            var vector = await (from value in db.Vectors join chunk in db.TextChunks on value.TextChunkId equals chunk.Id
                join artifact in db.Artifacts on chunk.ArtifactId equals artifact.Id where artifact.PipelineRecordId == recordId
                select new { value.VectorId, value.IndexGenerationId }).SingleAsync();
            return (source, vector.IndexGenerationId, vector.VectorId);
        }
    }

    [NativeSqlServerFact]
    public async Task A_previously_sealed_draft_with_root_debt_defers_before_membership_validation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "An old sealed draft cannot monopolise root recovery.", publish: false, segmented: true);
        var request = await ClaimSegmentedPublishAsync(environment, false);
        var id = request.IndexingOutput!.SegmentedCandidateId!.Value;
        await using var db = await environment.Factory.CreateDbContextAsync();
        var candidate = await db.IndexPublicationCandidates.SingleAsync(value => value.Id == id);
        var scopes = JsonSerializer.Deserialize<IndexAffectedScope[]>(candidate.AffectedScopesJson)!;
        // Model a worklist sealed by the preceding release. The old metadata is
        // retained; activation must refuse before walking its root-wide proof.
        candidate.AffectedScopesJson = JsonSerializer.Serialize(scopes.Append(new(3, Guid.NewGuid())));
        await db.SaveChangesAsync();
        var before = await db.IndexState.AsNoTracking().SingleAsync();
        var error = await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await new SqlStageTransitionStore(environment.Factory,
                sourceAdmission: new global::FluxKnowledge.Integration.Tests.Support.FixtureSourceAdmission(environment.Factory))
                .TransitionAsync(request, CancellationToken.None));
        Assert.Contains("Root-wide projection recovery", error.Message, StringComparison.Ordinal);
        Assert.Equal(before.ActiveIndexGenerationId, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(before.CorpusVersion, await db.IndexState.Select(value => value.CorpusVersion).SingleAsync());
        Assert.Equal(1, await db.IndexPublicationCandidates.Where(value => value.Id == id).Select(value => value.State).SingleAsync());
        Assert.False(await db.Artifacts.AnyAsync(value => value.Id == request.Artifact.Id));
    }
}
