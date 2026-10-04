using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    private static async Task<EligiblePassageCandidate[]> AttachProofsAsync(
        FluxKnowledgeDbContext context, EligiblePassageCandidate[] candidates, CancellationToken cancellationToken)
    {
        var windows = await ReadProofWindowsAsync(context, candidates.Select(candidate =>
            new ProofRequest(candidate.ArtifactId, candidate.ArtifactHash, candidate.StartOffset, candidate.Length)).ToArray(),
            cancellationToken).ConfigureAwait(false);
        return candidates.Select((candidate, index) => candidate with { DisclosureProof = windows[index] }).ToArray();
    }

    private static async Task<CodeDisclosureWindow?[]> ReadProofWindowsAsync(
        FluxKnowledgeDbContext context, IReadOnlyList<ProofRequest> requests, CancellationToken cancellationToken)
    {
        var windows = new CodeDisclosureWindow?[requests.Count];
        if (requests.Count == 0) return windows;
        var requestJson = JsonSerializer.Serialize(requests.Select((request, index) => new
        {
            Ordinal = index, request.ArtifactId, request.CanonicalHash, request.Start, request.Length
        }));
        // Ready rows are immutable. HOLDLOCK keeps the header, complete count and
        // intersecting rows coherent even when artifact cleanup races this statement.
        var rows = await context.Database.SqlQuery<ProofRow>($"""
            SELECT [request].[Ordinal], [proof].[ArtifactId], [proof].[CanonicalHash],
                   [proof].[CanonicalLength], [proof].[Fingerprint], [proof].[State], [proof].[SpanCount],
                   [count].[PersistedSpanCount], [span].[Start], [span].[End], [span].[Kind], [span].[Checksum]
            FROM OPENJSON({requestJson}) WITH (
                [Ordinal] int '$.Ordinal', [ArtifactId] uniqueidentifier '$.ArtifactId',
                [CanonicalHash] varchar(64) '$.CanonicalHash', [Start] int '$.Start', [Length] int '$.Length') AS [request]
            INNER JOIN [CanonicalCodeDisclosureProofs] AS [proof] WITH (HOLDLOCK)
                ON [proof].[ArtifactId] = [request].[ArtifactId]
               AND [proof].[CanonicalHash] = [request].[CanonicalHash]
               AND [proof].[Fingerprint] = {CodeDisclosureIntegrity.Fingerprint} AND [proof].[State] = 0
            CROSS APPLY (
                SELECT COUNT(*) AS [PersistedSpanCount]
                FROM [CanonicalCodeDisclosureSpans] AS [allspans] WITH (HOLDLOCK)
                WHERE [allspans].[ArtifactId] = [proof].[ArtifactId] AND [allspans].[Fingerprint] = [proof].[Fingerprint]
            ) AS [count]
            LEFT JOIN [CanonicalCodeDisclosureSpans] AS [span] WITH (HOLDLOCK)
                ON [span].[ArtifactId] = [proof].[ArtifactId] AND [span].[Fingerprint] = [proof].[Fingerprint]
               AND [span].[Start] < CONVERT(bigint, [request].[Start]) + [request].[Length]
               AND [span].[End] > [request].[Start]
            ORDER BY [request].[Ordinal], [span].[Start], [span].[End], [span].[Kind]
            """).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => row.Ordinal))
        {
            var header = group.First();
            var request = requests[group.Key];
            windows[group.Key] = new(header.ArtifactId, header.CanonicalHash, header.CanonicalLength,
                header.Fingerprint, (CodeDisclosureProofState)header.State, header.SpanCount, header.PersistedSpanCount,
                request.Start, request.Length, group.Where(row => row.Start.HasValue).Select(row =>
                    new CodeDisclosureSpan(row.Start!.Value, row.End!.Value, (CodeDisclosureSpanKind)row.Kind!.Value,
                        row.Checksum ?? "")).ToArray());
        }
        return windows;
    }

    private sealed record ProofRequest(Guid ArtifactId, string CanonicalHash, int Start, int Length);
    private sealed class ProofRow
    {
        public int Ordinal { get; init; }
        public Guid ArtifactId { get; init; }
        public string CanonicalHash { get; init; } = "";
        public int CanonicalLength { get; init; }
        public string Fingerprint { get; init; } = "";
        public int State { get; init; }
        public int SpanCount { get; init; }
        public int PersistedSpanCount { get; init; }
        public int? Start { get; init; }
        public int? End { get; init; }
        public int? Kind { get; init; }
        public string? Checksum { get; init; }
    }
}
