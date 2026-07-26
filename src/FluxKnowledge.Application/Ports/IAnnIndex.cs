namespace FluxKnowledge.Application.Ports;

public sealed record AnnMatch(long VectorId, float Distance);

public interface IAnnIndex
{
    ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(
        IReadOnlyList<float> query,
        int limit,
        CancellationToken cancellationToken);
}
