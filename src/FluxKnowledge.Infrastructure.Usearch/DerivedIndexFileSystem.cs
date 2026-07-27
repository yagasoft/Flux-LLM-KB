namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexFileSystem(UsearchIndexOptions options)
{
    private static readonly HashSet<string> RecoveryAreas = new(StringComparer.OrdinalIgnoreCase) { "staging", "quarantine" };
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));

    public bool IsValidDirectory(string path) => TryCanonicalInRoot(path, out var canonical) &&
        Directory.Exists(canonical) && IsTreeSafe(canonical);

    public bool IsIntendedGenerationPath(string path) => TryCanonicalInRoot(path, out var canonical) &&
        string.Equals(Path.GetDirectoryName(canonical), Path.Combine(_root, "generations"), StringComparison.OrdinalIgnoreCase);

    public bool TryCanonicalInRoot(string path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (!AncestorsAreSafe(_root)) return false;
            canonical = candidate;
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool IsUnreferenced(string path, IEnumerable<string> referencedPaths)
    {
        if (!TryCanonicalInRoot(path, out var canonical)) return false;
        foreach (var reference in referencedPaths)
        {
            if (!TryCanonicalInRoot(reference, out var referenced)) continue;
            if (IsSameOrUnder(canonical, referenced) || IsSameOrUnder(referenced, canonical)) return false;
        }
        return true;
    }

    public bool TryQuarantine(string path, IEnumerable<string> referencedPaths)
    {
        if (!IsValidDirectory(path) || !IsUnreferenced(path, referencedPaths)) return false;
        var quarantine = Path.Combine(_root, "quarantine");
        if (!EnsureSafeDirectory(quarantine)) return false;
        var destination = Path.Combine(quarantine, $"{Guid.NewGuid():N}");
        Directory.Move(Path.GetFullPath(path), destination);
        return true;
    }

    public int Cleanup(string area, TimeSpan retention, DateTimeOffset now, IEnumerable<string> referencedPaths)
    {
        if (!RecoveryAreas.Contains(area)) return 0;
        var root = Path.Combine(_root, area);
        if (!IsValidDirectory(root)) return 0;
        var count = 0;
        foreach (var candidate in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsValidDirectory(candidate) || !IsUnreferenced(candidate, referencedPaths)) continue;
            if (now - Directory.GetLastWriteTimeUtc(candidate) < retention) continue;
            if (!DeleteTreeAfterFinalSafetyCheck(candidate)) continue;
            count++;
        }
        return count;
    }

    private bool EnsureSafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return IsValidDirectory(path);
    }

    private static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool AncestorsAreSafe(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static bool DeleteTreeAfterFinalSafetyCheck(string candidate)
    {
        if (!IsTreeSafe(candidate)) return false;
        DeleteTree(candidate);
        return true;
    }

    private static void DeleteTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse point encountered during recovery cleanup.");
            if (attributes.HasFlag(FileAttributes.Directory)) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory, recursive: false);
    }

    private static bool IsTreeSafe(string directory)
    {
        try
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attributes.HasFlag(FileAttributes.Directory) && !IsTreeSafe(entry)) return false;
            }
            return true;
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (IOException) { return false; }
    }
}
