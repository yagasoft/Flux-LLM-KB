using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    internal static ValueTask<RecoveryIndexAudit> AuditRecoveryAsync(PipelineEnvironment environment, string recoveryRoot) =>
        new SqlIndexRecoveryReferences(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe())
            .AuditAsync(recoveryRoot, environment.IndexRoot, CancellationToken.None);
}
