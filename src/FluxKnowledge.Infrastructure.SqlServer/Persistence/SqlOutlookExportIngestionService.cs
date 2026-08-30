using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Owns the complete SQL-authoritative transition from one promoted private ready export to
/// immutable source work. Filesystem promotion and retained-file copies are deliberately not
/// claimed as part of the SQL transaction; a failed SQL commit leaves the ready export retryable.
/// </summary>
public sealed class SqlOutlookExportIngestionService(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider? timeProvider = null,
    PersistedOutlookSpoolRootPolicy? outlookSpoolPolicy = null)
{
    private const string TextProcessorVersion = "phase-3a-v1";
    private const string DeferredContentProcessorVersion = "phase-4-outlook-content-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<OutlookExportCommitReceipt> IngestReadyAsync(
        string spoolRoot,
        Guid exportId,
        CancellationToken cancellationToken)
    {
        var canonicalSpoolRoot = outlookSpoolPolicy?.RequireCanonicalBeforeIo(spoolRoot)
            ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
        var envelope = await new OutlookSpoolLayout(canonicalSpoolRoot)
            .ReadReadyRecoveryEnvelopeAsync(exportId, cancellationToken).ConfigureAwait(false);
        OutlookExportCommitRequest request;
        try
        {
            request = envelope.Manifest.Recovery.ToCommitRequest(exportId, envelope.ManifestHash);
        }
        catch (ArgumentException)
        {
            return await CommitMalformedRecoveryAsync(
                canonicalSpoolRoot,
                exportId,
                envelope,
                cancellationToken).ConfigureAwait(false);
        }
        return await IngestReadyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OutlookExportCommitReceipt> IngestReadyAsync(
        OutlookExportCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var observation = request.Observation
            ?? throw new ArgumentException("Ready-export ingestion requires a bound Outlook observation.", nameof(request));
        observation.Validate();

        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var priorOperation = await context.OutlookCaptureOperations
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureOperations] WITH (UPDLOCK, HOLDLOCK) WHERE [OperationId] = {request.OperationId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (priorOperation is not null)
        {
            if (priorOperation.ResourceId is null)
            {
                throw new InvalidOperationException("The Outlook ingestion operation has no durable export receipt.");
            }
            var priorReceipt = await context.OutlookCaptureExports.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == priorOperation.ResourceId.Value, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Outlook ingestion operation has no durable export receipt.");
            var isUnresolvedIdentityReceipt = priorOperation.Accepted == false &&
                priorOperation.ProfileId is null &&
                priorReceipt.State == (int)OutlookExportState.Blocked &&
                string.Equals(priorReceipt.BlockedReasonCode, "ready-manifest-identity-mismatch", StringComparison.Ordinal) &&
                string.Equals(priorOperation.RequestFingerprint, observation.ManifestHash, StringComparison.Ordinal);
            if (!string.Equals(priorOperation.Kind, "ingest-ready-export", StringComparison.Ordinal) ||
                (!isUnresolvedIdentityReceipt &&
                 (!string.Equals(priorOperation.RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal) ||
                  priorOperation.ProfileId != observation.ProfileId.Value)))
            {
                throw new InvalidOperationException("The Outlook ingestion operation does not match its immutable request.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutlookExportCommitReceipt(
                new OutlookCaptureExportId(priorReceipt.Id),
                priorOperation.Accepted,
                true,
                true);
        }

        var profile = await context.OutlookCaptureProfiles
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observation.ProfileId.Value}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var folder = await context.OutlookCaptureFolders
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureFolders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observation.FolderId.Value}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null || folder is null || folder.ProfileId != profile.Id || profile.SourceRootId == Guid.Empty)
        {
            return await CommitUnresolvedIdentityAsync(
                context,
                transaction,
                request.ExportId.Value,
                request.OperationId,
                observation.ManifestHash,
                observation.EntryId,
                observation.SourceFingerprint,
                request.FencingToken,
                "ready-manifest-identity-mismatch",
                cancellationToken).ConfigureAwait(false);
        }

        var catchUpIsActive = await context.OutlookCatchUps.AnyAsync(candidate =>
                candidate.Id == request.CatchUpId &&
                candidate.ProfileId == observation.ProfileId.Value &&
                candidate.State == 1 &&
                candidate.FencingToken == request.FencingToken &&
                candidate.LeaseExpiresAtUtc != null &&
                candidate.LeaseExpiresAtUtc >= _clock.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!catchUpIsActive)
        {
            throw new OutlookReadyExportLeaseException();
        }

        OutlookExportManifest manifest;
        string readyDirectory;
        string manifestHash;
        long manifestByteLength;
        string canonicalSpoolRoot;
        try
        {
            var expectedRelativePath = Path.Combine("ready", request.ExportId.Value.ToString("N"));
            if (!string.Equals(observation.RelativeSpoolPath, expectedRelativePath, StringComparison.Ordinal))
            {
                throw new OutlookIngestionBlockedException("ready-path-invalid");
            }

            canonicalSpoolRoot = outlookSpoolPolicy?.RequireCanonicalBeforeIo(profile.SpoolRoot)
                ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
            var layout = new OutlookSpoolLayout(canonicalSpoolRoot);
            var manifestEnvelope = await layout.ReadReadyManifestEnvelopeAsync(request.ExportId.Value, cancellationToken).ConfigureAwait(false);
            manifest = manifestEnvelope.Manifest;
            readyDirectory = layout.GetReadyExportDirectory(request.ExportId.Value);
            manifestHash = manifestEnvelope.ManifestHash;
            manifestByteLength = manifestEnvelope.ByteLength;
            if (!string.Equals(manifestHash, observation.ManifestHash, StringComparison.Ordinal))
            {
                throw new OutlookIngestionBlockedException("ready-manifest-checksum-invalid");
            }
            OutlookExportCommitRequest manifestRequest;
            try
            {
                manifestRequest = manifest.Recovery.ToCommitRequest(request.ExportId.Value, manifestHash);
            }
            catch (ArgumentException exception)
            {
                throw new OutlookIngestionBlockedException("ready-manifest-recovery-invalid", exception);
            }
            if (manifestRequest != request)
            {
                throw new OutlookIngestionBlockedException("ready-manifest-identity-mismatch");
            }
        }
        catch (Exception exception) when (exception is OutlookIngestionBlockedException or OutlookReadyExportValidationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return await CommitBlockedAsync(
                context,
                transaction,
                request,
                observation,
                profile.Id,
                folder.Id,
                profile.SourceRootId,
                BlockReason(exception, "ready-manifest-invalid"),
                cancellationToken).ConfigureAwait(false);
        }

        var exportWithSameId = await context.OutlookCaptureExports
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureExports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.ExportId.Value}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var exportsWithSameEntry = await context.OutlookCaptureExports
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureExports] WITH (UPDLOCK, HOLDLOCK) WHERE [FolderId] = {folder.Id} AND [EntryIdFingerprint] = CONVERT(char(64), HASHBYTES('SHA2_256', {observation.EntryId}), 2) AND [State] <> {(int)OutlookExportState.Blocked}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var exportWithSameEntry = exportsWithSameEntry
            .SingleOrDefault(row => string.Equals(row.EntryId, observation.EntryId, StringComparison.Ordinal));
        var canonicalExport = exportWithSameId ?? exportWithSameEntry;
        if (canonicalExport is not null)
        {
            var matchesCanonicalSource = canonicalExport.State == (int)OutlookExportState.Ingested &&
                canonicalExport.ProfileId == profile.Id &&
                canonicalExport.FolderId == folder.Id &&
                string.Equals(canonicalExport.EntryId, observation.EntryId, StringComparison.Ordinal) &&
                string.Equals(canonicalExport.SourceFingerprint, observation.SourceFingerprint, StringComparison.Ordinal);
            if (!matchesCanonicalSource)
            {
                return await CommitBlockedAsync(
                    context,
                    transaction,
                    request,
                    observation,
                    profile.Id,
                    folder.Id,
                    profile.SourceRootId,
                    "source-identity-conflict",
                    cancellationToken).ConfigureAwait(false);
            }

            AddOperation(context, request, profile.Id, canonicalExport.Id, accepted: true, _clock.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutlookExportCommitReceipt(
                new OutlookCaptureExportId(canonicalExport.Id),
                true,
                true,
                true);
        }

        var sourceRoot = await context.SourceRootConfigurations
            .FromSqlInterpolated($"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {profile.SourceRootId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Outlook profile source-root binding is missing.");

        var now = _clock.GetUtcNow();
        SourceRevisionEntity parentRevision;
        try
        {
            parentRevision = CreateRevision(
                sourceRoot.Id,
                $"outlook:{request.ExportId.Value:N}",
                manifestHash,
                Path.Combine(sourceRoot.CanonicalPath, "ready", request.ExportId.Value.ToString("N"), "message.manifest"),
                parentRevisionId: null,
                "OutlookMessage",
                manifestByteLength,
                ".json",
                now,
                "message");
            context.SourceRevisions.Add(parentRevision);

            var sidecars = manifest.Attachments.Prepend(manifest.Body).ToArray();
            var activityWriter = new SqlSourceActivityWriter(_clock);
            for (var ordinal = 0; ordinal < sidecars.Length; ordinal++)
            {
                var sidecar = sidecars[ordinal];
                var kind = ordinal == 0 ? "body" : $"attachment-{ordinal}";
                var isText = IsTextContent(sidecar.ContentType);
                var isSupportedText = string.Equals(sidecar.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase);
                var retainedRelativePath = await RetainSidecarAsync(
                    canonicalSpoolRoot,
                    readyDirectory,
                    sidecar,
                    cancellationToken).ConfigureAwait(false);
                var child = CreateRevision(
                    sourceRoot.Id,
                    $"outlook:{request.ExportId.Value:N}:{kind}",
                    sidecar.ContentSha256,
                    Path.Combine(sourceRoot.CanonicalPath, "ready", request.ExportId.Value.ToString("N"), kind),
                    parentRevision.Id,
                    isText ? "AcceptedUtf8Text" : "DeferredCapability",
                    sidecar.ByteLength,
                    SafeExtension(sidecar.RelativePath),
                    now,
                    kind);
                context.SourceRevisions.Add(child);
                context.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = Guid.NewGuid(),
                    SourceRevisionId = child.Id,
                    ContentSha256 = sidecar.ContentSha256,
                    StoreRelativePath = retainedRelativePath,
                    ByteLength = sidecar.ByteLength,
                    ChecksumVerifiedAtUtc = now,
                    ReferenceCount = 1
                });
                var requiredCapability = isSupportedText ? null : RequiredCapability(sidecar.ContentType);
                await activityWriter.FindOrCreateAsync(
                    context,
                    new SourceActivityDraft(
                        new SourceRevisionId(child.Id),
                        ActivityKind(sidecar.ContentType),
                        isSupportedText ? ExecutionClass.InProcess : ExecutionClass.DeferredCapability,
                        isText ? TextProcessorVersion : DeferredContentProcessorVersion,
                        sidecar.ContentSha256,
                        requiredCapability,
                        isSupportedText ? null : "outlook-content-capability-unavailable",
                        isSupportedText ? null : SourceActivityState.DeferredUnsupported),
                    cancellationToken).ConfigureAwait(false);
                if (requiredCapability is not null)
                {
                    context.DeferredCapabilities.Add(new DeferredCapabilityEntity
                    {
                        Id = Guid.NewGuid(),
                        SourceRevisionId = child.Id,
                        ArtifactFingerprint = sidecar.ContentSha256,
                        RequiredCapability = requiredCapability,
                        Provenance = "outlook-ready-export",
                        CreatedAtUtc = now
                    });
                }
            }
        }
        catch (Exception exception) when (exception is OutlookIngestionBlockedException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            context.ChangeTracker.Clear();
            return await CommitBlockedAsync(
                context,
                transaction,
                request,
                observation,
                profile.Id,
                folder.Id,
                profile.SourceRootId,
                BlockReason(exception, "retained-sidecar-invalid"),
                cancellationToken).ConfigureAwait(false);
        }

        var export = new OutlookCaptureExportEntity
        {
            Id = request.ExportId.Value,
            ProfileId = profile.Id,
            FolderId = folder.Id,
            CatchUpId = request.CatchUpId,
            EntryId = observation.EntryId,
            SourceFingerprint = observation.SourceFingerprint,
            ManifestHash = observation.ManifestHash,
            RelativeSpoolPath = observation.RelativeSpoolPath,
            State = (int)OutlookExportState.ReadyForIngestion,
            SourceRevisionId = parentRevision.Id,
            FencingToken = request.FencingToken
        };
        context.OutlookCaptureExports.Add(export);
        AddOperation(context, request, profile.Id, export.Id, accepted: true, now);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        export.State = (int)OutlookExportState.Ingested;
        if (folder.CursorUtc is null || observation.CursorUtc > folder.CursorUtc)
        {
            folder.CursorUtc = observation.CursorUtc;
            folder.CursorFingerprint = observation.CursorFingerprint;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutlookExportCommitReceipt(request.ExportId, true, true, false);
        }).ConfigureAwait(false);
    }

    private async ValueTask<OutlookExportCommitReceipt> CommitBlockedAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        OutlookExportCommitRequest request,
        OutlookExportObservation observation,
        Guid profileId,
        Guid folderId,
        Guid sourceRootId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var blockedId = await context.OutlookCaptureExports.AnyAsync(
            row => row.Id == request.ExportId.Value,
            cancellationToken).ConfigureAwait(false)
            ? Guid.NewGuid()
            : request.ExportId.Value;
        var blocked = new OutlookCaptureExportEntity
        {
            Id = blockedId,
            ProfileId = profileId,
            FolderId = folderId,
            CatchUpId = request.CatchUpId,
            EntryId = observation.EntryId,
            SourceFingerprint = observation.SourceFingerprint,
            ManifestHash = observation.ManifestHash,
            RelativeSpoolPath = observation.RelativeSpoolPath,
            State = (int)OutlookExportState.Blocked,
            BlockedReasonCode = reasonCode,
            FencingToken = request.FencingToken
        };
        context.OutlookCaptureExports.Add(blocked);
        AddOperation(context, request, profileId, blocked.Id, accepted: false, _clock.GetUtcNow());
        OperatorEventAppender.Add(context, new OperatorEventDraft(
            "outlook.export_blocked",
            "outlook",
            "warning",
            "outlook-ready-ingestion",
            _clock.GetUtcNow(),
            SourceRootId: sourceRootId,
            CorrelationId: $"outlook-export:{blocked.Id:N}",
            Details: new { reasonCode }));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutlookExportCommitReceipt(new OutlookCaptureExportId(blocked.Id), false, true, false);
    }

    private async ValueTask<OutlookExportCommitReceipt> CommitMalformedRecoveryAsync(
        string spoolRoot,
        Guid exportId,
        VerifiedOutlookReadyManifest envelope,
        CancellationToken cancellationToken)
    {
        const string reasonCode = "ready-manifest-recovery-invalid";
        var recovery = envelope.Manifest.Recovery;
        var operationId = recovery.OperationId == Guid.Empty
            ? DeterministicMalformedRecoveryOperationId(exportId, envelope.ManifestHash)
            : recovery.OperationId;
        var requestFingerprint = envelope.ManifestHash;

        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var priorOperation = await context.OutlookCaptureOperations
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureOperations] WITH (UPDLOCK, HOLDLOCK) WHERE [OperationId] = {operationId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (priorOperation is not null)
        {
            if (priorOperation.ResourceId is null)
            {
                throw new InvalidOperationException("The malformed Outlook recovery operation has no durable export receipt.");
            }
            var priorReceipt = await context.OutlookCaptureExports.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == priorOperation.ResourceId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The malformed Outlook recovery operation has no durable export receipt.");
            var isUnresolvedIdentityReceipt = priorOperation.ProfileId is null &&
                priorReceipt.State == (int)OutlookExportState.Blocked &&
                string.Equals(priorReceipt.BlockedReasonCode, reasonCode, StringComparison.Ordinal) &&
                string.Equals(priorOperation.RequestFingerprint, requestFingerprint, StringComparison.Ordinal);
            if (!string.Equals(priorOperation.Kind, "ingest-ready-export", StringComparison.Ordinal) ||
                (!isUnresolvedIdentityReceipt &&
                 (!string.Equals(priorOperation.RequestFingerprint, requestFingerprint, StringComparison.Ordinal) ||
                  priorOperation.ProfileId != recovery.ProfileId)))
            {
                throw new InvalidOperationException("The malformed Outlook recovery operation does not match its durable receipt.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutlookExportCommitReceipt(new OutlookCaptureExportId(priorReceipt.Id), false, true, true);
        }

        var profile = await context.OutlookCaptureProfiles
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {recovery.ProfileId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var folder = await context.OutlookCaptureFolders
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureFolders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {recovery.FolderId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var profileSpoolRoot = profile is null
            ? null
            : outlookSpoolPolicy?.RequireCanonicalBeforeIo(profile.SpoolRoot)
                ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
        if (profile is null || folder is null || folder.ProfileId != profile.Id || profile.SourceRootId == Guid.Empty ||
            !string.Equals(
                profileSpoolRoot,
                spoolRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return await CommitUnresolvedIdentityAsync(
                context,
                transaction,
                exportId,
                operationId,
                requestFingerprint,
                recovery.EntryId,
                recovery.SourceFingerprint,
                recovery.FencingToken,
                reasonCode,
                cancellationToken).ConfigureAwait(false);
        }

        var catchUpId = recovery.CatchUpId != Guid.Empty && await context.OutlookCatchUps
            .AnyAsync(row => row.Id == recovery.CatchUpId && row.ProfileId == profile.Id, cancellationToken).ConfigureAwait(false)
            ? recovery.CatchUpId
            : (Guid?)null;
        var blockedId = await context.OutlookCaptureExports.AnyAsync(
            row => row.Id == exportId,
            cancellationToken).ConfigureAwait(false)
            ? Guid.NewGuid()
            : exportId;
        context.OutlookCaptureExports.Add(new OutlookCaptureExportEntity
        {
            Id = blockedId,
            ProfileId = profile.Id,
            FolderId = folder.Id,
            CatchUpId = catchUpId,
            EntryId = SafeMalformedEntryId(recovery.EntryId),
            SourceFingerprint = IsCanonicalSha256(recovery.SourceFingerprint)
                ? recovery.SourceFingerprint
                : new string('0', 64),
            ManifestHash = envelope.ManifestHash,
            RelativeSpoolPath = Path.Combine("ready", exportId.ToString("N")),
            State = (int)OutlookExportState.Blocked,
            BlockedReasonCode = reasonCode,
            FencingToken = Math.Max(0, recovery.FencingToken)
        });
        context.OutlookCaptureOperations.Add(new OutlookCaptureOperationEntity
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            Kind = "ingest-ready-export",
            OperationId = operationId,
            RequestFingerprint = requestFingerprint,
            ResourceId = blockedId,
            Accepted = false,
            CompletedAtUtc = _clock.GetUtcNow()
        });
        OperatorEventAppender.Add(context, new OperatorEventDraft(
            "outlook.export_blocked",
            "outlook",
            "warning",
            "outlook-ready-ingestion",
            _clock.GetUtcNow(),
            SourceRootId: profile.SourceRootId,
            CorrelationId: $"outlook-export:{blockedId:N}",
            Details: new { reasonCode }));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutlookExportCommitReceipt(new OutlookCaptureExportId(blockedId), false, true, false);
        }).ConfigureAwait(false);
    }

    private async ValueTask<OutlookExportCommitReceipt> CommitUnresolvedIdentityAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        Guid exportId,
        Guid operationId,
        string manifestHash,
        string? entryId,
        string? sourceFingerprint,
        long fencingToken,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var blockedId = await context.OutlookCaptureExports.AnyAsync(
            row => row.Id == exportId,
            cancellationToken).ConfigureAwait(false)
            ? Guid.NewGuid()
            : exportId;
        context.OutlookCaptureExports.Add(new OutlookCaptureExportEntity
        {
            Id = blockedId,
            ProfileId = null,
            FolderId = null,
            CatchUpId = null,
            EntryId = SafeMalformedEntryId(entryId),
            SourceFingerprint = IsCanonicalSha256(sourceFingerprint) ? sourceFingerprint! : new string('0', 64),
            ManifestHash = manifestHash,
            RelativeSpoolPath = Path.Combine("ready", exportId.ToString("N")),
            State = (int)OutlookExportState.Blocked,
            BlockedReasonCode = reasonCode,
            FencingToken = Math.Max(0, fencingToken)
        });
        context.OutlookCaptureOperations.Add(new OutlookCaptureOperationEntity
        {
            Id = Guid.NewGuid(),
            ProfileId = null,
            Kind = "ingest-ready-export",
            OperationId = operationId,
            RequestFingerprint = manifestHash,
            ResourceId = blockedId,
            Accepted = false,
            CompletedAtUtc = _clock.GetUtcNow()
        });
        OperatorEventAppender.Add(context, new OperatorEventDraft(
            "outlook.export_blocked",
            "outlook",
            "warning",
            "outlook-ready-ingestion",
            _clock.GetUtcNow(),
            CorrelationId: $"outlook-blocked:{manifestHash}",
            Details: new { reasonCode }));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutlookExportCommitReceipt(new OutlookCaptureExportId(blockedId), false, true, false);
    }

    private static void AddOperation(
        FluxKnowledgeDbContext context,
        OutlookExportCommitRequest request,
        Guid profileId,
        Guid resourceId,
        bool accepted,
        DateTimeOffset completedAtUtc) => context.OutlookCaptureOperations.Add(new OutlookCaptureOperationEntity
    {
        Id = Guid.NewGuid(),
        ProfileId = profileId,
        Kind = "ingest-ready-export",
        OperationId = request.OperationId,
        RequestFingerprint = request.RequestFingerprint,
        ResourceId = resourceId,
        Accepted = accepted,
        CompletedAtUtc = completedAtUtc
    });

    private static SourceRevisionEntity CreateRevision(
        Guid sourceRootId,
        string stableIdentity,
        string contentSha256,
        string canonicalPath,
        Guid? parentRevisionId,
        string classification,
        long byteLength,
        string extension,
        DateTimeOffset now,
        string kind) => new()
    {
        Id = Guid.NewGuid(),
        SourceRootId = sourceRootId,
        StableSourceIdentity = stableIdentity,
        Revision = 1,
        ContentSha256 = contentSha256,
        CanonicalPath = canonicalPath,
        ParentSourceRevisionId = parentRevisionId,
        Classification = classification,
        Extension = extension,
        ByteLength = byteLength,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = JsonSerializer.Serialize(new { source = "outlook-ready-export", kind })
    };

    private static async Task<string> RetainSidecarAsync(
        string spoolRoot,
        string readyDirectory,
        OutlookExportSidecar sidecar,
        CancellationToken cancellationToken)
    {
        VerifiedContainedFile source;
        try
        {
            source = await ContainedFileReader.ReadAsync(
                readyDirectory,
                sidecar.RelativePath,
                64L * 1024 * 1024,
                cancellationToken,
                sidecar.ContentSha256,
                sidecar.ByteLength).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new OutlookIngestionBlockedException(
                exception is UnauthorizedAccessException ? "ready-sidecar-path-invalid" :
                exception is IOException ? "ready-sidecar-unavailable" : "ready-sidecar-checksum-invalid",
                exception);
        }
        if (IsTextContent(sidecar.ContentType))
        {
            try
            {
                _ = StrictUtf8.GetString(source.Bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The retained Outlook text sidecar is not valid UTF-8.", exception);
            }
        }
        var relativePath = Path.Combine("sha256", sidecar.ContentSha256[..2], $"{sidecar.ContentSha256}.bin");
        var targetPath = Path.Combine(spoolRoot, relativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDirectory);
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(targetDirectory);
        if (File.Exists(targetPath))
        {
            await VerifyRetainedSidecarAsync(spoolRoot, relativePath, sidecar, cancellationToken).ConfigureAwait(false);
            return relativePath;
        }

        var temporaryPath = Path.Combine(targetDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(spoolRoot);
            using var spoolLease = PhysicalFileIdentity.OpenDirectoryLease(spoolRoot);
            using var targetLease = PhysicalFileIdentity.OpenDirectoryLease(targetDirectory);
            if (!IsWithin(spoolLease.Identity.CanonicalPath, targetLease.Identity.CanonicalPath))
            {
                throw new InvalidDataException("The retained Outlook artifact target escapes its private spool.");
            }
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var finalTemporaryPath = PhysicalFileIdentity.GetFinalPath(target.SafeFileHandle);
                if (!IsWithin(targetLease.Identity.CanonicalPath, finalTemporaryPath))
                {
                    throw new InvalidDataException("The retained Outlook artifact temporary file escapes its leased shard.");
                }
                await target.WriteAsync(source.Bytes, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            File.Delete(temporaryPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        await VerifyRetainedSidecarAsync(spoolRoot, relativePath, sidecar, cancellationToken).ConfigureAwait(false);
        return relativePath;
    }

    private static async Task VerifyRetainedSidecarAsync(
        string spoolRoot,
        string relativePath,
        OutlookExportSidecar sidecar,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await ContainedFileReader.ReadAsync(
                spoolRoot,
                relativePath,
                64L * 1024 * 1024,
                cancellationToken,
                sidecar.ContentSha256,
                sidecar.ByteLength).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new OutlookIngestionBlockedException(
                exception is UnauthorizedAccessException ? "retained-sidecar-path-invalid" :
                exception is IOException ? "retained-sidecar-unavailable" : "retained-sidecar-checksum-invalid",
                exception);
        }
    }

    private static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafeExtension(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Length <= 32 ? extension : string.Empty;
    }

    private static string RequiredCapability(string contentType)
    {
        var normalised = contentType.Trim().ToLowerInvariant();
        var literal = $"outlook-content:{normalised}";
        if (literal.Length <= 256)
        {
            return literal;
        }

        return $"outlook-content:sha256:{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised)))}";
    }

    private static bool IsTextContent(string contentType) =>
        contentType.Trim().StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    private static SourceActivityKind ActivityKind(string contentType)
    {
        var normalised = contentType.Trim().ToLowerInvariant();
        if (normalised.StartsWith("text/", StringComparison.Ordinal))
        {
            return SourceActivityKind.TextExtraction;
        }

        if (normalised == "application/pdf")
        {
            return SourceActivityKind.DocumentParsing;
        }

        if (normalised.StartsWith("image/", StringComparison.Ordinal))
        {
            return SourceActivityKind.Ocr;
        }

        if (normalised.StartsWith("audio/", StringComparison.Ordinal) ||
            normalised.StartsWith("video/", StringComparison.Ordinal))
        {
            return SourceActivityKind.MediaTranscription;
        }

        if (normalised.Contains("zip", StringComparison.Ordinal) ||
            normalised.Contains("archive", StringComparison.Ordinal))
        {
            return SourceActivityKind.ArchiveExpansion;
        }

        return SourceActivityKind.MetadataExtraction;
    }

    private static string BlockReason(Exception exception, string fallback) =>
        exception is OutlookIngestionBlockedException blocked ? blocked.ReasonCode :
        exception is OutlookReadyExportValidationException ready ? ready.ReasonCode :
        exception is UnauthorizedAccessException ? "ready-path-invalid" : fallback;

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Guid DeterministicMalformedRecoveryOperationId(Guid exportId, string manifestHash)
    {
        var material = SHA256.HashData(Encoding.UTF8.GetBytes($"outlook-ready-recovery:{exportId:N}:{manifestHash}"));
        return new Guid(material.AsSpan(0, 16));
    }

    private static string SafeMalformedEntryId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? string.Empty
            : value;

    private sealed class OutlookIngestionBlockedException(string reasonCode, Exception? innerException = null)
        : Exception("The Outlook ready export is blocked.", innerException)
    {
        public string ReasonCode { get; } = reasonCode;
    }
}

/// <summary>Bounded signal that a complete ready export must be rebound to a fresh fenced claim.</summary>
public sealed class OutlookReadyExportLeaseException()
    : InvalidOperationException("The Outlook ready export requires a fresh capture lease.");
