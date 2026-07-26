using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;

namespace FluxKnowledge.Integrations.Files;

public sealed class Utf8FileSourceReader : IUtf8FileSourceReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyList<AllowedRoot> _allowedRoots;

    public Utf8FileSourceReader(LocalIngressOptions options)
    {
        _allowedRoots = LocalIngressOptionsValidator
            .ValidateAndCanonicalise(options)
            .Select(
                root => new AllowedRoot(
                    root,
                    ResolvePhysicalExistingPath(root, finalComponentIsDirectory: true)))
            .ToArray();
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

        var physicalPath = ResolvePhysicalExistingPath(
            canonicalPath,
            finalComponentIsDirectory: false);
        if (!candidateRoots.Any(root => IsWithinRoot(root.PhysicalPath, physicalPath)))
        {
            throw new UnauthorizedAccessException(
                "The source path physical target is outside the configured local ingress roots: " +
                physicalPath);
        }

        var bytes = await File.ReadAllBytesAsync(physicalPath, cancellationToken)
            .ConfigureAwait(false);
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

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
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
                current = Path.GetFullPath(candidate);
            }
        }

        return current;
    }

    private sealed record AllowedRoot(string ConfiguredPath, string PhysicalPath);
}
