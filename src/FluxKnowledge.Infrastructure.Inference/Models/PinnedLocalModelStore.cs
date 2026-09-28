using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Models;

/// <summary>Reuses a verified bundle only while its exact files and ancestors stay
/// protected by the original lease. Native model sessions still have per-work ownership.</summary>
public sealed class PinnedLocalModelStore(ILocalModelStore inner) : ILocalModelStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ModelResolutionResult> _verified = new(StringComparer.Ordinal);
    private bool _disposed;

    public async ValueTask<ModelResolutionResult> ResolveAsync(
        ModelBundleSpecification specification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();
        string fingerprint;
        try { fingerprint = ModelManifestCodec.Fingerprint(specification); }
        catch (ModelManifestException) { return await inner.ResolveAsync(specification, cancellationToken).ConfigureAwait(false); }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_verified.TryGetValue(fingerprint, out var cached))
                return cached with { Lease = cached.Lease!.Retain() };

            var resolved = await inner.ResolveAsync(specification, cancellationToken).ConfigureAwait(false);
            if (!resolved.Succeeded || !resolved.ReceiptPersisted || resolved.Lease is null ||
                resolved.BundleFingerprint != fingerprint)
                return resolved;
            _verified.Add(fingerprint, resolved);
            return resolved with { Lease = resolved.Lease.Retain() };
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var result in _verified.Values) result.Lease!.Dispose();
            _verified.Clear();
        }
        finally { _gate.Release(); }
    }
}
