using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Low_index_capacity_preserves_publication_and_success_clears_the_current_retry_reason()
    {
        var available = long.MaxValue;
        var clock = new CapacityClock();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "An already published source.",
            clock: clock, segmented: true, configureServices: services =>
                services.AddScoped<IIndexGenerationPublisher>(provider => new SegmentedIndexPublisher(
                    provider.GetRequiredService<UsearchGenerationBuilder>(), provider.GetRequiredService<ISegmentedIndexStore>(),
                    provider.GetRequiredService<IDerivedIndexRecoveryStore>(),
                    new UsearchSegmentBuilder(provider.GetRequiredService<UsearchIndexOptions>(),
                        provider.GetRequiredService<IIndexSegmentReadLeaseFactory>(), provider.GetRequiredService<IndexSegmentBuildOptions>())
                        { AvailableBytesOverride = () => available },
                    new UsearchManifestValidator(provider.GetRequiredService<UsearchIndexOptions>(),
                        provider.GetRequiredService<IIndexSegmentReadLeaseFactory>()), enabled: true)));
        var original = await environment.ActiveGenerationAsync();
        await using var db = await environment.Factory.CreateDbContextAsync();
        var candidates = await db.IndexPublicationCandidates.CountAsync();
        available = 0;
        await environment.AddAndPumpAsync("A second source waits for app-owned storage.");
        var waiting = await db.Jobs.AsNoTracking().SingleAsync(value => value.Reason == "index-storage-capacity-deferred");
        Assert.Equal((int)PublicJobState.WorkerQueued, waiting.PublicState);
        Assert.Equal(original.Id, (await environment.ActiveGenerationAsync()).Id);
        Assert.Equal(candidates, await db.IndexPublicationCandidates.CountAsync());
        Assert.False(await db.Jobs.AnyAsync(value => value.PublicState == (int)PublicJobState.Failed));

        available = long.MaxValue;
        clock.Advance();
        await environment.PumpAsync();
        var completed = await db.Jobs.AsNoTracking().SingleAsync(value => value.Id == waiting.Id);
        Assert.Equal((int)PublicJobState.Completed, completed.PublicState);
        Assert.Null(completed.Reason);
        Assert.Null(completed.ErrorDetails);
        Assert.NotEqual(original.Id, (await environment.ActiveGenerationAsync()).Id);
        await AssertSegmentedOracleAsync(environment);
    }

    private sealed class CapacityClock : TimeProvider
    {
        private TimeSpan _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
        public void Advance() => _offset = TimeSpan.FromMinutes(2);
    }
}
