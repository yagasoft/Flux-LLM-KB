using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>One verifier-only RH request, with a completion owner independent of verifier progress.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSourceVerificationOplock : IAsyncDisposable
{
    private const int Active = 0, Retiring = 1, Terminal = 2;
    private readonly object _sync = new();
    private readonly SafeFileHandle _handle;
    private readonly EventWaitHandle _event = new(false, EventResetMode.ManualReset);
    private readonly TaskCompletionSource<bool> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _grant = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _registration;
    private IntPtr _input, _output, _overlapped;
    private bool _pending;
    private int _phase, _invalid, _attested;
    internal sealed record NativeCompletion(bool Succeeded, int Error, long Status, int OriginalLevel, int NewLevel, int Flags);
    private NativeCompletion? _nativeCompletion;
    internal NativeCompletion? CompletionEvidence => Volatile.Read(ref _nativeCompletion);
    // Controlled native race probes; production leaves these unset. None runs under _sync.
    internal Action? BeforeNativeCheck { get; set; }
    internal Action? NativeCompletionObserved { get; set; }
    internal Action? BeforeTerminalPublication { get; set; }
    internal Action? BeforeNativeIssue { get; set; }
    internal Action? AfterNativeIssue { get; set; }
    internal Action? BeforeOwnerStart { get; set; }

    private WindowsSourceVerificationOplock(SafeFileHandle handle)
    {
        _handle = handle;
        try
        {
            _input = Allocate(12);
            _output = Allocate(24);
            _overlapped = Allocate(Marshal.SizeOf<Overlapped>());
            Marshal.WriteInt16(_input, 0, 1);
            Marshal.WriteInt16(_input, 2, 12);
            Marshal.WriteInt32(_input, 4, 3); // CACHE_READ | CACHE_HANDLE.
            Marshal.WriteInt32(_input, 8, 1); // REQUEST, never ACK/rearm after a break.
            Marshal.StructureToPtr(new Overlapped { Event = _event.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
        }
        catch { ReleaseNative(); throw; }
    }

    internal static WindowsSourceVerificationOplock Acquire(string root, string path,
        string rootIdentity, string fileIdentity, CancellationToken cancellationToken = default,
        Action<WindowsSourceVerificationOplock>? configureProbe = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsSourceVerificationOplock? owner = null;
        try
        {
            // This guard lasts only through admission/grant, never through pacing or the completion wait.
            using var guard = WindowsSourcePathAccess.OpenRead(root, path, rootIdentity, fileIdentity);
            var handle = WindowsSourcePathAccess.OpenRelative(guard.Parent.Handle, Path.GetFileName(path),
                0x100081, 7, 0x40, path); // Async, no-follow/no-recall, compatible sharing.
            try { owner = new(handle); }
            catch { handle.Dispose(); throw; }
            WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(handle));
            if (PhysicalFileIdentity.Get(handle) != fileIdentity ||
                !string.Equals(PhysicalFileIdentity.GetFinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new SourceSnapshotChangedException("Verification source identity changed before grant.");
        }
        catch (Exception error) { owner?.FinishWithoutPending(error); throw; }

        var started = false;
        try
        {
            configureProbe?.Invoke(owner);
            // Register before starting the owner so terminal cleanup cannot race registration assignment.
            owner._registration = cancellationToken.UnsafeRegister(static value =>
            {
                var lease = (WindowsSourceVerificationOplock)value!;
                Interlocked.Exchange(ref lease._invalid, 1);
                lease.RequestRetirement();
            }, owner);
            owner.BeforeOwnerStart?.Invoke();
            new Thread(owner.IssueAndComplete) { IsBackground = true, Name = "Source verification oplock" }.Start();
            started = true;
            // No source/ancestor guard is held across the owner-thread grant handshake.
            var failure = owner._grant.Task.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            using (var current = WindowsSourcePathAccess.OpenRead(root, path, rootIdentity, fileIdentity))
            {
                if (!owner.IsUnbroken()) throw new SourceSnapshotChangedException("SourceVerificationGrantInvalidated");
            }
            return owner;
        }
        catch (Exception error)
        {
            if (started)
            {
                owner.RequestRetirement();
                _ = owner._terminal.Task.GetAwaiter().GetResult(); // The owner alone drains an issued request.
            }
            else owner.FinishWithoutPending(error);
            throw;
        }
    }

    internal Task Completion => _terminal.Task;

    internal bool IsUnbroken()
    {
        BeforeNativeCheck?.Invoke();
        lock (_sync) return IsPendingUnderLock();
    }

    /// <summary>Caller must hold its short final identity/path/stamp guard.</summary>
    internal bool TryAttest()
    {
        BeforeNativeCheck?.Invoke();
        lock (_sync)
        {
            if (!IsPendingUnderLock()) return false;
            Volatile.Write(ref _attested, 1);
            return Volatile.Read(ref _invalid) == 0;
        }
    }

    private bool IsPendingUnderLock()
    {
        if (_phase != Active || Volatile.Read(ref _invalid) != 0) return false;
        var completed = GetOverlappedResult(_handle, _overlapped, out _, false);
        var error = Marshal.GetLastPInvokeError();
        if (completed || error != 996)
        {
            ObserveNativeCompletion(completed, error);
            Interlocked.Exchange(ref _invalid, 1);
            return false;
        }
        return Volatile.Read(ref _invalid) == 0;
    }

    internal async ValueTask<bool> RetireAsync()
    {
        RequestRetirement();
        return await _terminal.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => _ = await RetireAsync().ConfigureAwait(false);

    private void RequestRetirement()
    {
        lock (_sync)
        {
            if (_phase != Active) return;
            Volatile.Write(ref _phase, Retiring);
            if (!_pending) return; // The owner observes abort intent when publishing the real request.
            // Success and NOT_FOUND only describe the cancellation request, never terminal completion.
            if (!CancelIoEx(_handle, _overlapped) && Marshal.GetLastPInvokeError() != 1168)
                Interlocked.Exchange(ref _invalid, 1);
        }
    }

    private void IssueAndComplete()
    {
        var pending = false;
        Exception? failure = null;
        try
        {
            BeforeNativeIssue?.Invoke();
            if (Volatile.Read(ref _phase) != Active)
                failure = new OperationCanceledException("Source verification cancelled before grant.");
            else
            {
                var completed = DeviceIoControl(_handle, 0x00090240, _input, 12, _output, 24, IntPtr.Zero, _overlapped);
                var error = Marshal.GetLastPInvokeError();
                pending = !completed && error == 997;
                if (!pending) failure = new IOException("SourceVerificationOplockUnavailable", new Win32Exception(error));
                else AfterNativeIssue?.Invoke();
            }
        }
        catch (Exception error) { failure = error; }
        if (!pending) { FinishWithoutPending(failure!); return; }
        lock (_sync)
        {
            _pending = true;
            if (failure is not null) { Interlocked.Exchange(ref _invalid, 1); _phase = Retiring; }
            if (_phase == Retiring && !CancelIoEx(_handle, _overlapped) && Marshal.GetLastPInvokeError() != 1168)
                Interlocked.Exchange(ref _invalid, 1);
        }
        _grant.TrySetResult(failure);
        Complete(); // Issuance and the terminal wait have the same, uninterrupted native thread lifetime.
    }

    private void Complete()
    {
        bool completed;
        int error;
        do
        {
            // Sole blocking native wait, outside the ownership lock and all source/SQL guards.
            completed = GetOverlappedResult(_handle, _overlapped, out _, true);
            error = Marshal.GetLastPInvokeError();
        } while (!completed && error == 996);

        ObserveNativeCompletion(completed, error);
        try { NativeCompletionObserved?.Invoke(); }
        catch { Interlocked.Exchange(ref _invalid, 1); } // A failed probe must still retire the native request.
        // Publish invalidation before contending for disposal ownership or running any continuation.
        if (completed || error != 995 || Volatile.Read(ref _phase) != Retiring || Volatile.Read(ref _attested) == 0)
            Interlocked.Exchange(ref _invalid, 1);
        bool clean;
        lock (_sync)
        {
            clean = Volatile.Read(ref _invalid) == 0;
            _phase = Terminal;
            ReleaseNative(); // Closing relinquishes acknowledgement-requiring breaks promptly.
        }
        try { BeforeTerminalPublication?.Invoke(); }
        catch { Interlocked.Exchange(ref _invalid, 1); }
        _registration.Dispose(); // An in-flight cancellation callback may need _sync.
        _terminal.TrySetResult(clean && Volatile.Read(ref _invalid) == 0);
    }

    private void FinishWithoutPending(Exception error)
    {
        Interlocked.Exchange(ref _invalid, 1);
        lock (_sync) { _phase = Terminal; ReleaseNative(); }
        _registration.Dispose();
        _terminal.TrySetResult(false);
        _grant.TrySetResult(error);
    }

    private void ReleaseNative()
    {
        _handle.Dispose();
        if (_overlapped != IntPtr.Zero) Marshal.FreeHGlobal(_overlapped);
        if (_output != IntPtr.Zero) Marshal.FreeHGlobal(_output);
        if (_input != IntPtr.Zero) Marshal.FreeHGlobal(_input);
        _overlapped = _output = _input = IntPtr.Zero;
        _event.Dispose();
    }

    private static IntPtr Allocate(int bytes)
    {
        var memory = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(new byte[bytes], 0, memory, bytes);
        return memory;
    }

    private void ObserveNativeCompletion(bool succeeded, int error) =>
        Volatile.Write(ref _nativeCompletion, new(succeeded, error, Marshal.ReadIntPtr(_overlapped).ToInt64(),
            Marshal.ReadInt32(_output, 4), Marshal.ReadInt32(_output, 8), Marshal.ReadInt32(_output, 12)));

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength,
        IntPtr output, uint outputLength, IntPtr bytesReturned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint bytes,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
