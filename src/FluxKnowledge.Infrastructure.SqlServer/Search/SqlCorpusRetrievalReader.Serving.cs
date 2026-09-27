using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    private static async Task EnsureServingAsync(FluxKnowledgeDbContext context, CancellationToken ct)
    {
        if (await context.IndexState.AnyAsync(state => state.Id == 1 && state.CorpusRebuildOperationId != null, ct).ConfigureAwait(false))
            throw new PassageRetrievalRefusalException("rebuilding");
    }
}
