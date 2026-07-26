using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlOutboxStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : IOutboxStore
{
    public async ValueTask EnqueueAsync(
        DispatchMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.OutboxMessages.Add(
            new OutboxMessageEntity
            {
                Id = message.Id.Value,
                PipelineRecordId = message.PipelineRecordId.Value,
                SourceRevision = message.SourceRevision,
                Stage = (int)message.Stage,
                Operation = message.Operation,
                DispatchGeneration = message.DispatchGeneration,
                IdempotencyKey = message.IdempotencyKey,
                DueAtUtc = message.DueAtUtc,
                CreatedAtUtc = message.CreatedAtUtc
            });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ClaimedDispatchMessage?> ClaimNextDueAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string> registeredOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentNullException.ThrowIfNull(registeredOperations);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be positive.");
        }

        var operations = registeredOperations
            .Where(static operation => !string.IsNullOrWhiteSpace(operation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (operations.Length == 0)
        {
            return null;
        }

        var operationParameters = string.Join(
            ", ",
            Enumerable.Range(0, operations.Length).Select(index => $"@operation{index}"));
        var sql =
            $"""
             ;WITH [candidate] AS
             (
                 SELECT TOP (1)
                     [Id], [PipelineRecordId], [SourceRevision], [Stage], [Operation],
                     [DispatchGeneration], [IdempotencyKey], [DueAtUtc], [CreatedAtUtc],
                     [DispatchedAtUtc], [LeaseOwner], [LeaseExpiresAtUtc],
                     [LeaseGeneration], [RowVersion]
                 FROM [OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE [DispatchedAtUtc] IS NULL
                   AND [DueAtUtc] <= @nowUtc
                   AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= @nowUtc)
                   AND [Operation] IN ({operationParameters})
                 ORDER BY [DueAtUtc], [CreatedAtUtc], [Id]
             )
             UPDATE [candidate]
             SET [LeaseOwner] = @leaseOwner,
                 [LeaseExpiresAtUtc] = @leaseExpiresAtUtc,
                 [LeaseGeneration] = [LeaseGeneration] + 1
             OUTPUT
                 inserted.[Id],
                 inserted.[PipelineRecordId],
                 inserted.[SourceRevision],
                 inserted.[Stage],
                 inserted.[Operation],
                 inserted.[DispatchGeneration],
                 inserted.[IdempotencyKey],
                 inserted.[DueAtUtc],
                 inserted.[LeaseOwner],
                 inserted.[LeaseExpiresAtUtc],
                 inserted.[LeaseGeneration];
             """;

        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        AddParameter(command, "@nowUtc", SqlDbType.DateTimeOffset, nowUtc);
        AddParameter(
            command,
            "@leaseExpiresAtUtc",
            SqlDbType.DateTimeOffset,
            nowUtc.Add(leaseDuration));
        AddParameter(command, "@leaseOwner", SqlDbType.NVarChar, leaseOwner, 256);
        for (var index = 0; index < operations.Length; index++)
        {
            AddParameter(
                command,
                $"@operation{index}",
                SqlDbType.NVarChar,
                operations[index],
                128);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ClaimedDispatchMessage(
            new DispatchMessageId(reader.GetGuid(0)),
            new PipelineRecordId(reader.GetGuid(1)),
            reader.GetInt64(2),
            (PipelineStage)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10));
    }

    public async ValueTask ReleaseAsync(
        ClaimedDispatchMessage claim,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var affected = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [OutboxMessages]
                 SET [LeaseOwner] = NULL,
                     [LeaseExpiresAtUtc] = NULL,
                     [DueAtUtc] = {dueAtUtc}
                 WHERE [Id] = {claim.DispatchMessageId.Value}
                   AND [DispatchedAtUtc] IS NULL
                   AND [LeaseOwner] = {claim.LeaseOwner}
                   AND [LeaseGeneration] = {claim.LeaseGeneration};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "The outbox lease was lost before it could be released.");
        }
    }

    private static void AddParameter(
        SqlCommand command,
        string name,
        SqlDbType type,
        object value,
        int? size = null)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size is not null)
        {
            parameter.Size = size.Value;
        }

        parameter.Value = value;
    }
}
