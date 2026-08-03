using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuAdmissionConcurrencyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Application_lock_holds_the_second_admission_until_the_first_commits()
    {
        var selection = new SqlGpuAdmissionTests(_fixture);
        var factory = await selection.CreateEnvironmentAsync();
        var first = await selection.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var firstParentId = await ReadParentIdAsync(factory, first);
        var second = await selection.AddReadyForParentAsync(factory, firstParentId, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var lower = await selection.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "r", "s", 10);
        var lowerParentId = await ReadParentIdAsync(factory, lower);
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAtLockAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHook = 0;
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decision = (candidate, _) =>
            ValueTask.FromResult(candidate.PriorityLane == GpuPriorityLane.InteractiveRetrieval
                ? new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "slot-a", "test-owner", null)
                : new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
        var store = new SqlGpuSchedulerStore(
            factory,
            timeProvider: TimeProvider.System,
            beforeAdmissionLockAttempt: _ =>
            {
                if (Interlocked.Increment(ref firstHook) == 2)
                {
                    secondAtLockAttempt.SetResult();
                }

                return ValueTask.CompletedTask;
            },
            afterAdmissionLockAcquired: async _ =>
            {
                if (firstHook == 1)
                {
                    lockHeld.SetResult();
                    await releaseFirst.Task;
                }
            });

        var firstAdmission = store.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            decision,
            CancellationToken.None).AsTask();
        await lockHeld.Task;
        await using (var probeConnection = new SqlConnection(_fixture.ConnectionString))
        {
            await probeConnection.OpenAsync();
            await using var probe = new SqlCommand(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'FluxKnowledge.GpuScheduler.Admission', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @result;",
                probeConnection);
            var probeResult = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.True(probeResult < 0);
        }

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAdmission = Task.Run(async () =>
        {
            secondStarted.SetResult();
            return await store.RunAdmissionRoundAsync(
                GpuSchedulerWakeReason.WorkReady,
                new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
                decision,
                CancellationToken.None);
        });
        await secondStarted.Task;
        await secondAtLockAttempt.Task;
        Assert.False(secondAdmission.IsCompleted);
        releaseFirst.SetResult();
        var results = await Task.WhenAll(firstAdmission, secondAdmission);

        Assert.Single(results, result => result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(slot => slot.ActiveBatchId != null && slot.State == (int)GpuCapacitySlotState.Reserved));
        var selected = await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .OrderBy(task => task.CreatedSequence).Select(task => new { task.Id, task.BatchId, task.AdmissionGeneration }).ToListAsync();
        Assert.Equal([first, second], selected.Select(task => task.Id).ToArray());
        Assert.All(selected, task => Assert.Equal(1, task.AdmissionGeneration));
        Assert.All(selected, task => Assert.Equal(batch.Id, task.BatchId));
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.GpuProcessing, await verify.Jobs.Where(job => job.Id == firstParentId).Select(job => job.PublicState).SingleAsync());
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.GpuQueued, await verify.Jobs.Where(job => job.Id == lowerParentId).Select(job => job.PublicState).SingleAsync());
    }

    private static async Task<Guid> ReadParentIdAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid miniTaskId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await context.GpuMiniTasks.Where(task => task.Id == miniTaskId).Select(task => task.ParentJobId).SingleAsync();
    }
}
