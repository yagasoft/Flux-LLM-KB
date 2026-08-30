namespace FluxKnowledge.Application.Operations;

/// <summary>
/// Binds every persisted Outlook profile to the one canonical live spool and
/// proves its existing ancestors immediately before filesystem I/O.
/// </summary>
public sealed class PersistedOutlookSpoolRootPolicy
{
    private readonly Func<string, bool> _isExact;
    private readonly Action _validateBeforeIo;

    public PersistedOutlookSpoolRootPolicy(
        LiveRootLayout layout,
        LiveRootStorageSafety storageSafety)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(storageSafety);
        CanonicalSpoolRoot = layout.SpoolRoot;
        _isExact = layout.IsExactSpoolRoot;
        _validateBeforeIo = () => storageSafety.ValidateBeforeIo(layout.SpoolRoot);
    }

    private PersistedOutlookSpoolRootPolicy(
        string canonicalSpoolRoot,
        Func<string, bool> isExact,
        Action validateBeforeIo)
    {
        CanonicalSpoolRoot = canonicalSpoolRoot;
        _isExact = isExact;
        _validateBeforeIo = validateBeforeIo;
    }

    public string CanonicalSpoolRoot { get; }

    internal static PersistedOutlookSpoolRootPolicy CreateForIsolatedTests(string canonicalSpoolRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSpoolRoot);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalSpoolRoot));
        return new PersistedOutlookSpoolRootPolicy(
            canonical,
            candidate => IsExactCanonical(candidate, canonical),
            () => ValidateExistingAncestors(canonical));
    }

    public string RequireCanonicalBeforeIo(string persistedSpoolRoot)
    {
        if (!_isExact(persistedSpoolRoot))
        {
            throw Unavailable();
        }

        try
        {
            _validateBeforeIo();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }

        return CanonicalSpoolRoot;
    }

    private static bool IsExactCanonical(string candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            candidate.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            candidate.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "." or "..") ||
            !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateExistingAncestors(string canonicalSpoolRoot)
    {
        var pathRoot = Path.GetPathRoot(canonicalSpoolRoot)
            ?? throw new InvalidOperationException("The isolated spool root is not absolute.");
        var current = Path.TrimEndingDirectorySeparator(pathRoot);
        foreach (var segment in canonicalSpoolRoot[pathRoot.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info;
            if (Directory.Exists(current)) info = new DirectoryInfo(current);
            else if (File.Exists(current)) info = new FileInfo(current);
            else break;
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The isolated spool root traverses a reparse point.");
            }
        }
    }

    private static InvalidDataException Unavailable(Exception? innerException = null) =>
        new("The persisted Outlook spool root is unavailable.", innerException);
}
