using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Operator-only adapter. Never loaded by the application or its journal helper.
public sealed class FluxSourceJournalOperatorVolume : IDisposable
{
    private readonly SafeFileHandle handle;
    public string Drive { get; private set; }
    public string VolumeIdentity { get; private set; }

    public FluxSourceJournalOperatorVolume(string drive, string expectedVolume, bool write)
    {
        if (drive != "E:" && drive != "I:") throw new ArgumentException("Only the approved E:/I: drives are supported.");
        Drive = drive;
        VolumeIdentity = Resolve(drive);
        if (!string.IsNullOrEmpty(expectedVolume) && !string.Equals(VolumeIdentity, expectedVolume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The approved drive now resolves to another volume.");
        handle = Open(VolumeIdentity.TrimEnd('\\'), write ? 0xc0000000u : 0x80000000u, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot open the exact journal volume."); }
    }

    public FluxSourceJournalOperatorState Query()
    {
        RequireMapping();
        var filesystem = new StringBuilder(32);
        Require(GetVolumeInformation(VolumeIdentity, null, 0, out _, out _, out _, filesystem, (uint)filesystem.Capacity), "Query volume filesystem");
        var bytes = new byte[80];
        Require(Control(handle, 0x900f4, Array.Empty<byte>(), 0, bytes, (uint)bytes.Length, out uint returned, IntPtr.Zero), "Query existing journal");
        if (returned < 56 || returned > bytes.Length) throw new IOException("Malformed journal capability response.");
        Require(GetDiskFreeSpaceEx(VolumeIdentity, out ulong free, out _, out _), "Query free volume capacity");
        return new FluxSourceJournalOperatorState {
            Drive = Drive, VolumeIdentity = VolumeIdentity, FileSystem = filesystem.ToString(),
            JournalId = BitConverter.ToUInt64(bytes, 0).ToString("X16", CultureInfo.InvariantCulture),
            FirstUsn = BitConverter.ToInt64(bytes, 8), NextUsn = BitConverter.ToInt64(bytes, 16), LowestValidUsn = BitConverter.ToInt64(bytes, 24),
            MaximumSize = checked((long)BitConverter.ToUInt64(bytes, 40)), AllocationDelta = checked((long)BitConverter.ToUInt64(bytes, 48)),
            AvailableFreeBytes = checked((long)free), IsDeleting = false
        };
    }

    public void AssertReadable(string journalId, long position)
    {
        var input = new byte[40];
        BitConverter.GetBytes(position).CopyTo(input, 0);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(input, 8);
        BitConverter.GetBytes(ulong.Parse(journalId, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).CopyTo(input, 32);
        // Non-waiting read: Timeout=0, BytesToWaitFor=0; one bounded metadata page.
        var bytes = new byte[65536];
        Require(Control(handle, 0x900bb, input, (uint)input.Length, bytes, (uint)bytes.Length, out uint returned, IntPtr.Zero), "Read required journal cursor");
        if (returned < 8 || returned > bytes.Length || BitConverter.ToInt64(bytes, 0) < position)
            throw new IOException("Unconfirmed journal cursor readability.");
    }

    public void Resize(long maximum, long delta, string expectedJournalId)
    {
        var current = Query();
        if (current.FileSystem != "NTFS" || current.JournalId != expectedJournalId ||
            maximum != Math.Max(current.MaximumSize, 1073741824L) || delta != Math.Max(current.AllocationDelta, 134217728L) ||
            current.AvailableFreeBytes < 2147483648L)
            throw new IOException("The exact existing journal/size/free-space precondition changed; no mutation permitted.");
        var input = new byte[16]; BitConverter.GetBytes(maximum).CopyTo(input, 0); BitConverter.GetBytes(delta).CopyTo(input, 8);
        Require(Control(handle, 0x900e7, input, (uint)input.Length, Array.Empty<byte>(), 0, out _, IntPtr.Zero), "Update existing journal parameters");
    }

    private void RequireMapping()
    {
        if (!string.Equals(Resolve(Drive), VolumeIdentity, StringComparison.OrdinalIgnoreCase)) throw new IOException("Drive mapping changed during the operator action.");
    }
    private static string Resolve(string drive)
    {
        var path = new StringBuilder(1024);
        Require(GetVolumeNameForVolumeMountPoint(drive + "\\", path, (uint)path.Capacity), "Resolve approved volume");
        var result = path.ToString();
        if (!result.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !result.EndsWith(@"}\", StringComparison.Ordinal) ||
            result.Length != 49 || !Guid.TryParseExact(result.Substring(11, 36), "D", out Guid id) || id == Guid.Empty)
            throw new IOException("Noncanonical volume identity.");
        return result;
    }
    private static void Require(bool success, string operation)
    { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed; no recreate or automatic retry is permitted."); }
    public void Dispose() { handle.Dispose(); }

    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", EntryPoint="GetVolumeInformationW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformation(string root, StringBuilder name, uint nameLength, out uint serial, out uint component, out uint flags, StringBuilder filesystem, uint filesystemLength);
    [DllImport("kernel32.dll", EntryPoint="GetDiskFreeSpaceExW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", EntryPoint="DeviceIoControl", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Control(SafeFileHandle handle, uint code, [In] byte[] input, uint inputLength, [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}

public sealed class FluxSourceJournalOperatorState
{
    public string Drive { get; set; }
    public string VolumeIdentity { get; set; }
    public string FileSystem { get; set; }
    public string JournalId { get; set; }
    public long FirstUsn { get; set; }
    public long NextUsn { get; set; }
    public long LowestValidUsn { get; set; }
    public long MaximumSize { get; set; }
    public long AllocationDelta { get; set; }
    public long AvailableFreeBytes { get; set; }
    public bool IsDeleting { get; set; }
}
