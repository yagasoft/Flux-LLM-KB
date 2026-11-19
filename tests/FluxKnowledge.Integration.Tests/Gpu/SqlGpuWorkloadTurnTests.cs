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
    public async Task A_legacy_OCR_turn_does_not_delay_interactive_search_behind_deferred_OCR()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var ocr = await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10, Now.AddMinutes(2));
        var retrieval = await setup.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "retrieval", "retrieval-settings", 10);
        await using (var state = await factory.CreateDbContextAsync())
        {
            (await state.GpuSchedulerStates.SingleAsync()).SearchBatchesWhileOcrWaiting = 3;
            await state.SaveChangesAsync();
        }
        var result = await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options);
        Assert.Equal(GpuAdmissionDisposition.Admit, result.Disposition);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(retrieval, await verify.GpuMiniTasks.Where(t => t.ExecutionState == (int)GpuMiniTaskExecutionState.Active).Select(t => t.Id).SingleAsync());
        Assert.Equal(Now.AddMinutes(2), (await verify.GpuMiniTasks.SingleAsync(t => t.Id == ocr)).DeferredUntilUtc);
    }

    [NativeSqlServerFact]
    public async Task OCR_preserves_FIFO_across_existing_lanes_when_selected()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var oldest = await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10, Now.AddMinutes(2));
        var newer = await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr", "ocr-settings", 10);
        var result = await SqlGpuAdmissionTests.AdmitAsync(factory,
            (_, _) => throw new InvalidOperationException("Older deferred OCR was bypassed"), Options);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        var released = await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options, GpuSchedulerWakeReason.CapacityReleased);
        Assert.Equal(GpuAdmissionDisposition.Admit, released.Disposition);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == oldest)).ExecutionState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == newer)).ExecutionState);
    }

    [NativeSqlServerFact]
    public async Task Background_embeddings_yield_after_four_passages()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        for (var i = 0; i < 10; i++)
            await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "retrieval", "retrieval-settings", 10);
        await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(4, (await verify.GpuBatches.SingleAsync()).ItemCount);
    }

    [NativeSqlServerFact]
    public async Task OCR_FIFO_excludes_ineligible_sources_and_requires_exact_runtime_settings()
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var removed = await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "ocr-settings", 10);
        await setup.AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "ocr", "wrong-settings", 10);
        var eligible = await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr", "ocr-settings", 10);
        await using (var state = await factory.CreateDbContextAsync())
        {
            var parent = (await state.GpuMiniTasks.Include(t => t.ParentJob).ThenInclude(j => j!.PipelineRecord).SingleAsync(t => t.Id == removed)).ParentJob!;
            parent.PipelineRecord.IsDeleted = true;
            await state.SaveChangesAsync();
        }
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), Options);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(eligible, await verify.GpuMiniTasks.Where(t => t.ExecutionState == (int)GpuMiniTaskExecutionState.Active).Select(t => t.Id).SingleAsync());
    }
}
