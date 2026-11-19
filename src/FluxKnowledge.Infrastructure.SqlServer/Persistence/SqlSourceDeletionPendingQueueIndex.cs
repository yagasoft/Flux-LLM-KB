using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Operational performance index; it changes neither canonical data nor the EF migration contract.</summary>
public static class SqlSourceDeletionPendingQueueIndex
{
    internal static string ReadSql()
    {
        using var stream = typeof(SqlSourceDeletionPendingQueueIndex).Assembly
            .GetManifestResourceStream("FluxKnowledge.Sql.SourceDeletionPendingQueueIndex.sql")
            ?? throw new InvalidOperationException("The source deletion pending queue index declaration is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Task EnsureAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(ReadSql(), cancellationToken);
}
