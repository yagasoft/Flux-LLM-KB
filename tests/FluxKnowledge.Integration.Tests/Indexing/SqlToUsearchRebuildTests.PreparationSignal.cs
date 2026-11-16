using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Segmented_publication_wakes_preparation_only_after_commit_and_not_after_rollback(bool rollback)
    {
        var signal = new PreparationNotifications();
        var observer = new PreparationCommitObserver(signal);
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "A sealed publication wakes preparation without a query.",
            publish: false, segmented: true, configureServices: services =>
            {
                services.AddSingleton<ICorpusAnnPreparationSignal>(signal);
                services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                    new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, observer));
            });
        var request = await ClaimSegmentedPublishAsync(environment, false);
        var store = new SqlStageTransitionStore(environment.Factory, null, TimeProvider.System,
            sourceAdmission: new FixtureSourceAdmission(environment.Factory), annPreparation: signal);
        observer.Rollback = rollback;
        observer.Armed = true;
        if (rollback)
            await Assert.ThrowsAsync<PreparationCommitInterruptedException>(() => store.TransitionAsync(request, default).AsTask());
        else
            Assert.False((await store.TransitionAsync(request, default)).ExistingTransition);
        Assert.True(observer.Observed);
        Assert.Equal(rollback ? 0 : 1, signal.Count);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(rollback ? 1 : 2, await db.IndexPublicationCandidates
            .Where(value => value.Id == request.IndexingOutput!.SegmentedCandidateId).Select(value => value.State).SingleAsync());
        Assert.Equal(rollback ? null : request.IndexingOutput!.ActivateGeneration!.Id,
            await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        if (!rollback) await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Committed_refresh_wakes_preparation_before_optional_worklist_cleanup_and_replay_can_wake_again()
    {
        var signal = new PreparationNotifications();
        var observer = new PreparationCleanupObserver();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The keeper remains readable.", segmented: true,
            configureServices: services =>
            {
                services.AddSingleton<ICorpusAnnPreparationSignal>(signal);
                services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                    new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, observer));
            });
        var record = await environment.AddRetainedAndPumpAsync("Publication debt requires a new root manifest.");
        await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var before = signal.Count;
        observer.Armed = true;
        await Assert.ThrowsAsync<PreparationCleanupInterruptedException>(() => store.TryActivateRefreshAsync(snapshot, default).AsTask());
        Assert.True(observer.Observed);
        Assert.Equal(before + 1, signal.Count);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(snapshot.Generation.Id, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(2, await db.IndexPublicationCandidates.Where(value => value.Id == snapshot.SegmentedCandidateId)
            .Select(value => value.State).SingleAsync());
        Assert.True(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == snapshot.SegmentedCandidateId));
        Assert.True(await store.TryActivateRefreshAsync(snapshot, default));
        Assert.Equal(before + 2, signal.Count);
        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == snapshot.SegmentedCandidateId));
        await AssertSegmentedOracleAsync(environment);
    }

    private sealed class PreparationNotifications : ICorpusAnnPreparationSignal
    {
        public int Count { get; private set; }
        public void RequestPreparation() => Count++;
    }

    private sealed class PreparationCommitInterruptedException : Exception;
    private sealed class PreparationCleanupInterruptedException : Exception;

    private sealed class PreparationCommitObserver(PreparationNotifications signal) : DbTransactionInterceptor
    {
        public bool Armed, Rollback, Observed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<IndexPublicationCandidateEntity>()
                .Any(value => value.Entity.State == 2))
            {
                Armed = false;
                Observed = true;
                Assert.Equal(0, signal.Count); // The wake must never precede the durable commit.
                if (Rollback) throw new PreparationCommitInterruptedException();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PreparationCleanupObserver : DbCommandInterceptor
    {
        public bool Armed, Observed;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("DELETE TOP", StringComparison.Ordinal) &&
                command.CommandText.Contains("[IndexCandidateMembers]", StringComparison.Ordinal))
            {
                Armed = false;
                Observed = true;
                throw new PreparationCleanupInterruptedException();
            }
            return ValueTask.FromResult(result);
        }
    }
}
