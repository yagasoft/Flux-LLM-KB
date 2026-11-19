using System.Data;
using System.Data.SqlTypes;
using System.Transactions;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlCanonicalArtifactReader
{
    internal static Task<ArtifactEntity?> ReadAsync(FluxKnowledgeDbContext context, Guid recordId, long revision, CancellationToken ct)
    {
        // An enclosing transaction/strategy owns its retry scope. Outside it,
        // preserve EF's previous transient SELECT/materialisation retry policy.
        if (context.Database.CurrentTransaction is not null || context.Database.GetEnlistedTransaction() is not null ||
            Transaction.Current is not null || ExecutionStrategy.Current is not null)
            return ReadCoreAsync(context, recordId, revision, ct);
        return context.Database.CreateExecutionStrategy().ExecuteAsync(token => ReadCoreAsync(context, recordId, revision, token), ct);
    }

    private static async Task<ArtifactEntity?> ReadCoreAsync(FluxKnowledgeDbContext context, Guid recordId, long revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
            // Reading nvarchar(max) through EF's buffered async reader can spend
            // tens of seconds replaying packets for a single canonical input.
            // Keep both LOBs last and consume them in ordinal order on the same
            // transaction; all confirmation and publication proofs stay intact.
            command.CommandText = """
                SELECT [Id], [PipelineRecordId], [SourceRevision], [Stage], [ContentHash], [ContentType],
                    [CreatedAtUtc], [WindowEligibilityVersion], [DocumentMetadataJson], [SearchText]
                FROM [Artifacts]
                WHERE [PipelineRecordId] = @recordId AND [SourceRevision] = @revision AND [Stage] = @stage
                """;
            command.Parameters.Add(new SqlParameter("@recordId", SqlDbType.UniqueIdentifier) { Value = recordId });
            command.Parameters.Add(new SqlParameter("@revision", SqlDbType.BigInt) { Value = revision });
            command.Parameters.Add(new SqlParameter("@stage", SqlDbType.Int) { Value = (int)PipelineStage.CanonicalIndex });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            var result = new ArtifactEntity
            {
                Id = reader.GetGuid(0), PipelineRecordId = reader.GetGuid(1), SourceRevision = reader.GetInt64(2),
                Stage = reader.GetInt32(3), ContentHash = reader.GetString(4), ContentType = reader.GetString(5),
                CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(6), WindowEligibilityVersion = reader.GetGuid(7)
            };
            if (!await reader.IsDBNullAsync(8, ct).ConfigureAwait(false))
            {
                using var metadata = reader.GetTextReader(8);
                result.DocumentMetadataJson = await metadata.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            if (await reader.IsDBNullAsync(9, ct).ConfigureAwait(false))
                throw new SqlNullValueException("Canonical artifact text is null.");
            using (var text = reader.GetTextReader(9))
                result.SearchText = await text.ReadToEndAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Sequence contains more than one element.");
            return result;
        }
        finally
        {
            if (opened) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
