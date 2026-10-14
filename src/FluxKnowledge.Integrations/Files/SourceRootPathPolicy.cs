using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Integrations.Files;

public sealed record SourceRootPathPolicyOptions(IReadOnlyList<string> ProtectedRoots)
{
    public static readonly SourceRootPathPolicyOptions None = new([]);
}

public sealed class SourceRootPathPolicy : ISourceRootPathPolicy
{
    private readonly IReadOnlyList<string> _protectedRoots;

    public SourceRootPathPolicy(
        LocalIngressOptions allowedRoots,
        SourceRootPathPolicyOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local source-root validation requires Windows.");
        }

        _ = LocalIngressOptionsValidator.ValidateAndCanonicalise(allowedRoots);
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
        using var sourceLease = WindowsSourcePathAccess.OpenDirectoryPath(canonicalPath, enumerate: true);
        var physical = sourceLease.Identity;

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

        using (var entries = sourceLease.Entries(CancellationToken.None).GetEnumerator()) _ = entries.MoveNext();
        GitTrackedSourceDiscovery.Repository? repository = null;
        if (request.DiscoveryMode == SourceDiscoveryMode.GitTracked)
        {
            repository = GitTrackedSourceDiscovery.ResolveRepository(physical.CanonicalPath);
            if (_protectedRoots.Any(root => Overlaps(root, repository.GitDirectory) || Overlaps(root, repository.CommonDirectory)))
                throw new UnauthorizedAccessException("Git control metadata overlaps a protected location.");
        }
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physical.CanonicalPath)));
        var identityFingerprint = physical.IdentityFingerprint;
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
            new SourceRootPermissionEvidence(true, fingerprint, evidenceJson)) { GitRepositoryIdentityFingerprint = repository?.Identity };
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

}
