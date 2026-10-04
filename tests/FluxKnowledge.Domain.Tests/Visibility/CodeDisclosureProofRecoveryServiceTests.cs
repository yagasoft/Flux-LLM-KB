using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Visibility;

public sealed class CodeDisclosureProofRecoveryServiceTests
{
    [Theory]
    [InlineData("class C {}", CodeDisclosureProofState.Ready)]
    [InlineData("class C {", CodeDisclosureProofState.Invalid)]
    [InlineData(null, CodeDisclosureProofState.Unsupported)]
    public async Task Held_worker_builds_one_terminal_or_ready_projection_and_stops_during_idle_delay(
        string? text, CodeDisclosureProofState state)
    {
        var artifact = new CodeDisclosureArtifact(Guid.NewGuid(), Guid.NewGuid(), 1,
            CodeDisclosureIntegrity.Hash(text ?? "oversized retained source"), text?.Length ?? 4_000_001, text);
        var store = new Store(artifact);
        var hold = new Hold();
        using var worker = new CodeDisclosureProofRecoveryService(store,
            new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure()), hold,
            NullLogger<CodeDisclosureProofRecoveryService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, store.ReadCalls);
        hold.Release.TrySetResult();
        var proof = await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(state, proof.State);
        Assert.Equal(artifact.ArtifactId, proof.ArtifactId);
        Assert.Equal(artifact.CanonicalHash, proof.CanonicalHash);
        Assert.Equal(CodeDisclosureIntegrity.ProofChecksum(artifact.ArtifactId, artifact.CanonicalHash,
            proof.Fingerprint, state, artifact.CanonicalLength, proof.Spans), proof.Checksum);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, store.CommitCalls);
    }

    [Fact]
    public async Task Transient_failure_backs_off_retries_the_same_artifact_and_shutdown_cancels_the_wait()
    {
        const string text = "class C {}";
        var store = new Store(new(Guid.NewGuid(), Guid.NewGuid(), 1,
            CodeDisclosureIntegrity.Hash(text), text.Length, text), failFirst: true);
        var hold = new Hold();
        hold.Release.TrySetResult();
        using var worker = new CodeDisclosureProofRecoveryService(store,
            new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure()), hold,
            NullLogger<CodeDisclosureProofRecoveryService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, store.ReadCalls);
        Assert.False(store.Committed.Task.IsCompleted);
        await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(store.ReadCalls >= 2);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, store.CommitCalls);
    }

    private sealed class Store(CodeDisclosureArtifact artifact, bool failFirst = false) : ICodeDisclosureProofStore
    {
        public int ReadCalls;
        public int CommitCalls;
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CodeDisclosureProof> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<CodeDisclosureArtifact?> ReadNextAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref ReadCalls) == 1 && failFirst)
            {
                Failed.TrySetResult();
                throw new InvalidOperationException("transient synthetic failure");
            }
            return ValueTask.FromResult<CodeDisclosureArtifact?>(Committed.Task.IsCompleted ? null : artifact);
        }
        public ValueTask<bool> CommitAsync(CodeDisclosureArtifact value, CodeDisclosureProof proof, CancellationToken cancellationToken)
        {
            Assert.Equal(artifact, value);
            Interlocked.Increment(ref CommitCalls);
            Committed.TrySetResult(proof);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class Hold : IDeploymentValidationHold
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHeld => !Release.Task.IsCompleted;
        public async ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
