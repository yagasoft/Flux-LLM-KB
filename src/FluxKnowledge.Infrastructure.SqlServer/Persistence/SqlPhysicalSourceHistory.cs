using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlPhysicalSourceHistory
{
    internal static Task<bool> IsValidAsync(FluxKnowledgeDbContext context, SourceRevisionEntity source, CancellationToken ct)
    {
        if (source.OriginKind != 0) return Task.FromResult(false);
        if (source.ParentSourceRevisionId is null) return Task.FromResult(true);
        return context.SourceRevisions.AnyAsync(parent => parent.Id == source.ParentSourceRevisionId && parent.OriginKind == 0 &&
            parent.SourceRootId == source.SourceRootId && EF.Functions.Collate(parent.StableSourceIdentity, "Latin1_General_100_BIN2") == source.StableSourceIdentity &&
            parent.Revision == source.Revision - 1, ct);
    }
}
