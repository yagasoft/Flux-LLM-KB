using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Exact retained-only policy admission. This is never a startup or source-wide replay selector.</summary>
internal static class SqlDefaultRetainedPolicy
{
    internal sealed record Request(Guid RootId, Guid RevisionId, Guid ActivityId, string Hash, long ConfigurationRevision);
    internal static Request Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 5 ||
            !value.TryGetProperty("rootId", out var root) || !Guid.TryParse(root.GetString(), out var rootId) || rootId == Guid.Empty ||
            !value.TryGetProperty("sourceRevisionId", out var revision) || !Guid.TryParse(revision.GetString(), out var revisionId) || revisionId == Guid.Empty ||
            !value.TryGetProperty("predecessorActivityId", out var activity) || !Guid.TryParse(activity.GetString(), out var activityId) || activityId == Guid.Empty ||
            !value.TryGetProperty("inputSha256", out var hashElement) || hashElement.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("configurationRevision", out var configuration) || !configuration.TryGetInt64(out var configurationRevision) || configurationRevision <= 0)
            throw new NativeOperationException("invalid-payload");
        var hash = hashElement.GetString()!;
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new NativeOperationException("invalid-payload");
        return new Request(rootId, revisionId, activityId, hash, configurationRevision);
    }

    internal static async ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveAsync(FluxKnowledgeDbContext db, Request request, CancellationToken token)
    {
        var root = await db.SourceRootConfigurations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.RootId, token);
        var revision = await db.SourceRevisions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.RevisionId, token);
        var artifact = await db.SourceArtifacts.AsNoTracking().SingleOrDefaultAsync(value => value.SourceRevisionId == request.RevisionId, token);
        var activity = await db.SourceActivities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.ActivityId, token);
        if (root is null || revision is null || artifact is null || activity is null) throw new NativeOperationException("target-not-found");
        var targets = new List<NativeTargetVersion> { new($"root:{root.Id:D}", Convert.ToBase64String(root.RowVersion)),
            new($"policy-revision:{revision.Id:D}", Convert.ToBase64String(revision.RowVersion)),
            new($"policy-artifact:{artifact.Id:D}", Convert.ToBase64String(artifact.RowVersion)),
            new($"policy-activity:{activity.Id:D}", Convert.ToBase64String(activity.RowVersion)) };
        var branch = await db.SourceProcessorBranches.AsNoTracking().SingleOrDefaultAsync(value => value.SourceActivityId == activity.Id, token);
        if (branch is not null) targets.Add(new($"policy-branch:{branch.Id:D}", Convert.ToBase64String(branch.RowVersion)));
        return targets;
    }

    internal static async ValueTask<RetainedPolicyAdmission> PrepareAsync(FluxKnowledgeDbContext db, IRetainedSourceReader reader, Request request, CancellationToken token)
    {
        var root = await db.SourceRootConfigurations.AsNoTracking().SingleAsync(value => value.Id == request.RootId, token);
        var revision = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == request.RevisionId, token);
        var activity = await db.SourceActivities.AsNoTracking().SingleAsync(value => value.Id == request.ActivityId, token);
        var branch = await db.SourceProcessorBranches.AsNoTracking().SingleOrDefaultAsync(value => value.SourceActivityId == activity.Id, token);
        var descriptor = await SelectProcessorAsync(db, revision, activity, branch, token);
        var isText = descriptor is null && MatchesText(root, revision, activity, request);
        if (!MatchesScope(root, revision, request) || (!isText && descriptor is null) ||
            await HasUnconfirmedBindingAsync(db, revision, token)) throw new NativeOperationException("policy-item-not-eligible");
        // Verification happens before any SQL mutation transaction, and uses only app-retained bytes.
        var maximumBytes =
            descriptor?.Id == MediaMetadataRetainedProcessor.Capability.Id ? MediaMetadataRetainedProcessor.MaximumRetainedInputBytes :
            descriptor?.Id == RetainedCsharpCodeProcessor.Capability.Id ? RetainedCsharpCodeProcessor.MaximumInputBytes :
            descriptor is not null ? 128L * 1024 * 1024 : SourcePolicyDefaults.MaximumTextBytes;
        if (revision.ByteLength > maximumBytes) throw new NativeOperationException("policy-input-limit-exceeded");
        await using var retained = await OpenRetainedAsync(reader, revision.Id, maximumBytes, token).ConfigureAwait(false);
        if (retained.ContentSha256 != request.Hash || retained.ByteLength != revision.ByteLength)
            throw new NativeOperationException("retained-artifact-checksum-invalid");
        var bytes = new byte[checked((int)(isText ? retained.ByteLength : Math.Min(retained.ByteLength, 8192)))];
        await retained.Stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var allowed = JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson) ?? [];
        if (isText && !SourceClassifier.Classify(revision.CanonicalPath, bytes, retained.ByteLength,
                maximumAcceptedTextBytes: Math.Min(root.MaximumFileBytes, SourcePolicyDefaults.MaximumTextBytes),
                indexSourceText: allowed.Contains("text/x-source-code", StringComparer.OrdinalIgnoreCase)).IsAccepted)
            throw new NativeOperationException("policy-item-not-eligible");
        if (descriptor is not null && !MatchesSignature(descriptor, revision.Extension, bytes))
            throw new NativeOperationException("policy-item-not-eligible");
        return new RetainedPolicyAdmission(root.Id, revision.Id, activity.Id, root.ConfigurationRevision, request.Hash,
            retained.ByteLength, descriptor?.ProcessorVersion ?? RetainedDefaultPolicy.Version, descriptor?.ProcessorFingerprint ?? RetainedDefaultPolicy.TextFingerprint,
            (int)(descriptor?.AcceptedActivityKind ?? SourceActivityKind.TextExtraction), isText);
    }

    internal static async Task ApplyAsync(FluxKnowledgeDbContext db, string payload, RetainedPolicyAdmission admission,
        IReadOnlyList<NativeTargetVersion> targets, DateTimeOffset now, CancellationToken token)
    {
        var request = Parse(payload);
        if (admission.RootId != request.RootId || admission.SourceRevisionId != request.RevisionId || admission.PredecessorActivityId != request.ActivityId ||
            admission.ConfigurationRevision != request.ConfigurationRevision || admission.InputSha256 != request.Hash)
            throw new NativeOperationException("invalid-commit-operation");
        var root = await db.SourceRootConfigurations.FromSqlInterpolated($"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {request.RootId}").SingleAsync(token);
        var revision = await db.SourceRevisions.FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {request.RevisionId}").SingleAsync(token);
        var artifact = await db.SourceArtifacts.FromSqlInterpolated($"SELECT * FROM [SourceArtifacts] WITH (UPDLOCK,HOLDLOCK) WHERE [SourceRevisionId] = {request.RevisionId}").SingleAsync(token);
        var predecessor = await db.SourceActivities.FromSqlInterpolated($"SELECT * FROM [SourceActivities] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {request.ActivityId}").SingleAsync(token);
        var branch = await db.SourceProcessorBranches.FromSqlInterpolated($"SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK,HOLDLOCK) WHERE [SourceActivityId] = {request.ActivityId}").SingleOrDefaultAsync(token);
        Fence(targets, $"root:{root.Id:D}", root.RowVersion);
        Fence(targets, $"policy-revision:{revision.Id:D}", revision.RowVersion);
        Fence(targets, $"policy-artifact:{artifact.Id:D}", artifact.RowVersion);
        Fence(targets, $"policy-activity:{predecessor.Id:D}", predecessor.RowVersion);
        if (branch is not null) Fence(targets, $"policy-branch:{branch.Id:D}", branch.RowVersion);
        var descriptor = await SelectProcessorAsync(db, revision, predecessor, branch, token);
        var isText = descriptor is null && MatchesText(root, revision, predecessor, request);
        if (!MatchesScope(root, revision, request) || (!isText && descriptor is null) || admission.IsText != isText ||
            admission.ProcessorVersion != (descriptor?.ProcessorVersion ?? RetainedDefaultPolicy.Version) ||
            admission.DescriptorFingerprint != (descriptor?.ProcessorFingerprint ?? RetainedDefaultPolicy.TextFingerprint) ||
            admission.ActivityKind != (int)(descriptor?.AcceptedActivityKind ?? SourceActivityKind.TextExtraction) ||
            revision.ByteLength != admission.ByteLength ||
            artifact.ContentSha256 != request.Hash || artifact.ByteLength != admission.ByteLength ||
            await HasUnconfirmedBindingAsync(db, revision, token))
            throw new NativeOperationException("policy-item-not-eligible");
        var unsupported = descriptor?.Id == RetainedDefaultPolicy.UnsupportedBinary.Id;
        if (descriptor is not null && !unsupported && !await db.SourceCapabilities.AnyAsync(value => value.Id == descriptor.Id && value.IsRunnable &&
                value.ExecutionClass == (int)ExecutionClass.InProcess && value.ProcessorVersion == descriptor.ProcessorVersion &&
                value.ProcessorFingerprint == descriptor.ProcessorFingerprint && value.ProcessorKind == descriptor.ProcessorKind, token))
            throw new NativeOperationException("policy-processor-unavailable");
        var relation = await db.SourceActivityRelations.SingleOrDefaultAsync(value => value.PredecessorActivityId == predecessor.Id, token);
        if (relation is not null)
        {
            var existing = await db.SourceActivities.SingleOrDefaultAsync(value => value.Id == relation.SuccessorActivityId, token);
            if (relation.RelationshipKind != RetainedDefaultPolicy.RelationshipKind || relation.ReasonCode != RetainedDefaultPolicy.Version ||
                existing is null || existing.SourceRevisionId != revision.Id || existing.InputFingerprint != request.Hash ||
                existing.ProcessorVersion != admission.ProcessorVersion || existing.DescriptorFingerprint != admission.DescriptorFingerprint)
                throw new NativeOperationException("policy-successor-conflict");
            if (!unsupported && (existing.State == (int)SourceActivityState.FailedTerminal && existing.Reason == "policy-binding-stale" ||
                !await BindingIsCurrentAsync(db, existing.Id, token)))
            {
                var existingBranch = await db.SourceProcessorBranches.SingleOrDefaultAsync(value => value.SourceActivityId == existing.Id, token);
                if (existing.ResultingPipelineRecordId is not null || existing.AttemptCount != 0 ||
                    existing.State != (int)SourceActivityState.Pending && !(existing.State == (int)SourceActivityState.FailedTerminal && existing.Reason == "policy-binding-stale") ||
                    existingBranch is not null && (existingBranch.AttemptCount != 0 || existingBranch.LeaseOwner is not null || existingBranch.LeaseExpiresAtUtc is not null ||
                        existingBranch.State != (int)RetainedProcessorBranchState.Pending && existingBranch.State != (int)RetainedProcessorBranchState.Blocked ||
                        await db.SourceProcessorAttempts.AnyAsync(value => value.BranchId == existingBranch.Id, token) ||
                        await db.SourceProcessorForceRequests.AnyAsync(value => value.SourceProcessorBranchId == existingBranch.Id, token)))
                    throw new NativeOperationException("policy-successor-conflict");
                existing.AttemptEvidenceJson = AdmissionEvidence(root.ConfigurationRevision, request.Hash, admission.ByteLength);
                existing.State = (int)SourceActivityState.Pending;
                existing.Reason = null;
                existing.UpdatedAtUtc = now;
                if (existingBranch is not null) { existingBranch.State = (int)RetainedProcessorBranchState.Pending; existingBranch.UpdatedAtUtc = now; }
                db.AuditEvents.Add(new AuditEventEntity { EventType = "source.default_policy_revalidated", EventFamily = "source", Severity = "information",
                    Actor = "native-policy-reprocess", SourceRootId = root.Id, SourceRevisionId = revision.Id, SourceActivityId = existing.Id,
                    OccurredAtUtc = now, DetailsJson = existing.AttemptEvidenceJson });
            }
            return;
        }
        if (await db.SourceActivities.AnyAsync(value => value.SourceRevisionId == revision.Id && value.ActivityKind == admission.ActivityKind &&
                value.ProcessorVersion == admission.ProcessorVersion && value.DescriptorFingerprint == admission.DescriptorFingerprint && value.InputFingerprint == request.Hash, token))
            throw new NativeOperationException("policy-successor-conflict");
        var successor = new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revision.Id, InputFingerprint = request.Hash,
            ActivityKind = admission.ActivityKind, ExecutionClass = (int)(unsupported ? ExecutionClass.DeferredCapability : ExecutionClass.InProcess),
            ProcessorVersion = admission.ProcessorVersion, DescriptorFingerprint = admission.DescriptorFingerprint,
            State = (int)(unsupported ? SourceActivityState.DeferredUnsupported : SourceActivityState.Pending),
            RequiredCapability = unsupported ? RetainedDefaultPolicy.UnsupportedBinary.ProcessorKind : null,
            Reason = unsupported ? "No installed parser can process this retained binary format." : null, CreatedAtUtc = now, UpdatedAtUtc = now,
            AttemptEvidenceJson = AdmissionEvidence(root.ConfigurationRevision, request.Hash, admission.ByteLength) };
        db.SourceActivities.Add(successor);
        db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = predecessor.Id,
            SuccessorActivityId = successor.Id, RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now });
        if (isText)
        {
            // A scan may have recognised the new extension before explicit retained admission.
            // Supersede only unclaimed, unlinked legacy text work, under the same publication fence.
            var unlinked = await db.SourceActivities.Where(value => value.SourceRevisionId == revision.Id && value.InputFingerprint == request.Hash &&
                value.ActivityKind == (int)SourceActivityKind.TextExtraction && value.ExecutionClass == (int)ExecutionClass.InProcess &&
                value.ProcessorVersion == "phase-3a-v1" && value.DescriptorFingerprint == SourceActivityEntity.LegacyDescriptorFingerprint &&
                value.State == (int)SourceActivityState.Pending && value.AttemptCount == 0 && value.ResultingPipelineRecordId == null &&
                !db.SourceProcessorBranches.Any(other => other.SourceActivityId == value.Id) &&
                !db.SourceActivityRelations.Any(other => other.PredecessorActivityId == value.Id)).ToArrayAsync(token);
            foreach (var pending in unlinked)
            {
                pending.State = (int)SourceActivityState.CancelledSuperseded;
                pending.Reason = RetainedDefaultPolicy.RelationshipKind;
                pending.UpdatedAtUtc = now;
                pending.AttemptEvidenceJson = JsonSerializer.Serialize(new { policy = RetainedDefaultPolicy.Version, supersededByActivityId = successor.Id });
                db.AuditEvents.Add(new AuditEventEntity { EventType = "source.unlinked_text_superseded", EventFamily = "source", Severity = "information",
                    Actor = "native-policy-reprocess", SourceRootId = root.Id, SourceRevisionId = revision.Id, SourceActivityId = pending.Id,
                    OccurredAtUtc = now, DetailsJson = pending.AttemptEvidenceJson });
            }
        }
        if (descriptor is not null && !unsupported) db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = successor.Id,
            SourceRevisionId = revision.Id, InputSha256 = request.Hash, ProcessorVersion = descriptor.ProcessorVersion, ProcessorFingerprint = descriptor.ProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.AuditEvents.Add(new AuditEventEntity { EventType = "source.default_policy_applied", EventFamily = "source", Severity = "information", Actor = "native-policy-reprocess",
            SourceRootId = root.Id, SourceRevisionId = revision.Id, SourceActivityId = successor.Id, OccurredAtUtc = now,
            DetailsJson = JsonSerializer.Serialize(new { policy = RetainedDefaultPolicy.Version, predecessor = predecessor.Id, successor = successor.Id,
                descriptor = admission.DescriptorFingerprint, inputSha256 = request.Hash, configurationRevision = root.ConfigurationRevision, verifiedByteLength = admission.ByteLength }) });
        // Terminal predecessor and attempt evidence deliberately stay immutable. Current views follow the successor relation.
    }

    internal static bool MatchesText(SourceRootConfigurationEntity root, SourceRevisionEntity revision, SourceActivityEntity activity, Request request)
    {
        if (!MatchesScope(root, revision, request) || root.CrawlMode != (int)SourceDiscoveryMode.Filesystem ||
            revision.Classification is not ("Unknown" or "DeferredPolicy" or "AcceptedUtf8Text") ||
            revision.ByteLength < 0 || revision.ByteLength > Math.Min(root.MaximumFileBytes, SourcePolicyDefaults.MaximumTextBytes) ||
            activity.Id != request.ActivityId || activity.SourceRevisionId != revision.Id || activity.InputFingerprint != request.Hash ||
            activity.State != (int)SourceActivityState.DeferredPolicy || activity.ExecutionClass != (int)ExecutionClass.DeferredCapability ||
            activity.ResultingPipelineRecordId is not null || !RetainedDefaultPolicy.IsCorrectedTextReason(activity.Reason)) return false;
        return true;
    }

    private static bool MatchesScope(SourceRootConfigurationEntity root, SourceRevisionEntity revision, Request request)
    {
        if (root.Id != revision.SourceRootId || root.Id != request.RootId || root.State != (int)SourceRootState.Enabled || root.ConfigurationRevision != request.ConfigurationRevision ||
            root.FollowLinks || revision.Id != request.RevisionId || revision.SuppressedAtUtc is not null || revision.OriginKind != 0 ||
            revision.ContentSha256 != request.Hash || revision.ByteLength < 0) return false;
        var prefix = Path.TrimEndingDirectorySeparator(root.CanonicalPath) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(revision.CanonicalPath) || !string.Equals(Path.GetFullPath(revision.CanonicalPath), revision.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
            !revision.CanonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(revision.CanonicalPath), revision.Extension, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(root.CanonicalPath, revision.CanonicalPath);
        if (!root.Recursive && relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return false;
        var allowed = JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson) ?? [];
        return (allowed.Length == 0 || allowed.Contains("text/plain", StringComparer.OrdinalIgnoreCase)) &&
            LocalSourceEnumerator.MatchesPolicy(relative, JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson) ?? [], JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson) ?? []);
    }

    internal static async ValueTask<bool> IsTextSuccessorAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRevisionEntity revision, SourceActivity activity, CancellationToken token)
    {
        if (activity.ProcessorVersion != RetainedDefaultPolicy.Version || activity.DescriptorFingerprint != RetainedDefaultPolicy.TextFingerprint) return false;
        string[] reasons = ["File extension is unknown to local source ingestion.", "Source code ingestion is not enabled for this root.",
            "File exceeds the effective UTF-8 text ingestion limit."];
        var predecessor = await (from relation in db.SourceActivityRelations.AsNoTracking()
            join prior in db.SourceActivities.AsNoTracking() on relation.PredecessorActivityId equals prior.Id
            where relation.SuccessorActivityId == activity.Id.Value && relation.RelationshipKind == RetainedDefaultPolicy.RelationshipKind &&
                relation.ReasonCode == RetainedDefaultPolicy.Version && prior.State == (int)SourceActivityState.DeferredPolicy && reasons.Contains(prior.Reason!)
            select prior).SingleOrDefaultAsync(token);
        var durable = await db.SourceActivities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == activity.Id.Value, token);
        if (predecessor is null || durable?.AttemptEvidenceJson is null) return false;
        using var proof = JsonDocument.Parse(durable.AttemptEvidenceJson);
        if (!proof.RootElement.TryGetProperty("policy", out var policy) || policy.GetString() != RetainedDefaultPolicy.Version ||
            !proof.RootElement.TryGetProperty("configurationRevision", out var version) || !version.TryGetInt64(out var configurationRevision) ||
            !proof.RootElement.TryGetProperty("inputSha256", out var hash) || hash.GetString() != revision.ContentSha256 ||
            !proof.RootElement.TryGetProperty("verifiedByteLength", out var length) || length.GetInt64() != revision.ByteLength) return false;
        return MatchesText(root, revision, predecessor, new Request(root.Id, revision.Id, predecessor.Id, revision.ContentSha256, configurationRevision)) &&
            !await HasUnconfirmedBindingAsync(db, revision, token);
    }

    internal static async ValueTask<bool> BindingIsCurrentAsync(FluxKnowledgeDbContext db, Guid activityId, CancellationToken token)
    {
        var activity = await db.SourceActivities.SingleAsync(value => value.Id == activityId, token);
        if (SqlDocumentInputRecovery.IsMarked(activity) || await db.SourceActivityRelations.AnyAsync(value => value.SuccessorActivityId == activityId &&
                value.RelationshipKind == "superseded-by-retained-processor" && value.ReasonCode == DocumentProcessingInput.DocumentRecoveryVersion, token))
            return await SqlDocumentInputRecovery.BindingIsCurrentAsync(db, activityId, token);
        if (!await db.SourceActivityRelations.AnyAsync(value => value.SuccessorActivityId == activityId &&
                value.RelationshipKind == RetainedDefaultPolicy.RelationshipKind && value.ReasonCode == RetainedDefaultPolicy.Version, token)) return true;
        if (activity.AttemptEvidenceJson is null) return false;
        using var proof = JsonDocument.Parse(activity.AttemptEvidenceJson);
        if (!proof.RootElement.TryGetProperty("policy", out var policy) || policy.GetString() != RetainedDefaultPolicy.Version ||
            !proof.RootElement.TryGetProperty("configurationRevision", out var configuration) || !configuration.TryGetInt64(out var configurationRevision)) return false;
        var revision = await db.SourceRevisions.SingleAsync(value => value.Id == activity.SourceRevisionId, token);
        var root = await db.SourceRootConfigurations.SingleAsync(value => value.Id == revision.SourceRootId, token);
        return MatchesScope(root, revision, new Request(root.Id, revision.Id, activityId, activity.InputFingerprint, configurationRevision)) &&
            !await HasUnconfirmedBindingAsync(db, revision, token);
    }

    internal static async Task BlockStaleAsync(FluxKnowledgeDbContext db, Guid activityId, SourceProcessorBranchEntity? branch, DateTimeOffset now, CancellationToken token)
    {
        var activity = await db.SourceActivities.SingleAsync(value => value.Id == activityId, token);
        if (!SqlDocumentInputRecovery.IsMarked(activity) && !await db.SourceActivityRelations.AnyAsync(value => value.SuccessorActivityId == activityId &&
            (value.RelationshipKind == RetainedDefaultPolicy.RelationshipKind && value.ReasonCode == RetainedDefaultPolicy.Version ||
             value.RelationshipKind == "superseded-by-retained-processor" && value.ReasonCode == DocumentProcessingInput.DocumentRecoveryVersion), token)) return;
        if (activity.ResultingPipelineRecordId is not null) return;
        if (branch is not null)
        {
            var attempt = await db.SourceProcessorAttempts.SingleOrDefaultAsync(value => value.BranchId == branch.Id &&
                value.LeaseGeneration == branch.LeaseGeneration && value.FinishedAtUtc == null, token);
            if (attempt is not null) { attempt.FinishedAtUtc = now; attempt.OutcomeCode = "policy-binding-stale"; }
            branch.State = (int)RetainedProcessorBranchState.Blocked;
            branch.LeaseOwner = null;
            branch.LeaseExpiresAtUtc = null;
            branch.UpdatedAtUtc = now;
        }
        activity.State = (int)SourceActivityState.FailedTerminal;
        activity.Reason = "policy-binding-stale";
        activity.UpdatedAtUtc = now;
        var rootId = await db.SourceRevisions.Where(value => value.Id == activity.SourceRevisionId).Select(value => value.SourceRootId).SingleAsync(token);
        db.AuditEvents.Add(new AuditEventEntity { EventType = "source.policy_binding_stale", EventFamily = "source", Severity = "warning",
            Actor = "retained-policy-fence", SourceRootId = rootId, SourceRevisionId = activity.SourceRevisionId, SourceActivityId = activity.Id,
            OccurredAtUtc = now, DetailsJson = JsonSerializer.Serialize(new { policy = RetainedDefaultPolicy.Version, reason = activity.Reason, branchId = branch?.Id }) });
    }

    private static string AdmissionEvidence(long configurationRevision, string hash, long byteLength) =>
        JsonSerializer.Serialize(new { policy = RetainedDefaultPolicy.Version, configurationRevision, inputSha256 = hash, verifiedByteLength = byteLength });

    private static async ValueTask<SourceCapabilityDescriptor?> SelectProcessorAsync(FluxKnowledgeDbContext db, SourceRevisionEntity revision,
        SourceActivityEntity activity, SourceProcessorBranchEntity? branch, CancellationToken token)
    {
        if (activity.SourceRevisionId != revision.Id || activity.InputFingerprint != revision.ContentSha256 || activity.ResultingPipelineRecordId is not null) return null;
        var extension = revision.Extension.ToLowerInvariant();
        if (branch is null)
        {
            if (activity.State != (int)SourceActivityState.DeferredPolicy || activity.ExecutionClass != (int)ExecutionClass.DeferredCapability ||
                activity.DescriptorFingerprint != SourceActivityEntity.LegacyDescriptorFingerprint || activity.ProcessorVersion != "phase-3a-v1") return null;
            if (extension is ".rar" or ".7z" or ".db-wal" or ".db-shm" &&
                revision.Classification is "Unknown" or "DeferredPolicy" &&
                activity.Reason is "File extension is unknown to local source ingestion." or "File bytes contain binary control values and are not accepted as text.")
                return RetainedDefaultPolicy.UnsupportedBinary;
            if (activity.Reason == "media-metadata-input-too-large" && revision.Classification == "DeferredCapability" &&
                MediaMetadataSignature.TryGetFormatForExtension(extension, out _)) return MediaMetadataRetainedProcessor.Capability;
            if (extension == ".zip" && revision.ByteLength == 22 && revision.Classification == "Unknown" &&
                activity.Reason == "File extension is unknown to local source ingestion.") return ZipArchiveRetainedProcessor.Capability;
            return null;
        }
        if (branch.SourceRevisionId != revision.Id || branch.InputSha256 != revision.ContentSha256 || branch.State != (int)RetainedProcessorBranchState.Blocked ||
            branch.LeaseOwner is not null || branch.LeaseExpiresAtUtc is not null ||
            activity.ExecutionClass != (int)ExecutionClass.InProcess ||
            activity.State is not ((int)SourceActivityState.FailedTerminal) and not ((int)SourceActivityState.Pending) ||
            activity.ProcessorVersion != branch.ProcessorVersion || activity.DescriptorFingerprint != branch.ProcessorFingerprint ||
            await db.SourceProcessorAttempts.AnyAsync(value => value.BranchId == branch.Id && value.FinishedAtUtc == null, token) ||
            await db.SourceProcessorForceRequests.AnyAsync(value => value.SourceProcessorBranchId == branch.Id, token)) return null;
        var attempt = await db.SourceProcessorAttempts.AsNoTracking().SingleOrDefaultAsync(value => value.BranchId == branch.Id && value.LeaseGeneration == branch.LeaseGeneration, token);
        if (attempt?.FinishedAtUtc is null) return null;
        var reason = attempt.OutcomeCode;
        if (activity.Reason is not null && activity.Reason != reason) return null;
        if ((extension is ".docx" or ".xlsx" or ".pptx") &&
            ((branch.ProcessorVersion == OoxmlStructuralTextProcessor.LegacyProcessorVersion &&
             branch.ProcessorFingerprint == OoxmlStructuralTextProcessor.LegacyProcessorFingerprint &&
             reason is "office-document-container-invalid" or "office-document-element-limit" or "office-document-expanded-xml-limit" or "office-document-part-unsupported") ||
             (branch.ProcessorVersion == OoxmlStructuralTextProcessor.PreviousProcessorVersion &&
             branch.ProcessorFingerprint == OoxmlStructuralTextProcessor.PreviousProcessorFingerprint &&
             reason is "office-document-part-unsupported" or "office-document-container-invalid" or "office-document-element-limit") ||
             (branch.ProcessorVersion == OoxmlStructuralTextProcessor.Version3ProcessorVersion &&
             branch.ProcessorFingerprint == OoxmlStructuralTextProcessor.Version3ProcessorFingerprint &&
             reason is "office-document-container-invalid" or "office-document-element-limit")))
            return OoxmlStructuralTextProcessor.Capability;
        if (branch.ProcessorVersion == MediaMetadataRetainedProcessor.LegacyProcessorVersion && branch.ProcessorFingerprint == MediaMetadataRetainedProcessor.LegacyProcessorFingerprint &&
            reason == "media-metadata-input-too-large" && MediaMetadataSignature.TryGetFormatForExtension(extension, out _)) return MediaMetadataRetainedProcessor.Capability;
        if (extension == ".zip" && branch.ProcessorVersion == ZipArchiveRetainedProcessor.LegacyProcessorVersion && branch.ProcessorFingerprint == ZipArchiveRetainedProcessor.LegacyProcessorFingerprint &&
            reason is "archive-input-too-large" or "archive-member-size-limit" or "archive-expanded-total-limit" or "nested-archive-depth-limit") return ZipArchiveRetainedProcessor.Capability;
        if (extension == ".tar" && branch.ProcessorVersion == TarArchiveRetainedProcessor.LegacyProcessorVersion && branch.ProcessorFingerprint == TarArchiveRetainedProcessor.LegacyProcessorFingerprint &&
            reason is "archive-input-too-large" or "archive-member-size-limit" or "archive-expanded-total-limit") return TarArchiveRetainedProcessor.Capability;
        if (extension == ".cs" && revision.Classification == "AcceptedUtf8Text" && branch.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion &&
            branch.ProcessorFingerprint == RetainedCsharpCodeProcessor.LegacyDescriptorFingerprint && reason == "csharp-code-signature-limit") return RetainedCsharpCodeProcessor.Capability;
        return null;
    }

    private static bool MatchesSignature(SourceCapabilityDescriptor descriptor, string extension, ReadOnlySpan<byte> prefix) =>
        descriptor.Id == RetainedDefaultPolicy.UnsupportedBinary.Id ?
            extension is ".db-wal" or ".db-shm" || extension == ".rar" && prefix.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07 }) ||
            extension == ".7z" && prefix.StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }) :
        descriptor.Id == MediaMetadataRetainedProcessor.Capability.Id ? MediaMetadataRetainedProcessor.HasMatchingSupportedSignature(extension, prefix, out _) :
        descriptor.Id == RetainedCsharpCodeProcessor.Capability.Id ||
        descriptor.Id == TarArchiveRetainedProcessor.Capability.Id && TarArchiveRetainedProcessor.IsTarSignature(prefix) ||
        (descriptor.Id == ZipArchiveRetainedProcessor.Capability.Id || descriptor.Id == OoxmlStructuralTextProcessor.Capability.Id) && ZipArchiveRetainedProcessor.IsZipSignature(prefix);

    private static Task<bool> HasUnconfirmedBindingAsync(FluxKnowledgeDbContext db, SourceRevisionEntity revision, CancellationToken token) =>
        db.SourceRevisions.AnyAsync(value => value.Id != revision.Id && value.SourceRootId == revision.SourceRootId && value.SuppressedAtUtc == null &&
            (value.StableSourceIdentity == revision.StableSourceIdentity || value.OriginKind == 0 &&
             EF.Functions.Collate(value.CanonicalPath, "Latin1_General_100_CI_AS") == revision.CanonicalPath), token);

    private static async ValueTask<RetainedSourceReadLease> OpenRetainedAsync(IRetainedSourceReader reader, Guid revisionId, long maximumBytes, CancellationToken token)
    {
        try { return await reader.OpenReadAsync(new SourceRevisionId(revisionId), maximumBytes, token).ConfigureAwait(false); }
        catch (InvalidDataException) { throw new NativeOperationException("retained-artifact-checksum-invalid"); }
        catch (IOException) { throw new NativeOperationException("retained-content-unavailable"); }
        catch (UnauthorizedAccessException) { throw new NativeOperationException("retained-content-unavailable"); }
    }
    private static void Fence(IReadOnlyList<NativeTargetVersion> targets, string id, byte[] version)
    {
        if (!targets.Any(target => target.TargetId == id && target.RowVersion == Convert.ToBase64String(version)))
            throw new NativeOperationException("operation-fenced");
    }
}
