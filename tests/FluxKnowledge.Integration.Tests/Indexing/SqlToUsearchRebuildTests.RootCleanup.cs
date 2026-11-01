using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
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
    public async Task Root_cleanup_interruption_after_commit_preserves_publication_and_recovers_through_replay_or_maintenance(bool maintenance)
    {
        var observer = new InterruptRootCleanup();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The native keeper survives cleanup interruption.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, observer)));
        var record = await environment.AddRetainedAndPumpAsync("The committed root publication needs no temporary worklist to serve.");
        await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        observer.CandidateId = snapshot.SegmentedCandidateId;
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        await Assert.ThrowsAsync<RootCleanupInterruptedException>(() => store.TryActivateRefreshAsync(snapshot, default).AsTask());
        Assert.True(observer.Interrupted);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var completed = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == snapshot.SegmentedCandidateId);
        Assert.Equal(2, completed.State);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(snapshot.Generation.Id, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.True(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == completed.Id));
        await AssertSegmentedOracleAsync(environment);

        if (maintenance)
        {
            // A fresh maintenance instance has no caller replay or process-local cursor.
            var operations = await SegmentCatalogueMaintenanceTests.OperationsAsync(environment, new WindowsInteractiveGpuOwnerProbe());
            await operations.RunOnceAsync(CancellationToken.None);
        }
        else Assert.True(await store.TryActivateRefreshAsync(snapshot, default));

        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == completed.Id));
        var replayed = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == completed.Id);
        Assert.Equal(completed.CompletedAtUtc, replayed.CompletedAtUtc);
        Assert.Equal(snapshot.Generation.Id, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.True(await store.TryActivateRefreshAsync(snapshot, default));
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_abandonment_cleanup_recovers_after_terminal_commit_and_preserves_unfinished_work(bool maintenance)
    {
        var observer = new InterruptRootCleanup();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The keeper survives abandoned capture cleanup.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, observer)));
        var record = await environment.AddRetainedAndPumpAsync("The invalidated root preview can be discarded independently.");
        await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var abandoned = await store.PrepareAsync(Guid.Empty, default);
        var unfinished = await store.PrepareAsync(Guid.Empty, default);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var pointer = await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync();
        var unfinishedCount = await db.IndexCandidateMembers.CountAsync(value => value.CandidateId == unfinished.Id);
        Assert.True(unfinishedCount > 0);
        observer.CandidateId = abandoned.Id;
        await Assert.ThrowsAsync<RootCleanupInterruptedException>(() => store.AbandonAsync(abandoned.Id, default).AsTask());
        Assert.True(observer.Interrupted);
        var terminal = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == abandoned.Id);
        Assert.Equal(3, terminal.State);
        Assert.NotNull(terminal.CompletedAtUtc);
        Assert.True(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == abandoned.Id));
        if (maintenance)
        {
            var operations = await SegmentCatalogueMaintenanceTests.OperationsAsync(environment, new WindowsInteractiveGpuOwnerProbe());
            await operations.RunOnceAsync(CancellationToken.None);
        }
        else await store.AbandonAsync(abandoned.Id, default);
        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == abandoned.Id));
        Assert.Equal(unfinishedCount, await db.IndexCandidateMembers.CountAsync(value => value.CandidateId == unfinished.Id));
        Assert.Equal(0, await db.IndexPublicationCandidates.Where(value => value.Id == unfinished.Id).Select(value => value.State).SingleAsync());
        Assert.Equal(pointer, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(terminal.CompletedAtUtc, await db.IndexPublicationCandidates.Where(value => value.Id == abandoned.Id).Select(value => value.CompletedAtUtc).SingleAsync());
        await store.AbandonAsync(unfinished.Id, default);
    }

    private sealed class RootCleanupInterruptedException : Exception;

    private sealed class InterruptRootCleanup : DbTransactionInterceptor
    {
        public Guid? CandidateId { get; set; }
        public bool Interrupted { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Interrupted && CandidateId is { } id && eventData.Context!.ChangeTracker.Entries<IndexPublicationCandidateEntity>()
                .Any(value => value.Entity.Id == id && value.Entity.State is 2 or 3))
            {
                Interrupted = true;
                throw new RootCleanupInterruptedException();
            }
            return Task.CompletedTask;
        }
    }
}
