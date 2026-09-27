using System.Data;
using FluxKnowledge.Application.Indexing;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlCorpusRebuildStore
{
    private async ValueTask<T> WithMaintenanceTransactionAsync<T>(string slotKey,
        Func<FluxKnowledgeDbContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(factory, ct).ConfigureAwait(false);
        if (!await drain.IsDrainedAsync(slotKey, ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-gpu-drain-required");
        return await drain.WithHeldAdmissionAsync(async (connection, token) =>
        {
            await using var queryFence = connection.CreateCommand();
            queryFence.CommandText = """
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                    @LockOwner = N'Session', @LockTimeout = 0;
                SELECT @result;
                """;
            queryFence.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = SqlDerivedIndexRecoveryStore.LockResource;
            if (Convert.ToInt32(await queryFence.ExecuteScalarAsync(token).ConfigureAwait(false)) < 0)
                throw new CorpusRebuildRefusalException("corpus-rebuild-query-drain-required");
            await using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connection).Options);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, token).ConfigureAwait(false);
            var result = await action(context, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }, ct).ConfigureAwait(false);
    }
}
