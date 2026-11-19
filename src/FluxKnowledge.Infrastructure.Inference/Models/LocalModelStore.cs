using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Models;

public sealed class LocalModelStore : ILocalModelStore
{
    public const string VerifierVersion = "flux-offline-model-metadata/2";

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
                    var verification = ObserveMetadata(openedFile, specificationFile);
                    observation = verification.Observation;
                    if (verification.ReasonCode != ModelStoreReasons.BundleAvailable)
                    {
                        refusalReason = verification.ReasonCode;
                    }
                    else
                    {
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
                        null,
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
                verified: false,
                ModelStoreReasons.BundleAvailable,
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
                ModelStoreReasons.BundleAvailable,
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

    private static (string ReasonCode, ModelArtifactObservation Observation) ObserveMetadata(
        IModelVerificationFile file,
        ModelArtifactSpecification specification)
    {
        if (file.ByteLength != specification.ByteLength)
        {
            return (ModelStoreReasons.ArtifactLengthMismatch,
                new ModelArtifactObservation(specification.Filename, ModelStoreReasons.ArtifactLengthMismatch, file.ByteLength, null));
        }

        // The manifest selects an offline local artifact. Ordinary startup checks
        // availability and length through the held file; it does not hash weights.
        return (ModelStoreReasons.BundleAvailable,
            new ModelArtifactObservation(specification.Filename, ModelStoreReasons.BundleAvailable, file.ByteLength, null));
    }

    private static ModelResolutionResult Refusal(
        string reasonCode,
        string? fingerprint,
        IReadOnlyList<ModelArtifactObservation> observations,
        bool receiptPersisted,
        string? receiptLocation) =>
        new(false, reasonCode, fingerprint, observations.ToArray(), receiptPersisted, receiptLocation, null);
}
