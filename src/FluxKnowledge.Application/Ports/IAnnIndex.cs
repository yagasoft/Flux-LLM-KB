namespace FluxKnowledge.Application.Ports;

public sealed record AnnMatch(long VectorId, float Distance);

public interface IAnnIndex
{
    ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(
        IReadOnlyList<float> query,
        int limit,
        CancellationToken cancellationToken);
}

public interface ICorpusAnnLease : ICorpusGenerationLease, IAnnIndex;

public interface ICorpusAnnLeaseFactory
{
    /// <summary>Transfers SQL lease ownership, including disposal if native opening fails.</summary>
    ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken);
}
