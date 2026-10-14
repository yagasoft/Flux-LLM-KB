using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Cloud-aware source access only. Retained storage keeps its stricter no-reparse policy.</summary>
internal static class WindowsSourcePathAccess
{
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenNoRecall = 0x00400000;
    private const uint SynchronousIo = 0x20;
    private const uint NonDirectoryFile = 0x40;
    private const uint RecallMask = 0x00441000; // OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS

    internal readonly record struct Metadata(uint Attributes, uint ReparseTag, uint PlaceholderState)
    {
        public bool IsDirectory => (Attributes & 0x10) != 0;
        public bool NamespaceIncomplete => IsDirectory && ((Attributes & RecallMask) != 0 || (PlaceholderState & 0x30) != 0);
    }

    internal static bool IsCloudTag(uint tag) => (tag & 0xffff0fff) == 0x9000001a;

    internal static void ValidateTag(Metadata metadata)
    {
        if ((metadata.Attributes & 0x400) != 0 && !IsCloudTag(metadata.ReparseTag))
            throw new UnauthorizedAccessException($"Source reparse type 0x{metadata.ReparseTag:X8} is not supported; symbolic links and redirected folders are not allowed.");
    }

    internal static void EnsureContentAvailable(Metadata metadata)
    {
        ValidateTag(metadata);
        if ((metadata.Attributes & RecallMask) != 0 || metadata.PlaceholderState == uint.MaxValue ||
            (metadata.PlaceholderState & 0x30) != 0)
            throw new SourceContentUnavailableException("Source content is not fully available locally. It was not downloaded; make this item available in the sync client before rescanning.");
    }

    internal static Metadata ReadMetadata(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
            throw NativeError("Source attributes and reparse tag cannot be read.", Marshal.GetLastPInvokeError());
        var state = CfGetPlaceholderStateFromAttributeTag(information.Attributes, information.Tag);
        return new(information.Attributes, information.Tag, state);
    }

    internal static DirectoryPathLease OpenDirectoryPath(string path, bool enumerate = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Source physical-path validation requires Windows.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) throw new UnauthorizedAccessException("UNC source paths are not allowed.");
        var volume = Path.GetPathRoot(canonical) ?? throw new IOException("Source path has no volume.");
        var lease = new DirectoryPathLease(canonical);
        try
        {
            var handle = CreateFile(volume, ReadAttributes | Synchronize | (canonical == volume && enumerate ? 1u : 0),
                3, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw NativeError("Source volume cannot be opened.", error);
            }
            lease.Add(volume, handle);
            var relative = Path.GetRelativePath(volume, canonical);
            if (relative != ".")
            {
                var components = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var current = volume;
                for (var index = 0; index < components.Length; index++)
                {
                    current = Path.Combine(current, components[index]);
                    var access = ReadAttributes | Synchronize | (enumerate && index == components.Length - 1 ? 1u : 0);
                    // Type-neutral no-recall open; Add verifies Directory on the returned handle.
                    var child = OpenRelative(lease.Handle, components[index], access, 3, SynchronousIo);
                    lease.Add(current, child);
                }
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static SourceReadLease OpenRead(string root, string path, string? rootFingerprint = null, string? fileIdentity = null)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        if (!IsWithin(canonicalRoot, canonicalPath)) throw new UnauthorizedAccessException("Source path is outside its authorised root.");
        var parent = OpenDirectoryPath(Path.GetDirectoryName(canonicalPath)!);
        SafeFileHandle? handle = null;
        try
        {
            var rootIdentity = parent.IdentityAt(canonicalRoot);
            if (rootFingerprint is not null && rootIdentity.IdentityFingerprint != rootFingerprint)
                throw new SourceSnapshotChangedException("The admitted source root identity changed.");
            var name = Path.GetFileName(canonicalPath);
            using (var metadataHandle = OpenRelative(parent.Handle, name, ReadAttributes, 7, NonDirectoryFile))
                EnsureContentAvailable(ReadMetadata(metadataHandle));
            // The same handle supplies readiness, final path, identity and every content byte.
            handle = OpenRelative(parent.Handle, name, ReadAttributes | Synchronize | 1, 1, NonDirectoryFile);
            EnsureContentAvailable(ReadMetadata(handle));
            var finalPath = PhysicalFileIdentity.GetFinalPath(handle);
            if (!string.Equals(finalPath, canonicalPath, StringComparison.OrdinalIgnoreCase) || !IsWithin(rootIdentity.CanonicalPath, finalPath))
                throw new UnauthorizedAccessException("The source physical target escaped its held parent or authorised root.");
            var identity = PhysicalFileIdentity.Get(handle);
            if (fileIdentity is not null && fileIdentity != identity)
                throw new SourceSnapshotChangedException("The source identity changed after discovery.");
            return new SourceReadLease(parent, handle, identity, rootIdentity);
        }
        catch { handle?.Dispose(); parent.Dispose(); throw; }
    }

    internal static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint share, uint options)
    {
        if (name is "." or ".." || name.IndexOfAny(['\\', '/', ':']) >= 0)
            throw new UnauthorizedAccessException("Source path component is not a single local name.");
        var text = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text }, unicodePointer, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodePointer, Attributes = 0x40 };
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0, share, 1,
                options | OpenReparsePoint | OpenNoRecall, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0 || handle.IsInvalid)
            {
                handle.Dispose();
                throw NativeError("The source cannot be opened without redirection or cloud recall.", unchecked((int)RtlNtStatusToDosError(status)));
            }
            return handle;
        }
        finally { Marshal.FreeHGlobal(unicodePointer); Marshal.FreeHGlobal(text); }
    }

    private static Exception NativeError(string message, int code) => code switch
    {
        2 => new FileNotFoundException(message),
        3 => new DirectoryNotFoundException(message),
        5 => new UnauthorizedAccessException(message, new Win32Exception(code)),
        // ERROR_FLT_INVALID_NAME_REQUEST: a filter can refuse even a no-recall metadata open.
        // Keep the refusal; do not retry through a less restricted path or claim the file is absent.
        unchecked((int)0x801f0005) => new SourceContentUnavailableException(
            "The file-system provider could not supply this source path for a guarded local read. No download was requested; check the provider or sync client before retrying.",
            new Win32Exception(code)),
        _ => new IOException(message, new Win32Exception(code))
    };

    internal sealed class DirectoryPathLease(string path) : IDisposable
    {
        private readonly List<(string Path, SafeFileHandle Handle, PhysicalDirectoryIdentity Identity)> _chain = [];
        public string Path { get; } = path;
        public SafeFileHandle Handle => _chain[^1].Handle;
        public PhysicalDirectoryIdentity Identity => _chain[^1].Identity;
        public Metadata Metadata => ReadMetadata(Handle);
        public PhysicalDirectoryIdentity IdentityAt(string canonicalPath) =>
            _chain.First(item => string.Equals(item.Path, canonicalPath, StringComparison.OrdinalIgnoreCase)).Identity;

        internal void Add(string canonicalPath, SafeFileHandle handle)
        {
            try
            {
                var metadata = ReadMetadata(handle);
                ValidateTag(metadata);
                if (!metadata.IsDirectory) throw new DirectoryNotFoundException("A source ancestor is no longer a directory.");
                var finalPath = PhysicalFileIdentity.GetFinalPath(handle);
                if (!string.Equals(finalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("A source ancestor resolves outside its configured physical path.");
                var identity = PhysicalFileIdentity.Get(handle);
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity["ntfs:".Length..])));
                _chain.Add((canonicalPath, handle, new(finalPath, fingerprint)));
            }
            catch { handle.Dispose(); throw; }
        }

        public IEnumerable<(string Name, FileAttributes Attributes)> Entries(CancellationToken cancellationToken)
        {
            var capacity = 4096;
            var buffer = Marshal.AllocHGlobal(capacity);
            var restart = true;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Materialised namespace only; never fall back to a query that expands cloud folders.
                    var status = NtQueryDirectoryFileEx(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var io,
                        buffer, (uint)capacity, 37, 0x8u | (restart ? 0x2u : 0), IntPtr.Zero);
                    if (unchecked((uint)status) == 0x80000006) yield break; // STATUS_NO_MORE_FILES
                    if (status < 0) throw NativeError("The local source namespace cannot be enumerated.", unchecked((int)RtlNtStatusToDosError(status)));
                    var returned = checked((int)io.Information);
                    if (returned == 0)
                    {
                        if (capacity >= 65536) throw new IOException("The source namespace returned no bounded directory data.");
                        capacity *= 2;
                        buffer = Marshal.ReAllocHGlobal(buffer, capacity);
                        continue;
                    }
                    restart = false;
                    if (returned > capacity) throw new IOException("Invalid source directory response length.");
                    var offset = 0;
                    while (true)
                    {
                        if (returned - offset < 104) throw new IOException("Invalid source directory entry.");
                        var record = IntPtr.Add(buffer, offset);
                        var next = Marshal.ReadInt32(record);
                        var nameBytes = Marshal.ReadInt32(record, 60);
                        if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > returned - offset - 104)
                            throw new IOException("Invalid source directory name length.");
                        var name = Marshal.PtrToStringUni(IntPtr.Add(record, 104), nameBytes / 2)!;
                        if (name is not "." and not "..")
                        {
                            if (name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw new IOException("Invalid source directory name.");
                            yield return (name, (FileAttributes)Marshal.ReadInt32(record, 56));
                        }
                        if (next == 0) break;
                        if (next < 104 + nameBytes || (next & 7) != 0 || next > returned - offset - 104)
                            throw new IOException("Invalid source directory entry offset.");
                        offset += next;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            for (var index = _chain.Count - 1; index >= 0; index--) _chain[index].Handle.Dispose();
            _chain.Clear();
        }
    }

    internal sealed class SourceReadLease(DirectoryPathLease parent, SafeFileHandle handle, string identity, PhysicalDirectoryIdentity root) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public string Identity { get; } = identity;
        public PhysicalDirectoryIdentity Root { get; } = root;
        public void Dispose() { Handle.Dispose(); parent.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public uint Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public IntPtr Status; public nuint Information; }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTagInfo information, uint size);
    [DllImport("cldapi.dll")] private static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] private static extern int NtQueryDirectoryFileEx(SafeFileHandle handle, IntPtr @event, IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatus, IntPtr information, uint length, int informationClass, uint flags, IntPtr fileName);
#pragma warning restore SYSLIB1054
}
