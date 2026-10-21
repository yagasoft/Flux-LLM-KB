using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Strict physical guards for the two segment areas, including bounded native loading.</summary>
public sealed class WindowsIndexSegmentReadLeaseFactory(string indexRoot) : IIndexSegmentReadLeaseFactory
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));

    public IDisposable AcquireDirectory(string directory, bool create = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Held segment I/O requires Windows.");
        var canonical = Canonical(directory);
        var held = new List<IDisposable>();
        try
        {
            var current = Path.GetPathRoot(canonical) ?? throw new UnauthorizedAccessException("Segment volume is missing.");
            held.Add(PhysicalFileIdentity.OpenDirectoryLease(current));
            foreach (var component in Path.GetRelativePath(current, canonical).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                // Each held parent forbids replacement while the child is created/opened.
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                held.Add(PhysicalFileIdentity.OpenDirectoryLease(current));
            }
            return new Lease(held);
        }
        catch
        {
            Release(held);
            throw;
        }
    }

    public IDisposable Acquire(string segmentDirectory)
    {
        var canonical = Canonical(segmentDirectory);
        var relative = Path.GetRelativePath(_root, canonical).Split(Path.DirectorySeparatorChar);
        if (!(relative.Length == 2 && relative[0] == "segments" || relative.Length == 3 && relative[0] == "staging" && relative[1] == "segments"))
            throw new UnauthorizedAccessException("A segment read must name one immutable object.");
        var held = new List<IDisposable>();
        try
        {
            held.Add(AcquireDirectory(canonical));
            foreach (var filename in new[] { "metadata.json", "index.usearch" })
            {
                var path = Path.Combine(canonical, filename);
                var file = PhysicalFileIdentity.OpenReadNoFollow(path);
                held.Add(file);
                if (PhysicalFileIdentity.GetLinkCount(file) != 1 ||
                    !string.Equals(PhysicalFileIdentity.GetFinalPath(file), path, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Segment file identity or link count is unsafe.");
            }
            return new Lease(held);
        }
        catch
        {
            Release(held);
            throw;
        }
    }

    public IDisposable AcquireManifest(string manifestDirectory)
    {
        var canonical = Canonical(manifestDirectory);
        var relative = Path.GetRelativePath(_root, canonical).Split(Path.DirectorySeparatorChar);
        if (!(relative.Length == 2 && relative[0] == "manifests" || relative.Length == 3 && relative[0] == "staging" && relative[1] == "manifests"))
            throw new UnauthorizedAccessException("A manifest read must name one immutable manifest.");
        var held = new List<IDisposable>();
        try
        {
            held.Add(AcquireDirectory(canonical));
            var path = Path.Combine(canonical, "manifest.json");
            var file = PhysicalFileIdentity.OpenReadNoFollow(path);
            held.Add(file);
            if (PhysicalFileIdentity.GetLinkCount(file) != 1 ||
                !string.Equals(PhysicalFileIdentity.GetFinalPath(file), path, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Manifest file identity or link count is unsafe.");
            return new Lease(held);
        }
        catch { Release(held); throw; }
    }

    private string Canonical(string path)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(canonical, _root, StringComparison.OrdinalIgnoreCase)) return canonical;
        var parts = Path.GetRelativePath(_root, canonical).Split(Path.DirectorySeparatorChar);
        var valid = parts.Length == 1 && (parts[0] == "segments" || parts[0] == "staging" || parts[0] == "manifests") ||
            parts.Length == 2 && parts[0] == "staging" && (parts[1] == "segments" || parts[1] == "manifests") ||
            parts.Length == 2 && parts[0] == "segments" && IsHex(parts[1], 64) ||
            parts.Length == 2 && parts[0] == "manifests" && IsHex(parts[1], 32) ||
            parts.Length == 3 && parts[0] == "staging" && (parts[1] == "segments" || parts[1] == "manifests") && IsHex(parts[2], 32);
        if (!valid) throw new UnauthorizedAccessException("The segment path is outside its configured storage layout.");
        return canonical;
    }

    private static bool IsHex(string value, int length) => value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Release(IReadOnlyList<IDisposable> held)
    {
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }

    private sealed class Lease(IReadOnlyList<IDisposable> held) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release(held);
        }
    }
}
