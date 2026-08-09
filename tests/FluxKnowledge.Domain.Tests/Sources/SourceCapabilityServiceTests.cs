using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceCapabilityServiceTests
{
    [Fact]
    public async Task Native_executor_descriptor_is_persisted_non_runnable_and_never_exposes_a_local_handler()
    {
        var store = new RecordingCapabilityStore();
        var service = new SourceCapabilityService(store, new LocalSourceCapabilityHandlerRegistry([]));
        var descriptor = new SourceCapabilityDescriptor(
            Guid.NewGuid(),
            "document-parser",
            "phase-3a-v1",
            ExecutionClass.NativeExecutorLater,
            "native-v1");

        var registration = await service.RegisterAsync(descriptor, CancellationToken.None);

        Assert.False(registration.IsRunnable);
        Assert.False(service.TryResolveLocalHandler(registration.Id, out _));
        Assert.False(Assert.Single(store.Registered).IsRunnable);
    }

    [Fact]
    public async Task Replay_rejects_a_request_whose_processor_fingerprint_does_not_match_the_capability()
    {
        var capability = new RegisteredSourceCapability(Guid.NewGuid(), "document-parser", "v1",
            ExecutionClass.InProcess, "trusted-fingerprint", true);
        var store = new RecordingCapabilityStore(capability);
        var replay = new RecordingReplayStore();
        var service = new DeferredActivityReplayService(store, replay, new LocalSourceCapabilityHandlerRegistry([]));

        var result = await service.ReprocessAsync(
            [new DeferredContentReplayRequest(Guid.NewGuid(), "key", "document-parser", capability.Id, "v1", "different")],
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Empty(replay.Requests);
    }

    [Fact]
    public async Task Persisted_runnable_capability_without_a_concrete_local_handler_cannot_replay()
    {
        var capability = new RegisteredSourceCapability(Guid.NewGuid(), "text-metadata", "phase-3a-v1",
            ExecutionClass.InProcess, "phase-3a-inprocess-text-metadata-v1", true);
        var store = new RecordingCapabilityStore(capability);
        var replay = new RecordingReplayStore();
        var service = new DeferredActivityReplayService(store, replay, new LocalSourceCapabilityHandlerRegistry([]));

        var result = await service.ReprocessAsync(
            [new DeferredContentReplayRequest(Guid.NewGuid(), "key", "text-metadata", capability.Id,
                "phase-3a-v1", "phase-3a-inprocess-text-metadata-v1")], CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Empty(replay.Requests);
    }

    private sealed class RecordingCapabilityStore(RegisteredSourceCapability? existing = null) : ISourceCapabilityStore
    {
        public List<RegisteredSourceCapability> Registered { get; } = [];

        public ValueTask<RegisteredSourceCapability> RegisterAsync(
            RegisteredSourceCapability capability,
            CancellationToken cancellationToken)
        {
            Registered.Add(capability);
            return ValueTask.FromResult(capability);
        }

        public ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RegisteredSourceCapability?>(Registered.SingleOrDefault(value => value.Id == capabilityId) ?? existing);
    }

    private sealed class RecordingReplayStore : IDeferredActivityReplayStore
    {
        public List<DeferredContentReplayRequest> Requests { get; } = [];

        public ValueTask<int> ReplayAsync(RegisteredSourceCapability capability, Guid? rootId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask<int> ReplayActivityAsync(DeferredContentReplayRequest request, RegisteredSourceCapability capability, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(1);
        }
    }
}
