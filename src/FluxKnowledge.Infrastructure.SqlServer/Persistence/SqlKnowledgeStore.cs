using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Knowledge;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Retained-only read projections for native notes, claims and their derived graph.</summary>
public sealed class SqlKnowledgeStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    Action<int>? traversalRowsMaterialized = null) : IKnowledgeStore
{
    public async ValueTask<KnowledgeTarget?> FindTargetAsync(KnowledgeMutation mutation, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (mutation.Action == "note_create") return null;
        if (mutation.Action == "claim_upsert")
        {
            var claim = KnowledgeClaim.Create(mutation.Subject!, mutation.Predicate!, mutation.ObjectText!, mutation.Confidence!.Value);
            var identityHash = Hash(claim.CanonicalIdentity);
            var existing = await context.KnowledgeClaims.AsNoTracking().SingleOrDefaultAsync(value => value.CanonicalIdentityHash == identityHash && value.CanonicalIdentity == claim.CanonicalIdentity && value.ForgottenAtUtc == null, cancellationToken);
            return new KnowledgeTarget($"claim:{identityHash}", existing is null ? "absent" : Convert.ToBase64String(existing.RowVersion));
        }

        var id = Guid.Parse(mutation.ItemId!);
        var item = await context.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id && value.ForgottenAtUtc == null, cancellationToken);
        if (item is not null) return new KnowledgeTarget($"item:{id:D}", Convert.ToBase64String(item.RowVersion));
        var claimTarget = await context.KnowledgeClaims.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id && value.ForgottenAtUtc == null, cancellationToken);
        return claimTarget is null ? null : new KnowledgeTarget($"claim:{id:D}", Convert.ToBase64String(claimTarget.RowVersion));
    }

    public async ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var needle = NativeV1ContractLimits.CanonicalizeKnowledgeQuery(query);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var notes = await context.KnowledgeItems.AsNoTracking()
            .Where(value => value.ForgottenAtUtc == null && (value.Title.StartsWith(needle) || value.SafeSearchText.Contains(needle)))
            .OrderBy(value => value.Id).Take(limit)
            .Select(value => new KnowledgeSearchResult(value.Id, "note", value.Title, value.SafeBody, "knowledge"))
            .ToListAsync(cancellationToken);
        var remaining = limit - notes.Count;
        if (remaining <= 0) return notes;
        var claims = await context.KnowledgeClaims.AsNoTracking()
            .Where(value => value.ForgottenAtUtc == null && value.LifecycleState == "active" && (value.Subject.StartsWith(needle) || value.SafeSearchText.Contains(needle)))
            .OrderBy(value => value.Id).Take(remaining)
            .Select(value => new KnowledgeSearchResult(value.Id, "claim", value.Subject, value.Subject + " " + value.Predicate + " " + value.ObjectText, "knowledge", value.Confidence))
            .ToListAsync(cancellationToken);
        return [.. notes, .. claims];
    }

    public async ValueTask<Guid?> ResolveCodexWorkspaceAsync(string cwd, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveCodexWorkspaceAsync(context, cwd, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchCodexWorkspaceAsync(
        string query, string cwd, int limit, CancellationToken cancellationToken)
    {
        var needle = NativeV1ContractLimits.CanonicalizeKnowledgeQuery(query);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rootId = await ResolveCodexWorkspaceAsync(context, cwd, cancellationToken).ConfigureAwait(false)
            ?? throw new NativeOperationException("scope-unavailable");

        var notes = await context.KnowledgeItems.AsNoTracking()
            .Where(note => note.CodexWorkspaceRootId == rootId && note.ForgottenAtUtc == null &&
                (note.Title.StartsWith(needle) || note.SafeSearchText.Contains(needle)))
            .OrderByDescending(note => note.CreatedAtUtc).ThenBy(note => note.Id).Take(limit)
            .Select(note => new KnowledgeSearchResult(note.Id, "note", note.Title, note.SafeBody, "knowledge"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The closest registered root can change while the note page is read.
        if (await ResolveCodexWorkspaceAsync(context, cwd, cancellationToken).ConfigureAwait(false) != rootId)
            throw new NativeOperationException("scope-unavailable");
        return notes;
    }

    private static async ValueTask<Guid?> ResolveCodexWorkspaceAsync(
        FluxKnowledgeDbContext context, string cwd, CancellationToken cancellationToken)
    {
        if (cwd.Length > 2048 || cwd.Any(char.IsControl) || (cwd.Length > 2 && cwd.AsSpan(2).Contains(':')) ||
            !SqlCorpusRetrievalReader.TryCanonicalCwd(Path.TrimEndingDirectorySeparator(cwd), out var canonicalCwd) ||
            canonicalCwd[3..].Split('\\').Any(IsInvalidWindowsDirectoryComponent))
            return null;

        var roots = await context.SourceRootConfigurations.AsNoTracking()
            .Select(root => new { root.Id, root.CanonicalPath, root.State })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var closestPath = roots
            .Where(root => SqlCorpusRetrievalReader.IsContained(root.CanonicalPath, canonicalCwd))
            .GroupBy(root => Path.TrimEndingDirectorySeparator(root.CanonicalPath).Length)
            .OrderByDescending(group => group.Key)
            .FirstOrDefault();
        var candidates = closestPath?.ToArray();
        return candidates is [{ State: not (int)SourceRootState.Deleting } root] ? root.Id : null;
    }

    private static bool IsInvalidWindowsDirectoryComponent(string component)
    {
        if (component.EndsWith(' ') || component.EndsWith('.')) return true;
        var stem = component.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                 stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[^1]));
    }

    public async ValueTask<IReadOnlyList<KnowledgeGraphResult>> TraverseAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken)
    {
        var boundedNode = NativeV1ContractLimits.CanonicalizeGraphNode(node);
        var canonicalNode = string.Join(' ', boundedNode.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var frontier = new SortedSet<string>(StringComparer.Ordinal) { canonicalNode };
        var seenClaims = new HashSet<Guid>();
        var result = new List<KnowledgeGraphResult>();
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && result.Count < maxResults; depth++)
        {
            var remaining = maxResults - result.Count;
            var currentFrontier = frontier.ToArray();
            var candidates = from relation in context.KnowledgeRelations.AsNoTracking()
                             join claim in context.KnowledgeClaims.AsNoTracking() on relation.ClaimId equals claim.Id
                             where claim.ForgottenAtUtc == null && claim.LifecycleState == "active" &&
                                   (currentFrontier.Contains(relation.Subject) || currentFrontier.Contains(relation.ObjectText))
                             select new { relation.ClaimId, relation.Subject, relation.Predicate, relation.ObjectText };
            if (seenClaims.Count > 0)
            {
                var seenAtThisLevel = seenClaims.ToArray();
                candidates = candidates.Where(relation => !seenAtThisLevel.Contains(relation.ClaimId));
            }
            var rows = await candidates
                .OrderBy(relation => relation.ClaimId)
                .Take(remaining)
                .ToListAsync(cancellationToken);
            traversalRowsMaterialized?.Invoke(rows.Count);
            var next = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var relation in rows)
            {
                if (!seenClaims.Add(relation.ClaimId)) continue;
                result.Add(new KnowledgeGraphResult(relation.ClaimId, relation.Subject, relation.Predicate, relation.ObjectText, depth));
                if (result.Count == maxResults) break;
                next.Add(relation.Subject); next.Add(relation.ObjectText);
            }
            frontier = next;
        }
        return result;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
