using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Stable NTFS identity obtained from an open handle, never from a path string.</summary>
public static class PhysicalFileIdentity
{
    public static string Get(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "path:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        }

        using var handle = CreateFile(path, 0x80, 0x7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("The source file identity cannot be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return Get(handle);
    }

    public static string Get(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A handle-based file identity requires Windows.");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("The source file identity cannot be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return $"ntfs:{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    /// <summary>Opens one existing file without following a final reparse point.</summary>
    public static SafeFileHandle OpenReadNoFollow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("No-follow retained artifact access requires Windows.");
        }

        const uint genericRead = 0x80000000;
        const uint fileShareRead = 0x1;
        const uint openExisting = 3;
        const uint fileFlagOverlapped = 0x40000000;
        const uint fileFlagSequentialScan = 0x08000000;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        var handle = CreateFile(
            Path.GetFullPath(path),
            genericRead,
            fileShareRead,
            IntPtr.Zero,
            openExisting,
            fileFlagOverlapped | fileFlagSequentialScan | fileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var errorCode = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (errorCode is 2 or 3)
            {
                throw new FileNotFoundException("The retained artifact does not exist.", path);
            }
            throw new IOException("The retained artifact cannot be opened without following links.",
                new Win32Exception(errorCode));
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            handle.Dispose();
            throw new IOException("The retained artifact attributes cannot be read.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (((FileAttributes)information.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new IOException("The retained artifact final file is a reparse point.");
        }

        return handle;
    }

    /// <summary>Returns the canonical final path for an already-open Windows handle.</summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Handle-bound retained artifact path validation requires Windows.");
        }

        return GetNormalisedFinalPath(handle);
    }

    public static PhysicalDirectoryIdentity GetDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
        {
            return new PhysicalDirectoryIdentity(canonicalPath, PathIdentity(canonicalPath));
        }

        using var handle = CreateFile(canonicalPath, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new UnauthorizedAccessException("The source root cannot be opened by the application identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new PhysicalDirectoryIdentity(GetFinalPath(handle), Fingerprint(Get(handle)));
    }

    public static PhysicalDirectoryLease OpenDirectoryLease(string path)
    {
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
        {
            return new PhysicalDirectoryLease(null, GetDirectory(canonicalPath));
        }

        const uint fileReadAttributes = 0x80;
        const uint fileDeleteChild = 0x40;
        const uint fileFlagBackupSemantics = 0x02000000;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        var handle = CreateFile(
            canonicalPath,
            fileReadAttributes | fileDeleteChild,
            0x3,
            IntPtr.Zero,
            3,
            fileFlagBackupSemantics | fileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("The artifact directory cannot be held by the application identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            handle.Dispose();
            throw new IOException("The artifact directory attributes cannot be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (((FileAttributes)information.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("The artifact directory is a reparse point.");
        }

        var finalPath = GetFinalPath(handle);
        if (!string.Equals(finalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("The artifact directory resolves outside its configured path.");
        }

        return new PhysicalDirectoryLease(handle, new PhysicalDirectoryIdentity(finalPath, Fingerprint(Get(handle))));
    }

    public static void EnsureNoReparsePointTraversal(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath) ?? throw new IOException("The source root has no filesystem volume.");
        var relative = Path.GetRelativePath(root, canonicalPath);
        var current = root;
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var directory = new DirectoryInfo(current);
            directory.Refresh();
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException("The source root no longer exists.");
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Source root reparse-point traversal is not allowed.");
            }
        }
    }

    public static string GetProjectedPhysicalPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(fullPath);
        var suffix = new Stack<string>();
        while (!directory.Exists && directory.Parent is not null)
        {
            suffix.Push(directory.Name);
            directory = directory.Parent;
        }

        EnsureNoReparsePointTraversal(directory.FullName);
        var physical = GetDirectory(directory.FullName).CanonicalPath;
        while (suffix.Count > 0)
        {
            physical = Path.Combine(physical, suffix.Pop());
        }

        return Path.TrimEndingDirectorySeparator(physical);
    }

    private static string PathIdentity(string path) =>
        "path:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));

    private static string Fingerprint(string identity)
    {
        var material = identity.StartsWith("ntfs:", StringComparison.Ordinal)
            ? identity["ntfs:".Length..]
            : identity;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string GetNormalisedFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new IOException("The final path of the source root cannot be obtained.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < buffer.Capacity)
            {
                const string extendedPrefix = @"\\?\";
                var finalPath = buffer.ToString();
                if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("UNC source roots are not allowed.");
                }

                if (finalPath.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    finalPath = finalPath[extendedPrefix.Length..];
                }

                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalPath));
            }

            capacity = checked((int)length + 1);
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed record PhysicalDirectoryIdentity(string CanonicalPath, string IdentityFingerprint);

public sealed class PhysicalDirectoryLease(SafeFileHandle? handle, PhysicalDirectoryIdentity identity) : IDisposable
{
    public PhysicalDirectoryIdentity Identity { get; } = identity;

    public void Dispose() => handle?.Dispose();
}
