using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

public sealed record DensePassageCandidates(string Status, IReadOnlyList<EligiblePassageCandidate> Candidates);

public sealed class PassageRetrievalRefusalException : InvalidOperationException
{
    public PassageRetrievalRefusalException(string status) : base(status)
    {
        if (status is not ("busy" or "unavailable" or "timeout" or "scope-capacity-exceeded" or "query-too-long" or "index-updating" or "rebuilding"))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
    }
    public string Status { get; }
    public bool Retryable => Status is "busy" or "timeout" or "unavailable" or "index-updating" or "rebuilding";
}

public interface IHybridPassageCandidateReader
{
    ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLexicalCandidatesAsync(
        string query, ResolvedCorpusScope scope, CancellationToken cancellationToken);
    ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken cancellationToken);
}

/// <summary>One scheduler-owned request, with no retained model or private payload replay.</summary>
public interface IScheduledPassageInference
{
    EmbeddingProfile EmbeddingProfile { get; }
    string RerankerFingerprint { get; }
    ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken);
}

/// <summary>Attempts GPU admission only when the transaction can prove that no other GPU work is waiting or owned.</summary>
public interface IConditionalGpuPassageInference : IScheduledPassageInference
{
    ValueTask<T> ExecuteWhenIdleAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken);
}

public interface IHybridPassageRetrieval
{
    ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken cancellationToken);
}
