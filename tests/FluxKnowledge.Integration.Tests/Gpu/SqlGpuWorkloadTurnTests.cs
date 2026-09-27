using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuWorkloadTurnTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static DateTimeOffset Now => SqlGpuAdmissionTests.Now;
    private static readonly GpuSchedulerOptions Options = new(32, 1024, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(1), new GpuWorkloadPolicy([new("ocr", "ocr-settings", GpuWorkloadKind.Ocr),
            new("retrieval", "retrieval-settings", GpuWorkloadKind.Retrieval)]));

    [NativeSqlServerFact]
    public async Task Owed_deferred_OCR_cannot_be_bypassed_and_FIFO_spans_existing_lanes()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var oldest = await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10, Now.AddMinutes(2));
        var newer = await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr", "ocr-settings", 10);
        var retrieval = await setup.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "retrieval", "retrieval-settings", 10);
        await using (var state = await factory.CreateDbContextAsync())
        {
            (await state.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting = 3;
            await state.SaveChangesAsync();
        }
        var result = await SqlGpuAdmissionTests.AdmitAsync(factory,
            (_, _) => throw new InvalidOperationException("Owed deferred OCR was bypassed"), Options);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        await using (var state = await factory.CreateDbContextAsync())
        {
            Assert.Equal(3, (await state.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting);
            Assert.Empty(await state.GpuBatches.ToArrayAsync());
        }
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options, GpuSchedulerWakeReason.CapacityReleased);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == oldest)).ExecutionState);
        Assert.All(await verify.GpuMiniTasks.Where(t => t.Id == newer || t.Id == retrieval).ToArrayAsync(),
            t => Assert.Equal((int)GpuMiniTaskExecutionState.Ready, t.ExecutionState));
        Assert.Equal(0, (await verify.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting);
    }

    [NativeSqlServerFact]
    public async Task Background_embeddings_count_one_admitted_batch_and_yield_after_four_passages()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        for (var i = 0; i < 10; i++)
            await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "retrieval", "retrieval-settings", 10);
        await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(4, (await verify.GpuBatches.SingleAsync()).ItemCount);
        Assert.Equal(1, (await verify.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting);
        await SqlGpuAdmissionTests.AdmitAsync(factory,
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null)), Options);
        await verify.Entry(await verify.GpuSchedulerStates.SingleAsync()).ReloadAsync();
        Assert.Equal(1, (await verify.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting);
    }

    [NativeSqlServerFact]
    public async Task Invalid_OCR_waiters_do_not_hold_a_turn_and_runtime_settings_are_classified_exactly()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var removed = await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10);
        await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "wrong-settings", 10);
        var retrieval = await setup.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "retrieval", "retrieval-settings", 10);
        await using (var state = await factory.CreateDbContextAsync())
        {
            var parent = (await state.GpuMiniTasks.Include(t => t.ParentJob).ThenInclude(j => j!.PipelineRecord).SingleAsync(t => t.Id == removed)).ParentJob!;
            parent.PipelineRecord.IsDeleted = true;
            (await state.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting = 3;
            await state.SaveChangesAsync();
        }
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == retrieval)).ExecutionState);
        Assert.Equal(0, (await verify.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting);
    }
}
