using FluxKnowledge.Infrastructure.SqlServer.Configuration;

namespace FluxKnowledge.Infrastructure.SqlServer.Provisioning;

internal sealed class StagingSqlServerReadinessValidator(StagingSqlBinding binding) : ISqlServerReadinessValidator
{
    public Task<SqlServerReadinessResult> ValidateAsync(SqlServerOptions options, CancellationToken cancellationToken = default) =>
        new SqlServerReadinessValidator().ValidateStagingAsync(options, binding, cancellationToken);
}
