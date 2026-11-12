using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Domain.Pipeline;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public bool IsPrepared(Guid epoch, IndexSegmentDescriptor segment) => _bindingCache.TryGet(epoch, segment) is not null;
    public long MaximumRetainedBytes => _bindingCache.MaximumRetainedBytes;

    public async ValueTask<long> PrepareAsync(ICorpusGenerationLease lease, IndexSegmentDescriptor segment, CancellationToken cancellationToken) =>
        (await _bindingCache.GetAsync(lease.Generation.CorpusStamp!.CorpusEpoch, segment,
            ct => ReadImmutableSegmentBindingsAsync(lease, segment, ct), cancellationToken)).EstimatedBytes;

    private async ValueTask<IReadOnlySet<CanonicalBindingKey>> ReadLiveBindingKeysAsync(ICorpusGenerationLease lease,
        ResolvedCorpusScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQuery<LiveBindingRow>(LiveBindingQuery(lease, scope, includeRouting: false))
            .ToArrayAsync(cancellationToken);
        return rows.Select(row => new CanonicalBindingKey(row.ArtifactId, row.PipelineRecordId, row.SourceRevision)).ToHashSet();
    }

    private async ValueTask<SourceRoutingSelection> ReadLiveRoutingAsync(ICorpusGenerationLease lease,
        ResolvedCorpusScope scope, string queryText, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var capture = new SourceRoutingCapture(queryText);
        await foreach (var row in context.Database.SqlQuery<LiveRoutingRow>(LiveBindingQuery(lease, scope, includeRouting: true))
                           .AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture.Add(new(row.ArtifactId, row.PipelineRecordId, row.SourceRevision), row.RoutingSourcePath, row.RoutingRootPath);
        }
        return capture.Complete(true, cancellationToken);
    }

    private static FormattableString LiveBindingQuery(ICorpusGenerationLease lease, ResolvedCorpusScope scope, bool includeRouting)
    {
        var roots = JsonSerializer.Serialize(scope.RootIds);
        var kind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var prefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        var query = SqlPublishedPassageSelection.Bind($"""
            /* corpus-ann-live-bindings */
            SELECT DISTINCT [artifact].[Id] AS [ArtifactId], [record].[Id] AS [PipelineRecordId],
                [artifact].[SourceRevision] /*source-routing-projection*/
            FROM [Artifacts] AS [artifact]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1
            CROSS APPLY (SELECT TOP (1) [canonicalChunk].[SourceRevision]
                FROM [TextChunks] AS [canonicalChunk]
                WHERE [canonicalChunk].[ArtifactId] = [artifact].[Id]
                  AND [canonicalChunk].[SourceRevision] = [artifact].[SourceRevision]) AS [chunk]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND [state].[CorpusEpoch] = {lease.Generation.CorpusStamp!.CorpusEpoch}
              AND [state].[CorpusRebuildOperationId] IS NULL
              AND ({kind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                  SELECT [Value] FROM OPENJSON({roots}) WITH ([Value] uniqueidentifier '$')))
              AND ({kind} <> N'workspace' OR
                  (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                   LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END, LEN({prefix})) COLLATE Latin1_General_100_CI_AS = {prefix}))
            """);
        // Binary projection preserves case-only aliases for the optional ambiguity check.
        // Scope comparison and every publication predicate remain the existing query's predicates.
        var projection = includeRouting ? """
            , CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                   ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_BIN2 AS [RoutingSourcePath],
              [root].[CanonicalPath] COLLATE Latin1_General_100_BIN2 AS [RoutingRootPath]
            """ : string.Empty;
        return FormattableStringFactory.Create(query.Format.Replace("/*source-routing-projection*/", projection,
            StringComparison.Ordinal), query.GetArguments());
    }

    private async ValueTask<SegmentBindingGroups> ReadImmutableSegmentBindingsAsync(ICorpusGenerationLease lease,
        IndexSegmentDescriptor segment, CancellationToken cancellationToken)
    {
        if (!await lease.IsReadableAsync(cancellationToken)) throw new PassageRetrievalRefusalException("index-updating");
        if (segment.VectorCount is < 0 or >= int.MaxValue || segment.ModelFingerprint != lease.Generation.ModelFingerprint ||
            segment.Dimensions != lease.Generation.Dimensions)
            throw new InvalidOperationException("segment-binding-profile-invalid");
        var maximum = checked((int)segment.VectorCount + 1);
        var segmentId = new SqlParameter("segmentId", SqlDbType.Char, 64) { Value = segment.Id };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        FormattableString capture = $"""
            /* corpus-ann-static-bindings */
            SELECT TOP ({maximum}) [vector].[VectorId], [vector].[PayloadChecksum], [vector].[ModelFingerprint],
                [vector].[Dimensions], [vector].[IsDeleted], [vector].[SourceRevision] AS [VectorRevision],
                [vector].[TextChunkContentHash] AS [VectorChunkHash], [vector].[SearchInputHash] AS [VectorInputHash],
                [chunk].[SourceRevision] AS [ChunkRevision], [chunk].[ContentHash] AS [ChunkHash],
                [chunk].[SearchInputHash] AS [ChunkInputHash], [chunk].[Length], [artifact].[Id] AS [ArtifactId],
                [artifact].[PipelineRecordId], [artifact].[SourceRevision], [artifact].[Stage] AS [ArtifactStage],
                [chunk].[Content], CONVERT(bigint,[chunk].[StartOffset]) AS [StartOffset],
                CONVERT(bigint,DATALENGTH([artifact].[SearchText]))/2 AS [CanonicalLength],
                [window].[WindowStart], SUBSTRING([artifact].[SearchText] COLLATE Latin1_General_100_BIN2,
                    [window].[WindowStart]+1,16384) AS [WindowText]
            INTO #CorpusAnnWindows
            FROM [IndexSegmentVectors] AS [member]
            INNER JOIN [Vectors] AS [vector] ON [vector].[VectorId] = [member].[VectorId]
            INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [vector].[TextChunkId]
            INNER JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [chunk].[ArtifactId]
            CROSS APPLY (SELECT CASE WHEN [chunk].[StartOffset]>4096 THEN CONVERT(bigint,[chunk].[StartOffset])-4096
                ELSE CONVERT(bigint,0) END AS [WindowStart]) AS [window]
            WHERE [member].[SegmentId] = {segmentId}
              AND EXISTS (SELECT 1 FROM [IndexGenerationSegments] AS [manifest]
                  WHERE [manifest].[GenerationId] = {lease.Generation.Id} AND [manifest].[SegmentId] = [member].[SegmentId])
            ORDER BY [vector].[VectorId]
            OPTION (LOOP JOIN, FORCE ORDER, MAXDOP 1)
            """;
        var rows = await context.Database.SqlQuery<StaticBindingRow>(SqlDisclosureWindowAvailability.Select(capture, """
            i.VectorId,i.PayloadChecksum,i.ModelFingerprint,i.Dimensions,i.IsDeleted,i.VectorRevision,i.VectorChunkHash,
            i.VectorInputHash,i.ChunkRevision,i.ChunkHash,i.ChunkInputHash,i.Length,i.ArtifactId,i.PipelineRecordId,
            i.SourceRevision,i.ArtifactStage
            """)).ToArrayAsync(cancellationToken);
        if (rows.LongLength != segment.VectorCount || rows.Any(row => row.VectorId <= 0) ||
            rows.Select(row => row.VectorId).Distinct().Count() != rows.Length ||
            IndexSegmentDescriptor.ComputeMembershipChecksum(segment.ModelFingerprint, segment.Dimensions,
                rows.Select(row => (row.VectorId, row.PayloadChecksum))) != segment.MembershipChecksum)
            throw new InvalidOperationException("segment-binding-membership-invalid");
        var groups = new Dictionary<CanonicalBindingKey, List<long>>();
        var deleted = new HashSet<CanonicalBindingKey>();
        foreach (var row in rows)
        {
            if (row.ModelFingerprint != segment.ModelFingerprint || row.Dimensions != segment.Dimensions ||
                row.VectorRevision != row.ChunkRevision || row.ChunkRevision != row.SourceRevision ||
                row.VectorChunkHash != row.ChunkHash || row.ArtifactStage != (int)PipelineStage.CanonicalIndex ||
                row.VectorInputHash is not null && row.VectorInputHash != row.ChunkInputHash)
                throw new InvalidOperationException("segment-canonical-binding-invalid");
            var key = new CanonicalBindingKey(row.ArtifactId, row.PipelineRecordId, row.SourceRevision);
            if (row.IsDeleted) { deleted.Add(key); continue; }
            // Legacy inputs without a bound search hash or with oversized bodies are
            // not coherent semantic passages; preserve the existing SQL eligibility.
            if (row.VectorInputHash is null || row.Length is < 1 or > 1024 || !row.WindowAvailable) continue;
            if (!groups.TryGetValue(key, out var values)) groups.Add(key, values = []);
            values.Add(row.VectorId);
        }
        if (!await lease.IsReadableAsync(cancellationToken)) throw new PassageRetrievalRefusalException("index-updating");
        var compact = groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var descriptorCharacters = (long)segment.Id.Length + segment.ModelFingerprint.Length + segment.DirectoryPath.Length +
            segment.MembershipChecksum.Length + segment.FileChecksum.Length + segment.NativeFormat.Length;
        var bytes = checked(512L + descriptorCharacters * sizeof(char) +
            compact.Sum(pair => 192L + pair.Value.LongLength * sizeof(long)) + deleted.Count * 96L);
        return new(compact, deleted, bytes);
    }

    private sealed class CachedScopeSelection(SqlCorpusRetrievalReader reader, IndexGenerationDescriptor generation,
        IReadOnlySet<CanonicalBindingKey> live) : ICorpusAnnScopeSelection
    {
        public ValueTask<IReadOnlySet<long>> ReadSegmentKeysAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope,
            IndexSegmentDescriptor segment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lease.Generation != generation) throw new InvalidOperationException("segment-binding-generation-changed");
            var metadata = reader._bindingCache.TryGet(generation.CorpusStamp!.CorpusEpoch, segment)
                ?? throw new PassageRetrievalRefusalException("warming");
            if (metadata.DeletedBindings.Any(live.Contains)) throw new InvalidOperationException("published-binding-has-deleted-vectors");
            return ValueTask.FromResult<IReadOnlySet<long>>(metadata.Groups.Where(pair => live.Contains(pair.Key)).SelectMany(pair => pair.Value).ToHashSet());
        }
    }

    private sealed class LiveBindingRow
    {
        public Guid ArtifactId { get; init; }
        public Guid PipelineRecordId { get; init; }
        public long SourceRevision { get; init; }
    }

    private sealed class LiveRoutingRow
    {
        public Guid ArtifactId { get; init; }
        public Guid PipelineRecordId { get; init; }
        public long SourceRevision { get; init; }
        public string? RoutingSourcePath { get; init; }
        public string? RoutingRootPath { get; init; }
    }
    private sealed class StaticBindingRow
    {
        public long VectorId { get; init; }
        public string PayloadChecksum { get; init; } = "";
        public string ModelFingerprint { get; init; } = "";
        public int Dimensions { get; init; }
        public bool IsDeleted { get; init; }
        public long VectorRevision { get; init; }
        public long ChunkRevision { get; init; }
        public string VectorChunkHash { get; init; } = "";
        public string ChunkHash { get; init; } = "";
        public string? VectorInputHash { get; init; }
        public string? ChunkInputHash { get; init; }
        public int Length { get; init; }
        public Guid ArtifactId { get; init; }
        public Guid PipelineRecordId { get; init; }
        public long SourceRevision { get; init; }
        public int ArtifactStage { get; init; }
        public bool WindowAvailable { get; init; }
    }
}
