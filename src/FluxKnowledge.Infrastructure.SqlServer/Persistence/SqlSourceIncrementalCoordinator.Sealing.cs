namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    internal Action? BeforeCaptureAttestation { get; init; }
    internal Action? BeforeCaptureAttestationCommit { get; init; }
}
