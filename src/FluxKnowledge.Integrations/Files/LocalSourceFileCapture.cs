using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Owns the actual handle and completed hash produced by local discovery.</summary>
internal sealed class LocalSourceFileCapture(
    SourceDiscoveredFile snapshot,
    WindowsSourcePathAccess.SourceReadLease source,
    FileStream input) : ISourceFileCapture
{
    private int _disposed;
    private int _consumed;

    public SourceDiscoveredFile Snapshot { get; } = snapshot;
    internal WindowsSourcePathAccess.SourceReadLease Source { get; } = source;
    internal FileStream Input { get; } = input;

    internal void BeginRetention()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            throw new InvalidOperationException("A source capture can be retained only once.");
        ValidateCurrent();
    }

    internal void ValidateCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateSourceCurrent(Snapshot, Source);
    }

    internal static void ValidateSourceCurrent(SourceDiscoveredFile snapshot, WindowsSourcePathAccess.SourceReadLease source)
    {
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(source.Handle));
        if (PhysicalFileIdentity.Get(source.Handle) != snapshot.StableSourceIdentity ||
            RandomAccess.GetLength(source.Handle) != snapshot.ByteLength ||
            File.GetLastWriteTimeUtc(source.Handle) != snapshot.LastWriteAtUtc ||
            !string.Equals(PhysicalFileIdentity.GetFinalPath(source.Handle), snapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            throw new SourceSnapshotChangedException("The held physical capture changed before reference publication.");
        // The held file alone does not establish current ancestor/path authority.
        var binding = snapshot.SourcePath ?? throw new InvalidDataException("A physical capture requires source-root admission.");
        using var current = WindowsSourcePathAccess.OpenRead(binding.RootPath, snapshot.CanonicalPath,
            binding.RootIdentityFingerprint, snapshot.StableSourceIdentity);
        if (current.Root.IdentityFingerprint != binding.RootIdentityFingerprint ||
            RandomAccess.GetLength(current.Handle) != snapshot.ByteLength ||
            File.GetLastWriteTimeUtc(current.Handle) != snapshot.LastWriteAtUtc)
            throw new SourceSnapshotChangedException("The current source path differs from its held capture.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Input.DisposeAsync().ConfigureAwait(false); }
        finally { Source.Dispose(); }
    }
}
