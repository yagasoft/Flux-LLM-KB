namespace FluxKnowledge.Application.Operations;

/// <summary>Constructs and validates every ordinary app-owned live path from one root.</summary>
public sealed class LiveRootLayout
{
    public const string CanonicalProductionRoot = @"I:\FluxKnowledge";

    private LiveRootLayout(string root, bool isProduction)
    {
        Root = NormaliseAbsolute(root);
        IsProduction = isProduction;
        ApplicationRoot = Child("App");
        ConfigRoot = Child("Config");
        DataRoot = Child("Data");
        SqlRoot = Child("Data", "Sql");
        SqlDataRoot = Child("Data", "Sql", "Data");
        SqlLogRoot = Child("Data", "Sql", "Log");
        SqlDataFilePath = Path.Combine(SqlDataRoot, "FluxKnowledge.mdf");
        SqlLogFilePath = Path.Combine(SqlLogRoot, "FluxKnowledge_log.ldf");
        IndexRoot = Child("Data", "Index");
        RetainedRoot = Child("Data", "Retained");
        RuntimeRoot = Child("Runtime");
        SpoolRoot = Child("Runtime", "Spool");
        TempRoot = Child("Runtime", "Temp");
        LogsRoot = Child("Runtime", "Logs");
        CodexPluginRoot = Child("CodexPlugin");
        RecoveryRoot = Child("Recovery");
        AppOwnedLocations =
        [
            ApplicationRoot,
            ConfigRoot,
            DataRoot,
            SqlRoot,
            SqlDataRoot,
            SqlLogRoot,
            SqlDataFilePath,
            SqlLogFilePath,
            IndexRoot,
            RetainedRoot,
            RuntimeRoot,
            SpoolRoot,
            TempRoot,
            LogsRoot,
            CodexPluginRoot,
            RecoveryRoot
        ];
    }

    public static LiveRootLayout Production { get; } = new(CanonicalProductionRoot, true);

    public string Root { get; }
    public bool IsProduction { get; }
    public string ApplicationRoot { get; }
    public string ConfigRoot { get; }
    public string DataRoot { get; }
    public string SqlRoot { get; }
    public string SqlDataRoot { get; }
    public string SqlLogRoot { get; }
    public string SqlDataFilePath { get; }
    public string SqlLogFilePath { get; }
    public string IndexRoot { get; }
    public string RetainedRoot { get; }
    public string RuntimeRoot { get; }
    public string SpoolRoot { get; }
    public string TempRoot { get; }
    public string LogsRoot { get; }
    public string CodexPluginRoot { get; }
    public string RecoveryRoot { get; }
    public IReadOnlyList<string> AppOwnedLocations { get; }

    internal static LiveRootLayout CreateForIsolatedTests(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var layout = new LiveRootLayout(root, false);
        if (string.Equals(layout.Root, Production.Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An isolated layout cannot use the production root.", nameof(root));
        }

        return layout;
    }

    public bool IsOwnedPath(string candidate)
    {
        if (!TryNormaliseCandidate(candidate, out var canonical, out _)) return false;
        return IsWithin(canonical, Root, includeRoot: false);
    }

    internal bool IsExactSpoolRoot(string candidate) =>
        TryNormaliseCandidate(candidate, out var canonical, out _) &&
        string.Equals(canonical, SpoolRoot, StringComparison.OrdinalIgnoreCase);

    public LiveRootPathValidation ValidateOwnedPath(string candidate, ILiveRootPathInspector inspector) =>
        ValidateOwnedPath(candidate, inspector, requireExistingRoot: false);

    internal LiveRootPathValidation ValidateOwnedPathBeforeIo(
        string candidate,
        ILiveRootPathInspector inspector) =>
        ValidateOwnedPath(candidate, inspector, requireExistingRoot: true);

    private LiveRootPathValidation ValidateOwnedPath(
        string candidate,
        ILiveRootPathInspector inspector,
        bool requireExistingRoot)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (!TryNormaliseCandidate(candidate, out var canonical, out var reason))
        {
            return new(false, null, reason);
        }

        if (!IsWithin(canonical, Root, includeRoot: true))
        {
            return new(false, null, "root-escape");
        }

        try
        {
            foreach (var segment in EnumerateSegments(canonical))
            {
                var inspection = inspector.Inspect(segment);
                if (!inspection.Exists)
                {
                    if (requireExistingRoot && string.Equals(segment, Root, StringComparison.OrdinalIgnoreCase))
                    {
                        return new(false, null, "live-root-missing");
                    }
                    break;
                }
                if (inspection.IsReparsePoint)
                {
                    return new(false, null, "reparse-point-not-allowed");
                }
                if (string.IsNullOrWhiteSpace(inspection.ResolvedPath) ||
                    !TryNormaliseCandidate(inspection.ResolvedPath, out var resolved, out _) ||
                    !string.Equals(resolved, segment, StringComparison.OrdinalIgnoreCase))
                {
                    return new(false, null, "foreign-or-ambiguous-resolution");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, null, "path-inspection-failed");
        }

        return new(true, canonical, null);
    }

    /// <summary>Accepts only an absent or exact canonical production path without touching the filesystem.</summary>
    public static string RequireExactProductionPathOverride(
        string? configuredPath,
        string expectedPath,
        string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        if (string.IsNullOrWhiteSpace(configuredPath)) return expectedPath;

        var layout = Production;
        if (!layout.TryNormaliseCandidate(configuredPath, out var canonical, out _) ||
            !string.Equals(canonical, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{configurationKey} must equal the canonical production path {expectedPath}.");
        }

        return expectedPath;
    }

    private string Child(params string[] segments)
    {
        var candidate = Path.Combine([Root, .. segments]);
        if (!IsWithin(candidate, Root, includeRoot: false))
        {
            throw new InvalidOperationException("An app-owned path escaped the live root.");
        }

        return candidate;
    }

    private IEnumerable<string> EnumerateSegments(string candidate)
    {
        yield return Root;
        var relative = Path.GetRelativePath(Root, candidate);
        if (relative == ".") yield break;
        var current = Root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private bool TryNormaliseCandidate(string candidate, out string canonical, out string reason)
    {
        canonical = string.Empty;
        reason = "invalid-path";
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var normalisedSeparators = candidate.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalisedSeparators.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalisedSeparators.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            normalisedSeparators.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or "..") ||
            !Path.IsPathFullyQualified(normalisedSeparators))
        {
            reason = "path-traversal-or-noncanonical";
            return false;
        }

        try
        {
            canonical = NormaliseAbsolute(normalisedSeparators);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormaliseAbsolute(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string candidate, string root, bool includeRoot) =>
        (includeRoot && string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public interface ILiveRootPathInspector
{
    LiveRootPathInspection Inspect(string path);
}

public sealed record LiveRootPathInspection(bool Exists, bool IsReparsePoint, string? ResolvedPath);

public sealed record LiveRootPathValidation(bool IsValid, string? CanonicalPath, string? Reason);

/// <summary>Fail-closed physical ownership validation performed immediately before app-owned storage I/O.</summary>
public sealed class LiveRootStorageSafety(
    LiveRootLayout layout,
    ILiveRootPathInspector inspector)
{
    public void ValidateBeforeIo(string ownedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedPath);
        LiveRootPathInspection rootInspection;
        try
        {
            rootInspection = inspector.Inspect(layout.Root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("The live root could not be inspected safely before I/O.", exception);
        }

        if (!rootInspection.Exists || rootInspection.IsReparsePoint ||
            string.IsNullOrWhiteSpace(rootInspection.ResolvedPath) ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootInspection.ResolvedPath)),
                layout.Root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The live root is missing, foreign, ambiguous or a reparse point.");
        }

        var validation = layout.ValidateOwnedPathBeforeIo(ownedPath, inspector);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"The app-owned storage path failed safe inspection before I/O: {validation.Reason}.");
        }
    }
}

/// <summary>Physical path inspector used by production storage boundaries.</summary>
public sealed class FileSystemLiveRootPathInspector : ILiveRootPathInspector
{
    public static FileSystemLiveRootPathInspector Instance { get; } = new();

    private FileSystemLiveRootPathInspector()
    {
    }

    public LiveRootPathInspection Inspect(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileSystemInfo info;
        if (Directory.Exists(fullPath)) info = new DirectoryInfo(fullPath);
        else if (File.Exists(fullPath)) info = new FileInfo(fullPath);
        else return new(false, false, null);

        info.Refresh();
        var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        var resolved = reparse
            ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            : fullPath;
        return new(true, reparse, resolved);
    }
}
