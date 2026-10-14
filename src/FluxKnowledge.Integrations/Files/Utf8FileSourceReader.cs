using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

public interface IUtf8FileHandleOpener
{
    SafeFileHandle OpenRead(string canonicalPath);
}

public sealed class Utf8FileSourceReader : IUtf8FileSourceReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;
    private readonly IUtf8FileHandleOpener? _handleOpener;

    public Utf8FileSourceReader(
        LocalIngressOptions options,
        IUtf8FileHandleOpener? handleOpener = null)
    {
        _allowedRoots = LocalIngressOptionsValidator
            .ValidateAndCanonicalise(options)
            .Select(root =>
            {
                var physical = ResolvePhysicalExistingPath(root, finalComponentIsDirectory: true);
                using var lease = WindowsSourcePathAccess.OpenDirectoryPath(physical);
                return new AllowedRoot(root, lease.Identity.CanonicalPath, lease.Identity.IdentityFingerprint);
            })
            .ToArray();
        _handleOpener = handleOpener;
    }

    public async ValueTask<Utf8FileSource> ReadAsync(
        string suppliedPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        var canonicalPath = Path.GetFullPath(suppliedPath);
        var candidateRoots = _allowedRoots
            .Where(
                root =>
                    IsWithinRoot(root.ConfiguredPath, canonicalPath) ||
                    IsWithinRoot(root.PhysicalPath, canonicalPath))
            .ToArray();
        if (candidateRoots.Length == 0)
        {
            throw new UnauthorizedAccessException(
                $"The source path is outside the configured local ingress roots: {canonicalPath}");
        }

        var allowedRoot = candidateRoots.OrderByDescending(root => root.PhysicalPath.Length).First();
        var physicalCandidate = IsWithinRoot(allowedRoot.PhysicalPath, canonicalPath) ? canonicalPath :
            Path.GetFullPath(Path.GetRelativePath(allowedRoot.ConfiguredPath, canonicalPath), allowedRoot.PhysicalPath);
        // Legacy ingress permits aliases whose resolved target stays inside an authorised root.
        // Resolve metadata first, then read only the validated physical path through the held chain.
        var resolvedPath = ResolvePhysicalExistingPath(physicalCandidate, finalComponentIsDirectory: false);
        var targetRoot = candidateRoots.FirstOrDefault(root => IsWithinRoot(root.PhysicalPath, resolvedPath))
            ?? throw new UnauthorizedAccessException("The source path physical target is outside the configured local ingress roots.");
        using var source = OpenSource(targetRoot, resolvedPath);
        using var suppliedHandle = _handleOpener?.OpenRead(canonicalPath);
        var handle = suppliedHandle ?? source.Handle;
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(handle));
        var physicalPath = GetNormalisedFinalPath(handle);
        if (!candidateRoots.Any(root => IsWithinRoot(root.PhysicalPath, physicalPath)))
        {
            throw new UnauthorizedAccessException(
                "The source path physical target from the opened file handle is outside " +
                "the configured local ingress roots: " +
                physicalPath);
        }

        await using var stream = new FileStream(
            handle,
            FileAccess.Read,
            bufferSize: 81920,
            isAsync: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The source file is not valid UTF-8: {canonicalPath}",
                exception);
        }

        return new Utf8FileSource(
            physicalPath,
            bytes,
            text,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static WindowsSourcePathAccess.SourceReadLease OpenSource(AllowedRoot root, string path)
    {
        try { return WindowsSourcePathAccess.OpenRead(root.PhysicalPath, path, root.IdentityFingerprint); }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException("The source path physical target is not permitted by the configured local ingress root.", exception);
        }
    }

    private static string GetNormalisedFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-bound local ingress validation requires Windows.");
        }

        var capacity = 512;
        while (true)
        {
            var pathBuffer = new StringBuilder(capacity);
            var result = NativeMethods.GetFinalPathNameByHandle(
                handle,
                pathBuffer,
                checked((uint)pathBuffer.Capacity),
                flags: 0);
            if (result == 0)
            {
                throw new IOException(
                    "The final path of the opened local ingress file cannot be obtained.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (result < pathBuffer.Capacity)
            {
                return NormaliseFinalPath(pathBuffer.ToString());
            }

            capacity = checked((int)result + 1);
        }
    }

    private static string NormaliseFinalPath(string finalPath)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPathPrefix = @"\\?\";
        string dosPath;
        if (finalPath.StartsWith(
                extendedUncPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            dosPath = @"\\" + finalPath[extendedUncPrefix.Length..];
        }
        else if (finalPath.StartsWith(
                     extendedPathPrefix,
                     StringComparison.OrdinalIgnoreCase))
        {
            dosPath = finalPath[extendedPathPrefix.Length..];
        }
        else
        {
            dosPath = finalPath;
        }

        if (string.IsNullOrWhiteSpace(dosPath) ||
            dosPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            dosPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(dosPath))
        {
            throw new IOException(
                "The final path of the opened local ingress file cannot be normalised.");
        }

        return Path.GetFullPath(dosPath);
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

    private static string ResolvePhysicalExistingPath(
        string path,
        bool finalComponentIsDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var fileSystemRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(fileSystemRoot))
        {
            throw new IOException($"The source path has no filesystem root: {fullPath}");
        }

        var relative = Path.GetRelativePath(fileSystemRoot, fullPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return Path.GetFullPath(fileSystemRoot);
        }

        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetFullPath(fileSystemRoot);
        for (var index = 0; index < components.Length; index++)
        {
            var candidate = Path.Combine(current, components[index]);
            var isDirectory = index < components.Length - 1 || finalComponentIsDirectory;
            FileSystemInfo info = isDirectory
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            info.Refresh();
            if (!info.Exists)
            {
                throw isDirectory
                    ? new DirectoryNotFoundException(
                        $"The local ingress path does not exist: {candidate}")
                    : new FileNotFoundException(
                        "The local ingress file does not exist.",
                        candidate);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    throw new IOException(
                        $"The local ingress reparse target cannot be resolved: {candidate}");
                }

                current = Path.GetFullPath(target.FullName);
            }
            else
            {
                // Cloud/unsupported tags are classified by the subsequent physical handle walk.
                current = Path.GetFullPath(candidate);
            }
        }

        return current;
    }

    private sealed record AllowedRoot(string ConfiguredPath, string PhysicalPath, string IdentityFingerprint);

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054
        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] StringBuilder filePath,
            uint filePathLength,
            uint flags);
#pragma warning restore SYSLIB1054
    }
}
