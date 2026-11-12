using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadForDisclosureAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken) =>
        ReadDisclosureBatchAsync(bindings, 200, cancellationToken);

    public ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadExpandedForDisclosureAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken) =>
        ReadDisclosureBatchAsync(bindings, 250, cancellationToken);

    private async ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadDisclosureBatchAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, int maximumBindings, CancellationToken cancellationToken)
    {
        if (bindings.Count > maximumBindings || bindings.Select(binding => binding.ChunkId).Distinct().Count() != bindings.Count)
            throw new ArgumentOutOfRangeException(nameof(bindings));
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<long, EligibleContext>();
        if (bindings.Count == 0) return result;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureServingAsync(context, cancellationToken).ConfigureAwait(false);
        var requested = JsonSerializer.Serialize(bindings.Select(binding => new { binding.ChunkId, binding.CorpusEpoch }));
        var maximum = bindings.Count + 1;
        var rows = await context.Database.SqlQuery<CandidateRow>(SqlPublishedPassageSelection.Bind($"""
            SELECT TOP ({maximum})
                [chunk].[Id] AS [ChunkId], [chunk].[Ordinal], [chunk].[Content], 0 AS [FullTextRank],
                [chunk].[ContentHash] AS [ChunkHash], [state].[CorpusEpoch], [chunk].[ContextHeader],
                [chunk].[PassagePolicyFingerprint], [chunk].[SearchInputHash], [chunk].[StartOffset], [chunk].[Length],
                [artifact].[Id] AS [ArtifactId], [artifact].[ContentHash] AS [ArtifactHash],
                CAST(NULL AS nvarchar(max)) AS [DocumentMetadataJson],
                [record].[Id] AS [PipelineRecordId], [record].[Revision] AS [PipelineRecordRevision],
                COALESCE([owner].[Id], [retained].[Id]) AS [OwnerSourceRevisionId],
                COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [RootId],
                COALESCE([retained].[OriginKind], 0) AS [OriginKind],
                CASE WHEN [publication].[OwnerSourceRevisionId] IS NOT NULL THEN [owner].[CanonicalPath]
                     WHEN [retained].[Id] IS NOT NULL THEN [retained].[CanonicalPath]
                     ELSE [source].[StableKey] COLLATE Latin1_General_100_BIN2 END AS [SourceIdentity]
            FROM OPENJSON({requested}) WITH ([ChunkId] bigint '$.ChunkId', [CorpusEpoch] uniqueidentifier '$.CorpusEpoch') AS [request]
            INNER LOOP JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [request].[ChunkId]
            INNER JOIN [IndexState] AS [state] ON [state].[Id] = 1 AND [state].[CorpusEpoch] = [request].[CorpusEpoch]
            INNER JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [chunk].[ArtifactId]
                AND [artifact].[SourceRevision] = [chunk].[SourceRevision]
            INNER JOIN [PipelineRecords] AS [record] WITH (FORCESEEK) ON [record].[Id] = [artifact].[PipelineRecordId]
                AND [record].[Revision] = [artifact].[SourceRevision]
            INNER JOIN [SourceIdentities] AS [source] ON [source].[Id] = [record].[SourceIdentityId]
            /*publication-bindings*/
            WHERE [record].[IsDeleted] = 0 AND /*publication-eligibility*/
                AND [chunk].[Length] > 0 AND [chunk].[Length] <= 2048
            OPTION (LOOP JOIN, FORCE ORDER, MAXDOP 1)
            """)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Never truncate an ambiguous publication binding into an accepted candidate.
        if (rows.Length > bindings.Count || rows.Select(row => row.ChunkId).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("disclosure-candidate-binding-ambiguous");
        var byId = rows.ToDictionary(row => row.ChunkId);
        var matched = bindings.Where(binding => byId.TryGetValue(binding.ChunkId, out var row) && MatchesBinding(row, binding)).ToArray();
        if (matched.Length == 0) return result;
        var windowsJson = JsonSerializer.Serialize(matched.Select((binding, ordinal) => new
        {
            Ordinal = ordinal, binding.ArtifactId, binding.ArtifactHash, binding.PipelineRecordRevision,
            WindowStart = Math.Max(0, binding.CitedStart - 4096)
        }));
        var windows = await context.Database.SqlQuery<DisclosureCanonicalWindowRow>($"""
            SELECT [request].[Ordinal], CAST(DATALENGTH([artifact].[SearchText]) / 2 AS int) AS [CanonicalLength],
                SUBSTRING([artifact].[SearchText] COLLATE Latin1_General_100_BIN2, [request].[WindowStart] + 1, 16384) AS [Text]
            FROM OPENJSON({windowsJson}) WITH ([Ordinal] int '$.Ordinal', [ArtifactId] uniqueidentifier '$.ArtifactId',
                [ArtifactHash] varchar(64) '$.ArtifactHash', [PipelineRecordRevision] bigint '$.PipelineRecordRevision',
                [WindowStart] int '$.WindowStart') AS [request]
            INNER LOOP JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [request].[ArtifactId]
                AND [artifact].[ContentHash] = [request].[ArtifactHash]
                AND [artifact].[SourceRevision] = [request].[PipelineRecordRevision]
            OPTION (LOOP JOIN, FORCE ORDER, MAXDOP 1)
            """).ToDictionaryAsync(row => row.Ordinal, cancellationToken).ConfigureAwait(false);
        var requests = new List<ProofRequest>(matched.Length * 3);
        var planned = new List<DisclosureContextPlan>(matched.Length);
        for (var ordinal = 0; ordinal < matched.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = matched[ordinal]; var row = byId[binding.ChunkId];
            if (!windows.TryGetValue(ordinal, out var canonical) || canonical.Text is not { } joined) continue;
            var joinedStart = Math.Max(0, binding.CitedStart - 4096);
            if (joined.Length > 16384 || row.Length != row.Content.Length || Hash(row.Content) != row.ChunkHash ||
                row.StartOffset < joinedStart || (long)row.StartOffset + row.Length > (long)joinedStart + joined.Length ||
                !joined.AsSpan(row.StartOffset - joinedStart, row.Length).SequenceEqual(row.Content.AsSpan())) continue;
            var start = binding.CitedStart; var end = binding.CitedStart + binding.CitedLength;
            if (start > joinedStart && char.IsLowSurrogate(joined[start - joinedStart])) start++;
            if (end < joinedStart + joined.Length && end > start && char.IsHighSurrogate(joined[end - joinedStart - 1])) end--;
            var candidate = new EligiblePassageCandidate(row.RootId, row.OwnerSourceRevisionId, row.PipelineRecordId,
                row.PipelineRecordRevision, row.ArtifactId, row.ArtifactHash, row.ChunkId, row.ChunkHash,
                row.StartOffset, row.Length, row.Content, row.SourceIdentity, 0, row.OriginKind, row.CorpusEpoch,
                row.ContextHeader, row.PassagePolicyFingerprint, row.SearchInputHash);
            var disclosureText = BuildDisclosureWindow(joined, joinedStart, start, end, joinedStart == 0,
                joinedStart + joined.Length == canonical.CanonicalLength, out var guardStart);
            var bodyIndex = requests.Count;
            requests.Add(new(row.ArtifactId, row.ArtifactHash, row.StartOffset, row.Length));
            var textIndex = requests.Count;
            requests.Add(new(row.ArtifactId, row.ArtifactHash, start, end - start));
            int? guardIndex = null;
            if (disclosureText is not null)
            {
                guardIndex = requests.Count;
                requests.Add(new(row.ArtifactId, row.ArtifactHash, guardStart, disclosureText.Length));
            }
            planned.Add(new(candidate, start, joined.Substring(start - joinedStart, end - start),
                end - start < binding.CitedLength, disclosureText, bodyIndex, textIndex, guardIndex));
        }
        var proofs = await ReadProofWindowsAsync(context, requests, cancellationToken).ConfigureAwait(false);
        foreach (var plan in planned)
            result.Add(plan.Candidate.ChunkId, new(plan.Candidate with { DisclosureProof = proofs[plan.BodyIndex] },
                plan.Start, plan.Text, plan.ContextBounded, null, plan.DisclosureText,
                proofs[plan.TextIndex], plan.GuardIndex.HasValue ? proofs[plan.GuardIndex.Value] : null));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool MatchesBinding(CandidateRow row, CorpusEvidenceBinding binding) =>
        row.ChunkId == binding.ChunkId && row.CorpusEpoch == binding.CorpusEpoch && row.RootId == binding.RootId &&
        row.OwnerSourceRevisionId == binding.OwnerSourceRevisionId && row.PipelineRecordId == binding.PipelineRecordId &&
        row.PipelineRecordRevision == binding.PipelineRecordRevision && row.ArtifactId == binding.ArtifactId &&
        row.ArtifactHash == binding.ArtifactHash && row.ChunkHash == binding.ChunkHash &&
        Hash(row.SourceIdentity) == binding.SourceIdentityHash && binding.CitedStart >= row.StartOffset &&
        binding.CitedLength >= 0 && (long)binding.CitedStart + binding.CitedLength <= (long)row.StartOffset + row.Length;

    private sealed class DisclosureCanonicalWindowRow
    {
        public int Ordinal { get; init; }
        public int CanonicalLength { get; init; }
        public string? Text { get; init; }
    }
    private sealed record DisclosureContextPlan(EligiblePassageCandidate Candidate, int Start, string Text,
        bool ContextBounded, string? DisclosureText, int BodyIndex, int TextIndex, int? GuardIndex);
}
