using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Runs bounded retained-only archive replay passes; disabled processors are inert.</summary>
public sealed class RetainedProcessorActivationService
{
    private readonly SourceCapabilityService _capabilityService;
    private readonly IRetainedProcessorBranchStore _branchStore;
    private readonly IRetainedSourceReader _retainedSourceReader;
    private readonly ZipArchiveRetainedProcessor _zipProcessor;
    private readonly TarArchiveRetainedProcessor? _tarProcessor;
    private readonly OoxmlStructuralTextProcessor? _ooxmlProcessor;
    private readonly RetainedCsharpCodeProcessor? _csharpProcessor;
    private readonly MediaMetadataRetainedProcessor? _mediaMetadataProcessor;
    private readonly RetainedProcessorOptions _options;
    private readonly IStatusEventPublisher? _statusEvents;

    private int EffectiveAutomaticReplayBatchSize => Math.Min(
        RetainedProcessorOptions.MaximumAutomaticReplayBatchSize,
        _options.AutomaticReplayBatchSize);

    private int EffectiveCsharpReplayBatchSize => Math.Min(
        RetainedCsharpCodeProcessor.MaximumClaimBatchSize,
        EffectiveAutomaticReplayBatchSize);

    public RetainedProcessorActivationService(
        SourceCapabilityService capabilityService,
        IRetainedProcessorBranchStore branchStore,
        IRetainedSourceReader retainedSourceReader,
        ZipArchiveRetainedProcessor zipProcessor,
        RetainedProcessorOptions options,
        TimeProvider timeProvider,
        TarArchiveRetainedProcessor? tarProcessor = null,
        IStatusEventPublisher? statusEvents = null,
        OoxmlStructuralTextProcessor? ooxmlProcessor = null,
        RetainedCsharpCodeProcessor? csharpProcessor = null,
        MediaMetadataRetainedProcessor? mediaMetadataProcessor = null)
    {
        _ = timeProvider;
        _capabilityService = capabilityService;
        _branchStore = branchStore;
        _retainedSourceReader = retainedSourceReader;
        _zipProcessor = zipProcessor;
        _tarProcessor = tarProcessor;
        _ooxmlProcessor = ooxmlProcessor;
        _csharpProcessor = csharpProcessor;
        _mediaMetadataProcessor = mediaMetadataProcessor;
        _options = options;
        _statusEvents = statusEvents;
    }

    public async ValueTask<RetainedProcessorActivationResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var runs = new List<RetainedProcessorActivationResult>();
        var reconciledForceRequests = await _branchStore.ReconcileForceRequestsAsync(
            _options.OoxmlDocumentStructuralExtractEnabled, cancellationToken).ConfigureAwait(false);
        if (reconciledForceRequests != 0 && _statusEvents is not null)
        {
            for (var transition = 0; transition < reconciledForceRequests; transition++)
            {
                await _statusEvents.PublishAsync(new StatusChanged(null, "source", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
        }
        var legacyDesignations = await DesignateLegacyOfficeAsync(cancellationToken).ConfigureAwait(false);
        if (legacyDesignations != 0)
        {
            runs.Add(new RetainedProcessorActivationResult("document-office-legacy-structural-extract", legacyDesignations, 0, 0, 0, false));
        }
        if (_options.OoxmlDocumentStructuralExtractEnabled)
        {
            var ooxml = _ooxmlProcessor ?? throw new InvalidOperationException("The explicitly enabled OOXML processor is not registered.");
            runs.Add(await RunProcessorAsync(OoxmlStructuralTextProcessor.Capability, OoxmlStructuralTextProcessor.IsLikelyOoxml,
                ooxml.ProcessAsync, "ooxml", cancellationToken).ConfigureAwait(false));
        }
        if (_options.MediaMetadataEnabled)
        {
            var media = _mediaMetadataProcessor ?? throw new InvalidOperationException("The explicitly enabled media metadata processor is not registered.");
            await BlockRecognisedUnsupportedMediaAsync(cancellationToken).ConfigureAwait(false);
            var preflight = media.Preflight(cancellationToken);
            if (!preflight.IsAvailable)
            {
                await DeferUnavailableMediaMetadataAsync("media-metadata-parser-unavailable", cancellationToken).ConfigureAwait(false);
                runs.Add(new RetainedProcessorActivationResult(MediaMetadataRetainedProcessor.Capability.ProcessorKind, 0, 0, 0, 0, false));
            }
            else
            {
                runs.Add(await RunProcessorAsync(MediaMetadataRetainedProcessor.Capability,
                    static (candidate, bytes) => MediaMetadataRetainedProcessor.HasMatchingSupportedSignature(candidate.Extension, bytes, out _),
                    (claim, retained, _, token) => media.ProcessAsync(claim, retained, token), "media-metadata", cancellationToken).ConfigureAwait(false));
            }
        }
        if (_options.CsharpCodeEnabled)
        {
            runs.Add(await RunCsharpProcessorAsync(cancellationToken).ConfigureAwait(false));
        }
        if (_options.ArchiveZipExpandEnabled)
        {
            runs.Add(await RunProcessorAsync(ZipArchiveRetainedProcessor.Capability, static (_, bytes) => ZipArchiveRetainedProcessor.IsZipSignature(bytes),
                _zipProcessor.ProcessAsync, "zip", cancellationToken).ConfigureAwait(false));
        }
        if (_options.ArchiveTarExpandEnabled)
        {
            var tar = _tarProcessor ?? throw new InvalidOperationException("The explicitly enabled TAR processor is not registered.");
            runs.Add(await RunProcessorAsync(TarArchiveRetainedProcessor.Capability, static (_, bytes) => TarArchiveRetainedProcessor.IsTarSignature(bytes),
                tar.ProcessAsync, "tar", cancellationToken).ConfigureAwait(false));
        }
        var effectiveRuns = runs.Where(static result => result.Enabled ||
            result.PromotedBranches + result.ClaimedBranches + result.CompletedBranches + result.FailedBranches > 0).ToArray();
        if (effectiveRuns.Length == 0) return RetainedProcessorActivationResult.Disabled;
        var result = effectiveRuns.Length == 1 ? effectiveRuns[0] : new RetainedProcessorActivationResult("retained-archives", effectiveRuns.Sum(result => result.PromotedBranches),
            effectiveRuns.Sum(result => result.ClaimedBranches), effectiveRuns.Sum(result => result.CompletedBranches),
            effectiveRuns.Sum(result => result.FailedBranches), true);
        if (result.PromotedBranches + result.ClaimedBranches + result.CompletedBranches + result.FailedBranches > 0 && _statusEvents is not null)
        {
            await _statusEvents.PublishAsync(new StatusChanged(null, "source", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Designation is always local and source-neutral, but never registers, promotes or
    /// claims a legacy parser capability. The CFB probe receives only checksum-verified
    /// bytes from the immutable retained-artifact binding.
    /// </summary>
    private async ValueTask<int> DesignateLegacyOfficeAsync(CancellationToken cancellationToken)
    {
        var designated = 0;
        var candidates = await _branchStore.ReadLegacyOfficeDesignationCandidatesAsync(
            EffectiveAutomaticReplayBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            try
            {
                var inspection = await _retainedSourceReader.InspectAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(inspection.ContentSha256, candidate.InputSha256, StringComparison.Ordinal)) continue;
                var retained = await _retainedSourceReader.ReadBytesAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(retained.ContentSha256, candidate.InputSha256, StringComparison.Ordinal) ||
                    !IsLegacyOfficeCompoundFile(candidate.Extension, retained.Bytes)) continue;
                if (await _branchStore.DesignateLegacyOfficeAsync(candidate, cancellationToken).ConfigureAwait(false)) designated++;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or UnauthorizedAccessException)
            {
                // A legacy parser-unavailable designation must never override retained-artifact integrity evidence.
            }
        }
        return designated;
    }

    private static bool IsLegacyOfficeCompoundFile(string extension, ReadOnlySpan<byte> bytes) =>
        (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)) &&
         bytes.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 });

    private async ValueTask DeferUnavailableMediaMetadataAsync(string outcomeCode, CancellationToken cancellationToken)
    {
        var candidates = await _branchStore.ReadPromotionCandidatesAsync(
            EffectiveAutomaticReplayBatchSize, MediaMetadataRetainedProcessor.Capability, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            try
            {
                var inspection = await _retainedSourceReader.InspectAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(inspection.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (inspection.ByteLength > MediaMetadataRetainedProcessor.MaximumInputBytes)
                {
                    await _branchStore.BlockPromotionAsync(candidate, "media-metadata-input-too-large", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var retained = await _retainedSourceReader.ReadBytesAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (retained.SourceRevisionId != candidate.SourceRevisionId ||
                    !string.Equals(retained.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!MediaMetadataRetainedProcessor.HasMatchingSupportedSignature(candidate.Extension, retained.Bytes, out var signatureOutcome))
                {
                    await _branchStore.BlockPromotionAsync(candidate, signatureOutcome!, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await _branchStore.DeferPromotionAsync(candidate, outcomeCode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-missing", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask BlockRecognisedUnsupportedMediaAsync(CancellationToken cancellationToken)
    {
        var candidates = await _branchStore.ReadRecognisedUnsupportedMediaCandidatesAsync(
            EffectiveAutomaticReplayBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            try
            {
                var inspection = await _retainedSourceReader.InspectAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(inspection.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (inspection.ByteLength > MediaMetadataRetainedProcessor.MaximumInputBytes)
                {
                    await _branchStore.BlockPromotionAsync(candidate, "media-metadata-input-too-large", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var retained = await _retainedSourceReader.ReadBytesAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (retained.SourceRevisionId != candidate.SourceRevisionId ||
                    !string.Equals(retained.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _ = MediaMetadataRetainedProcessor.HasMatchingSupportedSignature(candidate.Extension, retained.Bytes, out var outcomeCode);
                await _branchStore.BlockPromotionAsync(candidate, outcomeCode!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-missing", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<RetainedProcessorActivationResult> RunProcessorAsync(
        SourceCapabilityDescriptor descriptor,
        Func<RetainedProcessorPromotionCandidate, ReadOnlySpan<byte>, bool> hasSignature,
        Func<RetainedProcessorClaim, RetainedSourceBytes, RetainedProcessorOptions, CancellationToken, ValueTask<RetainedProcessorCompletion>> process,
        string ownerKind,
        CancellationToken cancellationToken)
    {
        var capability = await _capabilityService.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (!capability.IsRunnable)
        {
            return new RetainedProcessorActivationResult(descriptor.ProcessorKind, 0, 0, 0, 0, false);
        }

        var promoted = 0;
        var candidates = await _branchStore.ReadPromotionCandidatesAsync(EffectiveAutomaticReplayBatchSize, descriptor, cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            RetainedArtifactInspection inspection;
            try
            {
                inspection = await _retainedSourceReader.InspectAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-missing", cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!string.Equals(inspection.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (descriptor.Id == MediaMetadataRetainedProcessor.Capability.Id &&
                inspection.ByteLength > MediaMetadataRetainedProcessor.MaximumInputBytes)
            {
                await _branchStore.BlockPromotionAsync(candidate, "media-metadata-input-too-large", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (descriptor.Id == OoxmlStructuralTextProcessor.Capability.Id && inspection.ByteLength > 128L * 1024 * 1024)
            {
                if (await _branchStore.PromoteAsync(candidate, descriptor, cancellationToken).ConfigureAwait(false))
                {
                    promoted++;
                }
                continue;
            }
            RetainedSourceBytes retained;
            try
            {
                retained = await _retainedSourceReader.ReadBytesAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-missing", cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (descriptor.Id == MediaMetadataRetainedProcessor.Capability.Id &&
                !MediaMetadataRetainedProcessor.HasMatchingSupportedSignature(candidate.Extension, retained.Bytes, out var mediaSignatureOutcome))
            {
                await _branchStore.BlockPromotionAsync(candidate, mediaSignatureOutcome!, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (hasSignature(candidate, retained.Bytes) &&
                await _branchStore.PromoteAsync(candidate, descriptor, cancellationToken).ConfigureAwait(false))
            {
                promoted++;
            }
        }

        var owner = $"retained-{ownerKind}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var forceClaims = descriptor.Id == OoxmlStructuralTextProcessor.Capability.Id
            ? await _branchStore.ClaimForceAsync(owner, EffectiveAutomaticReplayBatchSize, descriptor.ProcessorFingerprint, cancellationToken)
                .ConfigureAwait(false)
            : [];
        var remainingClaims = Math.Max(0, EffectiveAutomaticReplayBatchSize - forceClaims.Count);
        var ordinaryClaims = remainingClaims == 0
            ? []
            : await _branchStore.ClaimAsync(owner, remainingClaims, descriptor.ProcessorFingerprint, cancellationToken)
                .ConfigureAwait(false);
        var claims = forceClaims.Concat(ordinaryClaims).ToArray();
        var completed = 0;
        var failed = 0;
        foreach (var claim in claims)
        {
            try
            {
                var inspection = await _retainedSourceReader.InspectAsync(claim.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(inspection.ContentSha256, claim.InputSha256, StringComparison.Ordinal))
                    throw new RetainedProcessorException("retained-artifact-checksum-invalid");
                if (descriptor.Id == MediaMetadataRetainedProcessor.Capability.Id &&
                    inspection.ByteLength > MediaMetadataRetainedProcessor.MaximumInputBytes)
                {
                    throw new RetainedProcessorException("media-metadata-input-too-large");
                }
                if (descriptor.Id == OoxmlStructuralTextProcessor.Capability.Id && inspection.ByteLength > 128L * 1024 * 1024)
                    throw new RetainedProcessorException("office-document-input-too-large");
                var retained = await _retainedSourceReader.ReadBytesAsync(claim.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                var completion = await process(claim, retained, _options, cancellationToken).ConfigureAwait(false);
                if (await _branchStore.CommitAsync(claim, completion, cancellationToken).ConfigureAwait(false)) completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _branchStore.RetryAsync(claim, "processor-cancelled", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (IOException exception) when (exception is not RetainedProcessorException and not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (await _branchStore.RetryAsync(claim, "retained-artifact-transient", cancellationToken).ConfigureAwait(false)) failed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var failure = exception is RetainedProcessorException retainedException
                    ? new RetainedProcessorFailure(retainedException.OutcomeCode, retainedException.MemberOutcomes)
                    : exception is FileNotFoundException or DirectoryNotFoundException
                        ? new RetainedProcessorFailure("retained-artifact-missing", [])
                        : exception is InvalidDataException
                            ? new RetainedProcessorFailure("retained-artifact-checksum-invalid", [])
                            : new RetainedProcessorFailure("retained-artifact-path-invalid", []);
                if (await _branchStore.FailAsync(claim, failure, cancellationToken).ConfigureAwait(false)) failed++;
            }
        }
        return new RetainedProcessorActivationResult(descriptor.ProcessorKind, promoted, claims.Length, completed, failed, true);
    }

    private async ValueTask<RetainedProcessorActivationResult> RunCsharpProcessorAsync(
        CancellationToken cancellationToken)
    {
        if (!await _branchStore.IsRetainedCsharpCodeWriterReadyAsync(cancellationToken).ConfigureAwait(false) ||
            _csharpProcessor is null ||
            !_capabilityService.TryResolveLocalHandler(RetainedCsharpCodeProcessor.Capability.Id, out var registeredHandler) ||
            !LocalSourceCapabilityHandlerRegistry.SameDescriptor(registeredHandler, RetainedCsharpCodeProcessor.Capability))
        {
            return new RetainedProcessorActivationResult(RetainedCsharpCodeProcessor.ProcessorKind, 0, 0, 0, 0, false);
        }
        if (!_csharpProcessor.Preflight().IsAvailable)
        {
            return new RetainedProcessorActivationResult(RetainedCsharpCodeProcessor.ProcessorKind, 0, 0, 0, 0, false);
        }
        var capability = await _capabilityService.RegisterAsync(RetainedCsharpCodeProcessor.Capability, cancellationToken).ConfigureAwait(false);
        if (!capability.IsRunnable)
        {
            return new RetainedProcessorActivationResult(RetainedCsharpCodeProcessor.ProcessorKind, 0, 0, 0, 0, false);
        }
        var promoted = 0;
        var candidates = await _branchStore.ReadPromotionCandidatesAsync(EffectiveCsharpReplayBatchSize, RetainedCsharpCodeProcessor.Capability, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            try
            {
                var inspection = await _retainedSourceReader.InspectAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(inspection.ContentSha256, candidate.InputSha256, StringComparison.Ordinal))
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var retained = await _retainedSourceReader.ReadBytesAsync(candidate.SourceRevisionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(retained.ContentSha256, candidate.InputSha256, StringComparison.Ordinal) || retained.SourceRevisionId != candidate.SourceRevisionId)
                {
                    await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (await _branchStore.PromoteAsync(candidate, RetainedCsharpCodeProcessor.Capability, cancellationToken).ConfigureAwait(false)) promoted++;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-missing", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
            {
                await _branchStore.BlockPromotionAsync(candidate, "retained-artifact-checksum-invalid", cancellationToken).ConfigureAwait(false);
            }
        }

        var owner = $"retained-csharp:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var claims = await _branchStore.ClaimCsharpCodeAsync(owner, EffectiveCsharpReplayBatchSize, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, cancellationToken).ConfigureAwait(false);
        var completed = 0;
        var failed = 0;
        foreach (var claim in claims)
        {
            try
            {
                var completion = await _csharpProcessor.ProcessAsync(claim, cancellationToken).ConfigureAwait(false);
                if (completion.OutcomeCode is "success" or "csharp-code-syntax-invalid")
                {
                    if ((await _branchStore.CompleteRetainedCsharpCodeAsync(claim, completion, cancellationToken).ConfigureAwait(false)).IsCommitted) completed++;
                }
                else if (await _branchStore.FailAsync(new RetainedProcessorClaim(claim.BranchId, claim.SourceRevisionId, claim.ParentStableIdentity, claim.InputSha256, claim.LeaseOwner, claim.LeaseGeneration, claim.LeaseExpiresAtUtc), new RetainedProcessorFailure(completion.OutcomeCode, []), cancellationToken).ConfigureAwait(false))
                {
                    failed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _branchStore.RetryAsync(new RetainedProcessorClaim(claim.BranchId, claim.SourceRevisionId, claim.ParentStableIdentity, claim.InputSha256, claim.LeaseOwner, claim.LeaseGeneration, claim.LeaseExpiresAtUtc), "processor-cancelled", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (IOException)
            {
                if (await _branchStore.RetryAsync(new RetainedProcessorClaim(claim.BranchId, claim.SourceRevisionId, claim.ParentStableIdentity, claim.InputSha256, claim.LeaseOwner, claim.LeaseGeneration, claim.LeaseExpiresAtUtc), "retained-artifact-transient", cancellationToken).ConfigureAwait(false)) failed++;
            }
        }
        return new RetainedProcessorActivationResult(RetainedCsharpCodeProcessor.ProcessorKind, promoted, claims.Count, completed, failed, true);
    }
}

public sealed record RetainedProcessorActivationResult(
    string Capability,
    int PromotedBranches,
    int ClaimedBranches,
    int CompletedBranches,
    int FailedBranches,
    bool Enabled)
{
    public static readonly RetainedProcessorActivationResult Disabled = new("archive-zip-expand", 0, 0, 0, 0, false);
}
