using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceChangeJournalStore
{
    private const uint AncestorAuthorityReasons = 0x00113800;
    private const uint RenameOldName = 0x00001000;
    private const uint RenameNewName = 0x00002000;
    private const uint BasicInfoChange = 0x00008000;
    private const uint Close = 0x80000000;

    private async Task<AncestorCoverage> ObserveAncestorPathsAsync(Guid[] rootIds, JournalPage page, CancellationToken ct)
    {
        var result = new AncestorCoverage();
        if (rootIds.Length == 0 || !page.Changes.Any(change => (change.Reason & RenameOldName) != 0)) return result;
        var candidates = page.Changes.Where(change => (change.Reason & ~(Close | BasicInfoChange)) != 0)
            .GroupBy(change => change.FileId).Where(group => IsCaseOnlyRename(group.ToArray(), group.First().ParentId, group.First().Name))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (candidates.Count == 0) return result;
        var otherAuthorityChanges = page.Changes.Where(change => (change.Reason & AncestorAuthorityReasons) != 0 &&
                !candidates.Contains(change.FileId))
            .Select(change => change.FileId).Distinct().ToArray();

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roots = await db.SourceRootConfigurations.AsNoTracking()
            .Where(root => rootIds.Contains(root.Id) && root.State == (int)SourceRootState.Enabled &&
                db.SourceRootScanStates.Any(state => state.SourceRootId == root.Id && state.Mode == "Journal" &&
                    state.NamespaceComplete && !state.RecoveryRequired && state.CoveredUsn == page.Start.NextUsn) &&
                db.SourceInventoryEntries.Any(entry => entry.SourceRootId == root.Id && entry.IsAncestor && candidates.Contains(entry.FileId)) &&
                !db.SourceInventoryEntries.Any(entry => entry.SourceRootId == root.Id && entry.IsAncestor && otherAuthorityChanges.Contains(entry.FileId)))
            .OrderBy(root => root.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var reader = observations ?? new WindowsSourceObservationReader();
        var started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(2)) break;
                try
                {
                    var source = SqlSourceRemovalReconciler.Restore(root);
                    var policy = SqlRepositoryWorkRecovery.Policy(root);
                    if (reader.OpenRootForJournalCoverage(source) is { } lease)
                        result.Paths.Add(root.Id, new(root.ConfigurationRevision, policy, lease));
                }
                catch (Exception error) when (IsUncertainPath(error) || error is JsonException or DomainInvariantException) { }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static async Task<bool> CanKeepAncestorCoverageAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRootScanStateEntity state, JournalPage page, AncestorCoverage coverage, CancellationToken ct)
    {
        if (coverage.Rejected.Contains(root.Id) || !coverage.Paths.TryGetValue(root.Id, out var proof) || proof.Revision != root.ConfigurationRevision ||
            proof.Policy != state.PolicyFingerprint || proof.Policy != SqlRepositoryWorkRecovery.Policy(root) ||
            state.Mode != "Journal" || SqlSourceIncrementalState.RequiresBaseline(state) || !state.NamespaceComplete ||
            state.UnresolvedThroughUsn is not null || state.BaselineEpoch <= 0 ||
            state.VolumeIdentity != page.Start.VolumeIdentity || state.CoveredUsn != page.Start.NextUsn ||
            state.BaselineStartUsn > state.CoveredUsn)
            return false;

        var stored = await db.SourceInventoryEntries.AsNoTracking().Where(entry => entry.SourceRootId == root.Id &&
            (entry.IsAncestor || entry.RelativePath == ".")).ToDictionaryAsync(entry => entry.RelativePath, ct).ConfigureAwait(false);
        var chain = proof.Lease.Ancestors;
        if (stored.Count != chain.Count || chain.Count < 2 || chain.Select(entry => entry.FileId).Distinct().Count() != chain.Count ||
            chain.Any(entry => !stored.TryGetValue(entry.RelativePath, out var prior) || !prior.IsDirectory ||
                prior.IsAncestor != entry.IsAncestor || prior.BaselineEpoch != state.BaselineEpoch ||
                prior.FileId != entry.FileId || prior.ParentId != entry.ParentId))
            return false;

        var volumePath = Path.GetPathRoot(root.CanonicalPath)!;
        var names = Path.GetRelativePath(volumePath, root.CanonicalPath).Split(Path.DirectorySeparatorChar);
        if (chain.Count != names.Length + 1) return false;
        var changes = page.Changes.ToLookup(change => change.FileId);
        var provedRename = false;
        for (var i = 0; i < chain.Count; i++)
        {
            var component = chain[i];
            if (component.RelativePath != (i == chain.Count - 1 ? "." : $"!ancestor-{i}")) return false;
            var records = changes[component.FileId].Where(change => (change.Reason & ~(Close | BasicInfoChange)) != 0).ToArray();
            if (records.Length == 0) continue;
            // A case-only rename in one accepted page preserves the physical routing keys.
            // A root-level change, mixed reason or other change anywhere on the chain cannot
            // prove that. A CLOSE summary is useful only after the old/new pair is present.
            if (i == 0 || !component.IsAncestor || !IsCaseOnlyRename(records, component.ParentId, names[i - 1]))
                return false;
            provedRename = true;
        }

        if (provedRename) coverage.Used.Add(root.Id, proof.Lease);
        return provedRename;
    }

    private static bool IsCaseOnlyRename(JournalChange[] records, string parentId, string name)
    {
        if (records.Length is < 2 or > 3 || records.Any(change => !change.IsDirectory || change.ParentId != parentId) ||
            records[0].Usn >= records[1].Usn || (records[0].Reason & ~BasicInfoChange) != RenameOldName ||
            (records[1].Reason & ~BasicInfoChange) != RenameNewName ||
            !SameNtfsName(records[0].Name, name) || !SameNtfsName(records[1].Name, name))
            return false;

        return records.Length == 2 || records[1].Usn < records[2].Usn && records[2].Name == records[1].Name &&
            (records[2].Reason & ~BasicInfoChange) is (Close | RenameNewName) or (Close | RenameOldName | RenameNewName);
    }

    private static bool SameNtfsName(string observed, string configured)
    {
        if (observed.Length != configured.Length) return false;
        for (var i = 0; i < observed.Length; i++)
        {
            if (observed[i] == configured[i]) continue;
            // Do not infer NTFS collation for differing non-ASCII characters.
            if (!char.IsAsciiLetter(observed[i]) || !char.IsAsciiLetter(configured[i]) ||
                char.ToUpperInvariant(observed[i]) != char.ToUpperInvariant(configured[i])) return false;
        }
        return true;
    }

    private static bool IsUncertainPath(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException;

    private sealed record ObservedAncestorPath(long Revision, string Policy, ISourceRootObservationLease Lease);

    private sealed class AncestorCoverage : IDisposable
    {
        public Dictionary<Guid, ObservedAncestorPath> Paths { get; } = [];
        public Dictionary<Guid, ISourceRootObservationLease> Used { get; } = [];
        public HashSet<Guid> Rejected { get; } = [];

        public void ValidateBeforeCommit()
        {
            Exception? failure = null;
            foreach (var (id, lease) in Used)
            {
                try { lease.ValidateCurrent(); }
                catch (Exception error) when (IsUncertainPath(error))
                {
                    Rejected.Add(id);
                    failure ??= error;
                }
            }
            if (failure is not null) throw new AncestorCoverageChangedException(failure);
        }

        public void Dispose()
        {
            foreach (var path in Paths.Values) path.Lease.Dispose();
        }
    }

    private sealed class AncestorCoverageChangedException(Exception inner) : IOException("SourceAncestorCoverageChanged", inner);
}
