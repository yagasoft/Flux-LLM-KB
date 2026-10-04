using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class HybridPassageRetrievalTests
{
    [Theory]
    [InlineData("root", false)]
    [InlineData("workspace", false)]
    [InlineData("all", false)]
    [InlineData("root", true)]
    [InlineData("workspace", true)]
    [InlineData("all", true)]
    public async Task Scoped_search_uses_the_SQL_lease_without_opening_ANN_and_releases_once_on_success_or_embedding_failure(
        string kind, bool failEmbedding)
    {
        var reader = new Reader();
        var leases = new Leases();
        var models = new Models { FailEmbedding = failEmbedding };
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], kind == "workspace" ? @"C:\scope" : null);
        var response = await Engine(reader, models, leases).SearchAsync("query", scope, 1, CancellationToken.None);
        Assert.Equal(failEmbedding ? "unavailable" : "ready", response.SemanticStatus);
        Assert.Equal(kind == "all" ? 1 : 0, leases.OpenCalls);
        Assert.Equal(1, leases.DisposeCalls);
        Assert.Equal(failEmbedding ? 0 : 1, reader.DenseCalls);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("root")]
    public async Task ANN_open_failure_consumes_ownership_once_and_scoped_requests_bypass_that_factory(string kind)
    {
        var reader = new Reader();
        var leases = new Leases { FailOpen = true };
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], null);
        var response = await Engine(reader, new Models(), leases).SearchAsync("query", scope, 1, CancellationToken.None);
        Assert.Equal(kind == "all" ? "unavailable" : "ready", response.SemanticStatus);
        Assert.Equal(kind == "all" ? 1 : 0, leases.OpenCalls);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task Cancelled_caller_or_failed_scheduling_acquires_no_SQL_or_native_lease()
    {
        var leases = new Leases();
        var reader = new Reader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Engine(reader, new Models(), leases).SearchAsync("query", Scope, 1, cancellation.Token));
        var response = await Engine(reader, new Models { FailScheduling = true }, leases)
            .SearchAsync("query", Scope, 1, CancellationToken.None);
        Assert.Equal("timeout", response.SemanticStatus);
        Assert.Equal(0, leases.AcquireCalls);
        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, leases.DisposeCalls);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("index-updating")]
    public async Task Empty_degraded_search_refuses_instead_of_reporting_a_healthy_empty_match(string status)
    {
        var reader = new Reader { DenseStatus = status };
        var service = new PassageSearchService(Engine(reader, new Models()));
        var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => service.SearchAsync(
            new("paraphrase", 2, "local_first", null, null, null), CancellationToken.None).AsTask());
        Assert.Equal(status, refusal.Status);
    }

    [Fact]
    public async Task Search_adapter_preserves_shared_passage_order_complete_bodies_counts_and_degradation()
    {
        var reader = new Reader { Dense = [Passage(8, "First complete meaning"), Passage(2, "Second complete meaning")] };
        var models = new Models { FailRanking = true };
        var engine = Engine(reader, models);
        var corpus = await engine.SearchAsync(new CorpusSearchRequest("paraphrase", 2, "all", null, null), CancellationToken.None);
        var search = await new PassageSearchService(engine).SearchAsync(new("paraphrase", 2, "local_first", null, null, null), CancellationToken.None);
        Assert.Equal(corpus.Results.Select(hit => hit.Passage), search.Results.Select(hit => hit.Snippet));
        Assert.Equal(corpus.Results.Select(hit => hit.PipelineRecordId), search.Results.Select(hit => hit.PipelineRecordId.Value));
        Assert.Equal(corpus.CandidateCount, search.CandidateCount);
        Assert.Equal(corpus.IndexGeneration!.Value.ToString("N"), search.ActiveIndexGeneration);
        Assert.Equal(new double[] { 1, 0.5 }, search.Results.Select(hit => hit.Score));
        Assert.All(search.Results, hit => Assert.Contains("rerank:unavailable", hit.Explanation));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("readiness")]
    [InlineData("lexical")]
    [InlineData("fallback-read")]
    public async Task Overall_deadline_stops_waiting_for_blocked_sql_including_scope_and_fallback(string stage)
    {
        var clock = new ManualClock();
        var reader = new Reader { Lexical = [Passage(8, "query answer")], BlockStage = stage };
        var models = new Models { FailScheduling = true };
        var engine = Engine(reader, models, clock: clock);
        var responseTask = engine.SearchAsync(new CorpusSearchRequest("query", 2, "all", null, null), CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(10));
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("timeout", response.SemanticStatus);
        Assert.Contains("search-deadline-exceeded", response.Warnings);
        Assert.Empty(response.Results);
        reader.ReleaseBlock.TrySetResult();
    }

    [Fact]
    public async Task Explicit_twenty_five_second_deadline_extends_the_whole_search_without_changing_the_default()
    {
        var clock = new ManualClock();
        var reader = new Reader { BlockStage = "scope" };
        var leases = new Leases();
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases,
            new Models(), new Probe(), new Codec(), new LocalPrivateContentDisclosure(), clock,
            TimeSpan.FromSeconds(25));
        var responseTask = engine.SearchAsync(new CorpusSearchRequest("query", 2, "all", null, null), CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(responseTask.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(15));
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("timeout", response.SemanticStatus);
        reader.ReleaseBlock.TrySetResult();
    }

    [Fact]
    public async Task Dense_only_match_keeps_complete_body_identity_and_citation_with_no_lexical_anchor()
    {
        var reader = new Reader { Dense = [Passage(8, "Employees receive twenty days of annual leave.")] };
        var models = new Models(); var leases = new Leases();
        var response = await Engine(reader, models, leases).SearchAsync("holiday entitlement", Scope, 2, CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Equal(reader.Dense[0].Content, hit.Passage);
        Assert.Equal(8, hit.ChunkId);
        Assert.Contains("semantic:dense", hit.Explanation);
        Assert.Contains("rerank:trained", hit.Explanation);
        Assert.Equal("hybrid", response.RetrievalMode);
        Assert.Equal("ready", response.SemanticStatus);
        Assert.Equal(leases.Generation.Id, response.IndexGeneration);
        Assert.Equal(new long[] { 8 }, models.Inputs.Select(value => value.PassageId));
        Assert.Equal(reader.Dense[0].Content, models.Inputs[0].SearchText);
        Assert.True(reader.ReadUnderLease);
        Assert.True(leases.Disposed);
    }

    [Fact]
    public async Task Failed_reranking_preserves_entire_fused_order_and_reports_degradation()
    {
        var reader = new Reader { Dense = [Passage(8, "First meaning"), Passage(2, "Second meaning")] };
        var models = new Models { FailRanking = true };
        var response = await Engine(reader, models).SearchAsync("paraphrase", Scope, 2, CancellationToken.None);
        Assert.Equal(new long[] { 8, 2 }, response.Results.Select(value => value.ChunkId));
        Assert.Contains("rerank:unavailable", response.Warnings);
        Assert.All(response.Results, value => Assert.DoesNotContain("rerank:trained", value.Explanation));
    }

    [Fact]
    public async Task Incomplete_ranking_output_discards_all_partial_scores()
    {
        var reader = new Reader { Dense = [Passage(8, "First"), Passage(2, "Second")] };
        var response = await Engine(reader, new Models { PartialRanking = true }).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Equal(new long[] { 8, 2 }, response.Results.Select(value => value.ChunkId));
        Assert.Contains("rerank:invalid-output", response.Warnings);
    }

    [Fact]
    public async Task Changed_generation_discards_semantic_results_before_return_and_releases_lease()
    {
        var reader = new Reader { Dense = [Passage(8, "Meaning")] }; var leases = new Leases { Current = false };
        var response = await Engine(reader, new Models(), leases).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Empty(response.Results);
        Assert.Equal("index-updating", response.SemanticStatus);
        Assert.Equal("lexical", response.RetrievalMode);
        Assert.True(leases.Disposed);
    }

    [Fact]
    public async Task Scoped_capacity_refusal_uses_lexical_results_with_explicit_status_and_no_reranking()
    {
        var reader = new Reader { Lexical = [Passage(8, "query value")], DenseStatus = "scope-capacity-exceeded" };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Single(response.Results);
        Assert.Equal("scope-capacity-exceeded", response.SemanticStatus);
        Assert.Empty(models.Inputs);
    }

    [Fact]
    public async Task Withheld_inputs_are_never_sent_to_reranking_or_returned()
    {
        var reader = new Reader { Dense = [Passage(8, "password=private-secret-sentinel"), Passage(2, "Public passage")] };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Equal(2, Assert.Single(response.Results).ChunkId);
        Assert.Equal(2, Assert.Single(models.Inputs).PassageId);
    }

    [Fact]
    public async Task Stale_first_passage_tops_up_only_from_already_scored_shortlist()
    {
        var reader = new Reader { Dense = [Passage(8, "First"), Passage(2, "Second")], StaleId = 8 };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 1, CancellationToken.None);
        Assert.Equal(2, Assert.Single(response.Results).ChunkId);
        Assert.Equal(2, models.Inputs.Count);
        Assert.Equal(1, reader.DenseCalls);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("root")]
    [InlineData("workspace")]
    public async Task Timeout_keeps_native_or_SQL_lease_owned_until_background_callback_finishes(string kind)
    {
        var reader = new Reader { Dense = [Passage(8, "Meaning")] }; var leases = new Leases();
        var models = new Models { SimulateCallerTimeout = true };
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], kind == "workspace" ? @"C:\scope" : null);
        var response = await Engine(reader, models, leases).SearchAsync("query", scope, 1, CancellationToken.None);
        Assert.Equal("timeout", response.SemanticStatus);
        Assert.False(leases.Disposed);
        models.AllowNativeCompletion.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.DetachedWork!);
        Assert.True(leases.Disposed);
        Assert.Equal(1, leases.DisposeCalls);
        Assert.Equal(kind == "all" ? 1 : 0, leases.OpenCalls);
    }

    private static readonly ResolvedCorpusScope Scope = new("all", [], null);
    private static HybridPassageRetrievalEngine Engine(Reader reader, Models models, Leases? leases = null, TimeProvider? clock = null)
    {
        leases ??= new(); reader.Leases = leases;
        return new(reader, reader, leases, leases, models, new Probe(), new Codec(), new LocalPrivateContentDisclosure(), clock);
    }

    private static EligiblePassageCandidate Passage(long id, string body) => new(null, null, Guid.NewGuid(), 1,
        Guid.NewGuid(), new string('a', 64), id, Hash(body), 0, body.Length, body, "C:\\public\\policy.txt",
        CorpusEpoch: Leases.Epoch, PassagePolicyFingerprint: new string('b', 64), SearchInputHash: Hash(body));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Reader : ICorpusRetrievalReader, IHybridPassageCandidateReader
    {
        public Leases Leases { get; set; } = null!;
        public IReadOnlyList<EligiblePassageCandidate> Lexical { get; init; } = [];
        public IReadOnlyList<EligiblePassageCandidate> Dense { get; init; } = [];
        public string DenseStatus { get; init; } = "ready";
        public long? StaleId { get; init; }
        public int DenseCalls { get; private set; }
        public bool ReadUnderLease { get; private set; }
        public string? BlockStage { get; init; }
        public TaskCompletionSource BlockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task BlockAsync(string stage)
        { if (BlockStage == stage) { BlockStarted.TrySetResult(); await ReleaseBlock.Task; } }
        public async ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken ct)
        { await BlockAsync("readiness"); return new(true, true); }
        public ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(string query, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<string>>([query]);
        public async ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(string kind, Guid? root, string? cwd, CancellationToken ct)
        { await BlockAsync("scope"); return Scope; }
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(string query, ResolvedCorpusScope scope, int limit, CancellationToken ct) => throw new NotSupportedException();
        public async ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLexicalCandidatesAsync(string query, ResolvedCorpusScope scope, CancellationToken ct)
        { await BlockAsync("lexical"); return Lexical; }
        public ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken ct)
        { DenseCalls++; return ValueTask.FromResult(new DensePassageCandidates(DenseStatus, Dense)); }
        public async ValueTask<EligibleContext?> ReadAsync(CorpusEvidenceBinding binding, int context, CancellationToken ct)
        {
            await BlockAsync("fallback-read");
            ReadUnderLease = !Leases.Disposed;
            var passage = Lexical.Concat(Dense).FirstOrDefault(value => value.ChunkId == binding.ChunkId && value.ChunkId != StaleId);
            return passage is null ? null : new EligibleContext(passage, passage.StartOffset, passage.Content, false, null, passage.Content);
        }
    }

    private sealed class Models : IScheduledPassageInference, IEmbeddingProvider, IPassageReranker
    {
        public EmbeddingProfile EmbeddingProfile { get; } = new("embedding", 2);
        public string RerankerFingerprint => "ranking";
        public bool FailRanking { get; init; }
        public bool PartialRanking { get; init; }
        public bool SimulateCallerTimeout { get; init; }
        public bool FailScheduling { get; init; }
        public bool FailEmbedding { get; init; }
        public IReadOnlyList<RerankPassage> Inputs { get; private set; } = [];
        public TaskCompletionSource AllowNativeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? DetachedWork { get; private set; }
        public async ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            if (FailScheduling) throw new TimeoutException();
            if (!SimulateCallerTimeout) return await work(this, this, ct);
            DetachedWork = work(this, this, ct).AsTask();
            throw new TimeoutException();
        }
        public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct)
        {
            if (SimulateCallerTimeout) await AllowNativeCompletion.Task;
            if (FailEmbedding) throw new InvalidOperationException("synthetic-embedding-failure");
            return new([1, 0], "embedding");
        }
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
        {
            Inputs = passages;
            if (FailRanking) throw new InvalidOperationException("synthetic-ranking-failure");
            return ValueTask.FromResult(new RerankResult(passages.Take(PartialRanking ? 1 : passages.Count)
                .Select((value, i) => new RerankScore(value.PassageId, -i)).ToArray(), RerankerFingerprint));
        }
    }

    private sealed class Leases : ICorpusGenerationLeaseStore, ICorpusAnnLeaseFactory, ICorpusAnnLease
    {
        public static readonly Guid Epoch = Guid.NewGuid();
        public bool Current { get; init; } = true;
        public bool Disposed { get; private set; }
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public bool FailOpen { get; init; }
        public Guid LeaseId { get; } = Guid.NewGuid();
        public IndexGenerationDescriptor Generation { get; } = new(Guid.NewGuid(), "embedding", 2, "synthetic", "checksum", 1, new(Epoch, 1));
        public ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid instance, GpuInteractiveOwnerIdentity owner, string fingerprint, int dimensions, CancellationToken ct)
        { AcquireCalls++; return ValueTask.FromResult<ICorpusGenerationLease?>(this); }
        public async ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken ct)
        {
            OpenCalls++;
            if (FailOpen) { await lease.DisposeAsync(); throw new InvalidOperationException("synthetic-open-failure"); }
            return this;
        }
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => ValueTask.FromResult(Current);
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { DisposeCalls++; Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Probe : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => new(1, DateTimeOffset.UnixEpoch, new string('a', 64));
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => GpuInteractiveOwnerObservation.Alive;
    }
    private sealed class Codec : ICorpusEvidenceCodec
    {
        public string Encode(CorpusEvidenceBinding binding) => "synthetic-" + binding.ChunkId;
        public CorpusEvidenceBinding Decode(string reference) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime; return true; }
            public void Fire(DateTimeOffset now) { if (_due <= now) { _due = null; callback(state); } }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
