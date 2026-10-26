using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerFact]
    public async Task File_summary_and_bounded_pages_remain_coherent_and_usable_under_concurrent_current_file_writes()
    {
        // Keep this representative inventory out of the shared regression fixture.
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try { await new PipelineActivitySqlTests(owned, output).MeasureFileReadCostAsync(); }
        finally { await owned.DisposeAsync(); }
    }

    [NativeSqlServerFact]
    public async Task File_summary_and_pages_remain_usable_with_twenty_thousand_current_jobs()
    {
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try { await new PipelineActivitySqlTests(owned, output).MeasureFileReadCostAsync(20000); }
        finally { await owned.DisposeAsync(); }
    }

    private async Task<FileCostFixture> SeedFileCostFixtureAsync(int currentJobCount)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        Guid toggledActivity;
        Guid toggledJob;
        var codeBranch = Guid.NewGuid();
        await using (var setup = factory.CreateDbContext())
        {
            for (var i = 0; i < 1000; i++)
            {
                var revision = Revision(Guid.NewGuid(), root, $"concurrent-{i:D4}." + (i == 0 ? "cs" : "txt"), now, false);
                var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = revision.CanonicalPath, CreatedAtUtc = now };
                var record = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = identity.Id, SourceRevisionId = revision.Id,
                    Revision = 1, ContentHash = revision.ContentSha256, CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now };
                record.RootLineageRecordId = record.Id;
                var job = new JobEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1,
                    Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8,
                    PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now };
                setup.SourceRevisions.Add(revision); setup.SourceIdentities.Add(identity); setup.PipelineRecords.Add(record); setup.Jobs.Add(job);
                setup.SourceActivities.Add(Activity(revision.Id, SourceActivityState.Pending, record.Id));
                setup.OutboxMessages.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, PipelineRecordId = record.Id,
                    SourceRevision = 1, Stage = job.Stage, Operation = job.Operation, IdempotencyKey = $"file-cost:{record.Id:N}",
                    DueAtUtc = now, CreatedAtUtc = now });
            }
            var firstActivity = setup.SourceActivities.Local.First();
            toggledActivity = firstActivity.Id;
            toggledJob = setup.Jobs.Local.Single(j => j.PipelineRecordId == firstActivity.ResultingPipelineRecordId).Id;
            var firstRevision = setup.SourceRevisions.Local.Single(r => r.Id == firstActivity.SourceRevisionId);
            var code = Activity(firstRevision.Id, SourceActivityState.Pending);
            code.ActivityKind = (int)SourceActivityKind.CodeParsing;
            code.ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion;
            code.DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint;
            setup.SourceActivities.Add(code);
            setup.SourceProcessorBranches.Add(new() { Id = codeBranch, SourceActivityId = code.Id, SourceRevisionId = firstRevision.Id,
                InputSha256 = firstRevision.ContentSha256, ProcessorVersion = code.ProcessorVersion, ProcessorFingerprint = code.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Running, LeaseOwner = "synthetic-cost-code", LeaseGeneration = 1,
                LeaseExpiresAtUtc = now.AddMinutes(5), CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        // Include the wider discovered inventory, not only the files with admitted jobs.
        await using (var setup = factory.CreateDbContext())
        {
            for (var offset = 1000; offset < 30000; offset += 1000)
            {
                foreach (var i in Enumerable.Range(offset, 1000))
                {
                    var revision = Revision(Guid.NewGuid(), root, $"discovered-{i:D5}.txt", now, false);
                    setup.SourceRevisions.Add(revision);
                    if (i >= currentJobCount) continue;
                    var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = revision.CanonicalPath, CreatedAtUtc = now };
                    var record = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = identity.Id, SourceRevisionId = revision.Id,
                        Revision = 1, ContentHash = revision.ContentSha256, CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now };
                    record.RootLineageRecordId = record.Id;
                    var job = new JobEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1,
                        Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8,
                        PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now };
                    setup.SourceIdentities.Add(identity); setup.PipelineRecords.Add(record); setup.Jobs.Add(job);
                    setup.SourceActivities.Add(Activity(revision.Id, SourceActivityState.Pending, record.Id));
                    setup.OutboxMessages.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, PipelineRecordId = record.Id,
                        SourceRevision = 1, Stage = job.Stage, Operation = job.Operation, IdempotencyKey = $"file-cost:{record.Id:N}",
                        DueAtUtc = now, CreatedAtUtc = now });
                }
                await setup.SaveChangesAsync();
                setup.ChangeTracker.Clear();
            }
        }
        return new(root, toggledActivity, toggledJob, codeBranch);
    }

    private sealed record FileCostFixture(Guid Root, Guid ToggledActivity, Guid ToggledJob, Guid CodeBranch);

    private async Task MeasureFileReadCostAsync(int currentJobCount = 1000)
    {
        var (root, toggledActivity, toggledJob, codeBranch) = await SeedFileCostFixtureAsync(currentJobCount);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        // One bounded setup read prepares this warm-read qualification. It does not
        // alter the public deadline or retry any asserted/measurement operation.
        var warmTimings = new FileCostCommandTimings();
        var warmStatistics = new FileLogicalReadTrace();
        var internalWatch = Stopwatch.StartNew();
        using (var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await using var context = new TestDbContextFactory(_fixture.ConnectionString, warmTimings,
                warmStatistics, new FileStatisticsDrain(warmTimings.Samples.Add, setupDeadline.Token)).CreateDbContext();
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot, setupDeadline.Token);
            try
            {
                var warm = await new SqlPipelineActivityReader(null, TimeProvider.System)
                    .ReadAsync(context, DateTimeOffset.UtcNow, setupDeadline.Token, materializeGpuFacts: true);
                var task = Assert.Single(warm.RunningTasks, t => t.Stage == "Code analysis");
                Assert.Equal(codeBranch, task.Id); Assert.Equal(root, task.SourceRootId);
                Assert.Equal(30000, Assert.Single(warm.Sources, s => s.Id == root).KnownFiles);
                await transaction.CommitAsync(setupDeadline.Token);
            }
            finally
            {
                output.WriteLine($"Internal file-cost setup: elapsedMs={internalWatch.Elapsed.TotalMilliseconds:F1}; " + string.Join("; ", warmTimings.Samples));
                output.WriteLine("Internal SQL execution times: " + string.Join("; ", warmStatistics.SqlTimes));
            }
        }
        var commands = new FileCommandTrace();
        // Match application retry/buffering policy. Statistics messages and result
        // draining above are setup diagnostics, not part of runtime qualification.
        var runtimeFactory = new FileCostRuntimeFactory(_fixture.ConnectionString, commands);
        await using (var policy = runtimeFactory.CreateDbContext())
            Assert.True(policy.Database.CreateExecutionStrategy().RetriesOnFailure);
        var reader = SnapshotReader(runtimeFactory);
        var setupWatch = Stopwatch.StartNew();
        try { _ = await reader.ReadOverviewAsync(default); }
        finally
        {
            output.WriteLine($"Initial public setup overview: elapsedMs={setupWatch.Elapsed.TotalMilliseconds:F1}; serverGc={System.Runtime.GCSettings.IsServerGC}; production retry policy; no statistics/drain instrumentation.");
        }
        _ = await reader.ReadPipelineFilesAsync(new(), root, 0, default);
        commands.Commands.Clear();
        var timings = new List<double>();
        var writerCount = 0;
        using var stopping = new CancellationTokenSource();
        var writer = WriteAsync();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var watch = Stopwatch.StartNew();
                var overview = await reader.ReadOverviewAsync(default);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(overview.Activity.IsConsistent);
                var source = Assert.Single(overview.Activity.Sources, s => s.Id == root);
                Assert.Equal(30000, source.KnownFiles);
                Assert.Equal(source.KnownFiles, PipelineFilePresentation.Categories(source).Sum(c => c.Count));
                Assert.Equal(source.PendingFiles, overview.Activity.FileStages.Where(s => s.SourceRootId == root).Sum(s => s.Count));
                watch.Restart();
                var page = await reader.ReadPipelineFilesAsync(new(), root, i % 1200, default);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(30000, page.TotalCount); Assert.Equal(25, page.Items.Count);
                Assert.Equal(25, page.Items.Select(f => f.SourceRevisionId).Distinct().Count());
                await Task.Delay(25);
            }
        }
        finally
        {
            await stopping.CancelAsync(); await writer;
            if (timings.Count != 24)
                output.WriteLine($"Incomplete file-cost qualification: usableReads={timings.Count}/24; " +
                    $"completedReadMs={string.Join(",", timings.Select(ms => ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}; " +
                    "production retry policy; no statistics/drain instrumentation.");
        }
        var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * .95) - 1);
        output.WriteLine($"File-cost qualification: files=30000 currentJobs={currentJobCount} confirmedCodeProcessors=1 overlappingWrites={writerCount} usableReads={timings.Count}/24 " +
            $"p95Ms={p95:F1} maxMs={timings.Max():F1} readerCommands={commands.Commands.Count} setupLogicalReads={warmStatistics.LogicalReads}; " +
            "production retry policy; scalar metadata only; no statistics/drain instrumentation in measured reads; shared polling unchanged.");
        Assert.True(writerCount >= 2);
        Assert.True(warmStatistics.LogicalReads > 0);
        Assert.InRange(commands.Commands.Count, 1, 24 * 45);
        Assert.True(timings.All(ms => ms < 2500)); // Includes return/cleanup around the existing 2s operation budget.

        async Task WriteAsync()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    var failed = writerCount % 2 == 0;
                    await using var context = factory.CreateDbContext();
                    await using var transaction = await context.Database.BeginTransactionAsync(stopping.Token);
                    await context.SourceActivities.Where(a => a.Id == toggledActivity).ExecuteUpdateAsync(update => update
                        .SetProperty(a => a.State, (int)(failed ? SourceActivityState.FailedTerminal : SourceActivityState.Pending))
                        .SetProperty(a => a.Reason, failed ? "synthetic-current-failure" : null), stopping.Token);
                    await context.Jobs.Where(j => j.Id == toggledJob).ExecuteUpdateAsync(update => update
                        .SetProperty(j => j.PublicState, (int)(failed ? PublicJobState.Failed : PublicJobState.WorkerQueued))
                        .SetProperty(j => j.Reason, failed ? "synthetic-current-failure" : null), stopping.Token);
                    await transaction.CommitAsync(stopping.Token);
                    Interlocked.Increment(ref writerCount);
                    await Task.Delay(25, stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
    }

    private sealed class FileCostRuntimeFactory(string connectionString, params IInterceptor[] interceptors)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).AddInterceptors(interceptors).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class FileCostCommandTimings : DbCommandInterceptor
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private int _commands;
        public List<string> Samples { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Samples.Add($"start#{++_commands} at {_watch.Elapsed.TotalMilliseconds:F1}ms chars={command.CommandText.Length} codeIdentity={command.CommandText.Contains("ORDER BY [s].[Id]", StringComparison.Ordinal)}");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Samples.Add($"executed at {_watch.Elapsed.TotalMilliseconds:F1}ms duration={eventData.Duration.TotalMilliseconds:F1}ms");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Samples.Add($"nonquery#{++_commands} at {_watch.Elapsed.TotalMilliseconds:F1}ms chars={command.CommandText.Length} jobFacts={command.CommandText.Contains("INSERT INTO #FluxPipelineJobFacts", StringComparison.Ordinal)} gpuFacts={command.CommandText.Contains("INSERT INTO #FluxPipelineGpuFacts", StringComparison.Ordinal)}");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            Samples.Add($"nonquery executed at {_watch.Elapsed.TotalMilliseconds:F1}ms duration={eventData.Duration.TotalMilliseconds:F1}ms rows={result}");
            return ValueTask.FromResult(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Samples.Add($"failed at {_watch.Elapsed.TotalMilliseconds:F1}ms duration={eventData.Duration.TotalMilliseconds:F1}ms type={eventData.Exception.GetType().Name}");
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Samples.Add($"closing at {_watch.Elapsed.TotalMilliseconds:F1}ms chars={command.CommandText.Length} rows={eventData.ReadCount} readerMs={(DateTimeOffset.UtcNow - eventData.StartTime).TotalMilliseconds:F1}");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FileLogicalReadTrace : DbConnectionInterceptor
    {
        private long _logicalReads;
        public List<string> SqlTimes { get; } = [];
        public long LogicalReads => Interlocked.Read(ref _logicalReads);
        public void Reset() => Interlocked.Exchange(ref _logicalReads, 0);
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            var sql = (SqlConnection)connection;
            sql.InfoMessage += (_, message) =>
            {
                foreach (Match match in Regex.Matches(message.Message, @"logical reads (\d+)", RegexOptions.CultureInvariant))
                    Interlocked.Add(ref _logicalReads, long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                foreach (Match match in Regex.Matches(message.Message, @"CPU time = (\d+) ms,\s*elapsed time = (\d+) ms", RegexOptions.CultureInvariant))
                    SqlTimes.Add($"cpuMs={match.Groups[1].Value} elapsedMs={match.Groups[2].Value}");
            };
            await using var command = connection.CreateCommand();
            command.CommandText = "SET STATISTICS IO ON; SET STATISTICS TIME ON";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class FileStatisticsDrain(Action<string>? observe = null, CancellationToken cancellationToken = default) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            // SQL sends IO statistics after the result. EF normally closes its first
            // result without consuming those messages; drain them only for this probe.
            var watch = Stopwatch.StartNew();
            try
            {
                while (await eventData.DataReader.NextResultAsync(cancellationToken))
                    while (await eventData.DataReader.ReadAsync(cancellationToken)) { }
            }
            finally { observe?.Invoke($"statistics drain chars={command.CommandText.Length} elapsedMs={watch.Elapsed.TotalMilliseconds:F1}"); }
            return result;
        }
    }
}
