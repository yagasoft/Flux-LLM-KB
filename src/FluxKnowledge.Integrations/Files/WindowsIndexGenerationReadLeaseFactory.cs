using FluxKnowledge.Application.Ports;
using System.ComponentModel;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Pins every ancestor and both immutable files while a pathname-only native loader reads them.</summary>
public sealed class WindowsIndexGenerationReadLeaseFactory(string indexRoot) : IIndexGenerationReadLeaseFactory
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
    private readonly string _generationRoot = Path.Combine(Path.GetFullPath(indexRoot), "generations");

    public IDisposable AcquireBuildDirectory(string directory, bool create = false)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parts = Path.GetRelativePath(_root, canonical).Split(Path.DirectorySeparatorChar);
        var valid = canonical.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
            parts.Length == 1 && (parts[0] == "generations" || parts[0] == "staging") ||
            parts.Length == 2 && parts[0] == "staging" && parts[1] == "legacy" ||
            parts.Length == 2 && parts[0] == "generations" && IsId(parts[1]) ||
            parts.Length == 3 && parts[0] == "staging" && parts[1] == "legacy" && IsId(parts[2]);
        if (!valid) throw new UnauthorizedAccessException("The legacy build path is outside its configured layout.");
        return PinDirectory(canonical, create);
    }

    public IDisposable Acquire(string generationDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Held generation loading requires Windows.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generationDirectory));
        var relative = Path.GetRelativePath(_root, canonical).Split(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(canonical), _generationRoot, StringComparison.OrdinalIgnoreCase) &&
            !(relative.Length == 3 && relative[0] == "staging" && relative[1] == "legacy" && IsId(relative[2])))
            throw new UnauthorizedAccessException("The index base is outside the configured generation root.");
        var held = new List<IDisposable>();
        try
        {
            held.Add(PinDirectory(canonical, create: false, proveMissingLeaf: true));
            foreach (var filename in new[] { "metadata.json", "index.usearch" })
            {
                var path = Path.Combine(canonical, filename);
                var handle = PhysicalFileIdentity.OpenReadNoFollow(path);
                held.Add(handle);
                if (PhysicalFileIdentity.GetLinkCount(handle) != 1 || !string.Equals(PhysicalFileIdentity.GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("The index base file changed its physical path.");
            }
            return new Lease(held);
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    private static bool IsId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigitLower);

    private static IDisposable PinDirectory(string canonical, bool create, bool proveMissingLeaf = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Held generation I/O requires Windows.");
        var held = new List<IDisposable>();
        try
        {
            var current = Path.GetPathRoot(canonical) ?? throw new UnauthorizedAccessException("The index path has no local volume.");
            held.Add(PhysicalFileIdentity.OpenDirectoryLease(current));
            foreach (var component in Path.GetRelativePath(current, canonical).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                try { held.Add(PhysicalFileIdentity.OpenDirectoryLease(current)); }
                catch (UnauthorizedAccessException exception) when (proveMissingLeaf &&
                    string.Equals(current, canonical, StringComparison.OrdinalIgnoreCase) && exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
                { throw new DirectoryNotFoundException("The immutable index base directory no longer exists.", exception); }
            }
            return new Lease(held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    private sealed class Lease(IReadOnlyList<IDisposable> held) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }
}
