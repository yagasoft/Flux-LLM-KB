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

public sealed record IndexGenerationCandidateSnapshot(
    IndexGenerationDescriptor Generation,
    IReadOnlyList<CanonicalVector> Vectors);
