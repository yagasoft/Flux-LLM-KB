using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

/// <summary>Reads bounded canonical chunks from completed, currently eligible publications.</summary>
public sealed class SqlCorpusRetrievalReader(IDbContextFactory<FluxKnowledgeDbContext> contextFactory)
    : ICorpusRetrievalReader
{
    private const int CandidateBudget = 200;

    public async ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await context.Database.SqlQuery<FullTextStateRow>(
            $"""
             SELECT CAST(CASE WHEN EXISTS (
                 SELECT 1 FROM sys.fulltext_indexes
                 WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]'))
                 THEN 1 ELSE 0 END AS int) AS [IndexPresent],
                 CAST(CASE WHEN FULLTEXTCATALOGPROPERTY(N'FluxKnowledge', N'PopulateStatus') = 0
                     AND TRY_CONVERT(int, OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[TextChunks]'), N'TableFulltextPopulateStatus')) = 0
                     THEN 1 ELSE 0 END AS int) AS [PopulationComplete]
             """)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return new CorpusLexicalReadiness(state.IndexPresent == 1, state.PopulationComplete == 1);
    }

    public async ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(
        string query, CancellationToken cancellationToken)
    {
        // Parse the same English FREETEXT expansion used by the chunk index.
        // Sanitising to words makes the parser expression data, never caller syntax.
        var words = new StringBuilder(query.Length);
        foreach (var character in query)
            words.Append(char.IsLetterOrDigit(character) ? character : ' ');
        var normalised = string.Join(' ', words.ToString().Split(' ',
            StringSplitOptions.RemoveEmptyEntries));
        if (normalised.Length == 0) return [];
        var parserQuery = $"FORMSOF(FREETEXT, \"{normalised}\")";
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.SqlQuery<string>(
                $"""
                 SELECT DISTINCT TOP (512) CAST([display_term] AS nvarchar(4000)) AS [Value]
                 FROM sys.dm_fts_parser({parserQuery}, 1033, 0, 0)
                 WHERE [special_term] = N'Exact Match'
                   AND [display_term] <> N''
                 ORDER BY [Value]
                 """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(
        string kind, Guid? rootId, string? cwd, CancellationToken cancellationToken)
    {
        if (kind == "all" && rootId is null && cwd is null)
            return new ResolvedCorpusScope("all", [], null);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (kind == "root" && rootId.HasValue && cwd is null)
        {
            var exists = await context.SourceRootConfigurations.AsNoTracking().AnyAsync(
                root => root.Id == rootId.Value && root.State != (int)SourceRootState.Deleting,
                cancellationToken).ConfigureAwait(false);
            return exists ? new ResolvedCorpusScope("root", [rootId.Value], null) : null;
        }

        if (kind != "workspace" || rootId.HasValue || !TryCanonicalCwd(cwd, out var canonicalCwd))
            return null;

        var roots = await context.SourceRootConfigurations.AsNoTracking()
            .Where(root => root.State != (int)SourceRootState.Deleting)
            .Select(root => new { root.Id, root.CanonicalPath })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var ids = roots
            .Where(root => IsContained(canonicalCwd, root.CanonicalPath) ||
                           IsContained(root.CanonicalPath, canonicalCwd))
            .Select(root => root.Id).Order().ToArray();
        return ids.Length == 0 ? null : new ResolvedCorpusScope("workspace", ids, canonicalCwd);
    }

    public async ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(
        string query, ResolvedCorpusScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(scope);
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        if (scope.Kind is not ("all" or "root" or "workspace")) throw new ArgumentException("Unknown scope.", nameof(scope));

        var rootIdsJson = JsonSerializer.Serialize(scope.RootIds);
        var scopeKind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var cwdPrefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Database.SqlQuery<CandidateRow>(
                $"""
                 SELECT TOP ({CandidateBudget})
                     [chunk].[Id] AS [ChunkId], [chunk].[Ordinal] AS [Ordinal], [chunk].[Content] AS [Content],
                     COALESCE([fulltext].[RANK], 0) AS [FullTextRank],
                     [chunk].[ContentHash] AS [ChunkHash],
                     [chunk].[StartOffset] AS [StartOffset], [chunk].[Length] AS [Length],
                     [artifact].[Id] AS [ArtifactId], [artifact].[ContentHash] AS [ArtifactHash],
                     CAST(NULL AS nvarchar(max)) AS [DocumentMetadataJson],
                     [record].[Id] AS [PipelineRecordId],
                     [record].[Revision] AS [PipelineRecordRevision],
                     COALESCE([owner].[Id], [retained].[Id]) AS [OwnerSourceRevisionId],
                     COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [RootId],
                     COALESCE([retained].[OriginKind], 0) AS [OriginKind],
                     CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                          WHEN [retained].[Id] IS NOT NULL THEN [retained].[CanonicalPath]
                          ELSE [source].[StableKey] COLLATE Latin1_General_100_BIN2 END AS [SourceIdentity]
                 FROM [TextChunks] AS [chunk]
                 INNER JOIN [Artifacts] AS [artifact]
                   ON [chunk].[ArtifactId] = [artifact].[Id]
                  AND [chunk].[SourceRevision] = [artifact].[SourceRevision]
                 INNER JOIN [PipelineRecords] AS [record]
                   ON [artifact].[PipelineRecordId] = [record].[Id]
                  AND [artifact].[SourceRevision] = [record].[Revision]
                 INNER JOIN [SourceIdentities] AS [source]
                   ON [record].[SourceIdentityId] = [source].[Id]
                 LEFT JOIN FREETEXTTABLE([TextChunks], [Content], {query}) AS [fulltext]
                   ON [fulltext].[KEY] = [chunk].[Id]
                 LEFT JOIN [SourceRevisions] AS [retained]
                   ON [record].[SourceRevisionId] = [retained].[Id]
                 LEFT JOIN [DocumentPublications] AS [publication]
                   ON [publication].[PipelineRecordId] = [record].[Id]
                  AND [publication].[PipelineRecordRevision] = [record].[Revision]
                  AND [publication].[DocumentInputSourceRevisionId] = [retained].[Id]
                 LEFT JOIN [SourceRevisions] AS [owner]
                   ON [publication].[OwnerSourceRevisionId] = [owner].[Id]
                 LEFT JOIN [SourceRootConfigurations] AS [root]
                   ON COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) = [root].[Id]
                 WHERE [record].[IsDeleted] = 0
                   AND [record].[CompletionCriteriaMet] = 1
                   AND [record].[CurrentStage] = {(int)PipelineStage.Publish}
                   AND [artifact].[Stage] = {(int)PipelineStage.CanonicalIndex}
                   AND [chunk].[Length] > 0 AND [chunk].[Length] <= 2048
                   AND [retained].[SuppressedAtUtc] IS NULL
                   AND [owner].[SuppressedAtUtc] IS NULL
                   AND ([record].[SourceRevisionId] IS NULL OR
                        ([retained].[Id] IS NOT NULL AND [root].[State] <> {(int)SourceRootState.Deleting}))
                   AND ([retained].[Id] IS NULL OR [retained].[OriginKind] NOT IN (2, 3) OR
                        [publication].[OwnerSourceRevisionId] IS NOT NULL)
                   AND ([record].[SourceRevisionId] IS NOT NULL OR [record].[Revision] = (
                        SELECT MAX([current].[Revision]) FROM [PipelineRecords] AS [current]
                        WHERE [current].[SourceIdentityId] = [record].[SourceIdentityId]))
                   AND ({scopeKind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                        SELECT [scopeRoot].[Id] FROM OPENJSON({rootIdsJson})
                        WITH ([Id] uniqueidentifier '$') AS [scopeRoot]))
                   AND ({scopeKind} <> N'workspace' OR
                        (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                              ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                         LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                                   ELSE [retained].[CanonicalPath] END, LEN({cwdPrefix})) COLLATE Latin1_General_100_CI_AS = {cwdPrefix}))
                   AND (CHARINDEX({query}, [chunk].[Content] COLLATE Latin1_General_100_BIN2) > 0
                        OR [fulltext].[KEY] IS NOT NULL)
                 ORDER BY CASE WHEN CHARINDEX({query}, [chunk].[Content] COLLATE Latin1_General_100_BIN2) > 0
                               THEN 0 ELSE 1 END,
                          [fulltext].[RANK] DESC, [chunk].[Id]
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(static row => new EligiblePassageCandidate(
            row.RootId, row.OwnerSourceRevisionId, row.PipelineRecordId,
            row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash,
            row.ChunkId, row.ChunkHash, row.StartOffset, row.Length,
            row.Content, row.SourceIdentity, row.FullTextRank, row.OriginKind)).ToArray();
    }

    public async ValueTask<EligibleContext?> ReadAsync(
        CorpusEvidenceBinding binding, int contextCharacters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (contextCharacters is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(contextCharacters));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Database.SqlQuery<CandidateRow>(
                $"""
                 SELECT TOP (1)
                     [chunk].[Id] AS [ChunkId], [chunk].[Ordinal] AS [Ordinal], [chunk].[Content] AS [Content],
                     0 AS [FullTextRank],
                     [chunk].[ContentHash] AS [ChunkHash],
                     [chunk].[StartOffset] AS [StartOffset], [chunk].[Length] AS [Length],
                     [artifact].[Id] AS [ArtifactId], [artifact].[ContentHash] AS [ArtifactHash],
                     [artifact].[DocumentMetadataJson] AS [DocumentMetadataJson],
                     [record].[Id] AS [PipelineRecordId],
                     [record].[Revision] AS [PipelineRecordRevision],
                     COALESCE([owner].[Id], [retained].[Id]) AS [OwnerSourceRevisionId],
                     COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [RootId],
                     COALESCE([retained].[OriginKind], 0) AS [OriginKind],
                     CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                          WHEN [retained].[Id] IS NOT NULL THEN [retained].[CanonicalPath]
                          ELSE [source].[StableKey] COLLATE Latin1_General_100_BIN2 END AS [SourceIdentity]
                 FROM [TextChunks] AS [chunk]
                 INNER JOIN [Artifacts] AS [artifact]
                   ON [chunk].[ArtifactId] = [artifact].[Id]
                  AND [chunk].[SourceRevision] = [artifact].[SourceRevision]
                 INNER JOIN [PipelineRecords] AS [record]
                   ON [artifact].[PipelineRecordId] = [record].[Id]
                  AND [artifact].[SourceRevision] = [record].[Revision]
                 INNER JOIN [SourceIdentities] AS [source]
                   ON [record].[SourceIdentityId] = [source].[Id]
                 LEFT JOIN [SourceRevisions] AS [retained]
                   ON [record].[SourceRevisionId] = [retained].[Id]
                 LEFT JOIN [DocumentPublications] AS [publication]
                   ON [publication].[PipelineRecordId] = [record].[Id]
                  AND [publication].[PipelineRecordRevision] = [record].[Revision]
                  AND [publication].[DocumentInputSourceRevisionId] = [retained].[Id]
                 LEFT JOIN [SourceRevisions] AS [owner]
                   ON [publication].[OwnerSourceRevisionId] = [owner].[Id]
                 LEFT JOIN [SourceRootConfigurations] AS [root]
                   ON COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) = [root].[Id]
                 WHERE [chunk].[Id] = {binding.ChunkId}
                   AND [record].[IsDeleted] = 0
                   AND [record].[CompletionCriteriaMet] = 1
                   AND [record].[CurrentStage] = {(int)PipelineStage.Publish}
                   AND [artifact].[Stage] = {(int)PipelineStage.CanonicalIndex}
                   AND [chunk].[Length] > 0 AND [chunk].[Length] <= 2048
                   AND [retained].[SuppressedAtUtc] IS NULL
                   AND [owner].[SuppressedAtUtc] IS NULL
                   AND ([record].[SourceRevisionId] IS NULL OR
                        ([retained].[Id] IS NOT NULL AND [root].[State] <> {(int)SourceRootState.Deleting}))
                   AND ([retained].[Id] IS NULL OR [retained].[OriginKind] NOT IN (2, 3) OR
                        [publication].[OwnerSourceRevisionId] IS NOT NULL)
                   AND ([record].[SourceRevisionId] IS NOT NULL OR [record].[Revision] = (
                        SELECT MAX([current].[Revision]) FROM [PipelineRecords] AS [current]
                        WHERE [current].[SourceIdentityId] = [record].[SourceIdentityId]))
                 """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = rows.SingleOrDefault();
        if (row is null || row.RootId != binding.RootId ||
            row.OwnerSourceRevisionId != binding.OwnerSourceRevisionId ||
            row.PipelineRecordId != binding.PipelineRecordId ||
            row.PipelineRecordRevision != binding.PipelineRecordRevision ||
            row.ArtifactId != binding.ArtifactId || row.ArtifactHash != binding.ArtifactHash ||
            row.ChunkHash != binding.ChunkHash ||
            Hash(row.SourceIdentity) != binding.SourceIdentityHash ||
            binding.CitedStart < row.StartOffset ||
            (long)binding.CitedStart + binding.CitedLength > (long)row.StartOffset + row.Length)
            return null;

        // The canonical chunker emits contiguous, non-overlapping UTF-16 chunks. Bound
        // adjacent hydration by ordinal as well as the requested context size.
        var nearby = await context.TextChunks.AsNoTracking()
            .Where(chunk => chunk.ArtifactId == row.ArtifactId &&
                            chunk.SourceRevision == row.PipelineRecordRevision &&
                            chunk.Ordinal >= Math.Max(0, row.Ordinal - 16) &&
                            chunk.Ordinal <= row.Ordinal + 16)
            .OrderBy(chunk => chunk.Ordinal)
            .Select(chunk => new { chunk.Ordinal, chunk.StartOffset, chunk.Length, chunk.Content, chunk.ContentHash })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var citedIndex = nearby.FindIndex(chunk => chunk.StartOffset == row.StartOffset);
        if (citedIndex < 0) return null;
        var first = citedIndex;
        var last = citedIndex;
        while (first > 0 && nearby[first - 1].StartOffset >= 0 &&
               nearby[first - 1].Length == nearby[first - 1].Content.Length &&
               nearby[first - 1].StartOffset + nearby[first - 1].Length == nearby[first].StartOffset &&
               Hash(nearby[first - 1].Content) == nearby[first - 1].ContentHash) first--;
        while (last + 1 < nearby.Count && nearby[last + 1].StartOffset >= 0 &&
               nearby[last + 1].Length == nearby[last + 1].Content.Length &&
               nearby[last].StartOffset + nearby[last].Length == nearby[last + 1].StartOffset &&
               Hash(nearby[last + 1].Content) == nearby[last + 1].ContentHash) last++;
        var joined = string.Concat(nearby.Skip(first).Take(last - first + 1).Select(chunk => chunk.Content));
        var joinedStart = nearby[first].StartOffset;
        var citationEnd = binding.CitedStart + binding.CitedLength;
        var desiredBefore = Math.Min(contextCharacters / 2, binding.CitedStart - joinedStart);
        var desiredAfter = Math.Min(contextCharacters - desiredBefore,
            joinedStart + joined.Length - citationEnd);
        desiredBefore = Math.Min(contextCharacters - desiredAfter, binding.CitedStart - joinedStart);
        var start = binding.CitedStart - desiredBefore;
        var end = citationEnd + desiredAfter;
        if (start > joinedStart && char.IsLowSurrogate(joined[start - joinedStart])) start++;
        if (end < joinedStart + joined.Length && end > start && char.IsHighSurrogate(joined[end - joinedStart - 1])) end--;
        var candidate = new EligiblePassageCandidate(row.RootId, row.OwnerSourceRevisionId,
            row.PipelineRecordId, row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash,
            row.ChunkId, row.ChunkHash, row.StartOffset, row.Length, row.Content,
            row.SourceIdentity, 0, row.OriginKind);
        var disclosureText = BuildDisclosureWindow(joined, joinedStart, start, end,
            nearby[first].Ordinal == 0,
            last == nearby.Count - 1 && nearby[last].Ordinal < row.Ordinal + 16);
        return new EligibleContext(candidate, start, joined.Substring(start - joinedStart, end - start),
            desiredBefore + desiredAfter < contextCharacters, row.DocumentMetadataJson, disclosureText);
    }

    private static string? BuildDisclosureWindow(
        string joined, int joinedStart, int start, int end,
        bool hasDocumentStart, bool hasDocumentEnd)
    {
        // The detector accepts at most 16 Ki UTF-16 units. A fixed halo alone
        // can start inside a long credential value, hiding its assignment label.
        // Scan complete surrounding lines instead; if a line boundary is not
        // available within the retained-read/detector budget, fail closed.
        const int halo = 2048;
        const int scanLimit = 16 * 1024;
        var first = Math.Max(0, start - joinedStart - halo);
        var last = Math.Min(joined.Length, end - joinedStart + halo);
        while (first > 0 && joined[first - 1] is not ('\r' or '\n')) first--;
        while (last < joined.Length && joined[last] is not ('\r' or '\n')) last++;
        if (last < joined.Length) last++;
        if (last - first > scanLimit ||
            first == 0 && !hasDocumentStart ||
            last == joined.Length && !hasDocumentEnd)
            return null;
        return joined.Substring(first, last - first);
    }

    private sealed class CandidateRow
    {
        public Guid? RootId { get; init; }
        public int OriginKind { get; init; }
        public Guid? OwnerSourceRevisionId { get; init; }
        public Guid PipelineRecordId { get; init; }
        public long PipelineRecordRevision { get; init; }
        public Guid ArtifactId { get; init; }
        public string? DocumentMetadataJson { get; init; }
        public string ArtifactHash { get; init; } = string.Empty;
        public long ChunkId { get; init; }
        public int Ordinal { get; init; }
        public int FullTextRank { get; init; }
        public string ChunkHash { get; init; } = string.Empty;
        public int StartOffset { get; init; }
        public int Length { get; init; }
        public string Content { get; init; } = string.Empty;
        public string SourceIdentity { get; init; } = string.Empty;
    }

    private sealed class FullTextStateRow
    {
        public int IndexPresent { get; init; }
        public int PopulationComplete { get; init; }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool TryCanonicalCwd(string? cwd, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Length > 2048 ||
            cwd.Length < 3 || !char.IsAsciiLetter(cwd[0]) || cwd[1] != ':' || cwd[2] != '\\' ||
            cwd.Contains('/') || cwd.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            return false;

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
            if (!string.Equals(full, cwd, StringComparison.OrdinalIgnoreCase)) return false;
            canonical = full;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsContained(string root, string path)
    {
        var basePath = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(basePath, path, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(basePath.EndsWith('\\') ? basePath : basePath + '\\',
                   StringComparison.OrdinalIgnoreCase);
    }
}
