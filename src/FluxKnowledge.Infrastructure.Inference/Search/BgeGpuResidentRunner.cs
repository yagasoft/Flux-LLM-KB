using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public enum BgeGpuModelPhase { Embedding, Reranker }

/// <summary>One verified native session, with at most one revocable request borrow.</summary>
internal sealed class BgeGpuResidentRunner(GpuResidentModelKey key, string capacitySlotKey,
    IBgeTensorRunner runner, GpuOwnedWorkContext.NativeAllocation allocation) : IGpuProcessResidentModel
{
    private readonly object _sync = new();
    private BorrowedRunner? _borrower;
    private bool _disposed;

    internal GpuResidentModelKey Key { get; } = key;
    internal bool NativeCapacityReleased => allocation.IsReleased;
    public bool PhysicalReleaseConfirmed => NativeCapacityReleased;
    public bool IsIdle { get { lock (_sync) return !_disposed && _borrower is null; } }

    internal IBgeTensorRunner Borrow(GpuOwnedWorkContext owner, GpuResidentModelKey requested) =>
        Borrow(owner, requested, disposeSessionOnReturn: false);

    internal IBgeTensorRunner BorrowCold(GpuOwnedWorkContext owner)
    {
        try { return Borrow(owner, Key, disposeSessionOnReturn: true); }
        catch { Dispose(); throw; }
    }

    private BorrowedRunner Borrow(GpuOwnedWorkContext owner, GpuResidentModelKey requested, bool disposeSessionOnReturn)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (requested != Key || owner.Handle.CapacitySlotKey != capacitySlotKey)
                throw new BgeInferenceException("bge-resident-model-profile-mismatch");
            if (_borrower is not null) throw new BgeInferenceException("bge-resident-model-busy");
            owner.BindNativeAllocationForUse(allocation, Key.RuntimeKey, Key.SettingsFingerprint);
            return _borrower = new(this, owner, disposeSessionOnReturn);
        }
    }

    private BgeTensorOutput Run(BorrowedRunner borrower, BgeInputBatch batch, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(!ReferenceEquals(_borrower, borrower), borrower);
            var owner = borrower.Owner;
            owner.RequireActive(Key.RuntimeKey, Key.SettingsFingerprint);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var output = runner.Run(batch, linked.Token);
            owner.RequireActive(Key.RuntimeKey, Key.SettingsFingerprint);
            linked.Token.ThrowIfCancellationRequested();
            return output;
        }
    }

    private void Return(BorrowedRunner borrower)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_borrower, borrower)) return;
            _borrower = null;
            if (borrower.DisposeSessionOnReturn) Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _borrower = null;
            runner.Dispose();
        }
    }

    private sealed class BorrowedRunner(BgeGpuResidentRunner session, GpuOwnedWorkContext owner,
        bool disposeSessionOnReturn) : IBgeTensorRunner
    {
        internal GpuOwnedWorkContext Owner { get; } = owner;
        internal bool DisposeSessionOnReturn { get; } = disposeSessionOnReturn;

        public BgeTensorOutput Run(BgeInputBatch batch, CancellationToken cancellationToken) =>
            session.Run(this, batch, cancellationToken);

        public void Dispose() => session.Return(this);
    }
}
