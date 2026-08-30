using System.Data;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Persists the only valid no-index state: a proven empty durable catalogue.
/// </summary>
public sealed class EmptyCatalogueBootstrapper(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProveAndMarkAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        if (await context.Vectors.AnyAsync(cancellationToken).ConfigureAwait(false) ||
            await context.IndexGenerations.AnyAsync(cancellationToken).ConfigureAwait(false) ||
            await context.IndexGenerationVectors.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("empty-catalogue-state-not-empty");
        }

        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        state.ActiveIndexGenerationId = null;
        state.EmptyCatalogueValidatedAtUtc = _timeProvider.GetUtcNow();
        state.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
