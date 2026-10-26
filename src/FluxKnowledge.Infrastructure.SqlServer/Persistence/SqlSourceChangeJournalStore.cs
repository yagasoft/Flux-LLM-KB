using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>The accepted cursor and every routed dirty generation share one commit.</summary>
public sealed class SqlSourceChangeJournalStore(IDbContextFactory<FluxKnowledgeDbContext> factory, TimeProvider time) : ISourceChangeJournalStore
{
    private readonly string _owner = $"journal:{Guid.NewGuid():N}";

    public ValueTask<JournalReadClaim?> ClaimAsync(DateTimeOffset now, CancellationToken ct) => ClaimCoreAsync(null, now, false, ct);

    public ValueTask<JournalReadClaim?> ClaimVolumeAsync(string volumeIdentity, DateTimeOffset now, bool ignoreIdleDelay, CancellationToken ct)
        => ClaimCoreAsync(volumeIdentity, now, ignoreIdleDelay, ct);

    private async ValueTask<JournalReadClaim?> ClaimCoreAsync(string? volumeIdentity, DateTimeOffset now, bool ignoreIdleDelay, CancellationToken ct)
    {
        JournalReadClaim? result = null;
        await ExecuteAsync(async db =>
        {
            var query = volumeIdentity is null ? db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT TOP (1) * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc]<={now}) AND ([DueAtUtc] IS NULL OR [DueAtUtc]<={now}) ORDER BY [VolumeIdentity]")
                : db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                    $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={volumeIdentity} AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc]<={now}) AND ({ignoreIdleDelay}=1 OR [DueAtUtc] IS NULL OR [DueAtUtc]<={now})");
            var row = await query.SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null) return;
            row.LeaseOwner = _owner; row.LeaseGeneration++; row.LeaseExpiresAtUtc = now.AddSeconds(30);
            result = new(new(row.VolumeIdentity, Convert.ToUInt64(row.JournalId, 16), row.NextUsn), _owner, row.LeaseGeneration, row.RegistrationGeneration);
        }, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask AcceptAsync(JournalReadClaim claim, JournalPage page, CancellationToken ct)
    {
        if (page.Start != claim.Cursor || page.Next.VolumeIdentity != page.Start.VolumeIdentity ||
            page.Next.JournalId != page.Start.JournalId || page.Next.NextUsn < page.Start.NextUsn ||
            page.Changes.Any(x => x.Usn < page.Start.NextUsn || x.Usn >= page.Next.NextUsn))
            throw new InvalidDataException("SourceJournalPageBindingInvalid");
        // The native range is immutable; a parent's current path is transient routing evidence.
        var fingerprint = Hash(JsonSerializer.Serialize(page with
        { Changes = page.Changes.Select(change => change with { ParentPath = null }).ToArray() }));
        Guid[] rootIds;
        // Discovery is intentionally outside the serializable transaction. Registration is
        // rechecked below; retaining state-range locks while waiting for root locks can deadlock enrolment.
        await using (var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
            rootIds = await lookup.SourceRootScanStates.AsNoTracking().Where(x => x.VolumeIdentity == claim.Cursor.VolumeIdentity)
                .Select(x => x.SourceRootId).ToArrayAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(async db =>
        {
            // Match the source lock order: root(s) before incremental records. A concurrent
            // registration changes the checkpoint generation and forces a reroute/retry.
            var roots = new List<SourceRootConfigurationEntity>();
            foreach (var id in rootIds.Order())
            {
                var root = await db.SourceRootConfigurations.FromSqlInterpolated(
                    $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}")
                    .SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (root is not null) roots.Add(root);
            }
            var checkpoint = await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={claim.Cursor.VolumeIdentity}")
                .SingleAsync(ct).ConfigureAwait(false);
            if (checkpoint.LeaseOwner == claim.Owner && checkpoint.LeaseGeneration == claim.Generation &&
                checkpoint.RegistrationGeneration != claim.RegistrationGeneration)
            {
                // The newly enrolled root must participate in routing this exact range.
                checkpoint.LeaseOwner = null; checkpoint.LeaseExpiresAtUtc = null; checkpoint.DueAtUtc = null;
                return;
            }
            if (checkpoint.LastBatchFingerprint == fingerprint && checkpoint.NextUsn == page.Next.NextUsn)
            {
                // A retry of a committed range must not release another reader's claim. A new
                // owned empty read at the same cursor is different: it still completes its lease.
                if (page.Start == page.Next && checkpoint.LeaseOwner == claim.Owner && checkpoint.LeaseGeneration == claim.Generation &&
                    checkpoint.LeaseExpiresAtUtc > time.GetUtcNow() && checkpoint.JournalId == claim.Cursor.JournalId.ToString("X16") &&
                    checkpoint.RegistrationGeneration == claim.RegistrationGeneration)
                {
                    checkpoint.LeaseOwner = null; checkpoint.LeaseExpiresAtUtc = null;
                    checkpoint.DueAtUtc = time.GetUtcNow().AddSeconds(2);
                    checkpoint.UnavailableSinceUtc = null; checkpoint.LastFailureCode = null;
                }
                return;
            }
            if (checkpoint.LeaseOwner != claim.Owner || checkpoint.LeaseGeneration != claim.Generation ||
                checkpoint.LeaseExpiresAtUtc <= time.GetUtcNow() || checkpoint.NextUsn != claim.Cursor.NextUsn ||
                checkpoint.JournalId != claim.Cursor.JournalId.ToString("X16") || checkpoint.RegistrationGeneration != claim.RegistrationGeneration)
                throw new InvalidOperationException("SourceJournalClaimStale");
            var states = await db.SourceRootScanStates.Where(x => rootIds.Contains(x.SourceRootId)).ToListAsync(ct).ConfigureAwait(false);
            var identities = page.Changes.SelectMany(x => new[] { x.FileId, x.ParentId }).Distinct().ToArray();
            var entries = await db.SourceInventoryEntries.Where(x => rootIds.Contains(x.SourceRootId) && identities.Contains(x.FileId))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var disabled in roots.Where(x => x.State != (int)SourceRootState.Enabled))
            {
                var state = states.Single(x => x.SourceRootId == disabled.Id);
                if (!SqlSourceIncrementalState.RequiresBaseline(state) || state.Reason != "root-not-enabled")
                    SqlSourceIncrementalState.InvalidateCoverage(state, "root-not-enabled", page.Next.NextUsn);
            }
            foreach (var root in roots.Where(x => x.State == (int)SourceRootState.Enabled))
            {
                var state = states.Single(x => x.SourceRootId == root.Id);
                var owned = entries.Where(x => x.SourceRootId == root.Id && x.BaselineEpoch == state.BaselineEpoch).ToArray();
                var targets = new Dictionary<string, (long Reasons, bool Subtree, long First, long Last, long Content)>(StringComparer.OrdinalIgnoreCase);
                var namespaceChanged = false;
                foreach (var change in page.Changes)
                {
                    var aliases = owned.Where(x => x.FileId == change.FileId).ToArray();
                    foreach (var alias in aliases)
                    {
                        if (alias.LatestJournalUsn is not null && alias.LatestJournalUsn >= change.Usn) continue;
                        alias.LatestJournalUsn = change.Usn; alias.LatestJournalReason = change.Reason;
                        // A CLOSE never promotes an old capture. Only the guarded capture
                        // receipt grants a seal; later content evidence revokes it.
                        if (!alias.IsDirectory && change.ContentChanged)
                        { alias.IsSealed = false; alias.NextForcedCheckAtUtc ??= time.GetUtcNow().AddSeconds(root.ReconciliationCadenceSeconds); }
                    }
                    if (aliases.Any(x => x.IsAncestor) && (change.Reason & 0x00113800) != 0)
                    {
                        SqlSourceIncrementalState.InvalidateCoverage(state, "ancestor-authority-changed", page.Next.NextUsn);
                    }
                    foreach (var alias in aliases.Where(x => !x.IsAncestor)) Add(alias.RelativePath, change, alias.IsDirectory);
                    var parent = owned.FirstOrDefault(x => x.FileId == change.ParentId && x.IsDirectory && !x.IsAncestor);
                    if (parent is not null)
                        Add(parent.RelativePath == "." ? change.Name : Path.Combine(parent.RelativePath, change.Name), change, change.IsDirectory);
                    else if (change.ParentPath is { } parentPath && IsWithin(root.CanonicalPath, parentPath))
                        Add(Path.GetRelativePath(root.CanonicalPath, Path.Combine(parentPath, change.Name)), change, change.IsDirectory);
                    else if (aliases.Length == 0 && !state.NamespaceComplete && change.ParentPath is null)
                    {
                        state.UnresolvedThroughUsn = page.Next.NextUsn;
                        namespaceChanged = true;
                        targets["."] = targets.TryGetValue(".", out var debt)
                            ? (debt.Reasons | SourcePendingReasons.NamespaceMetadata, true, debt.First, change.Usn, debt.Content)
                            : (SourcePendingReasons.NamespaceMetadata, true, change.Usn, change.Usn, 0);
                    }
                }
                if (namespaceChanged)
                {
                    state.NamespaceComplete = false; state.NamespaceGeneration++;
                    if (state.Mode == "Journal") state.Mode = "JournalPartial";
                }
                if (targets.Count > 0)
                {
                    state.Generation++;
                    var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToListAsync(ct).ConfigureAwait(false);
                    foreach (var (path, item) in targets)
                    {
                        var key = PathKey(path);
                        var row = pending.SingleOrDefault(x => x.PathFingerprint == key);
                        if (row is not null && !row.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("SourceJournalPathCollision");
                        if (row is null)
                        {
                            if (pending.Count >= 10000) { SqlSourceIncrementalState.InvalidateCoverage(state, "pending-path-overflow", page.Next.NextUsn); break; }
                            row = new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = path, PathFingerprint = key,
                                FirstUsn = item.First, LastContentUsn = 0, LastUnjournaledGeneration = 0 };
                            pending.Add(row); db.SourcePendingChanges.Add(row);
                        }
                        row.ReasonFlags |= item.Reasons; row.IsSubtree |= item.Subtree;
                        row.Generation = state.Generation; row.LastUsn = item.Last;
                        if (row.LastContentUsn is { } priorContent) row.LastContentUsn = Math.Max(priorContent, item.Content);
                        row.FirstObservedAtUtc ??= time.GetUtcNow();
                        if ((row.ReasonFlags & SourcePendingReasons.NamespaceMetadata) != 0 && (row.ReasonFlags & uint.MaxValue) == 0)
                        {
                            var backstop = (state.LastReconciliationAtUtc ?? row.FirstObservedAtUtc.Value).AddSeconds(root.ReconciliationCadenceSeconds);
                            if (backstop < time.GetUtcNow()) backstop = time.GetUtcNow();
                            if (row.NotBeforeUtc is null || row.NotBeforeUtc > backstop) row.NotBeforeUtc = backstop;
                        }
                        else
                        {
                            var quietDue = time.GetUtcNow().AddSeconds(2);
                            var maximumDue = row.FirstObservedAtUtc.Value.AddSeconds(30);
                            row.NotBeforeUtc = quietDue < maximumDue ? quietDue : maximumDue;
                        }
                    }
                }
                state.CoveredUsn = page.Next.NextUsn;

                void Add(string path, JournalChange change, bool subtree)
                {
                    path = NormaliseRelative(path);
                    // Keep cursor/latest-record evidence above, but a duplicate pure
                    // CLOSE cannot create work or advance existing work generations.
                    // The same physical ID at a new path still needs discovery.
                    if (!subtree && change.Reason == 0x80000000u && owned.Any(entry => entry.IsSealed && !entry.IsDirectory &&
                        entry.FileId == change.FileId && entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                    // Directory BASIC_INFO/CLOSE notifications do not imply changed descendants.
                    if (subtree && (change.Reason & ~0x80008000u) == 0) return;
                    if (!root.Recursive && path.Contains(Path.DirectorySeparatorChar)) return;
                    if (!subtree && !LocalSourceEnumerator.MatchesPolicy(path,
                            JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson) ?? [],
                            JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson) ?? [])) return;
                    long reasons = change.Reason;
                    if (subtree && (change.Reason & 0x00103300) != 0)
                    { reasons |= SourcePendingReasons.ScopedNamespace; namespaceChanged = true; }
                    var content = change.ContentChanged ? change.Usn : 0;
                    if (targets.TryGetValue(path, out var old)) targets[path] = (old.Reasons | reasons, old.Subtree || subtree, old.First, change.Usn, Math.Max(old.Content, content));
                    else targets.Add(path, (reasons, subtree, change.Usn, change.Usn, content));
                }
            }
            checkpoint.NextUsn = page.Next.NextUsn; checkpoint.LastBatchFingerprint = fingerprint;
            checkpoint.UnavailableSinceUtc = null; checkpoint.LastFailureCode = null;
            checkpoint.LeaseOwner = null; checkpoint.LeaseExpiresAtUtc = null;
            checkpoint.DueAtUtc = page.Changes.Count == 0 ? time.GetUtcNow().AddSeconds(2) : null;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask MarkUnavailableAsync(JournalReadClaim claim, DateTimeOffset retryAt, CancellationToken ct)
    {
        await ExecuteAsync(async db =>
        {
            var point = await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={claim.Cursor.VolumeIdentity}")
                .SingleAsync(ct).ConfigureAwait(false);
            if (point.LeaseOwner != claim.Owner || point.LeaseGeneration != claim.Generation ||
                point.LeaseExpiresAtUtc <= time.GetUtcNow() || point.JournalId != claim.Cursor.JournalId.ToString("X16") ||
                point.NextUsn != claim.Cursor.NextUsn) return;
            if (point.RegistrationGeneration != claim.RegistrationGeneration)
            { point.LeaseOwner = null; point.LeaseExpiresAtUtc = null; point.DueAtUtc = null; return; }
            point.LeaseOwner = null; point.LeaseExpiresAtUtc = null; point.DueAtUtc = retryAt;
            point.UnavailableSinceUtc ??= time.GetUtcNow(); point.LastFailureCode = "journal-unavailable";
        }, ct).ConfigureAwait(false);
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string PathKey(string relative) => Hash(NormaliseRelative(relative).ToUpperInvariant());
    internal static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string NormaliseRelative(string value)
    {
        value = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value) || value.IndexOfAny([':', '\0']) >= 0 ||
            value.Split(Path.DirectorySeparatorChar).Any(x => x == "..")) throw new InvalidDataException("SourceJournalRelativePathInvalid");
        return value;
    }
    internal async Task ExecuteAsync(Func<FluxKnowledgeDbContext, Task> action, CancellationToken ct, Action? beforeCommit = null)
    {
        await using var strategy = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await action(db).ConfigureAwait(false); await db.SaveChangesAsync(ct).ConfigureAwait(false);
            beforeCommit?.Invoke(); await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
