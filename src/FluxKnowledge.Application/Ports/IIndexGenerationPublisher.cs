namespace FluxKnowledge.Application.Ports;

public interface IIndexGenerationPublisher
{
    ValueTask<IndexGenerationDescriptor> BuildAndPlaceAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);

    ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);
}
