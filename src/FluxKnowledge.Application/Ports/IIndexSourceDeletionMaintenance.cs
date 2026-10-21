namespace FluxKnowledge.Application.Ports;

/// <summary>Advance one exact index cleanup target outside the source purge transaction.</summary>
public interface IIndexSourceDeletionMaintenance
{
    ValueTask<string?> AdvanceAsync(SourceDeletionWorkItem work, CancellationToken cancellationToken);
}
