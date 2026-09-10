using System.Security.Cryptography;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Models;

public sealed class LocalModelStore : ILocalModelStore
{
    public const string VerifierVersion = "flux-offline-model-gate/1";

    private readonly Func<IModelVerificationFiles> _openFiles;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _nextReceiptId;

    public LocalModelStore(
        Func<IModelVerificationFiles> openFiles,
        TimeProvider? timeProvider = null,
        Func<Guid>? nextReceiptId = null)
    {
        _openFiles = openFiles ?? throw new ArgumentNullException(nameof(openFiles));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextReceiptId = nextReceiptId ?? Guid.NewGuid;
    }

    public ValueTask<ModelResolutionResult> ResolveAsync(
        ModelBundleSpecification specification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();
        ModelBundleSpecification validated;
        string fingerprint;
        try
        {
            validated = ModelManifestCodec.Validate(specification);
            fingerprint = ModelManifestCodec.Fingerprint(validated);
        }
        catch (ModelManifestException exception)
        {
            return ValueTask.FromResult(Refusal(exception.ReasonCode, null, [], receiptPersisted: false, null));
        }

        return ResolveValidatedAsync(validated, fingerprint, cancellationToken);
    }

    private async ValueTask<ModelResolutionResult> ResolveValidatedAsync(
        ModelBundleSpecification specification,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        IModelVerificationFiles? files = null;
        var heldFiles = new Dictionary<string, IModelVerificationFile>(StringComparer.Ordinal);
        var observations = new List<ModelArtifactObservation>(specification.Files.Count);
        long verifiedBytes = 0;
        try
        {
            files = _openFiles();
            foreach (var specificationFile in specification.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var opened = await files.OpenReadAsync(specificationFile, cancellationToken).ConfigureAwait(false);
                if (opened.File is null)
                {
                    observations.Add(new ModelArtifactObservation(specificationFile.Filename, opened.ReasonCode, null, null));
                    return await PersistRefusalAsync(
                        files,
                        fingerprint,
                        observations,
                        opened.ReasonCode,
                        verifiedBytes,
                        cancellationToken).ConfigureAwait(false);
                }

                var openedFile = opened.File;
                IModelVerificationFile? file = openedFile;
                string? refusalReason = null;
                ModelArtifactObservation? observation = null;
                try
                {
                    var verification = await VerifyAsync(openedFile, specificationFile, cancellationToken).ConfigureAwait(false);
                    observation = verification.Observation;
                    if (verification.ReasonCode != ModelStoreReasons.BundleVerified)
                    {
                        refusalReason = verification.ReasonCode;
                    }
                    else
                    {
                        verifiedBytes = checked(verifiedBytes + openedFile.ByteLength);
                        heldFiles.Add(specificationFile.Filename, openedFile);
                        file = null;
                    }
                }
                catch (IOException)
                {
                    refusalReason = ModelStoreReasons.ArtifactReadFailed;
                    observation = new ModelArtifactObservation(
                        specificationFile.Filename,
                        ModelStoreReasons.ArtifactReadFailed,
                        openedFile.ByteLength,
                        null);
                }
                finally
                {
                    file?.Dispose();
                }

                observations.Add(observation!);
                if (refusalReason is not null)
                {
                    return await PersistRefusalAsync(
                        files,
                        fingerprint,
                        observations,
                        refusalReason,
                        verifiedBytes,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var receipt = CreateReceipt(
                fingerprint,
                verified: true,
                ModelStoreReasons.BundleVerified,
                observations,
                verifiedBytes);
            var persisted = await files.PersistReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            if (!persisted.Persisted)
            {
                return Refusal(
                    persisted.ReasonCode ?? ModelStoreReasons.ReceiptFailed,
                    fingerprint,
                    observations,
                    receiptPersisted: false,
                    null);
            }

            var lease = new VerifiedLocalModelLease(heldFiles, files);
            heldFiles = null!;
            files = null;
            return new ModelResolutionResult(
                true,
                ModelStoreReasons.BundleVerified,
                fingerprint,
                observations.AsReadOnly(),
                true,
                persisted.ReceiptLocation,
                lease);
        }
        catch (ModelVerificationFilesException exception)
        {
            return Refusal(exception.ReasonCode, fingerprint, observations, receiptPersisted: false, null);
        }
        finally
        {
            if (heldFiles is not null)
            {
                foreach (var file in heldFiles.Values) file.Dispose();
            }

            files?.Dispose();
        }
    }

    private async ValueTask<ModelResolutionResult> PersistRefusalAsync(
        IModelVerificationFiles files,
        string fingerprint,
        IReadOnlyList<ModelArtifactObservation> observations,
        string reasonCode,
        long verifiedBytes,
        CancellationToken cancellationToken)
    {
        var receipt = CreateReceipt(fingerprint, verified: false, reasonCode, observations, verifiedBytes);
        try
        {
            var persisted = await files.PersistReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            return Refusal(reasonCode, fingerprint, observations, persisted.Persisted, persisted.ReceiptLocation);
        }
        catch (ModelVerificationFilesException)
        {
            return Refusal(reasonCode, fingerprint, observations, receiptPersisted: false, null);
        }
    }

    private ModelVerificationReceipt CreateReceipt(
        string fingerprint,
        bool verified,
        string reasonCode,
        IReadOnlyList<ModelArtifactObservation> observations,
        long verifiedBytes) =>
        new(
            _nextReceiptId(),
            VerifierVersion,
            fingerprint,
            _timeProvider.GetUtcNow(),
            verified,
            reasonCode,
            observations.ToArray(),
            verifiedBytes,
            0);

    private static async ValueTask<(string ReasonCode, ModelArtifactObservation Observation)> VerifyAsync(
        IModelVerificationFile file,
        ModelArtifactSpecification specification,
        CancellationToken cancellationToken)
    {
        if (file.ByteLength != specification.ByteLength)
        {
            return (ModelStoreReasons.ArtifactLengthMismatch,
                new ModelArtifactObservation(specification.Filename, ModelStoreReasons.ArtifactLengthMismatch, file.ByteLength, null));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < file.ByteLength)
        {
            var count = (int)Math.Min(buffer.Length, file.ByteLength - offset);
            var read = await file.ReadAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read != count)
            {
                return (ModelStoreReasons.ArtifactLengthMismatch,
                    new ModelArtifactObservation(specification.Filename, ModelStoreReasons.ArtifactLengthMismatch, file.ByteLength, null));
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        var observedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(observedSha256, specification.Sha256, StringComparison.Ordinal))
        {
            return (ModelStoreReasons.ArtifactSha256Mismatch,
                new ModelArtifactObservation(specification.Filename, ModelStoreReasons.ArtifactSha256Mismatch, file.ByteLength, observedSha256));
        }

        return (ModelStoreReasons.BundleVerified,
            new ModelArtifactObservation(specification.Filename, ModelStoreReasons.BundleVerified, file.ByteLength, observedSha256));
    }

    private static ModelResolutionResult Refusal(
        string reasonCode,
        string? fingerprint,
        IReadOnlyList<ModelArtifactObservation> observations,
        bool receiptPersisted,
        string? receiptLocation) =>
        new(false, reasonCode, fingerprint, observations.ToArray(), receiptPersisted, receiptLocation, null);
}
