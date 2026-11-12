using System.Text.Json;
using System.Diagnostics;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public async ValueTask<IReadOnlySet<long>> ReadSegmentKeysAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope,
        IndexSegmentDescriptor segment, CancellationToken cancellationToken)
    {
        var generation = lease.Generation;
        if (generation.FormatVersion != 2 || generation.CorpusStamp is null || segment.VectorCount is < 0 or >= int.MaxValue ||
            segment.Dimensions != generation.Dimensions || segment.ModelFingerprint != generation.ModelFingerprint)
            throw new InvalidOperationException("segment-scope-profile-invalid");
        if (scope.Kind is not ("all" or "root" or "workspace")) throw new ArgumentException("Unknown scope.", nameof(scope));
        if (!await lease.IsReadableAsync(cancellationToken)) throw new PassageRetrievalRefusalException("index-updating");
        var roots = JsonSerializer.Serialize(scope.RootIds);
        var kind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var prefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        var maximum = checked((int)segment.VectorCount + 1);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var capture = SqlPublishedPassageSelection.Bind($"""
            SELECT TOP ({maximum}) [vector].[VectorId], [chunk].[Content],
                CONVERT(bigint,[chunk].[StartOffset]) AS [StartOffset], [chunk].[Length],
                CONVERT(bigint,DATALENGTH([artifact].[SearchText]))/2 AS [CanonicalLength],
                [window].[WindowStart], SUBSTRING([artifact].[SearchText] COLLATE Latin1_General_100_BIN2,
                    [window].[WindowStart]+1,16384) AS [WindowText]
            INTO #CorpusAnnWindows
            FROM [IndexGenerationSegments] AS [manifest]
            INNER JOIN [IndexSegmentVectors] AS [member] ON [member].[SegmentId] = [manifest].[SegmentId]
            INNER JOIN [IndexGenerations] AS [generation] ON [generation].[Id] = [manifest].[GenerationId]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1
            INNER JOIN [Vectors] AS [vector] ON [vector].[VectorId] = [member].[VectorId]
            INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [vector].[TextChunkId]
            INNER JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [chunk].[ArtifactId]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            CROSS APPLY (SELECT CASE WHEN [chunk].[StartOffset]>4096 THEN CONVERT(bigint,[chunk].[StartOffset])-4096
                ELSE CONVERT(bigint,0) END AS [WindowStart]) AS [window]
            /*publication-bindings*/
            WHERE [manifest].[GenerationId] = {generation.Id} AND [manifest].[SegmentId] = {segment.Id}
              AND [generation].[CorpusEpoch] = {generation.CorpusStamp.CorpusEpoch}
              AND [generation].[CorpusVersion] = {generation.CorpusStamp.CorpusVersion}
              AND [generation].[RetiredAtUtc] IS NULL AND [generation].[ReclaimedAtUtc] IS NULL
              AND [generation].[ValidatedAtUtc] IS NOT NULL
              AND [state].[CorpusEpoch] = [generation].[CorpusEpoch] AND [state].[CorpusVersion] >= [generation].[CorpusVersion]
              AND [state].[CorpusRebuildOperationId] IS NULL
              AND /*publication-eligibility*/
              AND [vector].[IsDeleted] = 0 AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[Dimensions] = {generation.Dimensions}
              AND [vector].[ModelFingerprint] COLLATE Latin1_General_100_BIN2 = {generation.ModelFingerprint}
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2
              AND [chunk].[Length] BETWEEN 1 AND 1024
              AND ({kind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                  SELECT [Value] FROM OPENJSON({roots}) WITH ([Value] uniqueidentifier '$')))
              AND ({kind} <> N'workspace' OR
                  (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                   LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END, LEN({prefix})) COLLATE Latin1_General_100_CI_AS = {prefix}))
            """);
        var keys = await context.Database.SqlQuery<AvailableSegmentKeyRow>(
            SqlDisclosureWindowAvailability.Select(capture, "i.VectorId")).ToArrayAsync(cancellationToken);
        if (keys.Length > segment.VectorCount || keys.Any(key => key.VectorId <= 0) || keys.Select(key => key.VectorId).Distinct().Count() != keys.Length)
            throw new InvalidOperationException("segment-scope-membership-invalid");
        return keys.Where(key => key.WindowAvailable).Select(key => key.VectorId).ToHashSet();
    }

    private ValueTask<DensePassageCandidates> ReadIndexedCandidatesAsync(ICorpusAnnLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken cancellationToken) =>
        ReadIndexedCandidatesCoreAsync(lease, scope, query, null, cancellationToken);

    private async ValueTask<DensePassageCandidates> ReadIndexedCandidatesCoreAsync(ICorpusAnnLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, string? queryText, CancellationToken cancellationToken)
    {
        if (!await lease.IsReadableAsync(cancellationToken)) return new("index-updating", []);
        if (!lease.IsPrepared(this)) throw new PassageRetrievalRefusalException("warming");
        var generation = lease.Generation;
        var phase = Stopwatch.GetTimestamp();
        SourceRoutingSelection? routing = queryText is null ? null : await ReadLiveRoutingAsync(lease, scope, queryText, cancellationToken);
        var live = routing?.AllKeys ?? await ReadLiveBindingKeysAsync(lease, scope, cancellationToken);
        ReportIndexedPhase("dense-live-bindings", phase);
        phase = Stopwatch.GetTimestamp();
        var matches = await lease.SearchAsync(query, 100, scope, new CachedScopeSelection(this, generation, live), cancellationToken);
        ReportIndexedPhase("dense-ann", phase);
        var baseline = await HydrateIndexedMatchesAsync(lease, scope, matches, "dense", cancellationToken);
        if (baseline.Status != "ready" || routing is null) return baseline;
        if (routing.Status != SourceDirectedRetrievalStatus.Ready)
            return baseline with { SourceDirectedStatus = routing.Status };
        if (routing.DirectedKeys.Count == 0) return baseline with { SourceDirectedStatus = SourceDirectedRetrievalStatus.Ready };
        if (!routing.DirectedKeys.IsSubsetOf(live)) throw new InvalidOperationException("source-routing-membership-invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var sourceBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var began = Stopwatch.GetTimestamp();
        try
        {
            var directed = await lease.SearchAsync(query, 100, scope,
                new CachedScopeSelection(this, generation, routing.DirectedKeys), sourceBudget.Token);
            ReportIndexedPhase("source-ann", began);
            var result = await HydrateIndexedMatchesAsync(lease, scope, directed, "source", sourceBudget.Token);
            sourceBudget.Token.ThrowIfCancellationRequested();
            if (result.Status != "ready") return result;
            // A delayed timer must not admit work that exceeded the fixed source allowance.
            if (Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(1))
            {
                if (!await lease.IsReadableAsync(cancellationToken)) throw new FluxKnowledge.Application.Pipeline.PublicationSnapshotConflictException("The captured manifest is unreadable.");
                return baseline with { SourceDirectedStatus = SourceDirectedRetrievalStatus.Timeout };
            }
            return baseline with { SourceDirectedCandidates = result.Candidates, SourceDirectedStatus = SourceDirectedRetrievalStatus.Ready };
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested &&
            (exception.CancellationToken == sourceBudget.Token || exception.CancellationToken == deadline.Token))
        {
            // The native/SQL call has actually returned. Ownership, publication and identity errors are not optional failures.
            if (!await lease.IsReadableAsync(cancellationToken)) throw new FluxKnowledge.Application.Pipeline.PublicationSnapshotConflictException("The captured manifest is unreadable.");
            return baseline with { SourceDirectedStatus = SourceDirectedRetrievalStatus.Timeout };
        }
    }

    private async ValueTask<DensePassageCandidates> HydrateIndexedMatchesAsync(ICorpusAnnLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<AnnMatch> matches, string phasePrefix, CancellationToken cancellationToken,
        bool candidatePool = false)
    {
        var generation = lease.Generation;
        var phase = Stopwatch.GetTimestamp();
        if (matches.Count > (candidatePool ? IAnnCandidatePool.MaximumResults : 100) || matches.Any(value => value.VectorId <= 0 || !float.IsFinite(value.Distance)) ||
            matches.Select(value => value.VectorId).Distinct().Count() != matches.Count)
            throw new InvalidOperationException("corpus-dense-output-invalid");
        if (matches.Count == 0) return new("ready", []);
        var matchIds = JsonSerializer.Serialize(matches.Select(value => value.VectorId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        phase = Stopwatch.GetTimestamp();
        var mapping = await context.Database.SqlQuery<IndexedKeyRow>($"""
            SELECT [vector].[VectorId], [vector].[TextChunkId]
            FROM [Vectors] AS [vector]
            WHERE [vector].[VectorId] IN (SELECT [Value] FROM OPENJSON({matchIds}) WITH ([Value] bigint '$'))
              AND EXISTS (SELECT 1 FROM [IndexGenerationSegments] AS [manifest]
                  INNER JOIN [IndexSegmentVectors] AS [member] ON [member].[SegmentId] = [manifest].[SegmentId]
                  WHERE [manifest].[GenerationId] = {generation.Id} AND [member].[VectorId] = [vector].[VectorId])
            """).ToArrayAsync(cancellationToken);
        ReportIndexedPhase(phasePrefix + "-id-mapping", phase);
        if (mapping.Length != matches.Count) throw new InvalidOperationException("corpus-dense-membership-invalid");
        var chunks = JsonSerializer.Serialize(mapping.Select(value => value.TextChunkId).Distinct());
        var roots = JsonSerializer.Serialize(scope.RootIds);
        var kind = scope.Kind;
        var cwd = scope.CanonicalCwd ?? string.Empty;
        var prefix = cwd.EndsWith('\\') ? cwd : cwd + '\\';
        phase = Stopwatch.GetTimestamp();
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
            FROM OPENJSON({chunks}) WITH ([ChunkId] bigint '$') AS [requested]
            INNER JOIN [TextChunks] AS [chunk] WITH (FORCESEEK) ON [chunk].[Id] = [requested].[ChunkId]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1
            INNER JOIN [Artifacts] AS [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            -- Requested keys fix record identity; avoid rescanning a corpus-wide eligibility spool.
            INNER JOIN [PipelineRecords] AS [record] WITH (FORCESEEK) ON [artifact].[PipelineRecordId] = [record].[Id]
            INNER JOIN [SourceIdentities] AS [source] ON [record].[SourceIdentityId] = [source].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND EXISTS (SELECT 1 FROM OPENJSON({matchIds}) WITH ([Value] bigint '$') AS [wantedVector]
                  INNER JOIN [Vectors] AS [vector] WITH (FORCESEEK) ON [vector].[VectorId] = [wantedVector].[Value]
                  WHERE [vector].[TextChunkId] = [chunk].[Id]
                  AND [vector].[IsDeleted] = 0 AND [vector].[SourceRevision] = [chunk].[SourceRevision]
                  AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
                  AND [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
              AND [state].[CorpusEpoch] = {generation.CorpusStamp!.CorpusEpoch} AND [state].[CorpusRebuildOperationId] IS NULL
              AND [chunk].[Length] BETWEEN 1 AND 1024
              AND ({kind} = N'all' OR COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) IN (
                  SELECT [Value] FROM OPENJSON({roots}) WITH ([Value] uniqueidentifier '$')))
              AND ({kind} <> N'workspace' OR
                  (CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END COLLATE Latin1_General_100_CI_AS = {cwd} OR
                   LEFT(CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath] ELSE [retained].[CanonicalPath] END, LEN({prefix})) COLLATE Latin1_General_100_CI_AS = {prefix}))
            OPTION (LOOP JOIN, FORCE ORDER, MAXDOP 1)
            """)).ToArrayAsync(cancellationToken);
        ReportIndexedPhase(phasePrefix + "-hydration", phase);
        phase = Stopwatch.GetTimestamp();
        var candidates = await AttachProofsAsync(context, rows.Select(row => new EligiblePassageCandidate(row.RootId,
            row.OwnerSourceRevisionId, row.PipelineRecordId, row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash,
            row.ChunkId, row.ChunkHash, row.StartOffset, row.Length, row.Content, row.SourceIdentity, 0, row.OriginKind,
            row.CorpusEpoch, row.ContextHeader, row.PassagePolicyFingerprint, row.SearchInputHash)).ToArray(), cancellationToken);
        ReportIndexedPhase(phasePrefix + "-disclosure-proofs", phase);
        if (!await lease.IsReadableAsync(cancellationToken)) return new("index-updating", []);
        var byChunk = candidates.ToDictionary(value => value.ChunkId);
        var byVector = mapping.ToDictionary(value => value.VectorId, value => value.TextChunkId);
        return new("ready", matches.Select(value => byVector[value.VectorId]).Where(byChunk.ContainsKey)
            .Distinct().Select(id => byChunk[id]).ToArray());
    }

    private static void ReportIndexedPhase(string phase, long started)
    {
        if (!HybridSearchDiagnostics.Log.IsEnabled()) return;
        var correlation = HybridSearchDiagnostics.CurrentCorrelation;
        HybridSearchDiagnostics.Log.Phase(correlation.TraceId, correlation.SpanId, string.Empty, phase,
            "completed", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private sealed class IndexedKeyRow
    {
        public long VectorId { get; init; }
        public long TextChunkId { get; init; }
    }

    private sealed class AvailableSegmentKeyRow
    {
        public long VectorId { get; init; }
        public bool WindowAvailable { get; init; }
    }
}
