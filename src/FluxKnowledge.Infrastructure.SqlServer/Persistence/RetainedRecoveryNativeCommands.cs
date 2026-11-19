using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record RetainedRecoveryTarget(string Kind, Guid SourceRevisionId, Guid? ExactId = null);

public sealed record RetainedRecoveryPreview(RetainedRecoveryTarget Target, NativeActionPreview Confirmation,
    string FileName, long Revision, string Processor, string ReasonCode, string? MemberReference);

/// <summary>Dashboard-only native confirmations for one retained processor successor.</summary>
public sealed partial class RetainedRecoveryNativeCommands(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory, INativeOperationStore operationStore,
    RetainedProcessorOptions options, ISourceWorkAdmission sourceAdmission,
    ISourceObservationReader? observations = null, IDeploymentValidationHold? deploymentHold = null,
    NativeCorpusCommandService? corpusCommands = null)
{
    internal const string Actor = "web";
    internal const string Zip = "retained_zip_reconcile";
    internal const string Document = "retained_document_reprocess";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RetainedRecoveryPreview> PreviewAsync(RetainedRecoveryTarget target, CancellationToken cancellationToken)
    {
        EnsureAllowed(target);
        if (target.Kind == "policy") throw new NativeOperationException("invalid-payload");
        var payload = Payload(target);
        var candidate = await ReadSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
        var confirmation = await Service((_, _) => ValueTask.FromResult(candidate.Targets))
            .PreviewAsync(new(Action(target), payload, Actor), cancellationToken).ConfigureAwait(false);
        return new(target, confirmation, Path.GetFileName(candidate.Revision.CanonicalPath), candidate.Revision.Revision,
            candidate.Processor, candidate.Reason, candidate.MemberReference);
    }

    public async ValueTask<NativeActionReceipt> CommitAsync(RetainedRecoveryTarget target, string confirmationId, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Validate(target);
        if (target.Kind == "policy") throw new NativeOperationException("invalid-payload");
        var payload = Payload(target);
        var replay = await TryReplayAsync(target, payload, confirmationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        try
        {
            EnsureAllowed(target);
            return await Service().CommitAsync(new(Action(target), payload, confirmationId, idempotencyKey, Actor), cancellationToken).ConfigureAwait(false);
        }
        catch (NativeOperationException)
        {
            replay = await TryReplayAsync(target, payload, confirmationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (replay is not null) return replay;
            throw;
        }
    }

    private ValueTask<NativeActionReceipt?> TryReplayAsync(RetainedRecoveryTarget target, string payload, string confirmationId,
        string idempotencyKey, CancellationToken token) => operationStore.TryReplayAsync(Action(target),
        NativeOperationCanonicalization.CanonicalizeJson(payload), confirmationId, idempotencyKey, Actor, token);

    private NativeOperationService Service(NativeTargetVersionResolver? resolve = null) => new(operationStore,
    [
        new(Zip, "Reprocess the whole archive branch for this retained file revision. The current ZIP processor can skip members whose content is not UTF-8.",
            resolve ?? ResolveTargetsAsync, PrepareCommitAsync),
        new(Document, "Queue one successor for this retained document revision with the named processor. The original attempt stays in history.",
            resolve ?? ResolveTargetsAsync, PrepareCommitAsync)
    ]);

    private async ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string payload, CancellationToken token)
    {
        return (await ReadSnapshotAsync(Parse(payload), token).ConfigureAwait(false)).Targets;
    }

    private async Task<Candidate> ReadSnapshotAsync(RetainedRecoveryTarget target, CancellationToken token)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            var candidate = await ReadAsync(db, target, sourceAdmission, observations, token).ConfigureAwait(false);
            using var proof = candidate.PhysicalProof;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return candidate;
        }).ConfigureAwait(false);
    }

    private ValueTask<NativeActionCommitOperation> PrepareCommitAsync(string payload, IReadOnlyList<NativeTargetVersion> _, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Parse(payload);
        EnsureAllowed(target);
        return ValueTask.FromResult<NativeActionCommitOperation>(new CommitOperation(Action(target), payload, sourceAdmission, observations, deploymentHold));
    }

    internal static async Task<CommitGuard> ApplyAsync(FluxKnowledgeDbContext db, CommitOperation operation,
        IReadOnlyList<NativeTargetVersion> targets, DateTimeOffset now, CancellationToken token)
    {
        if (operation.DeploymentHold?.IsHeld == true) throw new NativeOperationException("deployment-validation-held");
        var candidate = await ReadAsync(db, Parse(operation.Payload), operation.SourceAdmission, operation.Observations, token).ConfigureAwait(false);
        var guard = new CommitGuard(operation, candidate.Root.Id, candidate.Root.ConfigurationRevision, candidate.PhysicalProof);
        try
        {
            EnsureTargetsMatch(targets, candidate.Targets);
            await candidate.QueueAsync(now, token).ConfigureAwait(false);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private static async Task<Candidate> ReadAsync(FluxKnowledgeDbContext db, RetainedRecoveryTarget target,
        ISourceWorkAdmission admission, ISourceObservationReader? observations, CancellationToken token)
    {
        var revision = await db.SourceRevisions.SingleOrDefaultAsync(value => value.Id == target.SourceRevisionId &&
            value.OriginKind == 0 && value.SuppressedAtUtc == null, token).ConfigureAwait(false);
        if (revision is null) throw new NativeOperationException("retained-recovery-revision-unavailable");
        var root = await db.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == revision.SourceRootId &&
            value.State == (int)SourceRootState.Enabled, token).ConfigureAwait(false);
        if (root is null || root.FollowLinks || !SqlDocumentInputRecovery.IsInScope(root, revision) || !await SqlSourceWorkEligibility.Inputs(db, admission)
            .AnyAsync(value => value.Id == revision.Id, token).ConfigureAwait(false))
            throw new NativeOperationException("retained-recovery-source-not-admitted");
        if (await db.SourceRevisions.AnyAsync(value => value.Id != revision.Id && value.SourceRootId == root.Id &&
            value.SuppressedAtUtc == null && (value.StableSourceIdentity == revision.StableSourceIdentity || value.OriginKind == 0 &&
                EF.Functions.Collate(value.CanonicalPath, "Latin1_General_100_CI_AS") == revision.CanonicalPath), token).ConfigureAwait(false))
            throw new NativeOperationException("retained-recovery-revision-unavailable");
        var artifact = await db.SourceArtifacts.SingleOrDefaultAsync(value => value.SourceRevisionId == revision.Id &&
            value.ContentSha256 == revision.ContentSha256 && value.ByteLength == revision.ByteLength, token).ConfigureAwait(false);
        if (artifact is null) throw new NativeOperationException("retained-recovery-input-unavailable");
        var versions = new List<NativeTargetVersion>
        {
            Version("root", root.Id, root.RowVersion), Version("revision", revision.Id, revision.RowVersion),
            Version("retained-input", artifact.Id, artifact.RowVersion)
        };

        if (target.Kind == "zip")
        {
            var snapshot = await SqlRetainedProcessorBranchStore.ReadZipEncodingFailureAsync(db, target.ExactId!.Value, token).ConfigureAwait(false);
            if (snapshot is null || snapshot.Revision.Id != revision.Id || snapshot.SuccessorBranchId is not null ||
                snapshot.Branch.LeaseOwner is not null || snapshot.Branch.LeaseExpiresAtUtc is not null ||
                await db.SourceProcessorBranches.AnyAsync(value => value.SourceRevisionId == revision.Id && value.Id != snapshot.Branch.Id &&
                    (value.State == (int)RetainedProcessorBranchState.Pending || value.State == (int)RetainedProcessorBranchState.Running), token).ConfigureAwait(false))
                throw new NativeOperationException("archive-zip-reconciliation-not-eligible");
            versions.Add(Version("processor-branch", snapshot.Branch.Id, snapshot.Branch.RowVersion));
            versions.Add(Version("predecessor", snapshot.Predecessor.Id, snapshot.Predecessor.RowVersion));
            var members = await db.SourceProcessorBranchMembers.Where(value => value.BranchId == snapshot.Branch.Id &&
                value.ReasonCode == "archive-member-not-utf8").Select(value => value.MemberFingerprint).Take(2).ToArrayAsync(token).ConfigureAwait(false);
            var member = members.Length == 1 ? members[0] : null;
            versions.Add(Proof("archive-attempt", snapshot.Attempt.Id, new { snapshot.Attempt.FinishedAtUtc, snapshot.Attempt.LeaseGeneration, member }));
            return new(revision, root, versions, ZipArchiveRetainedProcessor.Capability.ProcessorVersion, "archive-member-not-utf8", member,
                (now, _) => Task.FromResult(SqlRetainedProcessorBranchStore.QueueZipEncodingSuccessor(db, snapshot, Actor, now, root.ConfigurationRevision)));
        }

        var descriptor = target.Kind switch
        {
            "pdf" => PdfDocumentProcessor.Capability,
            "image" => ImageDocumentInputProcessor.Capability,
            _ => VisioDocumentInputProcessor.Capability
        };
        var request = new DocumentReprocessRequest(new(revision.Id), revision.ContentSha256, descriptor.ProcessorFingerprint,
            target.ExactId, target.Kind == "visio" ? null : root.ConfigurationRevision);
        var sourceProof = OpenPhysicalSource(root, revision, observations);
        try
        {
            versions.Add(Proof("physical-file", revision.Id, sourceProof.Observation));

            if (target.Kind == "visio")
            {
                var visio = await SqlRetainedProcessorBranchStore.ReadExactDocumentPredecessorAsync(db, request, descriptor, token).ConfigureAwait(false);
                if (visio?.Predecessor is not { } predecessor ||
                    await db.SourceProcessorBranches.AnyAsync(value => value.SourceRevisionId == revision.Id &&
                        (value.State == (int)RetainedProcessorBranchState.Pending || value.State == (int)RetainedProcessorBranchState.Running), token).ConfigureAwait(false))
                    throw new NativeOperationException("visio-preparation-not-eligible");
                versions.Add(Version("predecessor", predecessor.Id, predecessor.RowVersion));
                return new(revision, root, versions, descriptor.ProcessorVersion, predecessor.Reason ?? "visio-preparation-available", null,
                    (now, _) => Task.FromResult(SqlRetainedProcessorBranchStore.QueueExactDocumentSuccessor(db, visio, descriptor, Actor, now, root.ConfigurationRevision)), sourceProof);
            }

            var candidate = await SqlDocumentInputRecovery.ReadCandidateAsync(db, request, admission, requireCurrentAdmission: true, token).ConfigureAwait(false);
            if (candidate is null) throw new NativeOperationException("document-reprocess-not-eligible");
            var binding = candidate.Binding;
            var job = await db.Jobs.SingleAsync(value => value.Id == binding.FailedJobId, token).ConfigureAwait(false);
            versions.Add(Version("extract-job", job.Id, job.RowVersion));
            versions.Add(Proof("settled-document-proof", binding.PredecessorActivityId, binding));
            return new(revision, root, versions, descriptor.ProcessorVersion, binding.FailureReason, null,
                (now, cancellationToken) => SqlDocumentInputRecovery.QueueCandidateAsync(db, candidate, Actor, now, cancellationToken), sourceProof);
        }
        catch
        {
            sourceProof.Dispose();
            throw;
        }
    }

    private static PhysicalProof OpenPhysicalSource(SourceRootConfigurationEntity root, SourceRevisionEntity revision,
        ISourceObservationReader? observations)
    {
        if (observations is null || revision.FileLastWriteAtUtc is not { } lastWrite)
            throw new NativeOperationException("retained-recovery-physical-proof-required");
        ISourceRootObservationLease? authority = null;
        ISourceObservationLease? file = null;
        try
        {
            var configuration = SqlSourceRemovalReconciler.Restore(root);
            if (configuration.PhysicalIdentityFingerprint is null || !Path.IsPathFullyQualified(revision.CanonicalPath))
                throw new NativeOperationException("retained-recovery-physical-proof-required");
            var relative = Path.GetRelativePath(root.CanonicalPath, revision.CanonicalPath);
            authority = observations.OpenRoot(configuration);
            file = observations.Open(configuration, relative);
            var proof = new PhysicalProof(authority, file);
            proof.ValidateCurrent();
            if (file.Observation.Identity != revision.StableSourceIdentity || file.Observation.ByteLength != revision.ByteLength ||
                file.Observation.LastWriteTicks != lastWrite.ToFileTime())
                throw new NativeOperationException("retained-recovery-input-changed");
            authority = null;
            file = null;
            return proof;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            throw new NativeOperationException("retained-recovery-physical-proof-required");
        }
        finally
        {
            file?.Dispose();
            authority?.Dispose();
        }
    }

    private void EnsureAllowed(RetainedRecoveryTarget target)
    {
        Validate(target);
        if (deploymentHold?.IsHeld == true) throw new NativeOperationException("deployment-validation-held");
        if (target.Kind == "zip" && !options.ArchiveZipExpandEnabled)
            throw new NativeOperationException("archive-zip-descriptor-disabled");
        if (target.Kind == "image" && !options.ImageDocumentOcrEnabled)
            throw new NativeOperationException("document-image-processor-disabled");
    }

    private static void Validate(RetainedRecoveryTarget target)
    {
        if (target.SourceRevisionId == Guid.Empty || target.ExactId == Guid.Empty ||
            target.Kind is not ("zip" or "pdf" or "image" or "visio" or "policy") ||
            (target.Kind == "visio") != (target.ExactId is null))
            throw new NativeOperationException("invalid-payload");
    }

    private static string Action(RetainedRecoveryTarget target) => target.Kind switch
    {
        "zip" => Zip,
        "policy" => "policy_reprocess",
        _ => Document
    };
    private static string Payload(RetainedRecoveryTarget target) => JsonSerializer.Serialize(target, Json);

    private static RetainedRecoveryTarget Parse(string payload)
    {
        var target = JsonSerializer.Deserialize<RetainedRecoveryTarget>(payload, Json) ?? throw new NativeOperationException("invalid-payload");
        Validate(target);
        return target;
    }

    private static NativeTargetVersion Version(string kind, Guid id, byte[] version) => new($"{kind}:{id:D}", Convert.ToBase64String(version));
    private static NativeTargetVersion Proof<T>(string kind, Guid id, T data) =>
        new($"{kind}:{id:D}", Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data, Json))));

    private static void EnsureTargetsMatch(IReadOnlyList<NativeTargetVersion> current, IReadOnlyList<NativeTargetVersion> expected)
    {
        if (NativeOperationCanonicalization.SerializeTargets(NativeOperationCanonicalization.CanonicalizeTargets(current)) !=
            NativeOperationCanonicalization.SerializeTargets(NativeOperationCanonicalization.CanonicalizeTargets(expected)))
            throw new NativeOperationException("operation-fenced");
    }

    internal sealed record CommitOperation(string Action, string Payload, ISourceWorkAdmission SourceAdmission,
        ISourceObservationReader? Observations, IDeploymentValidationHold? DeploymentHold) : NativeActionCommitOperation;

    internal sealed class CommitGuard(CommitOperation operation, Guid rootId, long configurationRevision, PhysicalProof? physical) : IDisposable
    {
        public void EnsureCurrent()
        {
            if (!operation.SourceAdmission.ReadAdmittedRoots().Any(root => root.RootId == rootId && root.ConfigurationRevision == configurationRevision))
                throw new NativeOperationException("retained-recovery-source-not-admitted");
            physical?.ValidateCurrent();
            if (operation.DeploymentHold?.IsHeld == true) throw new NativeOperationException("deployment-validation-held");
        }

        public void Dispose() => physical?.Dispose();
    }

    internal sealed class PhysicalProof(ISourceRootObservationLease authority, ISourceObservationLease file) : IDisposable
    {
        public SourceFileObservation Observation => file.Observation;
        public void ValidateCurrent()
        {
            try
            {
                file.ValidateCurrent();
                authority.ValidateCurrent();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new NativeOperationException("retained-recovery-physical-proof-required");
            }
        }

        public void Dispose()
        {
            file.Dispose();
            authority.Dispose();
        }
    }

    private sealed record Candidate(SourceRevisionEntity Revision, SourceRootConfigurationEntity Root, IReadOnlyList<NativeTargetVersion> Targets,
        string Processor, string Reason, string? MemberReference, Func<DateTimeOffset, CancellationToken, Task<Guid>> QueueAsync,
        PhysicalProof? PhysicalProof = null);
}
