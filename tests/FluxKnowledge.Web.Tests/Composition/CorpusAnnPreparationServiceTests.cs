using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class CorpusAnnPreparationServiceTests
{
    [Fact]
    public async Task A_readable_unchanged_generation_does_not_self_schedule_when_only_corpus_version_advanced()
    {
        var state = new State { Current = false, Load = async _ => await Task.Yield() };
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.StartAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // Give an incorrectly self-scheduled pass a chance to run.
        await host.StopAsync(default);
        Assert.Equal(1, state.Prepares);
        Assert.Equal(2, state.Disposals); // Preparation and the active-identity observation.
        Assert.Equal("prepared", host.State.Status);
    }

    [Fact]
    public async Task A_different_active_generation_is_prepared_after_the_captured_one_finishes()
    {
        var state = new State();
        var next = state.Generation with { Id = Guid.NewGuid(), CorpusStamp = state.Generation.CorpusStamp! with { CorpusVersion = 2 } };
        state.Load = _ => { state.Generation = next; return Task.CompletedTask; };
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.StartAsync(default);
        await state.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync(default);
        Assert.Equal(2, state.Prepares);
        Assert.Equal(3, state.Disposals);
        Assert.Equal(next.Id, host.State.Generation);
        Assert.Equal("prepared", host.State.Status);
    }

    [Fact]
    public async Task Concurrent_requests_coalesce_and_preparation_owns_separate_leases()
    {
        var state = new State();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Load = ct => release.Task.WaitAsync(ct);
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.StartAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) host.RequestPreparation();
        Assert.Equal(1, state.Prepares);
        Assert.Equal("warming", host.State.Status);
        Assert.Equal(0, state.Disposals);
        release.SetResult();
        await state.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync(default);
        Assert.Equal(2, state.Prepares);
        Assert.Equal(2, state.Disposals);
        Assert.Equal("prepared", host.State.Status);
        Assert.Equal(state.Generation.Id, host.State.Generation);
    }

    [Fact]
    public async Task Nonfitting_generations_are_reported_without_repeating_the_same_load()
    {
        var state = new State { Load = _ => Task.FromException(new PassageRetrievalRefusalException("scope-capacity-exceeded")) };
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.PrepareOnceAsync(default);
        await host.PrepareOnceAsync(default);
        Assert.Equal(1, state.Prepares);
        Assert.Equal(2, state.Disposals);
        Assert.Equal("scope-capacity-exceeded", host.FailureFor(state.Generation.Id));
        Assert.Null(host.FailureFor(Guid.NewGuid()));
    }

    [Fact]
    public async Task Shutdown_deadline_does_not_dispose_a_lease_while_preparation_ignores_cancellation()
    {
        var state = new State();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Load = _ => release.Task;
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.StartAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await host.StopAsync(deadline.Token);
        Assert.Equal(0, state.Disposals);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExecuteTask!);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Lost_epoch_or_readability_during_preparation_never_publishes_prepared_state()
    {
        var state = new State();
        state.Load = _ => { state.Readable = false; return Task.CompletedTask; };
        await using var provider = Services(state).BuildServiceProvider();
        using var host = Host(provider);
        await host.PrepareOnceAsync(default);
        Assert.Equal("failed", host.State.Status);
        Assert.Equal("warming", host.State.Reason);
        Assert.Equal(1, state.Disposals);
    }

    private static CorpusAnnPreparationService Host(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CorpusAnnPreparationService>.Instance);
    private static ServiceCollection Services(State state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddScoped<ICorpusGenerationLeaseStore, Store>();
        services.AddScoped<ICorpusAnnLeaseFactory, Factory>();
        services.AddScoped<ICorpusRetrievalReader, Metadata>();
        services.AddSingleton<IScheduledPassageInference, Models>();
        services.AddSingleton<IGpuInteractiveOwnerProbe, Owner>();
        return services;
    }
    private sealed class State
    {
        public IndexGenerationDescriptor Generation = new(Guid.NewGuid(), "embedding", 2, "synthetic", "checksum", 1,
            new(Guid.NewGuid(), 1), 2, 1);
        public int Prepares, Disposals;
        public bool Readable = true;
        public bool Current = true;
        public Func<CancellationToken, Task> Load = _ => Task.CompletedTask;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Store(State state) : ICorpusGenerationLeaseStore
    {
        public ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid instance, GpuInteractiveOwnerIdentity owner, string fingerprint,
            int dimensions, CancellationToken ct) => ValueTask.FromResult<ICorpusGenerationLease?>(new Lease(state));
    }
    private sealed class Factory : ICorpusAnnLeaseFactory
    {
        public ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken ct) => ValueTask.FromResult((ICorpusAnnLease)lease);
    }
    private sealed class Lease(State state) : ICorpusAnnLease
    {
        private bool _prepared;
        private readonly IndexGenerationDescriptor _generation = state.Generation;
        public Guid LeaseId { get; } = Guid.NewGuid();
        public IndexGenerationDescriptor Generation => _generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => ValueTask.FromResult(state.Readable && state.Current && _generation == state.Generation);
        public ValueTask<bool> IsReadableAsync(CancellationToken ct) => ValueTask.FromResult(state.Readable);
        public bool IsPrepared(ICorpusAnnBindingPreparation metadata) => _prepared;
        public async ValueTask PrepareAsync(ICorpusAnnBindingPreparation metadata, CancellationToken ct)
        {
            var calls = Interlocked.Increment(ref state.Prepares);
            state.Started.TrySetResult();
            if (calls == 2) state.SecondStarted.TrySetResult();
            await state.Load(ct); _prepared = true;
        }
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Interlocked.Increment(ref state.Disposals); return ValueTask.CompletedTask; }
    }
    private sealed class Models : IScheduledPassageInference
    {
        public EmbeddingProfile EmbeddingProfile => new("embedding", 2);
        public string RerankerFingerprint => "synthetic";
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct) => throw new InvalidOperationException("Preparation must not run inference.");
    }
    private sealed class Owner : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => new(1, DateTimeOffset.UnixEpoch, new string('a', 64));
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => GpuInteractiveOwnerObservation.Alive;
    }
    private sealed class Metadata : ICorpusRetrievalReader, ICorpusAnnBindingPreparation
    {
        public bool IsPrepared(Guid epoch, IndexSegmentDescriptor segment) => true;
        public ValueTask<long> PrepareAsync(ICorpusGenerationLease lease, IndexSegmentDescriptor segment, CancellationToken ct) => ValueTask.FromResult(1L);
        public ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(string kind, Guid? root, string? cwd, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(string query, ResolvedCorpusScope scope, int limit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<EligibleContext?> ReadAsync(CorpusEvidenceBinding binding, int context, CancellationToken ct) => throw new NotSupportedException();
    }
}
