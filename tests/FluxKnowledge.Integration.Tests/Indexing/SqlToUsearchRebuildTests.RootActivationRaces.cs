using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
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
    public async Task Root_activation_revalidates_candidate_and_canonical_rows_after_staged_hash_proof(bool canonical)
    {
        var observer = new BeforeRootActivationFence();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The race keeper.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, observer)));
        var record = await environment.AddRetainedAndPumpAsync("The sealed proof is checked again at activation.");
        var (root, _) = await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        await using var before = await environment.Factory.CreateDbContextAsync();
        var pointer = await before.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync();
        observer.Mutate = async () =>
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            if (canonical)
                await db.CurrentIndexMembers.Where(value => value.SourceRootId == root)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OwnerSourceRevisionId, (Guid?)null));
            else
                await db.IndexPublicationCandidates.Where(value => value.Id == snapshot.SegmentedCandidateId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OwnerInstanceId, Guid.NewGuid()));
        };
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        if (canonical)
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryActivateRefreshAsync(snapshot, default).AsTask());
        else Assert.False(await store.TryActivateRefreshAsync(snapshot, default));
        Assert.True(observer.Mutated);
        Assert.Equal(pointer, await before.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(1, await before.IndexPublicationCandidates.Where(value => value.Id == snapshot.SegmentedCandidateId).Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_activation_concurrent_replays_acknowledge_the_same_completed_candidate()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The replay keeper.", segmented: true);
        var record = await environment.AddRetainedAndPumpAsync("The root proof supports concurrent acknowledgement replay.");
        await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var results = await Task.WhenAll(store.TryActivateRefreshAsync(snapshot, default).AsTask(), store.TryActivateRefreshAsync(snapshot, default).AsTask());
        Assert.All(results, value => Assert.True(value));
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IndexPublicationCandidates.Where(value => value.Id == snapshot.SegmentedCandidateId).Select(value => value.State).SingleAsync());
        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == snapshot.SegmentedCandidateId));
        await AssertSegmentedOracleAsync(environment);
    }

    private sealed class BeforeRootActivationFence : DbCommandInterceptor
    {
        public Func<Task>? Mutate { get; set; }
        public bool Mutated { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Mutate is { } mutation && command.CommandText.Contains("[IndexState]", StringComparison.Ordinal) && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            {
                Mutate = null;
                await mutation();
                Mutated = true;
            }
            return result;
        }
    }
}
