namespace FluxKnowledge.Application.Ports;

public interface IIndexGenerationPublisher
{
    ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);

    ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);
}

/// <summary>Signals the one valid no-generation path; SQL must still prove an empty catalogue.</summary>
public sealed class NoEligibleVectorsException : InvalidOperationException
{
    public NoEligibleVectorsException() : base("The current SQL corpus has no eligible vectors.")
    {
    }
}

public sealed record IndexGenerationCandidateSnapshot(
    IndexGenerationDescriptor Generation,
    IReadOnlyList<CanonicalVector> Vectors);
