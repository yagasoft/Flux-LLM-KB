using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Source_candidates_share_one_two_hundred_binding_disclosure_gate_and_the_fifty_input_model_limit()
    {
        var reader = new Reader
        {
            Lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "query lexical " + i)).ToArray(),
            Dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "query dense " + i)).ToArray(),
            Directed = Enumerable.Range(2001, 100).Select(i => Passage(i, "query directed " + i)).ToArray(),
            DirectedStatus = SourceDirectedRetrievalStatus.Ready, UncitableIds = [2001]
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchAsync("query", Scope, 10, default);
        Assert.Equal("query", reader.LastDenseQuery);
        var disclosed = Assert.Single(reader.DisclosureBatches);
        Assert.Equal(200, disclosed.Length);
        Assert.Equal(50, models.Inputs.Count);
        Assert.All(models.Inputs, p => Assert.Contains(p.PassageId, disclosed));
        Assert.DoesNotContain(models.Inputs, p => p.PassageId == 2001);
        Assert.Contains(models.Inputs, p => p.PassageId == 2002);
        Assert.Equal("ready", result.SemanticStatus);
        Assert.Equal(200, result.CandidateCount);
    }

    [Fact]
    public async Task Fresh_source_alternative_survives_withdrawal_of_the_original_representative()
    {
        var reader = new Reader
        {
            Lexical = [Passage(1, "query shared evidence")], Dense = [Passage(3, "query other evidence")],
            Directed = [Passage(2, "query shared evidence")], DirectedStatus = SourceDirectedRetrievalStatus.Ready,
            UncitableIds = [1]
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchAsync("query", Scope, 10, default);
        Assert.DoesNotContain(models.Inputs, p => p.PassageId == 1);
        Assert.Contains(models.Inputs, p => p.PassageId == 2);
        Assert.Contains(result.Results, p => p.ChunkId == 2 && p.Explanation.Contains("semantic:source-directed"));
        Assert.DoesNotContain(result.Results, p => p.ChunkId == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_identity_conflict_after_the_cutoff_refuses_ranking_and_retains_lexical_fallback(bool invalidSourceHash)
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "query lexical " + i)).ToArray();
        var changed = "query contradictory canonical input";
        var conflict = lexical[^1] with { Content = changed, Length = changed.Length,
            ChunkHash = invalidSourceHash ? lexical[^1].ChunkHash : Hash(changed), SearchInputHash = Hash(changed) };
        var reader = new Reader
        {
            Lexical = lexical, Dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "query dense " + i)).ToArray(),
            Directed = Enumerable.Range(2001, 99).Select(i => Passage(i, "query directed " + i)).Append(conflict).ToArray(),
            DirectedStatus = SourceDirectedRetrievalStatus.Ready
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchAsync("query", Scope, 10, default);
        Assert.Equal("unavailable", result.SemanticStatus);
        Assert.Equal("lexical", result.RetrievalMode);
        Assert.Empty(models.Inputs);
        Assert.All(result.Results, p => Assert.InRange(p.ChunkId, 1, 100));
    }

    [Theory]
    [InlineData(SourceDirectedRetrievalStatus.NotRequested, null)]
    [InlineData(SourceDirectedRetrievalStatus.Ready, null)]
    [InlineData(SourceDirectedRetrievalStatus.Timeout, "source-routing:timeout")]
    [InlineData(SourceDirectedRetrievalStatus.Unavailable, "source-routing:unavailable")]
    public async Task Empty_or_failed_source_channel_preserves_original_candidates_and_fresh_validation(
        SourceDirectedRetrievalStatus status, string? warning)
    {
        var lexical = Passage(1, "query lexical evidence"); var dense = Passage(2, "query dense evidence");
        var baselineReader = new Reader { Lexical = [lexical], Dense = [dense] }; var baselineModel = new Models();
        var baseline = await Engine(baselineReader, baselineModel).SearchAsync("query", Scope, 10, default);
        var reader = new Reader { Lexical = [lexical], Dense = [dense], DirectedStatus = status };
        var models = new Models();
        var current = await Engine(reader, models).SearchAsync("query", Scope, 10, default);
        Assert.Equal(baseline.Results.Select(p => p.ChunkId), current.Results.Select(p => p.ChunkId));
        Assert.Equal(baselineModel.Inputs, models.Inputs);
        Assert.Equal(Assert.Single(baselineReader.DisclosureBatches), Assert.Single(reader.DisclosureBatches));
        Assert.Equal(baseline.SemanticStatus, current.SemanticStatus);
        if (warning is not null) Assert.Contains(warning, current.Warnings);
        else Assert.Equal(baseline.Warnings, current.Warnings);
    }

    [Fact]
    public async Task Query_aware_default_overload_preserves_existing_reader_implementations_and_result_deconstruction()
    {
        var reader = new Reader { Dense = [Passage(1, "query evidence")] };
        IHybridPassageCandidateReader legacy = new LegacyCandidateReader(reader);
        var result = await legacy.ReadDenseCandidatesAsync(new Leases(), Scope, "query", [1, 0], default);
        var (status, rows) = result;
        Assert.Equal("ready", status); Assert.Single(rows);
        Assert.Equal(SourceDirectedRetrievalStatus.NotRequested, result.SourceDirectedStatus);
        Assert.Empty(result.SourceDirectedCandidates); Assert.Equal(1, reader.DenseCalls);
    }

    private sealed class LegacyCandidateReader(Reader reader) : IHybridPassageCandidateReader
    {
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLexicalCandidatesAsync(string query, ResolvedCorpusScope scope, CancellationToken ct) => reader.ReadLexicalCandidatesAsync(query, scope, ct);
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLiteralCandidatesAsync(string query, ResolvedCorpusScope scope, CancellationToken ct) => reader.ReadLiteralCandidatesAsync(query, scope, ct);
        public ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken ct) => reader.ReadDenseCandidatesAsync(lease, scope, query, ct);
    }
}
