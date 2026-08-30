using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public enum SourceRootLockPoint
{
    BeforeCanonicalRootLock,
    BeforeScanRequestLock
}

public sealed class SqlSourceRootStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider,
    Action? transactionFailureInjector = null,
    Func<SourceRootLockPoint, CancellationToken, Task>? lockObserver = null) : ISourceRootStore
{
    private const string SourceScanOperation = "source.scan";

    public async ValueTask<SourceRootReceipt> CreateAsync(
        SourceRootCreateRequest request,
        ScanStartIntent startIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestedBy);
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.FullPath));
        _ = SourceRootConfiguration.Create(
            canonicalPath,
            request.DisplayName,
            request.Recursive,
            request.FollowLinks,
            request.MaximumFileBytes,
            request.IncludePatterns,
            request.ExcludePatterns,
            request.AllowedClassifications,
            request.ReconciliationCadence);
        var configuration = SourceRootControlConfiguration.From(request);

        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    return await CreateWithinTransactionAsync(
                            context,
                            request,
                            startIntent,
                            canonicalPath,
                            configuration,
                            cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task<SourceRootReceipt> CreateWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        SourceRootCreateRequest request,
        ScanStartIntent startIntent,
        string canonicalPath,
        SourceRootControlConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        if (lockObserver is not null)
        {
            await lockObserver(SourceRootLockPoint.BeforeCanonicalRootLock, cancellationToken)
                .ConfigureAwait(false);
        }

        await LockCanonicalRootAsync(context, canonicalPath, cancellationToken).ConfigureAwait(false);
        var existing = await context.SourceRootConfigurations
            .SingleOrDefaultAsync(root => root.CanonicalPath == canonicalPath, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!configuration.Matches(existing) ||
                !PhysicalIdentityMatches(existing.HealthEvidenceJson, request.PathValidation))
            {
                throw new InvalidOperationException("A source root already exists with a different configuration fingerprint or physical identity.");
            }

            var receipt = await ExistingReceiptAsync(context, existing.Id, cancellationToken).ConfigureAwait(false);
            if (startIntent == ScanStartIntent.SaveAndScan && receipt.IsHeld)
            {
                await ReleaseHeldAsync(
                        context,
                        existing.Id,
                        receipt.SourceScanRequestId.Value,
                        timeProvider.GetUtcNow(),
                        request.RequestedBy,
                        cancellationToken)
                    .ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                transactionFailureInjector?.Invoke();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return receipt with { IsHeld = false };
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt;
        }

        var now = timeProvider.GetUtcNow();
        var rootId = SourceRootId.New();
        var requestId = SourceScanRequestId.New();
        var jobId = JobId.New();
        var outboxId = DispatchMessageId.New();
        var released = startIntent == ScanStartIntent.SaveAndScan;
        var dueAtUtc = released ? now : DateTimeOffset.MaxValue;
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId.Value,
            CanonicalPath = canonicalPath,
            DisplayName = request.DisplayName,
            State = (int)SourceRootState.Enabled,
            Recursive = request.Recursive,
            IncludePatternsJson = configuration.IncludePatternsJson,
            ExcludePatternsJson = configuration.ExcludePatternsJson,
            FollowLinks = request.FollowLinks,
            MaximumFileBytes = request.MaximumFileBytes,
            AllowedClassificationsJson = configuration.AllowedClassificationsJson,
            CrawlMode = 0,
            ReconciliationCadenceSeconds = checked((long)request.ReconciliationCadence.TotalSeconds),
            PermissionEvidenceJson = request.PathValidation?.PermissionEvidenceJson,
            HealthEvidenceJson = SourceRootControlAuditEvidence.CreateHealthEvidence(request.PathValidation, configuration),
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceScanRequests.Add(new SourceScanRequestEntity
        {
            Id = requestId.Value,
            SourceRootId = rootId.Value,
            RequestKind = 0,
            RequestedBy = request.RequestedBy,
            RequestedAtUtc = now,
            IsReleased = released,
            ReleasedAtUtc = released ? now : null,
            State = released ? (int)SourceScanRequestState.Released : (int)SourceScanRequestState.Held,
            AuditEvidenceJson = SourceRootControlAuditEvidence.CreateRequestEvidence(
                configuration,
                request.RequestedBy,
                released ? request.RequestedBy : null,
                released ? now : null)
        });
        context.SourceScanJobs.Add(new SourceScanJobEntity
        {
            Id = jobId.Value,
            SourceScanRequestId = requestId.Value,
            State = 0,
            DueAtUtc = dueAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceScanOutbox.Add(new SourceScanOutboxEntity
        {
            Id = outboxId.Value,
            SourceScanRequestId = requestId.Value,
            Operation = SourceScanOperation,
            IdempotencyKey = $"source-scan:{requestId.Value:N}",
            DueAtUtc = dueAtUtc,
            CreatedAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        transactionFailureInjector?.Invoke();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SourceRootReceipt(rootId, requestId, jobId, outboxId, IsHeld: !released);
    }

    public async ValueTask<bool> ReleaseAsync(
        SourceRootId sourceRootId,
        SourceScanRequestId sourceScanRequestId,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRootId);
        ArgumentNullException.ThrowIfNull(sourceScanRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    return await ReleaseWithinTransactionAsync(
                            context,
                            sourceRootId,
                            sourceScanRequestId,
                            actor,
                            cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task<bool> ReleaseWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        SourceRootId sourceRootId,
        SourceScanRequestId sourceScanRequestId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        if (lockObserver is not null)
        {
            await lockObserver(SourceRootLockPoint.BeforeScanRequestLock, cancellationToken)
                .ConfigureAwait(false);
        }

        await LockScanRequestAsync(
                context,
                sourceRootId.Value,
                sourceScanRequestId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        var request = await context.SourceScanRequests.SingleAsync(
                value => value.Id == sourceScanRequestId.Value && value.SourceRootId == sourceRootId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.IsReleased)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await ReleaseHeldAsync(
                context,
                sourceRootId.Value,
                sourceScanRequestId.Value,
                timeProvider.GetUtcNow(),
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        transactionFailureInjector?.Invoke();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<SourceRootReceipt> ExistingReceiptAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var request = await context.SourceScanRequests
            .Where(value => value.SourceRootId == rootId)
            .OrderBy(value => value.RequestedAtUtc)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var job = await context.SourceScanJobs.SingleAsync(
                value => value.SourceScanRequestId == request.Id,
                cancellationToken)
            .ConfigureAwait(false);
        var outbox = await context.SourceScanOutbox.SingleAsync(
                value => value.SourceScanRequestId == request.Id,
                cancellationToken)
            .ConfigureAwait(false);
        return new SourceRootReceipt(
            new SourceRootId(rootId),
            new SourceScanRequestId(request.Id),
            new JobId(job.Id),
            new DispatchMessageId(outbox.Id),
            IsHeld: !request.IsReleased);
    }

    private static async Task ReleaseHeldAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        Guid requestId,
        DateTimeOffset releasedAtUtc,
        string actor,
        CancellationToken cancellationToken)
    {
        var request = await context.SourceScanRequests.SingleAsync(
                value => value.Id == requestId && value.SourceRootId == rootId,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.IsReleased)
        {
            return;
        }

        request.IsReleased = true;
        request.ReleasedAtUtc = releasedAtUtc;
        request.State = (int)SourceScanRequestState.Released;
        request.AuditEvidenceJson = SourceRootControlAuditEvidence.AppendReleaseEvidence(
            request.AuditEvidenceJson,
            actor,
            releasedAtUtc);
        var job = await context.SourceScanJobs.SingleAsync(
                value => value.SourceScanRequestId == requestId,
                cancellationToken)
            .ConfigureAwait(false);
        job.DueAtUtc = releasedAtUtc;
        job.UpdatedAtUtc = releasedAtUtc;
        var outbox = await context.SourceScanOutbox.SingleAsync(
                value => value.SourceScanRequestId == requestId,
                cancellationToken)
            .ConfigureAwait(false);
        outbox.DueAtUtc = releasedAtUtc;
    }

    private static Task LockCanonicalRootAsync(
        FluxKnowledgeDbContext context,
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             SELECT [Id]
             FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRootConfigurations_CanonicalPathFingerprint]))
             WHERE [CanonicalPathFingerprint] = CONVERT(char(64), HASHBYTES('SHA2_256', {canonicalPath}), 2);
             """,
            cancellationToken);
    }

    private static Task LockScanRequestAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             SELECT [Id]
             FROM [SourceScanRequests] WITH (UPDLOCK, HOLDLOCK)
             WHERE [Id] = {requestId}
               AND [SourceRootId] = {rootId};
             """,
            cancellationToken);

    private static bool PhysicalIdentityMatches(
        string? healthEvidenceJson,
        SourceRootPathValidation? validation)
    {
        if (validation is null)
        {
            // Direct persistence fixtures have no filesystem policy dependency.
            return true;
        }

        try
        {
            var root = JsonNode.Parse(healthEvidenceJson ?? "{}") as JsonObject;
            var physicalIdentity = root?
                .FirstOrDefault(property => string.Equals(property.Key, "physicalIdentity", StringComparison.OrdinalIgnoreCase))
                .Value as JsonObject;
            var persistedFingerprint = physicalIdentity?
                .FirstOrDefault(property => string.Equals(property.Key, "identityFingerprint", StringComparison.OrdinalIgnoreCase))
                .Value?.GetValue<string>();
            return string.Equals(
                persistedFingerprint,
                validation.PhysicalIdentity.IdentityFingerprint,
                StringComparison.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Source root health evidence is invalid.", exception);
        }
    }

}
