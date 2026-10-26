using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    private sealed record CompletionCandidate(Guid Id, string CanonicalPath);
    internal Func<int, CancellationToken, ValueTask>? BeforeCompletionPage { get; init; }
    internal static GitTrackedSourceDiscovery.Inventory RequireGitCompletionInventory(SourceRootConfiguration root,
        GitInventoryEvidence? evidence, GitTrackedSourceDiscovery.Inventory current)
    {
        if (evidence is null || !current.IsComplete || root.RepositoryIdentityFingerprint is not { Length: 64 } ||
            evidence.RepositoryIdentity != root.RepositoryIdentityFingerprint || current.Repository.Identity != evidence.RepositoryIdentity ||
            evidence.Generation is not { Length: 64 } || !evidence.Generation.All(Uri.IsHexDigit) || current.Generation != evidence.Generation)
            throw new SourceSnapshotChangedException("GitInventoryChanged");
        return current;
    }

    public async ValueTask<bool> CompleteAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan,
        IReadOnlyList<SourceNamespaceObservation> directories, IReadOnlyList<SourceEnumerationEvidence> enumerationEvidence,
        IReadOnlySet<SourceRevisionId> seen, GitInventoryEvidence? inventory, CancellationToken ct)
    {
        var evidence = enumerationEvidence.ToList();
        using var rootAuthority = observations.OpenRoot(root);
        JournalCapability? closingBarrier = null;
        if (plan.Cursor is { } cursor)
        {
            closingBarrier = await reader.QueryAsync(cursor.VolumeIdentity, ct).ConfigureAwait(false);
            await DrainThroughAsync(closingBarrier, ct).ConfigureAwait(false);
        }
        var closingGit = root.DiscoveryMode == SourceDiscoveryMode.GitTracked
            ? RequireGitCompletionInventory(root, inventory, await new GitTrackedSourceDiscovery().ReadAsync(root.CanonicalPath, ct).ConfigureAwait(false))
            : null;
        var seenIds = seen.Select(x => x.Value).ToArray();
        var seenSet = seenIds.ToHashSet();
        bool full = false;
        await _journal.ExecuteAsync(async db =>
        {
            await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            SqlSourceIncrementalState.RequirePlan(state, plan);
            var old = await db.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id.Value && x.IsDirectory).ToDictionaryAsync(x => x.PathFingerprint, ct).ConfigureAwait(false);
            foreach (var directory in directories.Concat(rootAuthority.Ancestors).DistinctBy(x => x.RelativePath))
            {
                var key = SqlSourceChangeJournalStore.PathKey(directory.RelativePath);
                if (!old.TryGetValue(key, out var entry))
                {
                    entry = new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, PathFingerprint = key };
                    old[key] = entry; db.SourceInventoryEntries.Add(entry);
                }
                entry.RelativePath = directory.RelativePath; entry.FileId = directory.FileId; entry.ParentId = directory.ParentId;
                entry.IsDirectory = true; entry.IsAncestor = directory.IsAncestor; entry.BaselineEpoch = state.BaselineEpoch;
            }
            full = plan.IsNamespaceSweep && evidence.Count == 0 && HasCompleteNamespace(state);
        }, ct, rootAuthority.ValidateCurrent).ConfigureAwait(false);

        await using (var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var tracked = closingGit?.Paths.Select(x => x.Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Guid? after = null;
            for (var page = 1; ; page++)
            {
                // A large NOT IN(OPENJSON(seen)) can produce quadratic work. Only
                // candidate discovery is paged: absence/ownership still authorise removal below.
                var query = after is { } cursorAfter
                    ? lookup.Database.SqlQuery<CompletionCandidate>($"""
                        SELECT TOP(512) [Id], [CanonicalPath] FROM [SourceRevisions]
                        WHERE [SourceRootId]={root.Id.Value} AND [OriginKind]=0 AND [SuppressedAtUtc] IS NULL AND [Id]>{cursorAfter}
                        ORDER BY [Id]
                        """)
                    : lookup.Database.SqlQuery<CompletionCandidate>($"""
                        SELECT TOP(512) [Id], [CanonicalPath] FROM [SourceRevisions]
                        WHERE [SourceRootId]={root.Id.Value} AND [OriginKind]=0 AND [SuppressedAtUtc] IS NULL
                        ORDER BY [Id]
                        """);
                var candidates = await query.ToArrayAsync(ct).ConfigureAwait(false);
                if (candidates.Length == 0) break;
                // SQL uniqueidentifier ordering differs from Guid.CompareTo. Advance
                // using the last RAW row even when every row is seen/out of scope.
                after = candidates[^1].Id;
                if (BeforeCompletionPage is { } beforePage) await beforePage(page, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var selected = candidates.Where(x => !seenSet.Contains(x.Id) && InDeclaredScope(Path.GetRelativePath(root.CanonicalPath, x.CanonicalPath)))
                    .Select(x => x.Id).ToArray();
                foreach (var chunk in selected.Chunk(256))
                {
                    var absenceLeases = new List<ISourceAbsenceLease>();
                    try
                    {
                        await _journal.ExecuteAsync(async db =>
                        {
                            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
                            await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                            SqlSourceIncrementalState.RequirePlan(state, plan);
                            if (full && !HasCompleteNamespace(state)) full = false;
                            var rows = await db.SourceRevisions.Where(x => chunk.Contains(x.Id) && x.SuppressedAtUtc == null).ToArrayAsync(ct).ConfigureAwait(false);
                            var scopes = new List<IndexAffectedScope>();
                            foreach (var revision in rows)
                            {
                                var relative = Path.GetRelativePath(root.CanonicalPath, revision.CanonicalPath);
                                if (!InDeclaredScope(relative)) continue;
                                var excluded = !LocalSourceEnumerator.MatchesPolicy(relative, root.IncludePatterns, root.ExcludePatterns) ||
                                    !root.Recursive && relative.Contains(Path.DirectorySeparatorChar) || tracked is not null && !tracked.Contains(relative);
                                if (!excluded)
                                {
                                    ISourceAbsenceLease? absent;
                                    try { absent = observations.OpenAbsence(root, relative); }
                                    catch (SourceContentUnavailableException error)
                                    {
                                        evidence.Add(new("partial", Path.GetDirectoryName(relative) is { Length: > 0 } parent ? parent : ".", error.Message)
                                            { NamespaceIncomplete = true });
                                        continue;
                                    }
                                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                                    if (absent is null) continue;
                                    absenceLeases.Add(absent); absent.ValidateCurrent();
                                }
                                revision.SuppressedAtUtc = time.GetUtcNow(); revision.RetainUntilUtc = time.GetUtcNow().AddDays(30);
                                revision.RetentionEvidenceJson = JsonSerializer.Serialize(new SourceAuthoritativeRemovalWitness(1,
                                    root.Id.Value, revision.Id, relative, root.ConfigurationRevision, state.BaselineEpoch, state.RecoveryGeneration,
                                    request.Id.Value, plan.ManifestId, request.Lease!.Generation, excluded ? "OutOfScope" : "Deleted",
                                    revision.SuppressedAtUtc.Value, closingBarrier?.JournalId.ToString("X16"), closingBarrier?.NextUsn ?? 0,
                                    closingGit?.Repository.Identity, closingGit?.Generation));
                                revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Withdraw();
                                scopes.Add(new(2, revision.Id));
                                OperatorEventAppender.Add(db, OperatorEventDraft.SourceRemoved(root.Id.Value, revision.Id, $"source:{revision.Id:N}",
                                    new { revision = revision.Revision, reason = "incremental-authoritative-absence" }));
                            }
                            if (scopes.Count > 0) await SqlPublishedPassageSelection.AdvanceVersionAsync(db, time.GetUtcNow(), ct, scopes).ConfigureAwait(false);
                        }, ct, () => { rootAuthority.ValidateCurrent(); foreach (var lease in absenceLeases) lease.ValidateCurrent(); }).ConfigureAwait(false);
                    }
                    finally { foreach (var lease in absenceLeases) lease.Dispose(); }
                }
                if (candidates.Length < 512) break;
            }
        }

        await _journal.ExecuteAsync(async db =>
        {
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            SqlSourceIncrementalState.RequirePlan(state, plan);
            var oldIssues = JsonSerializer.Deserialize<SourceEnumerationEvidence[]>(state.DeferredEvidenceJson ?? "[]") ?? [];
            var deferred = plan.IsNamespaceSweep ? new List<SourceEnumerationEvidence>() : oldIssues.Where(x => !plan.Targets.Any(t =>
                SourcePathScope.Contains(t.RelativePath, t.IsSubtree, x.RelativePath))).ToList();
            deferred.AddRange(evidence.Where(x => x.Detail != "SourcePathNoLongerExists"));
            state.DeferredEvidenceJson = JsonSerializer.Serialize(deferred.Distinct().ToArray());
            var namespaceComplete = HasCompleteNamespace(state);
            if (namespaceComplete) { state.NamespaceComplete = true; state.UnresolvedThroughUsn = null; }
            if (evidence.Any(NamespaceIssue)) { state.NamespaceComplete = false; state.NamespaceGeneration++; }
            if (plan.IsBaseline)
            {
                state.LastBaselineAtUtc = time.GetUtcNow();
                if (!plan.IsFallback)
                {
                    state.RecoveryRequired = false;
                    if (state.Mode == "Baselining") state.Mode = "JournalPartial";
                }
                state.RecoveryInProgress = false;
            }
            if (plan.IsBaseline || plan.IsReconciliation) state.LastReconciliationAtUtc = time.GetUtcNow();
            state.LastStatisticsJson = JsonSerializer.Serialize(new { kind = plan.IsBaseline ? "baseline" : "targeted", seen = seen.Count, targets = plan.Targets.Count, deferred = deferred.Count });
            var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var item in pending.Where(x => plan.Targets.Any(target =>
                target.Generation == x.Generation && target.RelativePath.Equals(x.RelativePath, StringComparison.OrdinalIgnoreCase))))
            {
                var failed = deferred.Any(x => SourcePathScope.Contains(item.RelativePath, item.IsSubtree, x.RelativePath)) ||
                    (item.ReasonFlags & SourcePendingReasons.NamespaceMetadata) != 0 && !namespaceComplete;
                if (!failed) db.SourcePendingChanges.Remove(item);
                else
                {
                    item.NotBeforeUtc = time.GetUtcNow().Add(root.ReconciliationCadence);
                    item.RetryGeneration = item.Generation;
                }
            }
            foreach (var issue in deferred)
            {
                var path = SqlSourceChangeJournalStore.NormaliseRelative(issue.RelativePath);
                var key = SqlSourceChangeJournalStore.PathKey(path);
                if (pending.Any(x => x.PathFingerprint == key)) continue;
                db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, RelativePath = path,
                    PathFingerprint = key, Generation = ++state.Generation, IsSubtree = NamespaceIssue(issue),
                    ReasonFlags = NamespaceIssue(issue) ? SourcePendingReasons.NamespaceMetadata : 1,
                    LastContentUsn = 0, LastUnjournaledGeneration = state.Generation,
                    FirstObservedAtUtc = time.GetUtcNow(), NotBeforeUtc = time.GetUtcNow().Add(root.ReconciliationCadence),
                    RetryGeneration = state.Generation });
            }
            var remaining = db.ChangeTracker.Entries<SourcePendingChangeEntity>().Where(x => x.State != EntityState.Deleted &&
                x.Entity.SourceRootId == root.Id.Value).Select(x => x.Entity).ToArray();
            if (!plan.IsFallback && !SqlSourceIncrementalState.RequiresBaseline(state) && state.UnresolvedThroughUsn is null &&
                !deferred.Any(NamespaceIssue) && !remaining.Any(x => (x.ReasonFlags & SourcePendingReasons.NamespaceMask) != 0))
                state.NamespaceComplete = true;
            if (plan.IsFallback) state.NamespaceComplete = false;
            if (plan.IsNamespaceSweep)
                foreach (var item in remaining.Where(x => x.ReasonFlags == SourcePendingReasons.NamespaceMetadata))
                {
                    item.NotBeforeUtc = time.GetUtcNow().Add(root.ReconciliationCadence);
                    item.RetryGeneration = item.Generation;
                }
            state.Mode = plan.IsFallback ? "Fallback" : SqlSourceIncrementalState.RequiresBaseline(state) ? "BaselineRequired" :
                deferred.Count == 0 && state.NamespaceComplete ? "Journal" : "JournalPartial";
            state.Reason = state.Mode switch { "Fallback" => "journal-unavailable", "JournalPartial" => "deferred-or-incomplete-namespace", "Journal" => null, _ => state.Reason };
            if (inventory is not null && evidence.Count == 0)
            {
                foreach (var revision in await db.SourceRevisions.Where(x => seenIds.Contains(x.Id)).ToArrayAsync(ct).ConfigureAwait(false))
                {
                    var proof = SqlRepositoryWorkRecovery.ReadObservation(revision.CurrentDiscoveryEvidenceJson);
                    if (proof is not null && proof.ScanRequestId == request.Id.Value && proof.Inventory == inventory.Generation && proof.Repository == inventory.RepositoryIdentity &&
                        proof.ConfigurationRevision == current.ConfigurationRevision && proof.Policy == SqlRepositoryWorkRecovery.Policy(current) &&
                        proof.LeaseGeneration == request.Lease!.Generation)
                        revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Authorize(proof, SqlRepositoryWorkRecovery.RequiresRecovery(revision));
                }
            }
            full &= state.NamespaceComplete && state.NamespaceGeneration == plan.NamespaceGeneration;
        }, ct, rootAuthority.ValidateCurrent).ConfigureAwait(false);
        return full;

        bool InDeclaredScope(string relative) => plan.IsBaseline || plan.Targets.Any(x => x.HasCaptureScope &&
            SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relative));

        bool HasCompleteNamespace(SourceRootScanStateEntity state) => !plan.IsFallback && plan.IsNamespaceSweep &&
            !evidence.Any(NamespaceIssue) && state.NamespaceGeneration == plan.NamespaceGeneration && closingBarrier is not null &&
            state.CoveredUsn >= closingBarrier.NextUsn && (state.UnresolvedThroughUsn is null || state.UnresolvedThroughUsn <= closingBarrier.NextUsn);
    }

    private static bool NamespaceIssue(SourceEnumerationEvidence issue) => issue.NamespaceIncomplete ||
        issue.Kind is "partial" or "identity" or "git" or "limit" or "policy";
}
