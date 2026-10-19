using System.Data;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceScanStore
{
    private sealed record IssueCleanupAuthority(DateTimeOffset CompletedAtUtc, DateTimeOffset ExpiresAtUtc);

    private async Task TryCompactResolvedIssuesAsync(ClaimedSourceScan claim, IssueCleanupAuthority authority, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var context = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        context.Database.SetCommandTimeout(2);
        var strategy = context.Database.CreateExecutionStrategy();
        // A strategy scope permits the application's retry-enabled SQL provider to
        // use a transaction. Failures are handled INSIDE it: an ambiguous delete
        // commit must never be retried and delete another batch for the same scan.
        await strategy.ExecuteAsync(async _ =>
        {
            try
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
                await context.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 250;", token).ConfigureAwait(false);
                await SqlPublishedPassageSelection.AcquireFenceAsync(context, token).ConfigureAwait(false);
                var root = await context.SourceRootConfigurations.FromSqlInterpolated(
                    $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={claim.SourceRoot.Id.Value}")
                    .SingleOrDefaultAsync(token).ConfigureAwait(false);
                var now = timeProvider.GetUtcNow();
                if (now >= authority.ExpiresAtUtc || root is null || root.State != (int)SourceRootState.Enabled ||
                    root.ConfigurationRevision != claim.SourceRoot.ConfigurationRevision || root.CrawlMode != (int)claim.SourceRoot.DiscoveryMode ||
                    root.LastScanCompletedAtUtc != authority.CompletedAtUtc) return;
                var job = await context.SourceScanJobs.FromSqlInterpolated(
                    $"SELECT * FROM [SourceScanJobs] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={claim.ControlJobId}")
                    .SingleOrDefaultAsync(token).ConfigureAwait(false);
                if (job is null || job.State != (int)SourceScanJobState.Completed || job.LeaseGeneration != claim.LeaseGeneration ||
                    job.SourceScanRequestId != claim.ScanRequest.Id.Value || job.UpdatedAtUtc != authority.CompletedAtUtc ||
                    job.LeaseOwner != null || job.LeaseExpiresAtUtc != null ||
                    !await context.SourceScanRequests.AnyAsync(request => request.Id == job.SourceScanRequestId &&
                        request.SourceRootId == root.Id && request.State == (int)SourceScanRequestState.Completed && request.IsReleased, token).ConfigureAwait(false) ||
                    await context.SourceScanJobs.AnyAsync(other => other.Id != job.Id && other.SourceScanRequest.SourceRootId == root.Id &&
                        other.State == (int)SourceScanJobState.Running && other.LeaseExpiresAtUtc > now, token).ConfigureAwait(false)) return;

                // The advisory cursor makes bounded passes fair even when an old
                // unresolved prefix remains. It never supplies deletion authority.
                var evidence = JsonNode.Parse(root.HealthEvidenceJson ?? "{}") as JsonObject
                    ?? throw new InvalidOperationException("Source health evidence must be an object to retain cleanup progress.");
                var cursor = evidence["resolvedIssueCompactionCursor"] is JsonValue cursorNode &&
                    cursorNode.TryGetValue<long>(out var savedCursor) && savedCursor >= 0 ? savedCursor : 0;
                var candidates = context.AuditEvents.Where(value => value.SourceScanRequestId == null &&
                    (value.EventType == "activity.failed" || value.EventType == "pipeline.failed" || value.EventType == "retained_processor.blocked") &&
                    (value.SourceRootId == root.Id || value.SourceRootId == null && value.PipelineRecordId != null &&
                        context.PipelineRecords.Any(record => record.Id == value.PipelineRecordId && record.SourceRevision != null && record.SourceRevision.SourceRootId == root.Id)));
                var candidateIds = await candidates.Where(value => value.Id > cursor).OrderBy(value => value.Id)
                    .Take(1_000).Select(value => value.Id).ToArrayAsync(token).ConfigureAwait(false);
                if (candidateIds.Length == 0 && cursor > 0)
                    candidateIds = await candidates.Where(value => value.Id <= cursor).OrderBy(value => value.Id)
                        .Take(1_000).Select(value => value.Id).ToArrayAsync(token).ConfigureAwait(false);
                if (candidateIds.Length == 0) return;
                var resolvedIds = await SqlResolvedSourceIssueEvents.Query(context, root.Id).Where(value => candidateIds.Contains(value.Id))
                    .OrderBy(value => value.Id).Take(100).Select(value => value.Id).ToArrayAsync(token).ConfigureAwait(false);
                // Publication fence and serializable proof reads remain held through
                // deletion and cursor commit; neither can be replayed independently.
                if (resolvedIds.Length > 0)
                    await context.AuditEvents.Where(value => resolvedIds.Contains(value.Id)).ExecuteDeleteAsync(token).ConfigureAwait(false);
                evidence["resolvedIssueCompactionCursor"] = candidateIds[^1];
                root.HealthEvidenceJson = evidence.ToJsonString();
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogDebug("Resolved issue cleanup reached its deadline after source scan {ScanRequestId} completed.", claim.ScanRequest.Id.Value);
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested && exception.Number is -2 or 1205 or 1222)
            {
                logger?.LogDebug(exception, "Resolved issue cleanup did not confirm completion within its SQL budget after source scan {ScanRequestId}; no cleanup retry is attempted.", claim.ScanRequest.Id.Value);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning(exception, "Resolved issue cleanup did not confirm completion for source scan {ScanRequestId}; the completed scan is preserved.", claim.ScanRequest.Id.Value);
            }
        }, token).ConfigureAwait(false);
    }
}
