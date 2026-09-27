namespace FluxKnowledge.Application.Gpu;

/// <summary>Issued only after the exact scheduler ownership read; invalidated before lifecycle settlement.</summary>
public sealed class GpuOwnedWorkContext
{
    private readonly object _sync = new();
    private bool _active = true;
    private int _nativeAllocations;
    internal GpuOwnedWorkContext(GpuExecutorBatchHandle handle, string runtimeKey, string settingsFingerprint, CancellationToken cancellationToken)
    {
        Handle = handle; RuntimeKey = runtimeKey; SettingsFingerprint = settingsFingerprint; CancellationToken = cancellationToken;
    }
    public GpuExecutorBatchHandle Handle { get; }
    public string RuntimeKey { get; }
    public string SettingsFingerprint { get; }
    public CancellationToken CancellationToken { get; }
    public bool NativeCapacityReleased { get { lock (_sync) return _nativeAllocations == 0; } }

    internal NativeAllocation BeginNativeAllocation()
    {
        lock (_sync)
        {
            RequireActive(RuntimeKey, SettingsFingerprint);
            _nativeAllocations++;
            return new(this);
        }
    }

    internal sealed class NativeAllocation(GpuOwnedWorkContext owner)
    {
        private int _confirmed;
        // Call only after the native resource's disposal has returned successfully.
        // Constructor/disposal failure deliberately leaves allocation ownership live.
        internal void ConfirmReleased()
        {
            if (Interlocked.Exchange(ref _confirmed, 1) == 0)
                lock (owner._sync) owner._nativeAllocations--;
        }
    }

    public void RequireActive(string expectedRuntimeKey, string expectedSettingsFingerprint)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(!_active, this);
            CancellationToken.ThrowIfCancellationRequested();
            if (RuntimeKey != expectedRuntimeKey || SettingsFingerprint != expectedSettingsFingerprint)
                throw new InvalidOperationException("gpu-owned-model-profile-mismatch");
        }
    }
    internal void Invalidate() => InvalidateAndReadNativeRelease();
    internal bool InvalidateAndReadNativeRelease()
    {
        lock (_sync)
        {
            _active = false;
            return _nativeAllocations == 0;
        }
    }
}
