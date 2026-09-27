namespace FluxKnowledge.Application.Ports;

public interface IIndexGenerationVerifier
{
    void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors);
}
