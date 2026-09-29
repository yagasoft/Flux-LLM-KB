using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class GpuFirstPassageInferenceTests
{
    [Fact]
    public async Task Busy_without_handoff_uses_cpu_and_preserves_the_same_work_callback()
    {
        var gpu = new ConditionalGpu { Failure = new GpuInteractiveBusyWithoutHandoffException() };
        var cpu = new CpuInference();
        var route = new GpuFirstPassageInference(gpu, cpu);
        var result = await route.ExecuteAsync((embedding, reranker, _) =>
            ValueTask.FromResult(ReferenceEquals(embedding, cpu.Embedding) && ReferenceEquals(reranker, cpu.Reranker)),
            CancellationToken.None);
        Assert.True(result);
        Assert.Equal(1, gpu.Attempts);
        Assert.Equal(1, cpu.Attempts);
    }

    [Fact]
    public async Task Ambiguous_gpu_failure_never_starts_cpu()
    {
        var gpu = new ConditionalGpu { Failure = new TimeoutException("handoff response lost") };
        var cpu = new CpuInference();
        var route = new GpuFirstPassageInference(gpu, cpu);
        await Assert.ThrowsAsync<TimeoutException>(() => route.ExecuteAsync((_, _, _) =>
            ValueTask.FromResult(1), CancellationToken.None).AsTask());
        Assert.Equal(0, cpu.Attempts);
    }

    private sealed class ConditionalGpu : IConditionalGpuPassageInference
    {
        public Exception? Failure { get; init; }
        public int Attempts { get; private set; }
        public EmbeddingProfile EmbeddingProfile => new("test-profile", 1024);
        public string RerankerFingerprint => "test-reranker";
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct) => ExecuteWhenIdleAsync(work, ct);
        public ValueTask<T> ExecuteWhenIdleAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct)
        {
            Attempts++;
            throw Failure ?? new InvalidOperationException("Unexpected GPU execution in routing test");
        }
    }

    private sealed class CpuInference : IScheduledPassageInference
    {
        public int Attempts { get; private set; }
        public IEmbeddingProvider Embedding { get; } = new Embedding();
        public IPassageReranker Reranker { get; } = new Reranker();
        public EmbeddingProfile EmbeddingProfile => new("test-profile", 1024);
        public string RerankerFingerprint => "test-reranker";
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct)
        {
            Attempts++;
            return work(Embedding, Reranker, ct);
        }
    }
    private sealed class Embedding : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => throw new NotImplementedException();
    }
    private sealed class Reranker : IPassageReranker
    {
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
