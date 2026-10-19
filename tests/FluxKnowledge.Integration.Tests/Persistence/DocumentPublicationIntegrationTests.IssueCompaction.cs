using FluxKnowledge.Application.Contracts;
using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed partial class DocumentPublicationIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("configuration-changed")]
    [InlineData("expired-lease")]
    [InlineData("paused-root")]
    [InlineData("failed-scan")]
    [InlineData("withdrawn-publication")]
    public async Task Scan_completion_compacts_at_most_one_hundred_proved_events_only_with_fresh_authority(string change)
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        await AddRepeatedBlockedIssuesAsync(seed, 120);
        var now = DateTimeOffset.UtcNow;
        var store = new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), new IssueCleanupClock(now));
        var claim = await store.ClaimNextReleasedAsync("issue-cleanup", now,
            change == "expired-lease" ? TimeSpan.FromSeconds(-1) : TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claim);
        await using (var changeState = Context())
        {
            if (change == "configuration-changed") (await changeState.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++;
            if (change == "paused-root") (await changeState.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused;
            if (change == "withdrawn-publication") changeState.DocumentPublications.Remove(await changeState.DocumentPublications.SingleAsync());
            await changeState.SaveChangesAsync();
        }
        await store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0),
            change == "failed-scan" ? "io" : null, CancellationToken.None);
        await using var verify = Context();
        Assert.Equal(change == "none" ? 24 : 124, await verify.AuditEvents.CountAsync(value => value.Severity == "error" || value.Severity == "warning"));
        Assert.True(await verify.AuditEvents.AnyAsync(value => value.Id == seed.IndependentEventId));
        Assert.Equal(change == "failed-scan" ? (int)SourceScanRequestState.Released : (int)SourceScanRequestState.Completed,
            (await verify.SourceScanRequests.SingleAsync()).State);
        Assert.Equal(3, await verify.SourceActivityRelations.CountAsync());
        Assert.True(await verify.Jobs.AnyAsync(value => value.PipelineRecordId == seed.Prior.RecordId));
        Assert.True(await verify.SourceArtifacts.AnyAsync(value => value.SourceRevisionId == seed.OwnerId));
        Assert.Equal(-1, await verify.Database.SqlQuery<int>($"SELECT @@LOCK_TIMEOUT AS [Value]").SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Ambiguous_cleanup_commit_is_reported_without_repeating_the_batch_or_reopening_the_scan()
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        await AddRepeatedBlockedIssuesAsync(seed, 220);
        var now = DateTimeOffset.UtcNow;
        var claim = await new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), new IssueCleanupClock(now))
            .ClaimNextReleasedAsync("cleanup-ack-test", now, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claim);
        var commits = new CleanupCommitObserver(count => count == 2
            ? Task.FromException(new TimeoutException("Simulated lost cleanup commit acknowledgement.")) : Task.CompletedTask);
        var log = new IssueCleanupLog();
        var store = new SqlSourceScanStore(new IssueContextFactory(_fixture.ConnectionString, commits), new IssueCleanupClock(now), logger: log);
        await store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, CancellationToken.None);
        await using var verify = Context();
        Assert.Equal(2, commits.Count);
        Assert.Equal(124, await verify.AuditEvents.CountAsync(value => value.Severity == "error" || value.Severity == "warning"));
        Assert.Equal((int)SourceScanRequestState.Completed, (await verify.SourceScanRequests.SingleAsync()).State);
        Assert.Single(log.Warnings);
        Assert.Contains(claim.ScanRequest.Id.Value.ToString(), log.Warnings[0]);
    }

    [NativeSqlServerTheory]
    [InlineData("withdraw-publication")]
    [InlineData("change-root-configuration")]
    [InlineData("new-active-scan")]
    public async Task Cleanup_rechecks_publication_and_scan_authority_after_the_completion_commit(string change)
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var ordinary = new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), new IssueCleanupClock(now));
        var claim = await ordinary.ClaimNextReleasedAsync("cleanup-race", now, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.NotNull(claim);
        var commits = new CleanupCommitObserver(async count =>
        {
            if (count != 1) return;
            if (change == "new-active-scan")
            {
                Assert.NotNull(await ordinary.ClaimNextReleasedAsync("new-scan", now.AddMinutes(16), TimeSpan.FromMinutes(30), CancellationToken.None));
                return;
            }
            await using var mutate = Context();
            if (change == "withdraw-publication") mutate.DocumentPublications.Remove(await mutate.DocumentPublications.SingleAsync());
            else (await mutate.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++;
            await mutate.SaveChangesAsync();
        });
        await new SqlSourceScanStore(new IssueContextFactory(_fixture.ConnectionString, commits), new IssueCleanupClock(now))
            .CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, CancellationToken.None);
        await using var verify = Context();
        Assert.Equal(4, await verify.AuditEvents.CountAsync(value => seed.EventIds.Contains(value.Id)));
        Assert.Equal((int)SourceScanJobState.Completed, (await verify.SourceScanJobs.SingleAsync(value => value.Id == claim.ControlJobId)).State);
    }

    [NativeSqlServerFact]
    public async Task Current_issue_page_uses_one_bounded_set_query_with_a_large_unrelated_backlog()
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        await using (var arrange = Context())
        {
            for (var index = 0; index < 2_500; index++)
                arrange.AuditEvents.Add(new AuditEventEntity { EventType = "source.access_denied", EventFamily = "source", Severity = "error",
                    SourceRootId = seed.RootId, Actor = "test", OccurredAtUtc = DateTimeOffset.UtcNow, DetailsJson = "{}" });
            await arrange.SaveChangesAsync();
        }
        var commands = new IssueReadObserver();
        var reader = new SqlOperatorEventProjectionReader(new IssueContextFactory(_fixture.ConnectionString, commands));
        var page = await reader.ReadPageAsync(new OperatorEventQuery(new OperatorEventFilters(SourceRootId: seed.RootId), PageSize: 25), CancellationToken.None);
        Assert.Equal(25, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal("source.access_denied", item.EventType));
        Assert.NotNull(page.NextCursor);
        Assert.Equal(1, commands.ReaderCommands);
        Assert.True(commands.MaximumSqlDuration < TimeSpan.FromSeconds(5), $"SQL took {commands.MaximumSqlDuration}.");
    }

    private async Task AddRepeatedBlockedIssuesAsync(OfficeEventChain seed, int count)
    {
        await using var arrange = Context();
        var blocked = await arrange.AuditEvents.SingleAsync(value => value.Id == seed.BlockedEventId);
        for (var index = 0; index < count; index++)
            arrange.AuditEvents.Add(new AuditEventEntity
            {
                EventType = blocked.EventType, EventFamily = blocked.EventFamily, Severity = blocked.Severity,
                Actor = "test", SourceRootId = blocked.SourceRootId, SourceRevisionId = blocked.SourceRevisionId,
                CorrelationId = blocked.CorrelationId, OccurredAtUtc = blocked.OccurredAtUtc, DetailsJson = "{}"
            });
        await arrange.SaveChangesAsync();
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_setup_failure_preserves_scan_completion_and_caller_cancellation(bool callerCancelled)
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var claim = await new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), new IssueCleanupClock(now))
            .ClaimNextReleasedAsync("cleanup-setup", now, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claim);
        using var cancellation = new CancellationTokenSource();
        var factory = new IssueContextFactory(_fixture.ConnectionString)
        {
            CreationFailure = count =>
            {
                if (count != 3) return null;
                if (!callerCancelled) return new IOException("Simulated cleanup context setup failure.");
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            }
        };
        var log = new IssueCleanupLog();
        var store = new SqlSourceScanStore(factory, new IssueCleanupClock(now), logger: log);
        var completion = store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, cancellation.Token).AsTask();
        if (callerCancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        else await completion;
        await using var verify = Context();
        Assert.Equal((int)SourceScanJobState.Completed, (await verify.SourceScanJobs.SingleAsync()).State);
        Assert.Equal(4, await verify.AuditEvents.CountAsync(value => seed.EventIds.Contains(value.Id)));
        Assert.Equal(callerCancelled ? 0 : 1, log.Warnings.Count);
    }

    [NativeSqlServerFact]
    public async Task Bounded_cleanup_cursor_reaches_resolved_tail_without_discarding_unresolved_prefix_or_health_evidence()
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        await using (var arrange = Context())
        {
            (await arrange.SourceRootConfigurations.SingleAsync()).HealthEvidenceJson = "{\"existingEvidence\":\"preserve\"}";
            for (var index = 0; index < 1_050; index++)
                arrange.AuditEvents.Add(new AuditEventEntity { EventType = "retained_processor.blocked", EventFamily = "retained_processor", Severity = "warning",
                    Actor = "test", SourceRootId = seed.RootId, SourceRevisionId = seed.OwnerId, CorrelationId = $"retained-processor:{Guid.Empty:N}",
                    OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), DetailsJson = "{}" });
            await arrange.SaveChangesAsync();
        }
        await AddRepeatedBlockedIssuesAsync(seed, 120);
        var now = DateTimeOffset.UtcNow;
        var log = new IssueCleanupLog();
        var commands = new IssueReadObserver();
        for (var pass = 0; pass < 4; pass++)
        {
            var at = now.AddMinutes(16 * pass);
            var store = new SqlSourceScanStore(new IssueContextFactory(_fixture.ConnectionString, commands), new IssueCleanupClock(at), logger: log);
            var claim = await store.ClaimNextReleasedAsync("cleanup-pass", at, TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.NotNull(claim);
            await store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, CancellationToken.None);
        }
        await using var verify = Context();
        var remaining = await verify.AuditEvents.CountAsync(value => value.Severity == "warning");
        Assert.True(remaining == 1_050, $"Remaining warnings: {remaining}. Cleanup: {string.Join("; ", log.Diagnostics)}. SQL: {string.Join("; ", commands.Failures)}");
        Assert.Empty(commands.Failures);
        Assert.Empty(log.Warnings);
        Assert.True(await verify.AuditEvents.AnyAsync(value => value.Id == seed.IndependentEventId));
        using var evidence = JsonDocument.Parse((await verify.SourceRootConfigurations.SingleAsync()).HealthEvidenceJson!);
        Assert.Equal("preserve", evidence.RootElement.GetProperty("existingEvidence").GetString());
        Assert.True(evidence.RootElement.GetProperty("resolvedIssueCompactionCursor").GetInt64() > 0);
    }

    [NativeSqlServerFact]
    public async Task Contended_cleanup_leaves_completed_scan_and_events_intact_while_an_ordinary_writer_progresses()
    {
        var seed = await SeedOfficeEventChainAsync();
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var log = new IssueCleanupLog();
        var store = new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), new IssueCleanupClock(now), logger: log);
        var claim = await store.ClaimNextReleasedAsync("cleanup-contention", now, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claim);
        await using var fenceContext = Context();
        await using var fence = await fenceContext.Database.BeginTransactionAsync();
        await SqlPublishedPassageSelection.AcquireFenceAsync(fenceContext, CancellationToken.None);
        var completion = store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, CancellationToken.None).AsTask();
        await using (var writer = Context())
        {
            writer.AuditEvents.Add(new AuditEventEntity { EventType = "source.access_denied", EventFamily = "source", Severity = "error",
                SourceRootId = seed.RootId, Actor = "concurrent-writer", OccurredAtUtc = now, DetailsJson = "{}" });
            await writer.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = Context();
        Assert.Equal((int)SourceScanJobState.Completed, (await verify.SourceScanJobs.SingleAsync()).State);
        Assert.Equal(4, await verify.AuditEvents.CountAsync(value => seed.EventIds.Contains(value.Id)));
        Assert.True(await verify.AuditEvents.AnyAsync(value => value.Actor == "concurrent-writer"));
        Assert.Empty(log.Warnings);
        await fence.RollbackAsync();
    }

    private sealed class IssueContextFactory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private int _created;
        public Func<int, Exception?>? CreationFailure { get; init; }
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure().CommandTimeout(5)).AddInterceptors(interceptors).Options;
        public FluxKnowledgeDbContext CreateDbContext()
        {
            var failure = CreationFailure?.Invoke(++_created);
            if (failure is not null) throw failure;
            return new(_options);
        }
    }

    private sealed class CleanupCommitObserver(Func<int, Task> committed) : DbTransactionInterceptor
    {
        public int Count { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            committed(++Count);
    }

    private sealed class IssueReadObserver : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }
        public TimeSpan MaximumSqlDuration { get; private set; }
        public List<string> Failures { get; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            MaximumSqlDuration = eventData.Duration > MaximumSqlDuration ? eventData.Duration : MaximumSqlDuration;
            return new(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            var shape = command.CommandText.Contains("UNION", StringComparison.Ordinal) ? "resolution-proof" :
                command.CommandText.Contains("[AuditEvents]", StringComparison.Ordinal) ? "event-candidates" : "other";
            Failures.Add($"{shape}, {eventData.Duration.TotalMilliseconds:F0}ms, {command.CommandText.Length} SQL characters");
            return Task.CompletedTask;
        }
    }

    private sealed class IssueCleanupLog : ILogger<SqlSourceScanStore>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Diagnostics.Add(formatter(state, exception) + ": " + exception?.Message);
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class IssueCleanupClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
