namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexFileSystem(UsearchIndexOptions options)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));

    public bool IsValidDirectory(string path) => TryCanonicalInRoot(path, out var canonical) &&
        Directory.Exists(canonical) && !HasReparsePoint(canonical);

    public bool TryCanonicalInRoot(string path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            canonical = candidate;
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool IsUnreferenced(string path, IEnumerable<string> referencedPaths)
    {
        if (!TryCanonicalInRoot(path, out var canonical)) return false;
        foreach (var reference in referencedPaths)
            if (TryCanonicalInRoot(reference, out var referenced) &&
                string.Equals(canonical, referenced, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public bool TryQuarantine(string path, IEnumerable<string> referencedPaths)
    {
        if (!IsValidDirectory(path) || !IsUnreferenced(path, referencedPaths)) return false;
        var quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        var destination = Path.Combine(quarantine, $"{Guid.NewGuid():N}");
        Directory.Move(Path.GetFullPath(path), destination);
        return true;
    }

    public int Cleanup(string area, TimeSpan retention, DateTimeOffset now, IEnumerable<string> referencedPaths)
    {
        var root = Path.Combine(_root, area);
        if (!IsValidDirectory(root)) return 0;
        var count = 0;
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            if (!IsValidDirectory(candidate) || !IsUnreferenced(candidate, referencedPaths)) continue;
            if (now - Directory.GetLastWriteTimeUtc(candidate) < retention) continue;
            Directory.Delete(candidate, recursive: true);
            count++;
        }
        return count;
    }

    private static bool HasReparsePoint(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                    .Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (IOException) { return true; }
    }
}
