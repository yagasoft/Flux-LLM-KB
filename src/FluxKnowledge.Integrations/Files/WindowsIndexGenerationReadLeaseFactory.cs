using FluxKnowledge.Application.Ports;
using System.ComponentModel;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Pins every ancestor and both immutable files while a pathname-only native loader reads them.</summary>
public sealed class WindowsIndexGenerationReadLeaseFactory(string indexRoot) : IIndexGenerationReadLeaseFactory
{
    private readonly string _generationRoot = Path.Combine(Path.GetFullPath(indexRoot), "generations");

    public IDisposable Acquire(string generationDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Held generation loading requires Windows.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generationDirectory));
        if (!string.Equals(Path.GetDirectoryName(canonical), _generationRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The index base is outside the configured generation root.");
        var held = new List<IDisposable>();
        try
        {
            var current = Path.GetPathRoot(canonical) ?? throw new UnauthorizedAccessException("The index base has no local volume.");
            held.Add(PhysicalFileIdentity.OpenDirectoryLease(current));
            foreach (var component in Path.GetRelativePath(current, canonical).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                try { held.Add(PhysicalFileIdentity.OpenDirectoryLease(current)); }
                catch (UnauthorizedAccessException exception) when (
                    string.Equals(current, canonical, StringComparison.OrdinalIgnoreCase) &&
                    exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
                {
                    throw new DirectoryNotFoundException("The immutable index base directory no longer exists.", exception);
                }
            }
            foreach (var filename in new[] { "metadata.json", "index.usearch" })
            {
                var path = Path.Combine(canonical, filename);
                var handle = PhysicalFileIdentity.OpenReadNoFollow(path);
                held.Add(handle);
                if (!string.Equals(PhysicalFileIdentity.GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase))
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
