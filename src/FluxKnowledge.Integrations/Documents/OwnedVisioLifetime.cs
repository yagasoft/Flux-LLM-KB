using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>Owns precisely one newly-created VISIO.EXE and its descendants through a kill-on-close job.</summary>
internal sealed class OwnedVisioLifetime : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
    private readonly int _processId;
    private readonly DateTime _startedAtUtc;
    private Process? _process;
    private SafeFileHandle? _job;
    private readonly object _sync = new();
    private bool _exitConfirmed;

    private OwnedVisioLifetime(Process process, SafeFileHandle job)
    {
        _processId = process.Id;
        _startedAtUtc = process.StartTime.ToUniversalTime();
        _process = process; // Keep this exact process handle through job assignment and exit confirmation.
        _job = job;
    }

    public int ProcessId => _processId;

    public static OwnedVisioLifetime Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited || !string.Equals(process.ProcessName, "VISIO", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Visio process was not available for ownership.");
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Visio job object.");
        }
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the Visio job object.");
            }
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the Visio process to its job object.");
            }
            return new OwnedVisioLifetime(process, job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public bool IsTheOwnedProcessAlive() => !TryConfirmExit();

    private bool TryConfirmExit()
    {
        lock (_sync)
        {
            if (_exitConfirmed) return true;
            try
            {
                // Unknown/disposed is never evidence of exit. Only this held handle can confirm it.
                if (_process is not null && _process.HasExited) _exitConfirmed = true;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            if (_exitConfirmed) return true;
            return false;
        }
    }

    public long GetPrivateBytes()
    {
        var process = _process;
        if (process is null || !IsTheOwnedProcessAlive() || process.StartTime.ToUniversalTime() != _startedAtUtc)
        {
            throw new InvalidOperationException("The owned Visio process is no longer available.");
        }
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    public bool CloseAndConfirmExit(TimeSpan timeout)
    {
        var job = Interlocked.Exchange(ref _job, null);
        job?.Dispose(); // KILL_ON_JOB_CLOSE applies only to our assigned process tree.
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!IsTheOwnedProcessAlive())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return !IsTheOwnedProcessAlive();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _job, null)?.Dispose();
        lock (_sync) { Interlocked.Exchange(ref _process, null)?.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        uint jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
