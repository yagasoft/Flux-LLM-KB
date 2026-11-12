using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    public async ValueTask<IReadOnlySet<long>> ValidateCurrentBindingsAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken)
    {
        if (bindings.Count > 50) throw new ArgumentOutOfRangeException(nameof(bindings));
        if (bindings.Count == 0) return new HashSet<long>();
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Retry the complete read-only validation unit, never a partially collected set.
            var valid = new HashSet<long>();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // Publication, suppression and rebuild writers use this same fence. Hold it only
            // for bounded evidence revalidation, after embedding, ANN and reranking have ended.
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            foreach (var binding in bindings)
                if (await ReadAsync(binding, 0, cancellationToken).ConfigureAwait(false) is not null) valid.Add(binding.ChunkId);
            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlySet<long>)valid;
        });
    }
}
