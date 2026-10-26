using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore(IDbContextFactory<FluxKnowledgeDbContext> factory,
    TimeProvider time, SourceJournalOptions options, SqlSourceIncrementalCoordinator? incrementalCoordinator = null)
    : ISourceVerificationStore, ISourceVerificationExecutionStore, ISourceVerificationScheduler
{
    public const int RequestKind = 4;
    private readonly SqlSourceChangeJournalStore _transactions = new(factory, time);
    public async ValueTask<Guid> StartAsync(SourceRootId rootId, bool manual, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new InvalidOperationException("SourceVerificationFeatureDisabled");
        var result = Guid.Empty;
        await _transactions.ExecuteAsync(async db =>
        {
            var root = await LockRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            var run = await StartInTransactionAsync(db, root, manual, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            result = run.Id;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static async Task<SourceVerificationRunEntity> StartInTransactionAsync(FluxKnowledgeDbContext db,
        SourceRootConfigurationEntity root, bool manual, DateTimeOffset now, CancellationToken cancellationToken)
    {
            var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, cancellationToken).ConfigureAwait(false);
            if (root.State != (int)SourceRootState.Enabled || state is null || state.BaselineEpoch <= 0 ||
                SqlSourceIncrementalState.RequiresBaseline(state) || state.Mode == "Legacy" ||
                SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson) is not { } rootIdentity)
                throw new InvalidOperationException("SourceVerificationBaselineRequired");
            var policy = ReadPolicy(state);
            state.VerificationPolicyJson = JsonSerializer.Serialize(policy);
            state.VerificationPolicyRevision = Math.Max(1, state.VerificationPolicyRevision);
            var active = await db.SourceVerificationRuns.SingleOrDefaultAsync(x => x.SourceRootId == root.Id && x.Active, cancellationToken).ConfigureAwait(false);
            if (active is not null && !Matches(root, state, JsonSerializer.Deserialize<SourceVerificationAuthority>(active.AuthorityJson)
                    ?? throw new InvalidDataException("SourceVerificationAuthorityMissing")))
            {
                await InvalidateRunAsync(db, active, now, cancellationToken).ConfigureAwait(false);
                // Retire the filtered unique-index key before inserting the replacement, in the same transaction.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                active = null;
            }
            if (active is not null)
            {
                if (manual && !active.Manual)
                {
                    active.Manual = true;
                    // Do not bypass the inter-batch floor when overriding a distant quiet window.
                    if (active.NextBatchAtUtc > now.AddSeconds(5)) active.NextBatchAtUtc = now.AddSeconds(5);
                }
                await EnqueueBatchAsync(db, root, state, active, now, cancellationToken).ConfigureAwait(false);
                return active;
            }
            var last = await db.SourceVerificationRuns.Where(x => x.SourceRootId == root.Id).MaxAsync(x => (DateTimeOffset?)x.CreatedAtUtc, cancellationToken).ConfigureAwait(false);
            var due = manual ? null : SourceVerificationSchedule.DueCycle(policy, now, last, false, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
            if (!manual && due is null) throw new InvalidOperationException("SourceVerificationNotDue");
            var checkpoint = await db.SourceVolumeJournalCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.VolumeIdentity == state.VolumeIdentity, cancellationToken).ConfigureAwait(false);
            var run = new SourceVerificationRunEntity
            {
                Id = Guid.NewGuid(), SourceRootId = root.Id, CreatedAtUtc = now, State = "Pending", Active = true,
                Manual = manual, ScheduledForUtc = due, NextBatchAtUtc = now, OriginalPolicyJson = JsonSerializer.Serialize(policy),
                AuthorityJson = JsonSerializer.Serialize(new SourceVerificationAuthority(1, root.ConfigurationRevision, rootIdentity,
                    state.BaselineEpoch, state.RecoveryGeneration, state.NamespaceGeneration, state.PolicyFingerprint,
                    state.VolumeIdentity, checkpoint?.JournalId ?? "", state.NamespaceComplete && state.DeferredEvidenceJson is null or "[]"))
            };
            db.SourceVerificationRuns.Add(run);
            var pathPrefix = Path.EndsInDirectorySeparator(root.CanonicalPath) ? root.CanonicalPath : root.CanonicalPath + Path.DirectorySeparatorChar;
            var files = await (from entry in db.SourceInventoryEntries
                join artifactValue in db.SourceArtifacts on entry.SourceArtifactId equals artifactValue.Id into artifacts
                from artifact in artifacts.DefaultIfEmpty()
                where entry.SourceRootId == root.Id && entry.BaselineEpoch == state.BaselineEpoch && !entry.IsDirectory
                    && !db.SourceRevisions.Any(revision => revision.Id == entry.SourceRevisionId && revision.SourceRootId == root.Id &&
                        revision.OriginKind == 0 && revision.SuppressedAtUtc != null &&
                        revision.CanonicalPath == EF.Functions.Collate(pathPrefix + entry.RelativePath, SchemaConfiguration.SchedulerFenceCollation) &&
                        artifact != null && artifact.SourceRevisionId == revision.Id && artifact.ContentSha256 == revision.ContentSha256 && artifact.ByteLength == revision.ByteLength)
                select new { Entry = entry, Artifact = artifact }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in files)
            {
                var binding = BindingFor(file.Entry, file.Artifact);
                db.SourceVerificationItems.Add(new() { Id = Guid.NewGuid(), RunId = run.Id, InventoryEntryId = file.Entry.Id,
                    ExpectedRevisionId = file.Entry.SourceRevisionId, BindingJson = JsonSerializer.Serialize(binding) });
            }
            await EnqueueBatchAsync(db, root, state, run, now, cancellationToken).ConfigureAwait(false);
            return run;
    }

    public async ValueTask<SourceVerificationStatus?> ReadAsync(SourceRootId rootId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.SourceRootConfigurations.AnyAsync(x => x.Id == rootId.Value, cancellationToken).ConfigureAwait(false)) return null;
        var state = await db.SourceRootScanStates.AsNoTracking().SingleOrDefaultAsync(x => x.SourceRootId == rootId.Value, cancellationToken).ConfigureAwait(false);
        var run = await db.SourceVerificationRuns.AsNoTracking().Where(x => x.SourceRootId == rootId.Value)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        SourceVerificationRunStatus? status = null;
        if (run is not null)
        {
            var counts = await db.SourceVerificationItems.Where(x => x.RunId == run.Id).GroupBy(x => x.State)
                .Select(group => new { State = group.Key, Count = group.Count() }).ToDictionaryAsync(x => x.State, x => x.Count, cancellationToken).ConfigureAwait(false);
            status = new(run.Id, run.State, counts.Values.Sum(), counts.GetValueOrDefault("Verified"),
                counts.GetValueOrDefault("CapturedSuccessor") + counts.GetValueOrDefault("Deleted") + counts.GetValueOrDefault("OutOfScope"),
                counts.GetValueOrDefault("Deferred") + counts.GetValueOrDefault("AwaitingCapture"), run.SourceBytesRead,
                run.RetainedBytesRead, run.CreatedAtUtc, run.CompletedAtUtc, run.NextBatchAtUtc, run.Reason)
            { ReadCountsIncomplete = run.ReadCountsIncomplete };
        }
        var policy = ReadPolicy(state);
        var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
        var nextUsn = state is null ? 0 : await db.SourceVolumeJournalCheckpoints.AsNoTracking()
            .Where(x => x.VolumeIdentity == state.VolumeIdentity).Select(x => (long?)x.NextUsn).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var pending = await db.SourcePendingChanges.CountAsync(x => x.SourceRootId == rootId.Value, cancellationToken).ConfigureAwait(false);
        return new(rootId, policy, state?.VerificationPaused ?? false, state?.LastVerifiedAtUtc, status)
        {
            FeatureEnabled = options.Enabled, NextWindowAtUtc = window.Start, WindowEndAtUtc = window.End,
            Scanning = new(options.Enabled ? state?.Mode ?? "BaselineRequired" : "Legacy",
                options.Enabled ? state?.Reason : "incremental-disabled", state?.BaselineEpoch ?? 0, state?.NamespaceComplete ?? false,
                state is null || state.RecoveryRequired, state?.LastReconciliationAtUtc, Math.Max(0, nextUsn - (state?.CoveredUsn ?? 0)), pending,
                state?.LastBaselineAtUtc)
        };
    }

    private static SourceVerificationPolicy ReadPolicy(SourceRootScanStateEntity? state)
    {
        var policy = state?.VerificationPolicyJson is null or "{}" or ""
            ? SourceVerificationPolicy.Default(TimeZoneInfo.Local.Id)
            : JsonSerializer.Deserialize<SourceVerificationPolicy>(state.VerificationPolicyJson) ?? throw new InvalidDataException("SourceVerificationPolicyInvalid");
        policy.Validate();
        _ = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
        return policy;
    }

    private static Task<SourceRootConfigurationEntity> LockRootAsync(FluxKnowledgeDbContext db, Guid id, CancellationToken ct) =>
        db.SourceRootConfigurations.FromSqlInterpolated($"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}").SingleAsync(ct);

    private static async Task<bool> EnqueueBatchAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRootScanStateEntity state, SourceVerificationRunEntity run, DateTimeOffset now, CancellationToken ct)
    {
        var policy = ReadPolicy(state);
        if (!run.Active || run.NextBatchAtUtc > now) return false;
        if (!run.Manual)
        {
            if (!policy.Enabled) { run.Reason = "weekly-disabled"; return false; }
            var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, now, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
            if (!window.Contains(now)) { run.NextBatchAtUtc = window.Start; run.Reason = "quiet-window"; return false; }
        }
        if (state.VerificationPaused || root.LastScanCompletedAtUtc is null ||
            root.LastScanCompletedAtUtc.Value.AddSeconds(root.ReconciliationCadenceSeconds) <= now)
        { run.Reason = state.VerificationPaused ? "paused" : "foreground-reconciliation"; return false; }
        if (await db.SourceScanRequests.AnyAsync(x => x.SourceRootId == root.Id && x.RequestKind == RequestKind &&
            (x.State == (int)SourceScanRequestState.Held || x.State == (int)SourceScanRequestState.Released || x.State == (int)SourceScanRequestState.Running), ct).ConfigureAwait(false)) return false;
        if (await db.SourceScanRequests.AnyAsync(x => x.RequestKind != RequestKind && x.IsReleased && x.SourceRoot.State == (int)SourceRootState.Enabled &&
            (x.State == (int)SourceScanRequestState.Released || x.State == (int)SourceScanRequestState.Running), ct).ConfigureAwait(false) ||
            await db.SourcePendingChanges.AnyAsync(pending =>
                (pending.NotBeforeUtc == null || pending.NotBeforeUtc <= now ||
                 pending.ReasonFlags != SourcePendingReasons.NamespaceMetadata && (pending.RetryGeneration == null || pending.RetryGeneration != pending.Generation)) &&
                db.SourceRootConfigurations.Any(other => other.Id == pending.SourceRootId && other.State == (int)SourceRootState.Enabled), ct).ConfigureAwait(false))
        { run.Reason = "foreground-work"; return false; }
        var requestId = Guid.NewGuid();
        db.SourceScanRequests.Add(new() { Id = requestId, SourceRootId = root.Id, RequestKind = RequestKind,
            VerificationRunId = run.Id, RequestedBy = "source-verification", RequestedAtUtc = now, IsReleased = true,
            ReleasedAtUtc = now, State = (int)SourceScanRequestState.Released });
        db.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = requestId, State = (int)SourceScanJobState.Pending,
            DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SourceScanOutbox.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = requestId, Operation = "source.scan",
            IdempotencyKey = $"source-scan:{requestId:N}", DueAtUtc = now, CreatedAtUtc = now });
        run.Reason = null;
        return true;
    }
}
