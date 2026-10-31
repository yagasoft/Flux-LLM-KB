namespace FluxKnowledge.Application.Ports;

public sealed record SourceWorkAdmittedRoot(Guid RootId, long ConfigurationRevision);

public interface ISourceWorkAdmission
{
    IReadOnlyCollection<SourceWorkAdmittedRoot> ReadAdmittedRoots();
}
