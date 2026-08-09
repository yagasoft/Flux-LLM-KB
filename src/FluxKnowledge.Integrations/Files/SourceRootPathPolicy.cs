using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

public sealed record SourceRootPathPolicyOptions(IReadOnlyList<string> ProtectedRoots)
{
    public static readonly SourceRootPathPolicyOptions None = new([]);
}

public sealed class SourceRootPathPolicy : ISourceRootPathPolicy
{
    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;
    private readonly IReadOnlyList<string> _protectedRoots;

    public SourceRootPathPolicy(
        LocalIngressOptions allowedRoots,
        SourceRootPathPolicyOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local source-root validation requires Windows.");
        }

        _allowedRoots = LocalIngressOptionsValidator.ValidateAndCanonicalise(allowedRoots)
            .Select(root =>
            {
                EnsureNoReparsePointTraversal(root);
                return new AllowedRoot(root, ResolvePhysicalDirectory(root).CanonicalPath);
            })
            .ToArray();
        _protectedRoots = (options ?? SourceRootPathPolicyOptions.None).ProtectedRoots
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(CanonicalProtectedRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local source-root validation requires Windows.");
        }

        RejectUnc(request.FullPath);
        var canonicalPath = CanonicalExistingDirectory(request.FullPath);
        EnsureNoReparsePointTraversal(canonicalPath);
        var physical = ResolvePhysicalDirectory(canonicalPath);
        if (!_allowedRoots.Any(root =>
                IsWithinRoot(root.ConfiguredPath, canonicalPath) &&
                IsWithinRoot(root.PhysicalPath, physical.CanonicalPath)))
        {
            throw new UnauthorizedAccessException("The source root is outside the configured local ingress roots.");
        }

        if (_protectedRoots.Any(root =>
                Overlaps(root, canonicalPath) ||
                Overlaps(root, physical.CanonicalPath)))
        {
            throw new UnauthorizedAccessException("The source root overlaps a protected deployment, SQL, cache or secret location.");
        }

        var volumeRoot = Path.GetPathRoot(physical.CanonicalPath);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new UnauthorizedAccessException("The source root has no local filesystem volume.");
        }

        var drive = new DriveInfo(volumeRoot);
        if (drive.DriveType != DriveType.Fixed ||
            !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The source root must be on a fixed NTFS volume.");
        }

        EnsureCanEnumerate(physical.CanonicalPath);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physical.CanonicalPath)));
        var identityFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{physical.VolumeSerialNumber:X8}:{physical.FileIndexHigh:X8}{physical.FileIndexLow:X8}")));
        var evidenceJson = JsonSerializer.Serialize(new
        {
            canEnumerate = true,
            pathFingerprint = fingerprint,
            physicalIdentityFingerprint = identityFingerprint,
            volume = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            physicalPathChecked = true
        });
        return new SourceRootPathValidation(
            physical.CanonicalPath,
            new SourceRootPhysicalIdentity(physical.CanonicalPath, drive.Name, IsFixedNtfs: true, identityFingerprint),
            new SourceRootPermissionEvidence(true, fingerprint, evidenceJson));
    }

    private static string CanonicalExistingDirectory(string suppliedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        if (suppliedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            suppliedPath.StartsWith(@"//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("UNC source roots are not allowed.");
        }

        if (!Path.IsPathFullyQualified(suppliedPath))
        {
            throw new UnauthorizedAccessException("The source root must be an absolute local directory.");
        }

        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedPath));
        if (!Directory.Exists(canonicalPath))
        {
            throw new DirectoryNotFoundException("The source root does not exist.");
        }

        return canonicalPath;
    }

    private static string CanonicalProtectedRoot(string suppliedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        if (suppliedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            suppliedPath.StartsWith(@"//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(suppliedPath))
        {
            throw new ArgumentException("Protected source-root paths must be absolute local paths.", nameof(suppliedPath));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedPath));
    }

    private static void RejectUnc(string suppliedPath)
    {
        if (suppliedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            suppliedPath.StartsWith(@"//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("UNC source roots are not allowed.");
        }
    }

    private static void EnsureNoReparsePointTraversal(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath)!;
        var relative = Path.GetRelativePath(root, canonicalPath);
        var current = root;
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var directory = new DirectoryInfo(current);
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Source root reparse-point traversal is not allowed.");
            }
        }
    }

    private static void EnsureCanEnumerate(string canonicalPath)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(canonicalPath).GetEnumerator();
            _ = enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException("The source root cannot be enumerated by the application identity.", exception);
        }
    }

    private static bool IsWithinRoot(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string first, string second) =>
        IsWithinRoot(first, second) || IsWithinRoot(second, first);

    private static PhysicalDirectory ResolvePhysicalDirectory(string canonicalPath)
    {
        using var handle = NativeMethods.OpenDirectory(canonicalPath);
        return new PhysicalDirectory(
            GetNormalisedFinalPath(handle),
            NativeMethods.GetIdentity(handle));
    }

    private static string GetNormalisedFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
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

    private sealed record AllowedRoot(string ConfiguredPath, string PhysicalPath);

    private sealed record PhysicalDirectory(string CanonicalPath, NativeMethods.FileIdentity Identity)
    {
        public uint VolumeSerialNumber => Identity.VolumeSerialNumber;
        public uint FileIndexHigh => Identity.FileIndexHigh;
        public uint FileIndexLow => Identity.FileIndexLow;
    }

    private static class NativeMethods
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint FileReadAttributes = 0x00000080;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
#pragma warning restore SYSLIB1054

        internal static SafeFileHandle OpenDirectory(string path)
        {
            var handle = CreateFile(
                path,
                desiredAccess: FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new UnauthorizedAccessException("The source root cannot be opened by the application identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return handle;
        }

        internal static FileIdentity GetIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException("The source root physical identity cannot be obtained.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
        }

        internal readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow);

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
}
