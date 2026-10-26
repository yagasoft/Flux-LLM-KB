using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Only query/read IOCTLs are exposed; acquisition and journal mutation are not runtime capabilities.</summary>
public sealed class WindowsSourceChangeJournalReader : ISourceChangeJournalReader
{
    // Win32 BACKUP_SEMANTICS | OPEN_REPARSE_POINT | OPEN_NO_RECALL.
    // NtCreateFile's FILE_OPEN_NO_RECALL option uses a different value.
    internal const uint ParentMetadataOpenFlags = 0x02000000 | 0x00200000 | 0x00100000;
    public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = OpenVolume(volumeIdentity);
        var bytes = Control(handle, 0x000900f4, [], 128);
        if (bytes.Length < 56) throw new InvalidDataException("SourceJournalQueryInvalid");
        return ValueTask.FromResult(new JournalCapability(CanonicalVolume(volumeIdentity),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48))));
    }

    public async ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 4096 or > UsnJournalParser.MaximumPageBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var current = await QueryAsync(cursor.VolumeIdentity, cancellationToken).ConfigureAwait(false);
        if (current.JournalId != cursor.JournalId || cursor.NextUsn < Math.Max(current.FirstUsn, current.LowestValidUsn) || cursor.NextUsn > current.NextUsn)
            throw new SourceJournalGapException("SourceJournalContinuityLost");
        using var handle = OpenVolume(cursor.VolumeIdentity);
        var input = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(input, cursor.NextUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        // ReturnOnlyOnClose=0, Timeout=0, BytesToWaitFor=0: bounded catch-up includes open-writer hints.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), cursor.JournalId);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 3);
        var bytes = Control(handle, 0x000900bb, input, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = UsnJournalParser.Parse(bytes, cursor.NextUsn);
        var parents = new Dictionary<string, string?>();
        var changes = new List<JournalChange>(parsed.Changes.Count);
        foreach (var change in parsed.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parents.TryGetValue(change.ParentId, out var parent))
            {
                parent = ParentPath(handle, change.ParentId);
                parents.Add(change.ParentId, parent);
            }
            changes.Add(change with { ParentPath = parent });
        }
        return new(cursor, cursor with { NextUsn = parsed.NextUsn }, changes);
    }

    public static string CanonicalVolume(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.EndsWith(@"}\", StringComparison.Ordinal) &&
            Guid.TryParse(path.AsSpan(11, path.Length - 13), out _)) return path.ToUpperInvariant();
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new ArgumentException("SourceJournalVolumeInvalid");
        if (root.Length != 3 || root[1] != ':') throw new ArgumentException("SourceJournalVolumeInvalid");
        var info = new DriveInfo(root);
        if (info.DriveType != DriveType.Fixed || !string.Equals(info.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceJournalFixedNtfsRequired");
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity)) throw Native();
        return name.ToString().ToUpperInvariant();
    }

    private static SafeFileHandle OpenVolume(string path)
    {
        var canonical = CanonicalVolume(path);
        var handle = CreateFile(canonical.TrimEnd('\\'), 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw Native();
    }

    private static byte[] Control(SafeFileHandle handle, uint code, byte[] input, int maximumBytes)
    {
        var output = new byte[maximumBytes];
        if (!DeviceIoControl(handle, code, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
            throw Native();
        return output[..returned];
    }
    private static IOException Native() => NativeFailure(Marshal.GetLastPInvokeError());
    internal static IOException NativeFailure(int error) => error == 1181
        ? new SourceJournalGapException("SourceJournalEntryDeleted", new Win32Exception(error))
        : new IOException("SourceJournalNativeReadFailed", new Win32Exception(error));

    private static string? ParentPath(SafeFileHandle volume, string identity)
    {
        // Metadata only; names outside admitted sources are transient routing inputs.
        if (identity.Length != 16) return null;
        var descriptor = new FileIdDescriptor { Size = 24, Id = Convert.ToUInt64(identity, 16) };
        using var handle = OpenFileById(volume, ref descriptor, 0x80, 7, IntPtr.Zero, ParentMetadataOpenFlags);
        if (handle.IsInvalid) return null;
        try { return PhysicalFileIdentity.GetFinalPath(handle); }
        catch (IOException) { return null; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public int Type;
        [FieldOffset(8)] public ulong Id;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor descriptor, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
}
