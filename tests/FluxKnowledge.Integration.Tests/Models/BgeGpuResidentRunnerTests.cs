using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeGpuResidentRunnerTests
{
    private static readonly BgeInputBatch Batch = BgeInputBatch.Create([[0, 11, 2]]);
    private static readonly GpuResidentModelKey EmbeddingKey =
        BgeOfflineModels.GetGpuResidentModelKey(BgeGpuModelPhase.Embedding, BgeRerankerPrecision.Fp32);

    [Fact]
    public void Returning_a_borrow_revokes_old_access_but_keeps_the_same_allocation_until_disposal()
    {
        var first = Owner(EmbeddingKey);
        var second = Owner(EmbeddingKey);
        var allocation = first.BeginNativeAllocation();
        var session = new ProbeRunner(allocation);
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, first.Handle.CapacitySlotKey, session, allocation);

        var old = resident.Borrow(first, EmbeddingKey);
        Assert.Equal(7f, Assert.Single(old.Run(Batch, default).Values));
        Assert.False(first.NativeCapacityReleased);
        Assert.Throws<BgeInferenceException>(() => resident.Borrow(second, EmbeddingKey));
        Assert.True(second.NativeCapacityReleased);
        old.Dispose();

        Assert.False(first.NativeCapacityReleased);
        Assert.Throws<ObjectDisposedException>(() => old.Run(Batch, default));
        Assert.Throws<InvalidOperationException>(() => resident.Borrow(first, EmbeddingKey));
        Assert.False(first.InvalidateAndReadNativeRelease());
        using var next = resident.Borrow(second, EmbeddingKey);
        old.Dispose();
        Assert.Equal(7f, Assert.Single(next.Run(Batch, default).Values));
        Assert.False(second.InvalidateAndReadNativeRelease());
        Assert.Throws<ObjectDisposedException>(() => next.Run(Batch, default));
        Assert.Equal(2, session.Runs);
        Assert.Equal(0, session.Disposals);

        resident.Dispose();
        Assert.True(resident.NativeCapacityReleased);
        Assert.True(first.NativeCapacityReleased);
        Assert.True(second.NativeCapacityReleased);
        Assert.Equal(1, session.Disposals);
        Assert.Throws<ObjectDisposedException>(() => next.Run(Batch, default));
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("precision")]
    [InlineData("artifact")]
    [InlineData("settings")]
    [InlineData("device")]
    public void Incompatible_borrow_is_rejected_before_the_context_tracks_the_session(string mismatch)
    {
        var first = Owner(EmbeddingKey);
        var allocation = first.BeginNativeAllocation();
        var session = new ProbeRunner(allocation);
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, first.Handle.CapacitySlotKey, session, allocation);
        var requested = mismatch switch
        {
            "phase" => BgeOfflineModels.GetGpuResidentModelKey(BgeGpuModelPhase.Reranker, BgeRerankerPrecision.Fp32),
            "precision" => BgeOfflineModels.GetGpuResidentModelKey(BgeGpuModelPhase.Embedding, BgeRerankerPrecision.Fp16Internal),
            "artifact" => EmbeddingKey with { ModelKey = "different-model" },
            _ => EmbeddingKey
        };
        var requestOwner = Owner(mismatch == "settings" ? requested with { SettingsFingerprint = "different-settings" } : requested,
            slot: mismatch == "device" ? "gpu-1" : "gpu-0");

        Assert.ThrowsAny<InvalidOperationException>(() => resident.Borrow(requestOwner, requested));
        Assert.True(requestOwner.NativeCapacityReleased);
        Assert.Equal(0, session.Runs);
        using var borrow = resident.Borrow(first, EmbeddingKey);
        Assert.Equal(7f, Assert.Single(borrow.Run(Batch, default).Values));
    }

    [Fact]
    public void Matching_native_disposal_is_required_even_after_borrow_is_revoked()
    {
        var opener = Owner(EmbeddingKey);
        var borrower = Owner(EmbeddingKey);
        var allocation = opener.BeginNativeAllocation();
        var session = new ProbeRunner(allocation, disposeFails: true);
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, opener.Handle.CapacitySlotKey, session, allocation);

        using var borrow = resident.Borrow(borrower, EmbeddingKey);
        Assert.Throws<IOException>(resident.Dispose);

        Assert.False(resident.NativeCapacityReleased);
        Assert.False(opener.NativeCapacityReleased);
        Assert.False(borrower.NativeCapacityReleased);
        Assert.Throws<ObjectDisposedException>(() => borrow.Run(Batch, default));
        resident.Dispose();
        Assert.Equal(1, session.Disposals);
        Assert.False(borrower.NativeCapacityReleased);
    }

    [Fact]
    public void Cold_borrow_disposes_the_native_session_when_returned()
    {
        var owner = Owner(EmbeddingKey);
        var allocation = owner.BeginNativeAllocation();
        var session = new ProbeRunner(allocation);
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, owner.Handle.CapacitySlotKey, session, allocation);
        using (var borrow = resident.BorrowCold(owner))
        {
            Assert.Equal(7f, Assert.Single(borrow.Run(Batch, default).Values));
            Assert.False(owner.NativeCapacityReleased);
        }

        Assert.True(owner.NativeCapacityReleased);
        Assert.True(resident.NativeCapacityReleased);
        Assert.Equal(1, session.Disposals);
        Assert.Throws<ObjectDisposedException>(() => resident.Borrow(Owner(EmbeddingKey), EmbeddingKey));
    }

    [Fact]
    public void Revoked_context_cannot_start_a_run_or_create_a_borrow()
    {
        using var cancellation = new CancellationTokenSource();
        var owner = Owner(EmbeddingKey, cancellation.Token);
        var allocation = owner.BeginNativeAllocation();
        var session = new ProbeRunner(allocation);
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, owner.Handle.CapacitySlotKey, session, allocation);
        using var borrow = resident.Borrow(owner, EmbeddingKey);
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => borrow.Run(Batch, default));
        borrow.Dispose();
        Assert.ThrowsAny<OperationCanceledException>(() => resident.Borrow(owner, EmbeddingKey));
        Assert.Equal(0, session.Runs);
    }

    [Fact]
    public async Task Ownership_invalidated_during_native_inference_cannot_return_results()
    {
        var owner = Owner(EmbeddingKey);
        var allocation = owner.BeginNativeAllocation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var session = new ProbeRunner(allocation, run: () =>
        {
            entered.SetResult();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(10)));
        });
        using var resident = new BgeGpuResidentRunner(EmbeddingKey, owner.Handle.CapacitySlotKey, session, allocation);
        using var borrow = resident.Borrow(owner, EmbeddingKey);
        var inference = Task.Run(() => borrow.Run(Batch, default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(owner.InvalidateAndReadNativeRelease());
        }
        finally { finish.Set(); }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => inference);
        borrow.Dispose();
        Assert.False(owner.NativeCapacityReleased);
        resident.Dispose();
        Assert.True(owner.NativeCapacityReleased);
    }

    [Fact]
    public void Resident_identity_uses_the_actual_rank_artifact_for_each_precision()
    {
        var full = BgeOfflineModels.GetGpuResidentModelKey(BgeGpuModelPhase.Reranker, BgeRerankerPrecision.Fp32);
        var reduced = BgeOfflineModels.GetGpuResidentModelKey(BgeGpuModelPhase.Reranker, BgeRerankerPrecision.Fp16Internal);

        Assert.Equal("reranker:" + BgeOfflineModels.RerankerFingerprint, full.ModelKey);
        Assert.Equal("reranker:" + BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal), reduced.ModelKey);
        Assert.NotEqual(full.ModelKey, reduced.ModelKey);
        Assert.NotEqual(full.RuntimeKey, reduced.RuntimeKey);
        Assert.NotEqual(full.ModelKey, EmbeddingKey.ModelKey);
        Assert.Equal(full.SettingsFingerprint, EmbeddingKey.SettingsFingerprint);
    }

    private static GpuOwnedWorkContext Owner(GpuResidentModelKey key, CancellationToken token = default, string slot = "gpu-0") =>
        new(new(Guid.NewGuid(), slot, "executor", 1, Guid.NewGuid()), key.RuntimeKey, key.SettingsFingerprint, token);

    private sealed class ProbeRunner(GpuOwnedWorkContext.NativeAllocation allocation, bool disposeFails = false, Action? run = null) : IBgeTensorRunner
    {
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }

        public BgeTensorOutput Run(BgeInputBatch batch, CancellationToken cancellationToken)
        {
            Runs++;
            run?.Invoke();
            return new([7f], [1, 1]);
        }

        public void Dispose() => BgeOnnxTensorRunner.DisposeSession(
            new DisposeAction(() =>
            {
                Disposals++;
                if (disposeFails) throw new IOException("synthetic-native-disposal-failure");
            }),
            new VerifiedLocalModelLease(new Dictionary<string, IModelVerificationFile>(), new DisposeAction(() => { })),
            cpu: false, allocation);
    }

    private sealed class DisposeAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
