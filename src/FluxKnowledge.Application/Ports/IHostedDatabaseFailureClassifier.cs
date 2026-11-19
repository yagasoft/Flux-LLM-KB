namespace FluxKnowledge.Application.Ports;

/// <summary>Temporary database failures that defer a hosted iteration, without replaying a worker mutation.</summary>
public interface IHostedDatabaseFailureClassifier
{
    bool TryClassify(Exception exception, out int errorNumber);
}
