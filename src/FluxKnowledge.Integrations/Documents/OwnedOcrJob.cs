using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>A named, non-breakaway job. An empty held job is the descendant-cleanup observation.</summary>
internal sealed class OwnedOcrJob : IDisposable
{
    private const uint KillOnClose = 0x2000;
    private const uint BreakawayFlags = 0x800 | 0x1000;
    private const int AlreadyExists = 183;
    private readonly SafeFileHandle _handle;
    private OwnedOcrJob(SafeFileHandle handle) => _handle = handle;

    internal static string Name(Guid execution) => $"Global\\FluxKnowledge.Ocr.{execution:N}";

    public static OwnedOcrJob Create(Guid execution)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (execution == Guid.Empty) throw new ArgumentException("document-ocr-execution-instance-invalid");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("document-ocr-process-identity-unavailable");
        var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid})");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        SafeFileHandle handle;
        int error;
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pointer };
            handle = CreateJobObject(ref attributes, Name(execution));
            error = Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(pointer); }
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(error); }
        if (error == AlreadyExists) { handle.Dispose(); throw new InvalidOperationException("document-ocr-job-already-exists"); }
        var job = new OwnedOcrJob(handle);
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnClose } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            job.AssertContainment();
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    public static OwnedOcrJob? TryOpen(Guid execution)
    {
        var handle = OpenJobObject(0x0004 | 0x0008, false, Name(execution)); // QUERY | TERMINATE, never create.
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 2) return null;
            throw new Win32Exception(error);
        }
        var job = new OwnedOcrJob(handle);
        try { job.AssertContainment(); return job; }
        catch { job.Dispose(); throw; }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle) ||
            !IsProcessInJob(process.Handle, _handle, out var assigned) || !assigned)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        AssertContainment();
    }

    public uint ActiveProcesses
    {
        get
        {
            if (!QueryAccounting(_handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses;
        }
    }

    public async Task<bool> TerminateAndConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            AssertContainment();
            if (ActiveProcesses == 0) return true;
            if (!TerminateJobObject(_handle, 1)) return false;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                if (ActiveProcesses == 0) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            return ActiveProcesses == 0;
        }
        catch (Exception error) when (error is Win32Exception or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private void AssertContainment()
    {
        if (!QueryLimits(_handle, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((limits.Basic.LimitFlags & KillOnClose) == 0 || (limits.Basic.LimitFlags & BreakawayFlags) != 0)
            throw new InvalidOperationException("document-ocr-job-containment-invalid");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime; public long JobTime; public uint LimitFlags;
        public UIntPtr MinimumWorkingSet; public UIntPtr MaximumWorkingSet;
        public uint ActiveProcessLimit; public IntPtr Affinity; public uint Priority; public uint Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperations; public ulong WriteOperations; public ulong OtherOperations;
        public ulong ReadBytes; public ulong WriteBytes; public ulong OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory; public UIntPtr JobMemory; public UIntPtr PeakProcessMemory; public UIntPtr PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long UserTime; public long KernelTime; public long PeriodUserTime; public long PeriodKernelTime;
        public uint PageFaults; public uint TotalProcesses; public uint ActiveProcesses; public uint TerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(ref SecurityAttributes attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenJobObjectW")]
    private static extern SafeFileHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryAccounting(SafeFileHandle job, int informationClass, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryLimits(SafeFileHandle job, int informationClass, out ExtendedLimits info, uint length, IntPtr returned);
}
