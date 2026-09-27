using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public async ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusAnnLease lease,
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
        var matches = scope.Kind == "all"
            ? await lease.SearchAsync(query, 100, cancellationToken).ConfigureAwait(false) : [];
        if (matches.Count > 100 || matches.Any(value => value.VectorId <= 0 || !float.IsFinite(value.Distance)) ||
            matches.Select(value => value.VectorId).Distinct().Count() != matches.Count)
            throw new InvalidOperationException("corpus-dense-output-invalid");
        var matchIds = JsonSerializer.Serialize(matches.Select(value => value.VectorId));
        var rootIds = JsonSerializer.Serialize(scope.RootIds);
        var kind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var prefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        var budget = kind == "all" ? 100 : 10001;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureServingAsync(context, cancellationToken).ConfigureAwait(false);
        var vectors = await context.Database.SqlQuery<DenseVectorRow>(SqlPublishedPassageSelection.Bind($"""
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
            """)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false)) return new("index-updating", []);
        if (vectors.Length > 10000) return new("scope-capacity-exceeded", []);
        if (kind == "all" && vectors.Length != matches.Count) throw new InvalidOperationException("corpus-dense-membership-invalid");
        var ordered = kind == "all"
            ? matches.OrderBy(value => value.Distance).ThenBy(value => value.VectorId).Select(value => vectors.Single(row => row.VectorId == value.VectorId)).ToArray()
            : vectors.Select(row => (Row: row, Distance: Distance(row, query, generation.Dimensions)))
                .OrderBy(value => value.Distance).ThenBy(value => value.Row.VectorId).Take(100).Select(value => value.Row).ToArray();
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
        var passages = rows.ToDictionary(row => row.ChunkId, row => new EligiblePassageCandidate(row.RootId,
            row.OwnerSourceRevisionId, row.PipelineRecordId, row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash,
            row.ChunkId, row.ChunkHash, row.StartOffset, row.Length, row.Content, row.SourceIdentity, 0, row.OriginKind,
            row.CorpusEpoch, row.ContextHeader, row.PassagePolicyFingerprint, row.SearchInputHash));
        if (passages.Count != ordered.Select(value => value.TextChunkId).Distinct().Count())
            throw new InvalidOperationException("corpus-dense-passage-invalid");
        return new("ready", ordered.Select(row => passages[row.TextChunkId]).DistinctBy(value => value.ChunkId).ToArray());
    }

    private static double Distance(DenseVectorRow row, IReadOnlyList<float> query, int dimensions)
    {
        if (row.Values is null || row.Values.Length != dimensions * sizeof(float) ||
            Convert.ToHexStringLower(SHA256.HashData(row.Values)) != row.PayloadChecksum)
            throw new InvalidOperationException("corpus-dense-vector-invalid");
        var values = new float[dimensions]; Buffer.BlockCopy(row.Values, 0, values, 0, row.Values.Length);
        if (values.Any(value => !float.IsFinite(value)) || Math.Abs(values.Sum(value => (double)value * value) - 1) > 0.001)
            throw new InvalidOperationException("corpus-dense-vector-invalid");
        double similarity = 0;
        for (var i = 0; i < dimensions; i++) similarity += (double)values[i] * query[i];
        return 1 - similarity;
    }

    private sealed class DenseVectorRow
    {
        public long VectorId { get; init; }
        public long TextChunkId { get; init; }
        public byte[]? Values { get; init; }
        public string PayloadChecksum { get; init; } = string.Empty;
    }
}
