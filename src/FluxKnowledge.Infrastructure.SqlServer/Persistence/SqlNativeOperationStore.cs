using System.Data;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Knowledge;
using FluxKnowledge.Domain.Sources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Durable confirmation, idempotency and target-version fencing for native v1 operations.</summary>
public sealed class SqlNativeOperationStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider,
    Action? afterCommitFailureInjector = null,
    Action? beforeCommitInjector = null,
    Action? afterSaveBeforeCommitInjector = null) : INativeOperationStore
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(5);
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Action? _afterCommitFailureInjector = afterCommitFailureInjector;
    private readonly Action? _beforeCommitInjector = beforeCommitInjector;
    private readonly Action? _afterSaveBeforeCommitInjector = afterSaveBeforeCommitInjector;

    public async ValueTask<NativeActionReceipt?> FindReceiptAsync(string idempotencyKey, string actorSurface, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await context.NativeOperationReceipts.AsNoTracking().SingleOrDefaultAsync(
            value => value.ActorSurface == actorSurface && value.IdempotencyKey == idempotencyKey,
            cancellationToken);
        return receipt is null ? null : ToReplay(receipt);
    }

    public async ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(confirmationId) || confirmationId.Length > 256) return null;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await context.NativeOperationReceipts.AsNoTracking().SingleOrDefaultAsync(value => value.ActorSurface == actorSurface && value.IdempotencyKey == idempotencyKey, cancellationToken);
        if (receipt is null) return null;
        if (IsCodexStopCapture(action, idempotencyKey, actorSurface)) return ToReplay(receipt);
        var intent = await context.NativeOperationIntents.AsNoTracking().SingleOrDefaultAsync(value => value.Id == receipt.IntentId && value.ConfirmationHash == NativeOperationCanonicalization.CreateConfirmationHash(confirmationId), cancellationToken);
        if (intent is null) throw new NativeOperationException("confirmation-mismatch");
        var targets = JsonSerializer.Deserialize<NativeTargetVersion[]>(intent.TargetMetadataJson) ?? throw new NativeOperationException("confirmation-mismatch");
        var fingerprint = NativeOperationCanonicalization.CreateRequestFingerprint(action, canonicalPayload, targets);
        if (!string.Equals(receipt.RequestFingerprint, fingerprint, StringComparison.Ordinal) || !string.Equals(intent.Action, action, StringComparison.Ordinal)) throw new NativeOperationException("idempotency-key-conflict");
        return ToReplay(receipt);
    }

    public async ValueTask<NativeActionPreview> CreatePreviewAsync(
        NativeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PreparePreview(request);
        var now = _timeProvider.GetUtcNow();
        var confirmationId = NativeOperationCanonicalization.CreateConfirmationId();
        var intent = new NativeOperationIntentEntity
        {
            Id = Guid.NewGuid(),
            Action = prepared.Action,
            ActorSurface = prepared.ActorSurface,
            RequestFingerprint = prepared.RequestFingerprint,
            ConfirmationHash = NativeOperationCanonicalization.CreateConfirmationHash(confirmationId),
            TargetMetadataJson = NativeOperationCanonicalization.SerializeTargets(prepared.Targets),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(ConfirmationLifetime)
        };

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.NativeOperationIntents.Add(intent);
        await context.SaveChangesAsync(cancellationToken);
        return new NativeActionPreview(intent.Id, confirmationId, intent.RequestFingerprint, intent.ExpiresAtUtc, prepared.Targets, prepared.EffectSummary);
    }

    public async ValueTask<NativeActionReceipt> CommitAsync(
        NativeActionCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PrepareCommit(request);
        var confirmationHash = NativeOperationCanonicalization.CreateConfirmationHash(request.ConfirmationId);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireApplicationLockAsync(
            context,
            $"idempotency:{NativeOperationCanonicalization.CreateConfirmationHash($"{prepared.ActorSurface.Length}:{prepared.ActorSurface}{prepared.IdempotencyKey.Length}:{prepared.IdempotencyKey}")}",
            cancellationToken);
        await AcquireApplicationLockAsync(context, $"confirmation:{confirmationHash}", cancellationToken);
        if (prepared.Operation is KnowledgeMutationCommitOperation knowledgeOperation)
        {
            await AcquireKnowledgeMutationLockAsync(context, knowledgeOperation.Mutation, cancellationToken);
        }
        if (prepared.Operation is NativeCorpusMutationCommitOperation corpusOperation)
        {
            await AcquireCorpusMutationLockAsync(context, corpusOperation, cancellationToken);
        }

        var existing = await context.NativeOperationReceipts.SingleOrDefaultAsync(
            receipt => receipt.ActorSurface == prepared.ActorSurface && receipt.IdempotencyKey == prepared.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (IsCodexStopCapture(prepared.Action, prepared.IdempotencyKey, prepared.ActorSurface))
            {
                await transaction.CommitAsync(cancellationToken);
                return ToReplay(existing);
            }
            if (!string.Equals(existing.RequestFingerprint, prepared.RequestFingerprint, StringComparison.Ordinal))
            {
                throw new NativeOperationException("idempotency-key-conflict");
            }
            var replayIntent = await context.NativeOperationIntents.SingleOrDefaultAsync(
                intent => intent.Id == existing.IntentId && intent.ConfirmationHash == confirmationHash,
                cancellationToken);
            if (replayIntent is null)
            {
                throw new NativeOperationException("confirmation-mismatch");
            }
            await transaction.CommitAsync(cancellationToken);
            return ToReplay(existing);
        }

        var intent = await context.NativeOperationIntents.SingleOrDefaultAsync(
            candidate => candidate.ConfirmationHash == confirmationHash,
            cancellationToken);
        if (intent is null || !string.Equals(intent.Action, prepared.Action, StringComparison.Ordinal) ||
            !string.Equals(intent.ActorSurface, prepared.ActorSurface, StringComparison.Ordinal))
        {
            throw new NativeOperationException("confirmation-mismatch");
        }

        var intentTargets = JsonSerializer.Deserialize<NativeTargetVersion[]>(intent.TargetMetadataJson)
            ?? throw new NativeOperationException("confirmation-mismatch");
        var intentFingerprint = NativeOperationCanonicalization.CreateRequestFingerprint(
            prepared.Action,
            NativeOperationCanonicalization.CanonicalizeJson(request.CanonicalPayload),
            intentTargets);
        if (!string.Equals(intent.RequestFingerprint, intentFingerprint, StringComparison.Ordinal))
        {
            throw new NativeOperationException("confirmation-mismatch");
        }

        if (intent.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new NativeOperationException("confirmation-expired");
        }

        if (!string.Equals(intent.RequestFingerprint, prepared.RequestFingerprint, StringComparison.Ordinal) ||
            !string.Equals(intent.TargetMetadataJson, NativeOperationCanonicalization.SerializeTargets(prepared.Targets), StringComparison.Ordinal))
        {
            throw new NativeOperationException("operation-fenced");
        }

        if (intent.ConsumedAtUtc is not null)
        {
            throw new NativeOperationException("confirmation-consumed");
        }

        await ApplyMutationAsync(context, prepared, cancellationToken);
        var receipt = new NativeOperationReceiptEntity
        {
            OperationId = Guid.NewGuid(),
            IntentId = intent.Id,
            Action = prepared.Action,
            ActorSurface = prepared.ActorSurface,
            IdempotencyKey = prepared.IdempotencyKey,
            RequestFingerprint = prepared.RequestFingerprint,
            Outcome = "completed",
            CompletedAtUtc = _timeProvider.GetUtcNow()
        };
        intent.ConsumedAtUtc = receipt.CompletedAtUtc;
        context.NativeOperationReceipts.Add(receipt);
        _beforeCommitInjector?.Invoke();
        var committed = false;
        var sqlChangesSaved = false;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            sqlChangesSaved = true;
            _afterSaveBeforeCommitInjector?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            _afterCommitFailureInjector?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!sqlChangesSaved)
        {
            throw;
        }
        catch (Exception exception) when (sqlChangesSaved || committed)
        {
            throw new NativeOperationCommitUncertainException(exception);
        }
        return new NativeActionReceipt(receipt.OperationId, false, receipt.Outcome, receipt.ReasonCode);
    }

    private static PreparedPreview PreparePreview(NativeActionPreviewRequest request)
    {
        ValidateCommon(request.Action, request.CanonicalPayload, request.ActorSurface);
        if (string.IsNullOrWhiteSpace(request.EffectSummary) || request.EffectSummary.Length > 512)
        {
            throw new NativeOperationException("invalid-effect-summary");
        }

        var targets = NativeOperationCanonicalization.CanonicalizeTargets(request.Targets);
        return new PreparedPreview(
            NativeOperationCanonicalization.CanonicalizeAction(request.Action),
            request.ActorSurface,
            ValidateFingerprint(request.Action, request.CanonicalPayload, targets, request.RequestFingerprint),
            targets,
            request.EffectSummary);
    }

    private static PreparedCommit PrepareCommit(NativeActionCommitRequest request)
    {
        ValidateCommon(request.Action, request.CanonicalPayload, request.ActorSurface);
        if (string.IsNullOrWhiteSpace(request.ConfirmationId) || request.ConfirmationId.Length > 256)
        {
            throw new NativeOperationException("confirmation-mismatch");
        }

        ValidateIdempotencyKey(request.IdempotencyKey);
        var canonicalAction = NativeOperationCanonicalization.CanonicalizeAction(request.Action);
        var canonicalPayload = NativeOperationCanonicalization.CanonicalizeJson(request.CanonicalPayload);
        var targets = NativeOperationCanonicalization.CanonicalizeTargets(request.Targets);
        NativeActionCommitOperation operation = request.CommitOperation switch
        {
            NativeFenceTargetMutation mutation when !string.IsNullOrWhiteSpace(mutation.TargetId) && !string.IsNullOrWhiteSpace(mutation.NewValue) && mutation.NewValue.Length <= 512 &&
                targets.Count == 1 && string.Equals(targets[0].TargetId, mutation.TargetId.Trim().ToLowerInvariant(), StringComparison.Ordinal) => mutation with { TargetId = mutation.TargetId.Trim().ToLowerInvariant() },
            KnowledgeMutationCommitOperation knowledge when string.Equals(knowledge.Mutation.Action, canonicalAction, StringComparison.Ordinal) => knowledge,
            NativeCorpusMutationCommitOperation corpus when
                string.Equals(corpus.Action, canonicalAction, StringComparison.Ordinal) &&
                string.Equals(corpus.CanonicalPayload, canonicalPayload, StringComparison.Ordinal) &&
                CorpusOperationMatchesPayload(corpus, canonicalPayload) => corpus,
            NativeCodeFeedbackCommitOperation feedback when
                string.Equals(canonicalAction, "feedback", StringComparison.Ordinal) &&
                string.Equals(feedback.CanonicalPayload, canonicalPayload, StringComparison.Ordinal) => feedback,
            _ => throw new NativeOperationException("invalid-commit-operation")
        };
        return new PreparedCommit(
            canonicalAction,
            request.ActorSurface,
            request.IdempotencyKey,
            ValidateFingerprint(canonicalAction, canonicalPayload, targets, request.RequestFingerprint),
            targets,
            operation);
    }

    private static bool CorpusOperationMatchesPayload(NativeCorpusMutationCommitOperation operation, string canonicalPayload)
    {
        if (operation.Action != "root_create")
        {
            return operation.RootAdmission is null;
        }

        if (operation.RootAdmission is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(canonicalPayload);
            var suppliedPath = String(document.RootElement, "path", 2048);
            var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedPath));
            return string.Equals(operation.RootAdmission.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static void ValidateCommon(string action, string payload, string actorSurface)
    {
        NativeOperationCanonicalization.CanonicalizeAction(action);
        NativeOperationCanonicalization.CanonicalizeJson(payload);
        if (string.IsNullOrWhiteSpace(actorSurface) || actorSurface.Length > 64)
        {
            throw new NativeOperationException("invalid-actor-surface");
        }
    }

    private static string ValidateFingerprint(string action, string payload, IReadOnlyList<NativeTargetVersion> targets, string fingerprint)
    {
        var expected = NativeOperationCanonicalization.CreateRequestFingerprint(
            NativeOperationCanonicalization.CanonicalizeAction(action),
            NativeOperationCanonicalization.CanonicalizeJson(payload),
            targets);
        if (!string.Equals(expected, fingerprint, StringComparison.Ordinal))
        {
            throw new NativeOperationException("invalid-request-fingerprint");
        }

        return expected;
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new NativeOperationException("invalid-idempotency-key");
        }
    }

    private static NativeActionReceipt ToReplay(NativeOperationReceiptEntity receipt) =>
        new(receipt.OperationId, true, receipt.Outcome, receipt.ReasonCode);

    private static bool IsCodexStopCapture(string action, string idempotencyKey, string actorSurface) =>
        action == "note_create" &&
        actorSurface == "codex-hook" &&
        idempotencyKey.StartsWith("codex-stop-", StringComparison.Ordinal);

    private async Task ApplyMutationAsync(
        FluxKnowledgeDbContext context,
        PreparedCommit prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Operation is NativeFenceTargetMutation mutation)
        {
            if (prepared.Targets.Count != 1) throw new NativeOperationException("invalid-commit-operation");
            byte[] expectedRowVersion;
            try { expectedRowVersion = Convert.FromBase64String(prepared.Targets[0].RowVersion); }
            catch (FormatException) { throw new NativeOperationException("invalid-targets"); }
            var affected = await context.NativeOperationFenceTargets
                .Where(target => target.TargetId == mutation.TargetId && target.RowVersion == expectedRowVersion)
                .ExecuteUpdateAsync(setters => setters.SetProperty(target => target.Value, mutation.NewValue), cancellationToken);
            if (affected != 1) throw new NativeOperationException("operation-fenced");
            return;
        }

        if (prepared.Operation is KnowledgeMutationCommitOperation knowledge)
        {
            await ApplyKnowledgeMutationAsync(context, prepared.Targets, knowledge.Mutation, cancellationToken);
            return;
        }

        if (prepared.Operation is NativeCorpusMutationCommitOperation corpus)
        {
            await ApplyCorpusMutationAsync(context, prepared.Targets, corpus, prepared.ActorSurface, _timeProvider.GetUtcNow(), cancellationToken);
            return;
        }

        if (prepared.Operation is NativeCodeFeedbackCommitOperation)
        {
            var feedback = (NativeCodeFeedbackCommitOperation)prepared.Operation;
            using var feedbackDocument = JsonDocument.Parse(feedback.CanonicalPayload);
            context.AuditEvents.Add(new AuditEventEntity
            {
                EventFamily = "code",
                Severity = "information",
                EventType = "native_code_feedback.recorded",
                Actor = prepared.ActorSurface,
                CorrelationId = $"native-operation:{prepared.RequestFingerprint}",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    feedbackHash = HashFeedbackValue(feedback.CanonicalPayload),
                    categoryHash = HashFeedbackProperty(feedbackDocument.RootElement, "category", canonicalCategory: true),
                    resultHash = HashFeedbackProperty(feedbackDocument.RootElement, "resultId", canonicalCategory: false)
                }),
                OccurredAtUtc = _timeProvider.GetUtcNow()
            });
            return;
        }

        throw new NativeOperationException("invalid-commit-operation");
    }

    private static string? HashFeedbackProperty(JsonElement payload, string propertyName, bool canonicalCategory)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return null;
        }

        var value = property.GetString()!.Trim();
        return HashFeedbackValue(canonicalCategory ? value.ToLowerInvariant() : value);
    }

    private static string HashFeedbackValue(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task ApplyKnowledgeMutationAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyList<NativeTargetVersion> targets,
        KnowledgeMutation mutation,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (mutation.Action == "note_create")
        {
            if (targets.Count != 0 || !Guid.TryParse(mutation.ItemId, out var itemId)) throw new NativeOperationException("invalid-commit-operation");
            if (await context.KnowledgeItems.AnyAsync(value => value.Id == itemId, cancellationToken)) throw new NativeOperationException("operation-fenced");
            var item = KnowledgeItem.Create(mutation.Title!, mutation.Body!, now, itemId);
            context.KnowledgeItems.Add(new KnowledgeItemEntity { Id = item.Id, Title = item.Title, SafeBody = item.Body, SafeSearchText = $"{item.Title}\n{item.Body}", CreatedAtUtc = item.CreatedAtUtc });
            return;
        }

        if (mutation.Action == "claim_upsert")
        {
            var desired = KnowledgeClaim.Create(mutation.Subject!, mutation.Predicate!, mutation.ObjectText!, mutation.Confidence!.Value, now);
            var identityHash = HashKnowledgeIdentity(desired.CanonicalIdentity);
            var existing = await context.KnowledgeClaims.SingleOrDefaultAsync(value => value.CanonicalIdentityHash == identityHash && value.CanonicalIdentity == desired.CanonicalIdentity && value.ForgottenAtUtc == null, cancellationToken);
            if (existing is null)
            {
                EnsureAbsentKnowledgeClaimTarget(targets, identityHash);
                context.KnowledgeClaims.Add(new KnowledgeClaimEntity
                {
                    Id = desired.Id, CanonicalIdentity = desired.CanonicalIdentity, CanonicalIdentityHash = identityHash, Subject = desired.Subject, Predicate = desired.Predicate, ObjectText = desired.ObjectText, SafeSearchText = $"{desired.Subject}\n{desired.Predicate}\n{desired.ObjectText}",
                    Confidence = desired.Confidence, Revision = desired.Revision, LifecycleState = desired.LifecycleState, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                context.KnowledgeClaimHistory.Add(new KnowledgeClaimHistoryEntity { Id = Guid.NewGuid(), ClaimId = desired.Id, Revision = 1, LifecycleState = "active", Confidence = desired.Confidence, RecordedAtUtc = now });
                context.KnowledgeRelations.Add(new KnowledgeRelationEntity { ClaimId = desired.Id, Subject = desired.Subject, Predicate = desired.Predicate, ObjectText = desired.ObjectText });
                return;
            }

            EnsureKnowledgeClaimTarget(targets, identityHash, existing.RowVersion);
            var revised = new KnowledgeClaim(existing.Id, existing.Subject, existing.Predicate, existing.ObjectText, existing.CanonicalIdentity, existing.Confidence, existing.Revision, existing.LifecycleState, existing.CreatedAtUtc, existing.UpdatedAtUtc, existing.ForgottenAtUtc).Revise(desired.Confidence, now);
            existing.Confidence = revised.Confidence; existing.Revision = revised.Revision; existing.UpdatedAtUtc = now;
            context.KnowledgeClaimHistory.Add(new KnowledgeClaimHistoryEntity { Id = Guid.NewGuid(), ClaimId = existing.Id, Revision = existing.Revision, LifecycleState = existing.LifecycleState, Confidence = existing.Confidence, RecordedAtUtc = now });
            return;
        }

        if (!Guid.TryParse(mutation.ItemId, out var targetId)) throw new NativeOperationException("invalid-commit-operation");
        var itemTarget = await context.KnowledgeItems.SingleOrDefaultAsync(value => value.Id == targetId && value.ForgottenAtUtc == null, cancellationToken);
        if (itemTarget is not null)
        {
            if (mutation.Action != "forget") throw new NativeOperationException("invalid-knowledge-target");
            EnsureKnowledgeTarget(targets, "item", targetId, itemTarget.RowVersion);
            itemTarget.Title = string.Empty; itemTarget.SafeBody = string.Empty; itemTarget.SafeSearchText = string.Empty; itemTarget.ForgottenAtUtc = now;
            context.KnowledgeTombstones.Add(new KnowledgeTombstoneEntity { Id = Guid.NewGuid(), TargetKind = "item", TargetId = targetId, ForgottenAtUtc = now });
            return;
        }

        var claimTarget = await context.KnowledgeClaims.SingleOrDefaultAsync(value => value.Id == targetId && value.ForgottenAtUtc == null, cancellationToken);
        if (claimTarget is null) throw new NativeOperationException("knowledge-not-found");
        EnsureKnowledgeTarget(targets, "claim", targetId, claimTarget.RowVersion);
        if (mutation.Action == "claim_transition")
        {
            var updated = new KnowledgeClaim(claimTarget.Id, claimTarget.Subject, claimTarget.Predicate, claimTarget.ObjectText, claimTarget.CanonicalIdentity, claimTarget.Confidence, claimTarget.Revision, claimTarget.LifecycleState, claimTarget.CreatedAtUtc, claimTarget.UpdatedAtUtc, null).Transition(mutation.Transition!, now);
            claimTarget.LifecycleState = updated.LifecycleState; claimTarget.Revision = updated.Revision; claimTarget.UpdatedAtUtc = now;
            context.KnowledgeClaimHistory.Add(new KnowledgeClaimHistoryEntity { Id = Guid.NewGuid(), ClaimId = targetId, Revision = updated.Revision, LifecycleState = updated.LifecycleState, Confidence = updated.Confidence, RecordedAtUtc = now });
            return;
        }
        if (mutation.Action == "forget")
        {
            claimTarget.CanonicalIdentity = string.Empty; claimTarget.CanonicalIdentityHash = string.Empty; claimTarget.Subject = string.Empty; claimTarget.Predicate = string.Empty; claimTarget.ObjectText = string.Empty; claimTarget.SafeSearchText = string.Empty; claimTarget.ForgottenAtUtc = now; claimTarget.UpdatedAtUtc = now;
            await context.KnowledgeRelations.Where(value => value.ClaimId == targetId).ExecuteDeleteAsync(cancellationToken);
            context.KnowledgeTombstones.Add(new KnowledgeTombstoneEntity { Id = Guid.NewGuid(), TargetKind = "claim", TargetId = targetId, ForgottenAtUtc = now });
            return;
        }
        throw new NativeOperationException("invalid-knowledge-mutation");
    }

    private static async Task ApplyCorpusMutationAsync(FluxKnowledgeDbContext context, IReadOnlyList<NativeTargetVersion> targets, NativeCorpusMutationCommitOperation operation, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(operation.CanonicalPayload);
        var payload = document.RootElement;
        if (operation.Action == "root_create")
        {
            if (operation.RootAdmission is null) throw new NativeOperationException("invalid-commit-operation");
            var path = operation.RootAdmission.CanonicalPath; var name = String(payload, "displayName", 256);
            EnsureAbsentTarget(targets, CanonicalPathTargetId(path));
            await LockCanonicalRootAsync(context, path, cancellationToken);
            if (await context.SourceRootConfigurations.AnyAsync(value => value.CanonicalPath == path, cancellationToken)) throw new NativeOperationException("operation-fenced");
            var rootId = Guid.NewGuid();
            var recursive = Bool(payload, "recursive", true); var followLinks = Bool(payload, "followLinks", false); var maximumFileBytes = Long(payload, "maximumFileBytes", 16L * 1024 * 1024); var cadenceSeconds = Long(payload, "reconciliationSeconds", 900);
            var configuration = SourceRootControlConfiguration.From(new SourceRootCreateRequest(
                path,
                name,
                recursive,
                [],
                [],
                followLinks,
                maximumFileBytes,
                [],
                TimeSpan.FromSeconds(cadenceSeconds),
                actor));
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = path, DisplayName = name, State = (int)SourceRootState.Enabled,
                Recursive = recursive, FollowLinks = followLinks, IncludePatternsJson = configuration.IncludePatternsJson,
                ExcludePatternsJson = configuration.ExcludePatternsJson, AllowedClassificationsJson = configuration.AllowedClassificationsJson,
                MaximumFileBytes = maximumFileBytes, CrawlMode = 0, ReconciliationCadenceSeconds = cadenceSeconds,
                PermissionEvidenceJson = operation.RootAdmission.PermissionEvidenceJson,
                HealthEvidenceJson = SourceRootControlAuditEvidence.CreateHealthEvidence(operation.RootAdmission, configuration),
                ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            CreateScanControl(context, rootId, actor, now, requestKind: 0, configuration);
            return;
        }
        if (operation.Action == "job_retry")
        {
            var jobId = RequiredGuid(payload, "jobId"); var job = await context.SourceScanJobs.SingleOrDefaultAsync(value => value.Id == jobId, cancellationToken);
            FenceOne(targets, "job", jobId, job?.RowVersion);
            var request = await context.SourceScanRequests.SingleOrDefaultAsync(value => value.Id == job!.SourceScanRequestId, cancellationToken);
            FenceOne(targets, "request", request?.Id ?? Guid.Empty, request?.RowVersion);
            var outbox = await context.SourceScanOutbox.SingleOrDefaultAsync(value => value.SourceScanRequestId == request!.Id, cancellationToken);
            FenceOne(targets, "outbox", outbox?.Id ?? Guid.Empty, outbox?.RowVersion);
            if (!IsRetryable(job!, request!, outbox!, now)) throw new NativeOperationException("operation-fenced");

            // Preserve the failed attempt history and failure reason. A retry only schedules the same fenced control again.
            job!.State = (int)SourceScanJobState.Pending; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.DueAtUtc = now; job.UpdatedAtUtc = now;
            request!.IsReleased = true; request.State = (int)SourceScanRequestState.Released; request.ReleasedAtUtc = now;
            request.AuditEvidenceJson = SourceRootControlAuditEvidence.AppendReleaseEvidence(request.AuditEvidenceJson, actor, now);
            outbox!.DueAtUtc = now; outbox.DispatchedAtUtc = null; outbox.LeaseOwner = null; outbox.LeaseExpiresAtUtc = null;
            return;
        }
        var rootIdValue = RequiredGuid(payload, "rootId"); var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == rootIdValue, cancellationToken);
        FenceOne(targets, "root", rootIdValue, root?.RowVersion);
        if (operation.Action == "root_update")
        {
            root!.DisplayName = String(payload, "displayName", 256); root.ConfigurationRevision++; root.UpdatedAtUtc = now;
            var configuration = SourceRootControlConfiguration.From(root);
            root.HealthEvidenceJson = SourceRootControlAuditEvidence.UpdateConfigurationFingerprint(root.HealthEvidenceJson, configuration);
            await EnqueueAsync(context, targets, rootIdValue, actor, now, requestKind: 1, configuration, cancellationToken);
            return;
        }
        if (operation.Action == "root_disable")
        {
            var watch = await context.SourceRootWatchStates.SingleOrDefaultAsync(value => value.SourceRootId == rootIdValue, cancellationToken);
            FenceExpectedTarget(targets, "watch", rootIdValue, watch?.RowVersion);
            if (watch is not null)
            {
                if (watch.LeaseOwner is not null && watch.LeaseExpiresAtUtc > now) throw new NativeOperationException("operation-fenced");
                context.SourceRootWatchStates.Remove(watch);
            }
            root!.State = (int)SourceRootState.Paused; root.ConfigurationRevision++; root.UpdatedAtUtc = now; return;
        }
        if (operation.Action == "watcher_set")
        {
            var enabled = BoolRequired(payload, "enabled");
            var watch = await context.SourceRootWatchStates.SingleOrDefaultAsync(value => value.SourceRootId == rootIdValue, cancellationToken);
            FenceExpectedTarget(targets, "watch", rootIdValue, watch?.RowVersion);
            if (watch is not null)
            {
                if (!enabled && watch.LeaseOwner is not null && watch.LeaseExpiresAtUtc > now) throw new NativeOperationException("operation-fenced");
                if (!enabled) context.SourceRootWatchStates.Remove(watch);
            }
            root!.State = (int)(enabled ? SourceRootState.Enabled : SourceRootState.Paused); root.ConfigurationRevision++; root.UpdatedAtUtc = now; return;
        }
        if (operation.Action == "source_sync")
        {
            await EnqueueAsync(context, targets, rootIdValue, actor, now, requestKind: 0, SourceRootControlConfiguration.From(root!), cancellationToken);
            return;
        }
        throw new NativeOperationException("invalid-corpus-mutation");
    }

    private static async Task EnqueueAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyList<NativeTargetVersion> targets,
        Guid rootId,
        string actor,
        DateTimeOffset now,
        int requestKind,
        SourceRootControlConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var active = await ReadActiveControlsAsync(context, rootId, cancellationToken);
        EnsureActiveControlTargets(targets, rootId, active);
        var coalescible = active.Where(control => control.Request.RequestKind == requestKind && IsCoalescible(control, now)).ToArray();
        if (coalescible.Length > 1)
        {
            throw new NativeOperationException("operation-fenced");
        }

        if (coalescible.Length == 1)
        {
            var control = coalescible[0];
            if (requestKind == 1)
            {
                control.Request.AuditEvidenceJson = SourceRootControlAuditEvidence.UpdateRequestConfigurationFingerprint(control.Request.AuditEvidenceJson, configuration);
            }
            if (control.Request.State == (int)SourceScanRequestState.Held)
            {
                control.Request.IsReleased = true; control.Request.ReleasedAtUtc = now; control.Request.State = (int)SourceScanRequestState.Released;
                control.Request.AuditEvidenceJson = SourceRootControlAuditEvidence.AppendReleaseEvidence(control.Request.AuditEvidenceJson, actor, now);
                control.Job.DueAtUtc = now; control.Job.UpdatedAtUtc = now;
                control.Outbox.DueAtUtc = now;
            }
            return;
        }
        CreateScanControl(context, rootId, actor, now, requestKind, configuration);
    }

    private static void CreateScanControl(FluxKnowledgeDbContext context, Guid rootId, string actor, DateTimeOffset now, int requestKind, SourceRootControlConfiguration configuration)
    {
        var requestId = Guid.NewGuid();
        context.SourceScanRequests.Add(new SourceScanRequestEntity
        {
            Id = requestId, SourceRootId = rootId, RequestKind = requestKind, RequestedBy = actor, RequestedAtUtc = now,
            IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Released,
            AuditEvidenceJson = SourceRootControlAuditEvidence.CreateRequestEvidence(configuration, actor, actor, now)
        });
        context.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, State = (int)SourceScanJobState.Pending, DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceScanOutbox.Add(new SourceScanOutboxEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, Operation = "source.scan", IdempotencyKey = $"source-scan:{requestId:N}", DueAtUtc = now, CreatedAtUtc = now });
    }

    private static async Task<IReadOnlyList<ActiveScanControl>> ReadActiveControlsAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var requests = await context.SourceScanRequests
            .Where(value => value.SourceRootId == rootId &&
                (value.State == (int)SourceScanRequestState.Held ||
                 value.State == (int)SourceScanRequestState.Released ||
                 value.State == (int)SourceScanRequestState.Running))
            .OrderBy(value => value.RequestedAtUtc).ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (requests.Length == 0)
        {
            return [];
        }

        if (requests.Length > 40)
        {
            throw new NativeOperationException("operation-conflict");
        }

        var requestIds = requests.Select(value => value.Id).ToArray();
        var jobs = await context.SourceScanJobs.Where(value => requestIds.Contains(value.SourceScanRequestId)).ToArrayAsync(cancellationToken);
        var outbox = await context.SourceScanOutbox.Where(value => requestIds.Contains(value.SourceScanRequestId)).ToArrayAsync(cancellationToken);
        if (jobs.Length != requests.Length || outbox.Length != requests.Length)
        {
            throw new NativeOperationException("target-not-found");
        }

        var controls = new List<ActiveScanControl>(requests.Length);
        foreach (var request in requests)
        {
            var job = jobs.SingleOrDefault(value => value.SourceScanRequestId == request.Id);
            var dispatch = outbox.SingleOrDefault(value => value.SourceScanRequestId == request.Id);
            if (job is null || dispatch is null || request.RowVersion.Length != 8 || job.RowVersion.Length != 8 || dispatch.RowVersion.Length != 8)
            {
                throw new NativeOperationException("target-not-found");
            }
            controls.Add(new ActiveScanControl(request, job, dispatch));
        }
        return controls;
    }

    private static void EnsureActiveControlTargets(
        IReadOnlyList<NativeTargetVersion> targets,
        Guid rootId,
        IReadOnlyList<ActiveScanControl> controls)
    {
        var expected = NativeOperationCanonicalization.CanonicalizeTargets(targets
            .Where(target => target.TargetId.StartsWith("active-controls:", StringComparison.Ordinal) ||
                target.TargetId.StartsWith("request:", StringComparison.Ordinal) ||
                target.TargetId.StartsWith("job:", StringComparison.Ordinal) ||
                target.TargetId.StartsWith("outbox:", StringComparison.Ordinal))
            .ToArray());
        var actual = NativeOperationCanonicalization.CanonicalizeTargets(ActiveControlTargets(rootId, controls));
        if (!string.Equals(NativeOperationCanonicalization.SerializeTargets(expected), NativeOperationCanonicalization.SerializeTargets(actual), StringComparison.Ordinal))
        {
            throw new NativeOperationException("operation-fenced");
        }
    }

    private static IReadOnlyList<NativeTargetVersion> ActiveControlTargets(Guid rootId, IReadOnlyList<ActiveScanControl> controls)
    {
        if (controls.Count == 0)
        {
            return [new NativeTargetVersion($"active-controls:{rootId:D}", "absent")];
        }

        var targets = new List<NativeTargetVersion>(1 + (controls.Count * 3));
        foreach (var control in controls.OrderBy(value => value.Request.RequestedAtUtc).ThenBy(value => value.Request.Id))
        {
            targets.Add(new NativeTargetVersion($"request:{control.Request.Id:D}", Convert.ToBase64String(control.Request.RowVersion)));
            targets.Add(new NativeTargetVersion($"job:{control.Job.Id:D}", Convert.ToBase64String(control.Job.RowVersion)));
            targets.Add(new NativeTargetVersion($"outbox:{control.Outbox.Id:D}", Convert.ToBase64String(control.Outbox.RowVersion)));
        }
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            NativeOperationCanonicalization.SerializeTargets(NativeOperationCanonicalization.CanonicalizeTargets(targets)))));
        targets.Add(new NativeTargetVersion($"active-controls:{rootId:D}", signature));
        return targets;
    }

    private static bool IsCoalescible(ActiveScanControl control, DateTimeOffset now) =>
        (control.Request.State == (int)SourceScanRequestState.Held || control.Request.State == (int)SourceScanRequestState.Released) &&
        control.Job.State == (int)SourceScanJobState.Pending &&
        !HasLiveLease(control.Job.LeaseOwner, control.Job.LeaseExpiresAtUtc, now) &&
        !HasLiveLease(control.Outbox.LeaseOwner, control.Outbox.LeaseExpiresAtUtc, now);

    private static bool IsRetryable(SourceScanJobEntity job, SourceScanRequestEntity request, SourceScanOutboxEntity outbox, DateTimeOffset now)
    {
        var failed = job.State == (int)SourceScanJobState.Failed && request.State == (int)SourceScanRequestState.Failed;
        var expiredRunning = job.State == (int)SourceScanJobState.Running &&
            request.State == (int)SourceScanRequestState.Running &&
            job.LeaseExpiresAtUtc is { } expiry && expiry <= now;
        return (failed || expiredRunning) &&
            !HasLiveLease(job.LeaseOwner, job.LeaseExpiresAtUtc, now) &&
            !HasLiveLease(outbox.LeaseOwner, outbox.LeaseExpiresAtUtc, now);
    }

    private static bool HasLiveLease(string? leaseOwner, DateTimeOffset? leaseExpiresAtUtc, DateTimeOffset now) =>
        leaseExpiresAtUtc is { } expiry ? expiry > now : leaseOwner is not null;

    private static void FenceExpectedTarget(IReadOnlyList<NativeTargetVersion> targets, string type, Guid id, byte[]? rowVersion)
    {
        if (rowVersion is null)
        {
            EnsureAbsentTarget(targets, $"{type}:{id:D}");
            return;
        }
        FenceOne(targets, type, id, rowVersion);
    }

    private static void EnsureAbsentTarget(IReadOnlyList<NativeTargetVersion> targets, string targetId)
    {
        if (!targets.Any(target => string.Equals(target.TargetId, targetId, StringComparison.Ordinal) && string.Equals(target.RowVersion, "absent", StringComparison.Ordinal)))
        {
            throw new NativeOperationException("operation-fenced");
        }
    }

    private static string CanonicalPathTargetId(string canonicalPath) =>
        $"root-path:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)))}";

    private static Task LockCanonicalRootAsync(FluxKnowledgeDbContext context, string canonicalPath, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             SELECT [Id]
             FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRootConfigurations_CanonicalPathFingerprint]))
             WHERE [CanonicalPathFingerprint] = CONVERT(char(64), HASHBYTES('SHA2_256', {canonicalPath}), 2);
             """,
            cancellationToken);

    private sealed record ActiveScanControl(SourceScanRequestEntity Request, SourceScanJobEntity Job, SourceScanOutboxEntity Outbox);
    private static void FenceOne(IReadOnlyList<NativeTargetVersion> targets, string type, Guid id, byte[]? rowVersion)
    { if (rowVersion is null || !targets.Any(target => string.Equals(target.TargetId, $"{type}:{id:D}", StringComparison.Ordinal) && string.Equals(target.RowVersion, Convert.ToBase64String(rowVersion), StringComparison.Ordinal))) throw new NativeOperationException("operation-fenced"); }
    private static string String(JsonElement payload, string name, int maximum) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= maximum ? value.GetString()! : throw new NativeOperationException("invalid-payload");
    private static Guid RequiredGuid(JsonElement payload, string name) => System.Guid.TryParse(String(payload, name, 64), out var value) ? value : throw new NativeOperationException("invalid-payload");
    private static bool Bool(JsonElement payload, string name, bool defaultValue) => !payload.TryGetProperty(name, out var value) ? defaultValue : value.ValueKind == JsonValueKind.True;
    private static bool BoolRequired(JsonElement payload, string name) => payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new NativeOperationException("invalid-payload");
    private static long Long(JsonElement payload, string name, long defaultValue) => payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) && result > 0 ? result : defaultValue;
    private static string SourceRootConfigurationFingerprint(string displayName, bool recursive, bool followLinks, long maximumFileBytes, long cadenceSeconds) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", displayName, "[]", "[]", "[]", recursive ? "1" : "0", followLinks ? "1" : "0", maximumFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), cadenceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    private static void EnsureKnowledgeTarget(IReadOnlyList<NativeTargetVersion> targets, string kind, Guid id, byte[] rowVersion)
    {
        if (targets.Count != 1 || !string.Equals(targets[0].TargetId, $"{kind}:{id:D}", StringComparison.Ordinal) ||
            !string.Equals(targets[0].RowVersion, Convert.ToBase64String(rowVersion), StringComparison.Ordinal))
        {
            throw new NativeOperationException("operation-fenced");
        }
    }

    private static void EnsureAbsentKnowledgeClaimTarget(IReadOnlyList<NativeTargetVersion> targets, string identityHash)
    {
        if (targets.Count != 1 || !string.Equals(targets[0].TargetId, $"claim:{identityHash}", StringComparison.Ordinal) ||
            !string.Equals(targets[0].RowVersion, "absent", StringComparison.Ordinal))
        {
            throw new NativeOperationException("operation-fenced");
        }
    }

    private static void EnsureKnowledgeClaimTarget(IReadOnlyList<NativeTargetVersion> targets, string identityHash, byte[] rowVersion)
    {
        if (targets.Count != 1 || !string.Equals(targets[0].TargetId, $"claim:{identityHash}", StringComparison.Ordinal) ||
            !string.Equals(targets[0].RowVersion, Convert.ToBase64String(rowVersion), StringComparison.Ordinal))
        {
            throw new NativeOperationException("operation-fenced");
        }
    }

    private static string HashKnowledgeIdentity(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task AcquireApplicationLockAsync(FluxKnowledgeDbContext context, string resourceSuffix, CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "DECLARE @result int; " +
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', " +
            "@LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @result;";
        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.Value = $"fluxknowledge:native-v1:{resourceSuffix}";
        command.Parameters.Add(resource);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (result < 0)
        {
            throw new NativeOperationException("operation-conflict");
        }
    }

    private static Task AcquireKnowledgeMutationLockAsync(
        FluxKnowledgeDbContext context,
        KnowledgeMutation mutation,
        CancellationToken cancellationToken)
    {
        var key = mutation.Action == "claim_upsert"
            ? HashKnowledgeIdentity(KnowledgeClaim.Create(mutation.Subject!, mutation.Predicate!, mutation.ObjectText!, mutation.Confidence!.Value).CanonicalIdentity)
            : Guid.TryParse(mutation.ItemId, out var targetId)
                ? targetId.ToString("D")
                : throw new NativeOperationException("invalid-knowledge-mutation");
        return AcquireApplicationLockAsync(context, $"knowledge:{key}", cancellationToken);
    }

    private static Task AcquireCorpusMutationLockAsync(
        FluxKnowledgeDbContext context,
        NativeCorpusMutationCommitOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.Action == "root_create")
        {
            if (operation.RootAdmission is null) throw new NativeOperationException("invalid-commit-operation");
            return AcquireApplicationLockAsync(context, $"corpus-root-path:{CanonicalPathTargetId(operation.RootAdmission.CanonicalPath)}", cancellationToken);
        }

        try
        {
            using var document = JsonDocument.Parse(operation.CanonicalPayload);
            var property = operation.Action == "job_retry" ? "jobId" : "rootId";
            if (!document.RootElement.TryGetProperty(property, out var value) || !Guid.TryParse(value.GetString(), out var id))
            {
                throw new NativeOperationException("invalid-commit-operation");
            }
            return AcquireApplicationLockAsync(context, operation.Action == "job_retry" ? $"corpus-job:{id:D}" : $"corpus-root:{id:D}", cancellationToken);
        }
        catch (JsonException)
        {
            throw new NativeOperationException("invalid-commit-operation");
        }
    }

    private sealed record PreparedPreview(
        string Action,
        string ActorSurface,
        string RequestFingerprint,
        IReadOnlyList<NativeTargetVersion> Targets,
        string EffectSummary);

    private sealed record PreparedCommit(
        string Action,
        string ActorSurface,
        string IdempotencyKey,
        string RequestFingerprint,
        IReadOnlyList<NativeTargetVersion> Targets,
        NativeActionCommitOperation Operation);
}
