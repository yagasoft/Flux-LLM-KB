using System.Diagnostics;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [SqlServerScaleFact]
    [Trait("Category", "Scale")]
    public async Task Overview_keeps_removed_source_verification_usable_with_large_unrelated_GPU_history()
    {
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try { await new PipelineActivitySqlTests(owned, output).MeasureRemovalProofReadAsync(); }
        finally { await owned.DisposeAsync(); }
    }

    [SqlServerScaleFact]
    [Trait("Category", "Scale")]
    public async Task Historical_GPU_and_file_attention_drawers_keep_counts_and_pages_with_large_unrelated_GPU_history()
    {
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try { await new PipelineActivitySqlTests(owned, output).MeasureAttentionDrawerReadsAsync(); }
        finally { await owned.DisposeAsync(); }
    }

    private async Task MeasureAttentionDrawerReadsAsync()
    {
        var fixture = await SeedRemovalProofReadAsync();
        var now = DateTimeOffset.UtcNow;
        var released = await SeedGpuReadLockAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            // Exercise the original bounded release sample at its full capacity.
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE TOP (4095) GpuMiniTasks SET State = 3 WHERE State = 2;
                UPDATE a SET State = 6, Reason = N'synthetic-terminal'
                FROM SourceActivities a INNER JOIN SourceRevisions s ON s.Id = a.SourceRevisionId
                WHERE s.SourceRootId = {fixture.Root} AND s.CanonicalPath LIKE N'%concurrent-0[1-5][0-9][0-9].txt';
                UPDATE j SET PublicState = 5, Reason = N'synthetic-terminal'
                FROM Jobs j INNER JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
                INNER JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
                WHERE s.SourceRootId = {fixture.Root} AND s.CanonicalPath LIKE N'%concurrent-0[1-5][0-9][0-9].txt';
                """);
            var task = await setup.GpuMiniTasks.SingleAsync(t => t.Id == released.Task);
            var batch = await setup.GpuBatches.SingleAsync(b => b.Id == released.Batch);
            var slot = await setup.GpuCapacitySlots.SingleAsync(s => s.SlotKey == released.Slot);
            var dispatch = await setup.GpuExecutorDispatches.SingleAsync(d => d.BatchId == batch.Id);
            task.ExecutionState = 3;
            batch.State = 3;
            slot.State = 0;
            slot.ActiveBatchId = null;
            slot.OwnerKey = null;
            dispatch.State = 3;
            setup.GpuExecutorResultReceipts.Add(new() { OperationId = Guid.NewGuid(), DispatchId = dispatch.DispatchId,
                BatchId = batch.Id, MiniTaskId = task.Id, ExecutorKey = dispatch.ExecutorKey,
                AdmissionGeneration = task.AdmissionGeneration, Disposition = 1, EvidenceClass = 2,
                RequestFingerprint = new('a', 64), CreatedAtUtc = now });
            await setup.SaveChangesAsync();
            Assert.Equal(4096, await setup.GpuMiniTasks.CountAsync(t => t.ExecutionState == 3));
        }
        var timings = new FileCostCommandTimings();
        var trace = new FileCommandTrace();
        var reader = SnapshotReader(new FileCostRuntimeFactory(_fixture.ConnectionString, timings, trace));
        for (var run = 0; run < 3; run++)
        {
            await MeasureAsync($"GPU history {run + 1}", async () =>
            {
                var history = await reader.ReadPipelineAttentionAsync("historical-gpu", null, 200,
                    "Historical unresolved", default);
                Assert.Equal(4096, history.TotalCount);
                Assert.Equal(163, history.Page);
                Assert.Equal(21, history.Items.Count);
                Assert.Equal(4096, Assert.Single(history.Reasons).Count);
            });
            await MeasureAsync($"Current failed file drawer {run + 1}", async () =>
            {
                var files = await reader.ReadPipelineFilesAsync(new("Failed", Reason: "synthetic-terminal",
                    Attention: PipelineFileAttentionScope.Current), null, 30, default);
                Assert.True(files.IsAvailable);
                Assert.Equal(500, files.TotalCount);
                Assert.Equal(19, files.Page);
                Assert.Equal(25, files.Items.Count);
                Assert.Equal(500, Assert.Single(files.Reasons).Count);
            });
        }

        async Task MeasureAsync(string name, Func<Task> read)
        {
            timings.Reset();
            trace.Commands.Clear();
            var watch = Stopwatch.StartNew();
            try { await read(); }
            finally
            {
                output.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds:F1} ms.");
                output.WriteLine(string.Join("; ", timings.Samples));
                output.WriteLine("Reader commands: " + string.Join("; ", trace.Commands.Select(command =>
                {
                    var prefix = string.Join(' ', command.Split('\n', 3).Take(2).Select(line => line.Trim()));
                    return $"{command.Length}:{prefix[..Math.Min(150, prefix.Length)]}";
                })));
            }
        }
    }

    private async Task MeasureRemovalProofReadAsync()
    {
        var fixture = await SeedRemovalProofReadAsync();
        var fileReader = SnapshotReader(new FileCostRuntimeFactory(_fixture.ConnectionString));
        var fileWatch = Stopwatch.StartNew();
        var filePage = await fileReader.ReadPipelineFilesAsync(new(Attention: PipelineFileAttentionScope.Ignored), null, 0, default);
        Assert.True(filePage.IsAvailable);
        Assert.Equal(0, filePage.TotalCount);
        output.WriteLine($"Ignored file drawer read: {fileWatch.Elapsed.TotalMilliseconds:F1} ms.");

        var historyWatch = Stopwatch.StartNew();
        var history = await fileReader.ReadPipelineAttentionAsync("historical-jobs", null, 0, default);
        Assert.Equal(8_999, history.TotalCount);
        Assert.Equal(25, history.Items.Count);
        output.WriteLine($"Historical job drawer read: {historyWatch.Elapsed.TotalMilliseconds:F1} ms.");

        var timings = new FileCostCommandTimings();
        var reader = SnapshotReader(new FileCostRuntimeFactory(_fixture.ConnectionString, timings));
        for (var run = 0; run < 2; run++)
        {
            timings.Reset();
            var watch = Stopwatch.StartNew();
            try
            {
                var overview = await reader.ReadOverviewAsync(default);
                Assert.True(overview.Activity.IsConsistent);
                var view = PipelineActivityView.From(overview.Activity, fixture.Root);
                Assert.Equal(1, view.AttentionJobs);
                Assert.Equal(8_999, view.HistoricalJobs);
                Assert.Contains(view.Jobs, job => job.State == PipelineWorkState.Attention &&
                    job.Reason == "Worker cleanup is unconfirmed after source removal.");
            }
            finally
            {
                output.WriteLine($"Removal proof status run {run + 1}: {watch.Elapsed.TotalMilliseconds:F1} ms.");
                output.WriteLine(string.Join("; ", timings.Samples));
            }
        }
    }

    private async Task<FileCostFixture> SeedRemovalProofReadAsync()
    {
        const string removed = "{\"reason\":\"unseen-during-authoritative-scan\"}";
        var fixture = await SeedFileCostFixtureAsync(10_000, 20_000);
        var contexts = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = contexts.CreateDbContext())
        {
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE SourceRevisions SET SuppressedAtUtc = SYSUTCDATETIME(),
                    RetentionEvidenceJson = CASE WHEN CanonicalPath LIKE N'%discovered-01[0-9][0-9][0-9].txt' THEN {removed} ELSE NULL END
                    WHERE SourceRootId = {fixture.Root} AND CanonicalPath LIKE N'%discovered-%';
                UPDATE j SET PublicState = 5
                FROM Jobs j JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
                JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
                WHERE s.SourceRootId = {fixture.Root} AND s.SuppressedAtUtc IS NOT NULL;
                UPDATE j SET PublicState = 1, LeaseOwner = N'expired-test-worker', LeaseGeneration = 1,
                    LeaseExpiresAtUtc = DATEADD(minute,-2,SYSUTCDATETIME())
                FROM Jobs j JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
                JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
                WHERE s.SourceRootId = {fixture.Root} AND s.CanonicalPath LIKE N'%discovered-01000.txt';
                DECLARE @tasks TABLE (Id uniqueidentifier PRIMARY KEY, JobId uniqueidentifier,
                    PipelineRecordId uniqueidentifier, Revision bigint);
                INSERT @tasks
                SELECT NEWID(), j.Id, j.PipelineRecordId, j.SourceRevision
                FROM Jobs j JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
                JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
                CROSS JOIN (VALUES (1),(2),(3),(4)) repetition(n)
                WHERE s.SourceRootId = {fixture.Root} AND s.CanonicalPath LIKE N'%discovered-0[2-9][0-9][0-9][0-9].txt';
                INSERT GpuMiniTasks (Id,ParentJobId,SourceRevision,PriorityLane,ModelRuntimeKey,SettingsFingerprint,EstimatedBytes,
                    AdmissionGeneration,IdempotencyKey,State,ReservationAttemptCount,CreatedAtUtc,InteractiveCancellationRequested)
                SELECT Id,JobId,Revision,0,N'synthetic',N'synthetic',1,0,CONVERT(nvarchar(36),Id),2,0,SYSUTCDATETIME(),0 FROM @tasks;
                INSERT EmbeddingGpuRequests (MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,GenerationId,CorpusEpoch,
                    ModelFingerprint,Dimensions,InputsJson,InputDigest,State,ExecutorInstanceId,ClaimOperationId,OwnerProcessId,
                    OwnerStartedAtUtc,OwnerMachineFingerprint,DispatchId,NativeCleanupConfirmed,CreatedAtUtc,UpdatedAtUtc)
                SELECT Id,JobId,PipelineRecordId,Revision,NEWID(),NEWID(),N'synthetic',1,N'[]',REPLICATE(N'a',64),0,
                    NEWID(),NEWID(),42,SYSUTCDATETIME(),REPLICATE(N'b',64),NEWID(),0,SYSUTCDATETIME(),SYSUTCDATETIME() FROM @tasks;
                """);
            Assert.Equal(32_000, await setup.EmbeddingGpuRequests.CountAsync());
            Assert.Equal(1_000, await setup.SourceRevisions.CountAsync(r => r.RetentionEvidenceJson == removed));
        }

        return fixture;
    }
}
