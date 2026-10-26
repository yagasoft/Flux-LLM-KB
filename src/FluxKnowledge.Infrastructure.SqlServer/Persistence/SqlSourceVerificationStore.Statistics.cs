using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    private sealed record BatchStatistics(Guid AttemptId, long SourceBytesRead, long RetainedBytesRead, bool Completed = false);

    private static BatchStatistics RequireBatchProgress(SourceScanRequestEntity request, SourceVerificationBatch batch,
        long sourceBytesRead, long retainedBytesRead)
    {
        var old = JsonSerializer.Deserialize<BatchStatistics>(request.AuditEvidenceJson ?? "{}")
            ?? throw new InvalidDataException("SourceVerificationAttemptMissing");
        if (old.AttemptId != batch.AttemptId || old.SourceBytesRead < 0 || old.RetainedBytesRead < 0 ||
            old.SourceBytesRead > sourceBytesRead || old.RetainedBytesRead > retainedBytesRead ||
            old.Completed && (old.SourceBytesRead != sourceBytesRead || old.RetainedBytesRead != retainedBytesRead))
            throw new InvalidDataException("SourceVerificationAttemptChanged");
        return old;
    }

    private static void SaveBatchProgress(SourceVerificationRunEntity run, SourceScanRequestEntity request,
        BatchStatistics old, long sourceBytesRead, long retainedBytesRead, bool completed)
    {
        run.SourceBytesRead = checked(run.SourceBytesRead + sourceBytesRead - old.SourceBytesRead);
        run.RetainedBytesRead = checked(run.RetainedBytesRead + retainedBytesRead - old.RetainedBytesRead);
        run.BytesRead = checked(run.SourceBytesRead + run.RetainedBytesRead);
        request.AuditEvidenceJson = JsonSerializer.Serialize(new BatchStatistics(old.AttemptId, sourceBytesRead, retainedBytesRead, completed));
    }

    private static bool HasUnfinishedAttempt(string? audit)
    {
        if (audit is null) return false; // An enqueued request has not started an attempt yet.
        try { return JsonSerializer.Deserialize<BatchStatistics>(audit) is not { Completed: true }; }
        catch (JsonException) { return true; }
    }

    private static async Task InvalidateRunAsync(FluxKnowledgeDbContext db, SourceVerificationRunEntity run,
        DateTimeOffset now, CancellationToken ct, Guid? completedRequestId = null, string reason = "source-authority-changed")
    {
        if (!run.ReadCountsIncomplete)
        {
            // The caller holds the root fence. Include old requests: invalidation can precede Prepare's recovery path.
            // Finish has just completed its own tracked audit, which is not saved until this transaction commits.
            var audits = await db.SourceScanRequests.Where(x => x.VerificationRunId == run.Id && x.RequestKind == RequestKind &&
                    (completedRequestId == null || x.Id != completedRequestId))
                .Select(x => x.AuditEvidenceJson).ToArrayAsync(ct).ConfigureAwait(false);
            if (audits.Any(HasUnfinishedAttempt)) run.ReadCountsIncomplete = true;
        }
        run.Active = false; run.State = "Invalidated"; run.Reason = reason; run.CompletedAtUtc = now;
    }
}
