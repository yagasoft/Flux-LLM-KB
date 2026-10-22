using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// A worker waits at a start gate until the parent assigns it to this job.
// Descendant ownership then survives worker exit, including inherited pipes.
public sealed class WorkflowScenarioJob : IDisposable
{
    private readonly SafeFileHandle handle;

    public WorkflowScenarioJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            handle.Dispose();
            throw error;
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
    }

    public bool HasActiveProcesses
    {
        get
        {
            if (!QueryInformationJobObject(handle, 1, out Accounting value,
                    (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) throw new Win32Exception();
            return value.ActiveProcesses != 0;
        }
    }

    public void Terminate()
    {
        if (!TerminateJobObject(handle, 1)) throw new Win32Exception();
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting value, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
