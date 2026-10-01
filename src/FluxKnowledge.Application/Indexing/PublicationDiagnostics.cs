using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Application.Indexing;

/// <summary>Phase evidence without SQL messages, paths, content or exception stacks.</summary>
public static class PublicationDiagnostics
{
    public static void Failure(ILogger? logger, Guid identity, string phase, Exception failure)
    {
        var sql = failure.GetType().FullName == "Microsoft.EntityFrameworkCore.DbUpdateException" ? failure.InnerException : failure;
        var number = sql?.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            ? sql.GetType().GetProperty("Number")?.GetValue(sql) as int? : null;
        logger?.LogWarning("Publication {PublicationId} failed during {PublicationPhase}; exception {ExceptionType}; SQL error {SqlErrorNumber}.",
            identity, phase, failure.GetType().Name, number);
    }
}
