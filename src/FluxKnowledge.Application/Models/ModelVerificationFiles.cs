namespace FluxKnowledge.Application.Models;

public interface IModelVerificationFiles : IDisposable
{
    ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(
        ModelArtifactSpecification specification,
        CancellationToken cancellationToken);

    ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(
        ModelVerificationReceipt receipt,
        CancellationToken cancellationToken);
}

public interface IModelVerificationFile : IDisposable
{
    long ByteLength { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);
}

public sealed record ModelVerificationFileOpenResult(string ReasonCode, IModelVerificationFile? File)
{
    public static ModelVerificationFileOpenResult Found(IModelVerificationFile file) =>
        new(ModelStoreReasons.BundleVerified, file ?? throw new ArgumentNullException(nameof(file)));

    public static ModelVerificationFileOpenResult Refused(string reasonCode) => new(reasonCode, null);
}

public sealed record ModelVerificationReceipt(
    Guid ReceiptId,
    string VerifierVersion,
    string BundleFingerprint,
    DateTimeOffset ObservedAtUtc,
    bool Verified,
    string ReasonCode,
    IReadOnlyList<ModelArtifactObservation> Files,
    long VerifiedBytes,
    long TransferredBytes);

public sealed record ModelReceiptPersistenceResult(
    bool Persisted,
    string? ReceiptLocation,
    string? ReasonCode)
{
    public static ModelReceiptPersistenceResult Published(string receiptLocation) => new(true, receiptLocation, null);

    public static ModelReceiptPersistenceResult Refused(string reasonCode) => new(false, null, reasonCode);
}

public sealed class ModelVerificationFilesException : IOException
{
    public ModelVerificationFilesException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
