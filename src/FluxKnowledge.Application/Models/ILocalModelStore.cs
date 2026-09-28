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
    public const string BundleLeaseUnavailable = "model-bundle-lease-unavailable";
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
    private readonly SharedFiles _shared;
    private bool _disposed;

    internal VerifiedLocalModelLease(
        IReadOnlyDictionary<string, IModelVerificationFile> files,
        IDisposable owner)
    {
        _shared = new SharedFiles(files, owner);
    }

    private VerifiedLocalModelLease(SharedFiles shared) => _shared = shared;

    /// <summary>Retains the same verified, protected file handles for another native
    /// session. The files and their protected ancestors close after the last lease.</summary>
    public VerifiedLocalModelLease Retain()
    {
        lock (_shared.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shared.ReferenceCount++;
            return new VerifiedLocalModelLease(_shared);
        }
    }

    public IReadOnlyList<VerifiedModelFile> Files =>
        _shared.Files.Select(static item => new VerifiedModelFile(item.Key, item.Value.ByteLength)).ToArray();

    /// <summary>Only for local native loaders. Keep this lease alive until the loader/session
    /// is disposed; a returned path has no protection after lease disposal.</summary>
    public string GetVerifiedLocalPath(string filename)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (!_shared.Files.TryGetValue(filename, out var file)) throw new KeyNotFoundException(filename);
        return file.ProtectedLocalPath ?? throw new ModelVerificationFilesException(ModelStoreReasons.PathUnsafe);
    }

    public ValueTask<int> ReadAsync(
        string filename,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (offset < 0 || !_shared.Files.TryGetValue(filename, out var file))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return file.ReadAsync(offset, buffer, cancellationToken);
    }

    public void Dispose()
    {
        lock (_shared.Sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (--_shared.ReferenceCount != 0) return;
        }
        try { foreach (var file in _shared.Files.Values) file.Dispose(); }
        finally { _shared.Owner.Dispose(); }
    }

    private sealed class SharedFiles(IReadOnlyDictionary<string, IModelVerificationFile> files, IDisposable owner)
    {
        public object Sync { get; } = new();
        public IReadOnlyDictionary<string, IModelVerificationFile> Files { get; } = files;
        public IDisposable Owner { get; } = owner;
        public int ReferenceCount = 1;
    }
}

public sealed record VerifiedModelFile(string Filename, long ByteLength);
