namespace FluxKnowledge.Application.Ports;

/// <summary>Identifies provider failures for which a fresh retained-processor iteration is safe.</summary>
public interface IRetainedProcessorFailureClassifier
{
    bool TryClassify(Exception exception, out int errorNumber);
}
