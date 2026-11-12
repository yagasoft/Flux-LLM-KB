namespace FluxKnowledge.Application.Ports;

/// <summary>Optional bounded candidate window; the normal ANN search contract remains 100.</summary>
public interface IAnnCandidatePool
{
    const int MaximumResults = 400;
    ValueTask<IReadOnlyList<AnnMatch>> SearchCandidatePoolAsync(IReadOnlyList<float> query, int limit,
        ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken cancellationToken);
}

public sealed record DenseCandidateWindow(string Status, IReadOnlyList<EligiblePassageCandidate> Baseline,
    IReadOnlyList<EligiblePassageCandidate> Pool)
{
    public int BaselineRawMatches { get; init; }
    public int PoolRawMatches { get; init; }
}

public interface ICorpusDenseCandidateWindowReader
{
    ValueTask<DenseCandidateWindow> ReadDenseCandidateWindowAsync(ICorpusAnnLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken cancellationToken);
}
