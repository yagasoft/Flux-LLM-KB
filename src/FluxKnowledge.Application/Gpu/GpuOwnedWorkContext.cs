namespace FluxKnowledge.Application.Gpu;

/// <summary>Issued only after the exact scheduler ownership read; invalidated before lifecycle settlement.</summary>
public sealed class GpuOwnedWorkContext
{
    private readonly object _sync = new();
    private bool _active = true;
    // The proof belongs to the physical allocation. A resident session can
    // charge successive requests without holding references to those requests.
    private Dictionary<NativeAllocation, bool>? _nativeAllocations;
    private int _residentModelUsed;
    internal GpuOwnedWorkContext(GpuExecutorBatchHandle handle, string runtimeKey, string settingsFingerprint, CancellationToken cancellationToken)
    {
        Handle = handle; RuntimeKey = runtimeKey; SettingsFingerprint = settingsFingerprint; CancellationToken = cancellationToken;
    }
    public GpuExecutorBatchHandle Handle { get; }
    public string RuntimeKey { get; }
    public string SettingsFingerprint { get; }
    public CancellationToken CancellationToken { get; }
    public bool NativeCapacityReleased { get { lock (_sync) return ReadNativeRelease(); } }
    internal bool ResidentModelUsed => Volatile.Read(ref _residentModelUsed) != 0;
    internal void MarkResidentModelUsed() => Volatile.Write(ref _residentModelUsed, 1);

    internal NativeAllocation BeginNativeAllocation()
    {
        lock (_sync)
        {
            RequireActive(RuntimeKey, SettingsFingerprint);
            var allocation = new NativeAllocation();
            (_nativeAllocations ??= new()).Add(allocation, false);
            return allocation;
        }
    }

    internal void BindNativeAllocationForUse(NativeAllocation allocation, string runtimeKey, string settingsFingerprint)
    {
        lock (_sync)
        {
            RequireActive(runtimeKey, settingsFingerprint);
            var allocations = _nativeAllocations ??= new();
            if (allocation.IsReleased || (allocations.TryGetValue(allocation, out var alreadyBorrowed) && alreadyBorrowed))
                throw new InvalidOperationException("gpu-native-allocation-borrow-invalid");
            allocations[allocation] = true;
        }
    }

    internal sealed class NativeAllocation
    {
        private int _confirmed;
        internal bool IsReleased => Volatile.Read(ref _confirmed) != 0;
        // Call only after the native resource's disposal has returned successfully.
        // Constructor/disposal failure deliberately leaves allocation ownership live.
        internal void ConfirmReleased() => Volatile.Write(ref _confirmed, 1);
    }

    private bool ReadNativeRelease() => _nativeAllocations is null || _nativeAllocations.Keys.All(allocation => allocation.IsReleased);

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
            return ReadNativeRelease();
        }
    }
}
