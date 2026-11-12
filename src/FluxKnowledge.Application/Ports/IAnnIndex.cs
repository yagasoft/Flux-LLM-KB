using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

public sealed record AnnMatch(long VectorId, float Distance);

public interface IAnnIndex
{
    ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(
        IReadOnlyList<float> query,
        int limit,
        CancellationToken cancellationToken);
}

public interface ICorpusAnnScopeSelection
{
    ValueTask<IReadOnlySet<long>> ReadSegmentKeysAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope,
        IndexSegmentDescriptor segment, CancellationToken cancellationToken);
}

/// <summary>Immutable binding preparation; it never grants current publication eligibility.</summary>
public interface ICorpusAnnBindingPreparation
{
    long MaximumRetainedBytes => long.MaxValue;
    bool IsPrepared(Guid epoch, IndexSegmentDescriptor segment);
    ValueTask<long> PrepareAsync(ICorpusGenerationLease lease, IndexSegmentDescriptor segment, CancellationToken cancellationToken);
}

public interface ICorpusAnnPreparationSignal
{
    void RequestPreparation();
    string? FailureFor(Guid generation) => null;
}

public interface ICorpusAnnLease : ICorpusGenerationLease, IAnnIndex
{
    bool IsPrepared(ICorpusAnnBindingPreparation metadata) => true;
    ValueTask PrepareAsync(ICorpusAnnBindingPreparation metadata, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit,
        ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken cancellationToken) =>
        throw new PassageRetrievalRefusalException("unavailable");
}

public interface ICorpusAnnLeaseFactory
{
    /// <summary>Transfers SQL lease ownership, including disposal if native opening fails.</summary>
    ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken);
}
