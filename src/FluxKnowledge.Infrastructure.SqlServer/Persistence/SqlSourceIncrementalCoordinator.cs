using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator(
    IDbContextFactory<FluxKnowledgeDbContext> factory, TimeProvider time, ISourceChangeJournalReader reader,
    ISourceObservationReader observations, ISourceArtifactStore artifacts, SourceJournalOptions options) : ISourceIncrementalCoordinator
{
    private readonly SqlSourceChangeJournalStore _journal = new(factory, time);
    private readonly WindowsSourceJournalSealQualification _qualifications = new(options);
    private sealed record CachedObservation(int Version, SourceFileObservation Source, RetainedFileObservation Retained,
        bool JournalCovered, long RecoveryGeneration, long? SealUsn = null, string? SealProfileId = null,
        long? CaptureGeneration = null, string? JournalId = null);

    public async ValueTask<SourceIncrementalPlan?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
    {
        if (request.Lease is null) return null;
        if (!options.Enabled)
        {
            await _journal.ExecuteAsync(async db =>
            {
                await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                if (state is null) return;
                if (state.Mode != "Legacy") SqlSourceIncrementalState.InvalidateCoverage(state, "incremental-disabled", state.CoveredUsn);
                state.Mode = "Legacy"; state.Reason = "incremental-disabled";
                var row = await db.SourceScanRequests.SingleAsync(x => x.Id == request.Id.Value, ct).ConfigureAwait(false);
                row.IncrementalManifestJson = null; // This owned request now executes a conservative whole-root scan.
            }, ct).ConfigureAwait(false);
            return null;
        }
        var volume = "";
        JournalCapability? capability = null;
        try
        {
            volume = WindowsSourceChangeJournalReader.CanonicalVolume(root.CanonicalPath);
            if (!options.ApprovedVolumes.Contains(volume, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("SourceJournalVolumeNotApproved");
            capability = await reader.QueryAsync(volume, ct).ConfigureAwait(false);
            await EnrolAsync(root, request, capability, ct).ConfigureAwait(false);
            await DrainThroughAsync(capability, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not SourceJournalGapException &&
            (error is IOException or UnauthorizedAccessException || error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            capability = null;
            await EnterFallbackAsync(root, request, volume, ct).ConfigureAwait(false);
        }
        var fallback = capability is null;
        GitTrackedSourceDiscovery.Inventory? git = root.DiscoveryMode == SourceDiscoveryMode.GitTracked
            ? await new GitTrackedSourceDiscovery().ReadAsync(root.CanonicalPath, ct).ConfigureAwait(false) : null;
        SourceIncrementalPlan? result = null;
        await _journal.ExecuteAsync(async db =>
        {
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            await QueueUnsealedChecksAsync(db, current, state, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // Include newly queued scoped debt in the frozen claim.
            var row = await db.SourceScanRequests.SingleAsync(x => x.Id == request.Id.Value, ct).ConfigureAwait(false);
            SourceIncrementalPlan? superseded = null;
            if (row.IncrementalManifestJson is { } manifest)
            {
                var saved = JsonSerializer.Deserialize<SourceIncrementalPlan>(manifest) ?? throw new InvalidDataException("SourceScanManifestInvalid");
                if (saved.Version != 1 || saved.LeaseGeneration <= 0) throw new InvalidDataException("SourceScanManifestVersionUnsupported");
                if (saved.LeaseGeneration > request.Lease.Generation) throw new SourceSnapshotChangedException("SourceScanClaimChanged");
                var availabilityChanged = fallback && !saved.IsFallback;
                if (saved.BaselineEpoch == state.BaselineEpoch && saved.RecoveryGeneration == state.RecoveryGeneration && !availabilityChanged &&
                    (!saved.IsBaseline || saved.LeaseGeneration == request.Lease.Generation))
                { result = saved; return; }
                if (request.Lease.Generation <= saved.LeaseGeneration) throw new SourceSnapshotChangedException("SourceScanEpochChanged");
                // Bind each higher baseline claim once, even without newer work. Keeping
                // the old lease's manifest would permit a later same-claim replan or leave
                // newer content debt outside every recapture's acknowledgement scope.
                // Targeted retries keep their frozen manifest while its epoch remains valid.
                superseded = saved;
            }
            if (!fallback && state.Mode == "Fallback")
            {
                await QueueUnprovedCapturesAsync(db, root, state, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false); // Include newly durable recaptures in this manifest, within the same transaction.
                state.Mode = SqlSourceIncrementalState.RequiresBaseline(state) ? "BaselineRequired" :
                    state.NamespaceComplete && (state.DeferredEvidenceJson is null or "[]") ? "Journal" : "JournalPartial";
            }
            var baseline = SqlSourceIncrementalState.RequiresBaseline(state) || superseded?.IsBaseline == true;
            var now = time.GetUtcNow();
            var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value &&
                (x.NotBeforeUtc == null || x.NotBeforeUtc <= now)).ToArrayAsync(ct).ConfigureAwait(false);
            // Legacy rows carry unknown provenance. Turn them into explicit forced work
            // before freezing the claim, rather than interpreting missing evidence as zero.
            foreach (var item in pending.Where(x => x.LastContentUsn is null || x.LastUnjournaledGeneration is null))
            {
                item.Generation = ++state.Generation; item.ReasonFlags |= 1;
                item.LastContentUsn ??= item.LastUsn;
                item.LastUnjournaledGeneration = item.Generation;
            }
            var targets = pending.Select(x => new SourceScanTarget(x.RelativePath, x.Generation, x.IsSubtree, x.ReasonFlags)
                { LastContentUsn = x.LastContentUsn, LastUnjournaledGeneration = x.LastUnjournaledGeneration }).ToList();
            // Dispatch may queue the next target before the recurring scheduler claims.
            // Promote only a fresh manifest; a frozen retry returned above stays immutable.
            var reconciliation = row.RequestKind == 1 || superseded?.IsReconciliation == true ||
                !baseline && (current.LastScanCompletedAtUtc is null ||
                    current.LastScanCompletedAtUtc.Value.AddSeconds(current.ReconciliationCadenceSeconds) <= now);
            if (reconciliation && !baseline)
                targets.Add(new(".", 0, true, SourcePendingReasons.NamespaceMetadata)); // Generation0 never acknowledges durable pending work.
            if (git is not null && !baseline)
            {
                var prior = await db.SourceInventoryEntries.AsNoTracking().Where(x => x.SourceRootId == root.Id.Value &&
                    x.BaselineEpoch == state.BaselineEpoch && !x.IsDirectory).Select(x => x.RelativePath).ToArrayAsync(ct).ConfigureAwait(false);
                var tracked = git.Paths.Select(x => x.Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in tracked.Except(prior, StringComparer.OrdinalIgnoreCase).Concat(prior.Except(tracked, StringComparer.OrdinalIgnoreCase)))
                    targets.Add(new(path, 0, false, 0x100));
                state.GitInventoryGeneration = git.Generation;
            }
            if (baseline) { state.Mode = fallback ? "Fallback" : "Baselining"; state.RecoveryRequired = true; state.RecoveryInProgress = true; }
            result = new(Guid.NewGuid(), state.BaselineEpoch, baseline, state.Mode,
                fallback ? null : new(volume, capability!.JournalId, state.CoveredUsn), state.Generation, targets)
            {
                IsReconciliation = reconciliation ||
                    targets.Any(x => x.RelativePath == "." && (x.ReasonFlags & SourcePendingReasons.NamespaceMetadata) != 0),
                LeaseGeneration = request.Lease.Generation, RecoveryGeneration = state.RecoveryGeneration,
                NamespaceGeneration = state.NamespaceGeneration, SupersedesManifestId = superseded?.ManifestId,
                MetadataEntriesPerSecond = options.MetadataEntriesPerSecond
            };
            row.IncrementalManifestJson = JsonSerializer.Serialize(result);
        }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task EnterFallbackAsync(SourceRootConfiguration root, SourceScanRequest request, string volume, CancellationToken ct)
    {
        var ancestors = observations.ReadRootAncestors(root);
        await _journal.ExecuteAsync(async db =>
        {
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var point = volume.Length == 0 ? null : await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={volume}").SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            var policy = SqlRepositoryWorkRecovery.Policy(current);
            var enrol = state is null || state.PolicyFingerprint != policy || volume.Length != 0 && state.VolumeIdentity != volume;
            if (state is null) { state = new() { SourceRootId = root.Id.Value }; db.SourceRootScanStates.Add(state); }
            if (enrol)
            {
                SqlSourceIncrementalState.InvalidateCoverage(state, "journal-unavailable", point?.NextUsn ?? 0);
                state.VolumeIdentity = volume; state.PolicyFingerprint = policy; state.CoveredUsn = point?.NextUsn ?? 0;
                if (point is not null) point.RegistrationGeneration++;
            }
            state.RecoveryRequired |= SqlSourceIncrementalState.RequiresBaseline(state);
            state.Mode = "Fallback"; state.Reason = "journal-unavailable"; state.NamespaceComplete = false;
            if (point is not null) { point.UnavailableSinceUtc ??= time.GetUtcNow(); point.LastFailureCode = "journal-unavailable"; }
            foreach (var ancestor in ancestors)
            {
                var key = SqlSourceChangeJournalStore.PathKey(ancestor.RelativePath);
                var entry = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value && x.PathFingerprint == key, ct).ConfigureAwait(false);
                if (entry is null) { entry = new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, PathFingerprint = key }; db.SourceInventoryEntries.Add(entry); }
                entry.RelativePath = ancestor.RelativePath; entry.FileId = ancestor.FileId; entry.ParentId = ancestor.ParentId;
                entry.IsAncestor = ancestor.IsAncestor; entry.IsDirectory = true; entry.BaselineEpoch = state.BaselineEpoch;
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task QueueUnprovedCapturesAsync(FluxKnowledgeDbContext db, SourceRootConfiguration root, SourceRootScanStateEntity state, CancellationToken ct)
    {
        if (SqlSourceIncrementalState.RequiresBaseline(state)) return;
        var entries = await db.SourceInventoryEntries.AsNoTracking().Where(x => x.SourceRootId == root.Id.Value &&
            x.BaselineEpoch == state.BaselineEpoch && !x.IsDirectory && x.ObservationJson != null).ToArrayAsync(ct).ConfigureAwait(false);
        var unproved = entries.Where(x =>
        {
            var proof = JsonSerializer.Deserialize<CachedObservation>(x.ObservationJson!);
            return !x.IsSealed || proof is not { Version: 3, JournalCovered: true } || proof.RecoveryGeneration != state.RecoveryGeneration ||
                proof.SealProfileId != _qualifications.GetProfile(root.PhysicalIdentityFingerprint, root.CanonicalPath);
        }).ToArray();
        var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value).ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count + unproved.Count(entry => pending.All(x => x.PathFingerprint != entry.PathFingerprint)) > 10000)
        { SqlSourceIncrementalState.InvalidateCoverage(state, "fallback-capture-overflow", state.CoveredUsn); return; }
        if (unproved.Length == 0) return;
        var generation = ++state.Generation;
        foreach (var entry in unproved)
        {
            var item = pending.SingleOrDefault(x => x.PathFingerprint == entry.PathFingerprint);
            if (item is null)
            {
                item = new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, RelativePath = entry.RelativePath, PathFingerprint = entry.PathFingerprint };
                pending.Add(item); db.SourcePendingChanges.Add(item);
            }
            item.ReasonFlags |= 1; item.Generation = generation; item.FirstObservedAtUtc ??= time.GetUtcNow(); item.NotBeforeUtc ??= time.GetUtcNow();
            item.LastContentUsn ??= item.LastUsn; item.LastUnjournaledGeneration = generation;
        }
    }

    private async Task EnrolAsync(SourceRootConfiguration root, SourceScanRequest request, JournalCapability capability, CancellationToken ct)
    {
        var ancestors = observations.ReadRootAncestors(root);
        await _journal.ExecuteAsync(async db =>
        {
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var policy = SqlRepositoryWorkRecovery.Policy(current);
            var checkpoint = await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={capability.VolumeIdentity}")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var newCheckpoint = checkpoint is null;
            if (checkpoint is null)
            {
                checkpoint = new() { VolumeIdentity = capability.VolumeIdentity, JournalId = capability.JournalId.ToString("X16"), NextUsn = capability.NextUsn };
                db.SourceVolumeJournalCheckpoints.Add(checkpoint);
            }
            if (checkpoint.JournalId != capability.JournalId.ToString("X16")) throw new SourceJournalGapException("SourceJournalEpochChanged");
            var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            var reset = newCheckpoint || state is null || state.PolicyFingerprint != policy || state.VolumeIdentity != capability.VolumeIdentity;
            if (state is null) { state = new() { SourceRootId = root.Id.Value }; db.SourceRootScanStates.Add(state); }
            if (!reset && !SqlSourceIncrementalState.RequiresBaseline(state)) return;
            if (reset) SqlSourceIncrementalState.InvalidateCoverage(state, "baseline-required", checkpoint.NextUsn);
            state.VolumeIdentity = capability.VolumeIdentity; state.PolicyFingerprint = policy;
            if (reset) state.CoveredUsn = checkpoint.NextUsn;
            var old = await db.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id.Value && x.IsDirectory).ToListAsync(ct).ConfigureAwait(false);
            var changed = reset;
            foreach (var ancestor in ancestors)
            {
                var key = SqlSourceChangeJournalStore.PathKey(ancestor.RelativePath);
                var entry = old.SingleOrDefault(x => x.PathFingerprint == key);
                if (entry is null) { entry = new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, PathFingerprint = key }; db.SourceInventoryEntries.Add(entry); }
                changed |= entry.BaselineEpoch != state.BaselineEpoch || entry.FileId != ancestor.FileId || entry.ParentId != ancestor.ParentId;
                entry.RelativePath = ancestor.RelativePath; entry.FileId = ancestor.FileId; entry.ParentId = ancestor.ParentId;
                entry.IsAncestor = ancestor.IsAncestor; entry.IsDirectory = true; entry.BaselineEpoch = state.BaselineEpoch;
            }
            var keys = ancestors.Select(x => SqlSourceChangeJournalStore.PathKey(x.RelativePath)).ToHashSet(StringComparer.Ordinal);
            var stale = old.Where(x => x.IsAncestor && !keys.Contains(x.PathFingerprint)).ToArray();
            if (stale.Length != 0) { db.SourceInventoryEntries.RemoveRange(stale); changed = true; }
            if (changed) checkpoint.RegistrationGeneration++;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<int> PumpAsync(CancellationToken ct)
    {
        if (!options.Enabled) return 0;
        var pages = 0;
        var burst = System.Diagnostics.Stopwatch.StartNew();
        while (pages < 32)
        {
            var advanced = false;
            // Finish a fair round before checking its soft time budget. A slow first volume
            // cannot consume every turn; idle/unavailable volumes cannot block dispatch.
            foreach (var volume in options.ApprovedVolumes)
            {
                if (pages >= 32) break;
                var result = await PollVolumeAsync(volume, false, ct).ConfigureAwait(false);
                if (result != JournalPollResult.NoClaim) pages++;
                advanced |= result == JournalPollResult.Advanced;
            }
            if (!advanced || burst.Elapsed >= TimeSpan.FromSeconds(1)) break;
        }
        await QueueUnsealedChecksAsync(ct).ConfigureAwait(false);
        return await QueuePendingAsync(ct).ConfigureAwait(false);
    }

    private enum JournalPollResult { NoClaim, Idle, Advanced, Reset, Unavailable }

    private async ValueTask<JournalPollResult> PollVolumeAsync(string volume, bool ignoreIdleDelay, CancellationToken ct)
    {
        if (!options.ApprovedVolumes.Contains(volume, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceJournalVolumeNotApproved");
        var claim = await _journal.ClaimVolumeAsync(volume, time.GetUtcNow(), ignoreIdleDelay, ct).ConfigureAwait(false);
        if (claim is null) return JournalPollResult.NoClaim;
        try
        {
            try
            {
                var page = await reader.ReadAsync(claim.Cursor, 65536, ct).ConfigureAwait(false);
                await _journal.AcceptAsync(claim, page, ct).ConfigureAwait(false);
                return page.Next.NextUsn > page.Start.NextUsn ? JournalPollResult.Advanced : JournalPollResult.Idle;
            }
            catch (SourceJournalGapException)
            {
                var current = await reader.QueryAsync(claim.Cursor.VolumeIdentity, ct).ConfigureAwait(false);
                return await ResetCoverageAsync(claim, current, ct).ConfigureAwait(false) ? JournalPollResult.Reset : JournalPollResult.NoClaim;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            await _journal.MarkUnavailableAsync(claim, time.GetUtcNow().AddSeconds(options.PollSeconds), ct).ConfigureAwait(false);
            return JournalPollResult.Unavailable;
        }
    }

    private async Task<int> QueuePendingAsync(CancellationToken ct)
    {
        var queued = 0;
        var now = time.GetUtcNow();
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roots = await lookup.SourcePendingChanges.Where(x => x.NotBeforeUtc == null || x.NotBeforeUtc <= now)
            .Select(x => x.SourceRootId).Union(lookup.SourceRootScanStates.Where(x => x.Mode != "Fallback" && (x.RecoveryRequired ||
                x.Mode == "BaselineRequired" || x.Mode == "Baselining")).Select(x => x.SourceRootId)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var id in roots.Order())
        {
            var dispatchable = false;
            await _journal.ExecuteAsync(async db =>
            {
                dispatchable = false;
                var root = await db.SourceRootConfigurations.FromSqlInterpolated(
                    $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}").SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (root is null || root.State != (int)SourceRootState.Enabled) return;
                var active = await db.SourceScanRequests.Where(x => x.SourceRootId == id && x.RequestKind != SqlSourceVerificationStore.RequestKind &&
                    (x.State == (int)SourceScanRequestState.Held || x.State == (int)SourceScanRequestState.Released || x.State == (int)SourceScanRequestState.Running))
                    .Select(x => x.State).ToArrayAsync(ct).ConfigureAwait(false);
                if (active.Length != 0)
                {
                    dispatchable = active.Contains((int)SourceScanRequestState.Released) ||
                        await db.SourceScanJobs.AnyAsync(x => x.SourceScanRequest.SourceRootId == id &&
                            x.State == (int)SourceScanJobState.Running && x.LeaseExpiresAtUtc <= now, ct).ConfigureAwait(false);
                    return;
                }
                var request = Guid.NewGuid();
                db.SourceScanRequests.Add(new() { Id = request, SourceRootId = id, RequestedBy = "journal", RequestKind = 3,
                    State = (int)SourceScanRequestState.Released, IsReleased = true, RequestedAtUtc = now, ReleasedAtUtc = now });
                db.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = request, State = (int)SourceScanJobState.Pending,
                    DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
                db.SourceScanOutbox.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = request, Operation = "source.scan",
                    IdempotencyKey = $"source-scan:{request:N}", DueAtUtc = now, CreatedAtUtc = now });
                dispatchable = true;
            }, ct).ConfigureAwait(false);
            if (dispatchable) queued++;
        }
        return queued;
    }

    private async Task DrainThroughAsync(JournalCapability barrier, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var db = await factory.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            var point = await db.SourceVolumeJournalCheckpoints.AsNoTracking().SingleAsync(x => x.VolumeIdentity == barrier.VolumeIdentity, timeout.Token).ConfigureAwait(false);
            if (point.JournalId != barrier.JournalId.ToString("X16")) throw new SourceJournalGapException("SourceJournalEpochChanged");
            if (point.NextUsn >= barrier.NextUsn) return;
            // A foreground proof barrier is already known to be ahead. Idle poll delay must
            // not defer it, and forcing this read must still respect another reader's lease.
            var result = await PollVolumeAsync(barrier.VolumeIdentity, true, timeout.Token).ConfigureAwait(false);
            if (result == JournalPollResult.Unavailable) throw new IOException("SourceJournalUnavailable");
            if (result == JournalPollResult.Reset) throw new SourceJournalGapException("SourceJournalCoverageReset");
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    internal async ValueTask<JournalCapability> ReadVerificationBarrierAsync(string volume, string journalId, CancellationToken ct)
    {
        if (!options.Enabled || !options.ApprovedVolumes.Contains(volume, StringComparer.OrdinalIgnoreCase))
            throw new IOException("SourceVerificationJournalNotAdmitted");
        var barrier = await reader.QueryAsync(volume, ct).ConfigureAwait(false);
        if (barrier.JournalId.ToString("X16") != journalId) throw new SourceJournalGapException("SourceVerificationJournalChanged");
        await DrainThroughAsync(barrier, ct).ConfigureAwait(false);
        return barrier;
    }

    private async Task<bool> ResetCoverageAsync(JournalReadClaim claim, JournalCapability current, CancellationToken ct)
    {
        if (!current.VolumeIdentity.Equals(claim.Cursor.VolumeIdentity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SourceJournalResetBindingInvalid");
        Guid[] rootIds;
        await using (var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
            rootIds = await lookup.SourceRootScanStates.AsNoTracking().Where(x => x.VolumeIdentity == current.VolumeIdentity)
                .Select(x => x.SourceRootId).ToArrayAsync(ct).ConfigureAwait(false);
        var accepted = false;
        await _journal.ExecuteAsync(async db =>
        {
            accepted = false;
            foreach (var id in rootIds.Order())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}", ct).ConfigureAwait(false);
            var point = await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={current.VolumeIdentity}").SingleAsync(ct).ConfigureAwait(false);
            if (point.LeaseOwner != claim.Owner || point.LeaseGeneration != claim.Generation || point.LeaseExpiresAtUtc <= time.GetUtcNow() ||
                point.NextUsn != claim.Cursor.NextUsn || point.JournalId != claim.Cursor.JournalId.ToString("X16")) return;
            if (point.RegistrationGeneration != claim.RegistrationGeneration)
            {
                point.LeaseOwner = null; point.LeaseExpiresAtUtc = null; point.DueAtUtc = null;
                return;
            }
            var states = await db.SourceRootScanStates.Where(x => x.VolumeIdentity == current.VolumeIdentity && rootIds.Contains(x.SourceRootId)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                SqlSourceIncrementalState.InvalidateCoverage(state, "journal-continuity-lost", current.NextUsn);
                state.CoveredUsn = current.NextUsn;
            }
            point.JournalId = current.JournalId.ToString("X16"); point.NextUsn = current.NextUsn;
            point.RegistrationGeneration++; point.LeaseOwner = null; point.LeaseExpiresAtUtc = null; point.DueAtUtc = null;
            point.LastBatchFingerprint = null; point.UnavailableSinceUtc = null; point.LastFailureCode = null;
            accepted = true;
        }, ct).ConfigureAwait(false);
        return accepted;
    }

    private async Task<SourceRootConfigurationEntity> RequireOwnedAsync(FluxKnowledgeDbContext db, SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
    {
        var current = await db.SourceRootConfigurations.FromSqlInterpolated(
            $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={root.Id.Value}").SingleAsync(ct).ConfigureAwait(false);
        if (current.State != (int)SourceRootState.Enabled || current.ConfigurationRevision != root.ConfigurationRevision ||
            current.CrawlMode != (int)root.DiscoveryMode || request.Lease is null ||
            !await SqlSourceScanStore.OwnsRootScanAsync(db, current.Id, new(request.Id, request.Lease), time.GetUtcNow(), ct).ConfigureAwait(false))
            throw new SourceSnapshotChangedException("SourceIncrementalScanAuthorityLost");
        return current;
    }
}
