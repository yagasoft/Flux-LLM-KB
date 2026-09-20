using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Application.Documents;

public enum DocumentOcrModelRole
{
    TextDetection,
    EnglishRecognition,
    ArabicRecognition,
    PageOrientation,
    LineOrientation,
    Layout,
    Tables
}

public interface IDocumentOcrModelResolver
{
    ValueTask<DocumentOcrModelResolution> ResolveAsync(CancellationToken cancellationToken);
}

public sealed record DocumentOcrModelRoleResolution(
    DocumentOcrModelRole Role,
    string ReasonCode,
    string? BundleFingerprint,
    IReadOnlyList<ModelArtifactObservation> Files,
    string? ReceiptLocation);

public sealed record DocumentOcrModelResolution(
    bool Succeeded,
    string ReasonCode,
    IReadOnlyList<DocumentOcrModelRoleResolution> Roles,
    DocumentOcrModelLease? Lease);

public sealed class DocumentOcrModelLease : IDisposable
{
    private readonly IReadOnlyDictionary<DocumentOcrModelRole, VerifiedLocalModelLease> _leases;
    private bool _disposed;

    internal DocumentOcrModelLease(IReadOnlyDictionary<DocumentOcrModelRole, VerifiedLocalModelLease> leases)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    public IReadOnlyList<DocumentOcrModelRole> Roles => _leases.Keys.Order().ToArray();

    public VerifiedLocalModelLease GetLease(DocumentOcrModelRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _leases.TryGetValue(role, out var lease)
            ? lease
            : throw new KeyNotFoundException($"No verified OCR model lease exists for '{role}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases.Values) lease.Dispose();
    }
}
