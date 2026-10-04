using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public async ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(scope);
        var generation = lease.Generation;
        if (generation.CorpusStamp is null || query.Count != generation.Dimensions ||
            query.Any(value => !float.IsFinite(value)) || Math.Abs(query.Sum(value => (double)value * value) - 1) > 0.001)
            throw new InvalidOperationException("corpus-dense-profile-invalid");
        if (scope.Kind is not ("all" or "root" or "workspace")) throw new ArgumentException("Unknown scope.", nameof(scope));
        if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
        IReadOnlyList<AnnMatch> matches = [];
        if (scope.Kind == "all")
        {
            if (lease is not ICorpusAnnLease ann) throw new InvalidOperationException("corpus-dense-ann-lease-required");
            matches = await ann.SearchAsync(query, 100, cancellationToken).ConfigureAwait(false);
        }
        if (matches.Count > 100 || matches.Any(value => value.VectorId <= 0 || !float.IsFinite(value.Distance)) ||
            matches.Select(value => value.VectorId).Distinct().Count() != matches.Count)
            throw new InvalidOperationException("corpus-dense-output-invalid");
        var matchIds = JsonSerializer.Serialize(matches.Select(value => value.VectorId));
        var rootIds = JsonSerializer.Serialize(scope.RootIds);
        var kind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var prefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        var budget = kind == "all" ? 100 : 256;
        var matchDistances = matches.ToDictionary(value => value.VectorId, value => (double)value.Distance);
        // Reverse priority puts the worst distance/highest tied ID first. The heap
        // retains identifiers and scores only, never vector payloads between pages.
        var top = new PriorityQueue<DenseSelection, (double Distance, long VectorId)>(
            Comparer<(double Distance, long VectorId)>.Create((left, right) =>
            {
                var distance = right.Distance.CompareTo(left.Distance);
                return distance != 0 ? distance : right.VectorId.CompareTo(left.VectorId);
            }));
        long lastVectorId = 0;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureServingAsync(context, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
            var pageQuery = SqlPublishedPassageSelection.Bind($"""
            SELECT TOP ({budget}) [vector].[VectorId], [vector].[TextChunkId],
                   CASE WHEN {kind} = N'all' THEN CAST(NULL AS varbinary(max)) ELSE [vector].[Values] END AS [Values],
                   [vector].[PayloadChecksum]
            FROM [IndexGenerationVectors] AS [member]
            INNER JOIN [IndexGenerations] AS [generation] ON [member].[GenerationId] = [generation].[Id]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1
            INNER JOIN [Vectors] AS [vector] ON [member].[VectorId] = [vector].[VectorId]
            INNER JOIN [TextChunks] AS [chunk] ON [vector].[TextChunkId] = [chunk].[Id]
            INNER JOIN [Artifacts] AS [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER JOIN [PipelineRecords] AS [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND [member].[GenerationId] = {generation.Id} AND [state].[ActiveIndexGenerationId] = {generation.Id}
              AND [generation].[RetiredAtUtc] IS NULL
              AND [generation].[CorpusEpoch] = {generation.CorpusStamp.CorpusEpoch}
              AND [generation].[CorpusVersion] = {generation.CorpusStamp.CorpusVersion}
              AND [state].[CorpusEpoch] = [generation].[CorpusEpoch] AND [state].[CorpusVersion] = [generation].[CorpusVersion]
              AND [vector].[IsDeleted] = 0 AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[VectorId] > {lastVectorId}
              AND [vector].[Dimensions] = {generation.Dimensions}
              AND [vector].[ModelFingerprint] COLLATE Latin1_General_100_BIN2 = {generation.ModelFingerprint}
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2
              AND ({kind} <> N'all' OR [vector].[VectorId] IN (SELECT [Value] FROM OPENJSON({matchIds}) WITH ([Value] bigint '$')))
              AND ({kind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                  SELECT [Value] FROM OPENJSON({rootIds}) WITH ([Value] uniqueidentifier '$')))
              AND ({kind} <> N'workspace' OR
                  (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                   LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END, LEN({prefix})) COLLATE Latin1_General_100_CI_AS = {prefix}))
            ORDER BY [vector].[VectorId]
            """);
            if (kind != "all")
            {
                // Seek the existing (GenerationId, VectorId) key in order. Every
                // publication/scope predicate still applies before the page limit.
                pageQuery = FormattableStringFactory.Create(pageQuery.Format
                    .Replace("[IndexGenerationVectors] AS [member]", "[IndexGenerationVectors] AS [member] WITH (FORCESEEK)", StringComparison.Ordinal)
                    .Replace("[vector].[VectorId] >", "[member].[VectorId] >", StringComparison.Ordinal)
                    .Replace("ORDER BY [vector].[VectorId]", "ORDER BY [member].[VectorId]", StringComparison.Ordinal)
                    + " OPTION (LOOP JOIN, FORCE ORDER)", pageQuery.GetArguments());
            }
            var vectors = await context.Database.SqlQuery<DenseVectorRow>(pageQuery)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
            if (kind == "all" && vectors.Length != matches.Count) throw new InvalidOperationException("corpus-dense-membership-invalid");
            foreach (var row in vectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.VectorId <= lastVectorId || row.TextChunkId <= 0)
                    throw new InvalidOperationException("corpus-dense-output-invalid");
                lastVectorId = row.VectorId;
                var distance = kind == "all" ? matchDistances[row.VectorId] :
                    Distance(row, query, generation.Dimensions, cancellationToken);
                var selected = new DenseSelection(row.VectorId, row.TextChunkId, distance);
                if (top.Count < 100) top.Enqueue(selected, (distance, row.VectorId));
                else
                {
                    var worst = top.Peek();
                    if (distance < worst.Distance || distance == worst.Distance && row.VectorId < worst.VectorId)
                        top.DequeueEnqueue(selected, (distance, row.VectorId));
                }
            }
            if (kind != "all" && HybridSearchDiagnostics.Log.IsEnabled())
                HybridSearchDiagnostics.Log.ScopedDensePage(Activity.Current?.TraceId.ToString() ?? string.Empty,
                    Activity.Current?.SpanId.ToString() ?? string.Empty, vectors.Length, top.Count,
                    vectors.Sum(value => (long)(value.Values?.Length ?? 0)));
            if (kind == "all" || vectors.Length < budget) break;
        }
        if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
        var ordered = top.UnorderedItems.Select(value => value.Element)
            .OrderBy(value => value.Distance).ThenBy(value => value.VectorId).ToArray();
        var ids = JsonSerializer.Serialize(ordered.Select(value => value.TextChunkId).Distinct());
        var rows = await context.Database.SqlQuery<CandidateRow>(SqlPublishedPassageSelection.Bind($"""
            SELECT [chunk].[Id] AS [ChunkId], [chunk].[Ordinal], [chunk].[Content], 0 AS [FullTextRank],
                   [chunk].[ContentHash] AS [ChunkHash], [state].[CorpusEpoch], [chunk].[ContextHeader],
                   [chunk].[PassagePolicyFingerprint], [chunk].[SearchInputHash], [chunk].[StartOffset], [chunk].[Length],
                   [artifact].[Id] AS [ArtifactId], [artifact].[ContentHash] AS [ArtifactHash],
                   CAST(NULL AS nvarchar(max)) AS [DocumentMetadataJson], [record].[Id] AS [PipelineRecordId],
                   [record].[Revision] AS [PipelineRecordRevision], COALESCE([owner].[Id], [retained].[Id]) AS [OwnerSourceRevisionId],
                   COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [RootId], COALESCE([retained].[OriginKind], 0) AS [OriginKind],
                   CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                        WHEN [retained].[Id] IS NOT NULL THEN [retained].[CanonicalPath]
                        ELSE [source].[StableKey] COLLATE Latin1_General_100_BIN2 END AS [SourceIdentity]
            FROM [TextChunks] AS [chunk]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1
            INNER JOIN [Artifacts] AS [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER JOIN [PipelineRecords] AS [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            INNER JOIN [SourceIdentities] AS [source] ON [record].[SourceIdentityId] = [source].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND [chunk].[Id] IN (SELECT [Value] FROM OPENJSON({ids}) WITH ([Value] bigint '$'))
              AND [state].[ActiveIndexGenerationId] = {generation.Id}
              AND [state].[CorpusEpoch] = {generation.CorpusStamp.CorpusEpoch}
              AND [state].[CorpusVersion] = {generation.CorpusStamp.CorpusVersion}
              AND [chunk].[Length] BETWEEN 1 AND 1024
              AND ({kind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                  SELECT [Value] FROM OPENJSON({rootIds}) WITH ([Value] uniqueidentifier '$')))
              AND ({kind} <> N'workspace' OR
                  (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                   LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END, LEN({prefix})) COLLATE Latin1_General_100_CI_AS = {prefix}))
            """)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
        var withProofs = await AttachProofsAsync(context, rows.Select(row => new EligiblePassageCandidate(row.RootId,
            row.OwnerSourceRevisionId, row.PipelineRecordId, row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash,
            row.ChunkId, row.ChunkHash, row.StartOffset, row.Length, row.Content, row.SourceIdentity, 0, row.OriginKind,
            row.CorpusEpoch, row.ContextHeader, row.PassagePolicyFingerprint, row.SearchInputHash)).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
        var passages = withProofs.ToDictionary(row => row.ChunkId);
        if (passages.Count != ordered.Select(value => value.TextChunkId).Distinct().Count())
            throw new InvalidOperationException("corpus-dense-passage-invalid");
        return new("ready", ordered.Select(row => passages[row.TextChunkId]).DistinctBy(value => value.ChunkId).ToArray());
    }

    private static double Distance(DenseVectorRow row, IReadOnlyList<float> query, int dimensions,
        CancellationToken cancellationToken)
    {
        if (row.Values is null || row.Values.Length != dimensions * sizeof(float) ||
            Convert.ToHexStringLower(SHA256.HashData(row.Values)) != row.PayloadChecksum)
            throw new InvalidOperationException("corpus-dense-vector-invalid");
        var values = MemoryMarshal.Cast<byte, float>(row.Values.AsSpan());
        double similarity = 0;
        double norm = 0;
        for (var i = 0; i < dimensions; i++)
        {
            if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(values[i])) throw new InvalidOperationException("corpus-dense-vector-invalid");
            norm += (double)values[i] * values[i];
            similarity += (double)values[i] * query[i];
        }
        if (Math.Abs(norm - 1) > 0.001) throw new InvalidOperationException("corpus-dense-vector-invalid");
        return 1 - similarity;
    }

    private sealed record DenseSelection(long VectorId, long TextChunkId, double Distance);

    private sealed class DenseVectorRow
    {
        public long VectorId { get; init; }
        public long TextChunkId { get; init; }
        public byte[]? Values { get; init; }
        public string PayloadChecksum { get; init; } = string.Empty;
    }
}
