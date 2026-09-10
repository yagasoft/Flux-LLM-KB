namespace FluxKnowledge.Application.Models;

public interface ILocalModelStore
{
    ValueTask<ModelResolutionResult> ResolveAsync(
        ModelBundleSpecification specification,
        CancellationToken cancellationToken);
}

public static class ModelStoreReasons
{
    public const string BundleVerified = "model-bundle-verified";
    public const string SpecificationInvalid = "model-specification-invalid";
    public const string PathUnsafe = "model-path-unsafe";
    public const string StoreUnavailable = "model-store-unavailable";
    public const string StoreNotWritable = "model-store-not-writable";
    public const string ArtifactMissing = "model-artifact-missing";
    public const string ArtifactLengthMismatch = "model-artifact-length-mismatch";
    public const string ArtifactSha256Mismatch = "model-artifact-sha256-mismatch";
    public const string ArtifactInUse = "model-artifact-in-use";
    public const string ArtifactReadFailed = "model-artifact-read-failed";
    public const string ReceiptFailed = "model-store-receipt-failed";
    public const string StoreIoError = "model-store-io-error";
}

public sealed class ModelManifestException : IOException
{
    public ModelManifestException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed record ModelArtifactObservation(
    string Filename,
    string ReasonCode,
    long? ObservedByteLength,
    string? ObservedSha256);

public sealed record ModelResolutionResult(
    bool Succeeded,
    string ReasonCode,
    string? BundleFingerprint,
    IReadOnlyList<ModelArtifactObservation> Files,
    bool ReceiptPersisted,
    string? ReceiptLocation,
    VerifiedLocalModelLease? Lease);

public sealed class VerifiedLocalModelLease : IDisposable
{
    private readonly IReadOnlyDictionary<string, IModelVerificationFile> _files;
    private readonly IDisposable _owner;
    private bool _disposed;

    internal VerifiedLocalModelLease(
        IReadOnlyDictionary<string, IModelVerificationFile> files,
        IDisposable owner)
    {
        _files = files;
        _owner = owner;
    }

    public IReadOnlyList<VerifiedModelFile> Files =>
        _files.Select(static item => new VerifiedModelFile(item.Key, item.Value.ByteLength)).ToArray();

    public ValueTask<int> ReadAsync(
        string filename,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (offset < 0 || !_files.TryGetValue(filename, out var file))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return file.ReadAsync(offset, buffer, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var file in _files.Values) file.Dispose();
        _owner.Dispose();
    }
}

public sealed record VerifiedModelFile(string Filename, long ByteLength);
