using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_ocr_request_cannot_attach_another_roots_task_to_the_deleting_record(bool publicPath)
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("ocr-reverse-delete", publicPath ? SourceRootState.Enabled : SourceRootState.Deleting, now);
        var control = await SeedRootAsync("ocr-reverse-control", SourceRootState.Enabled, now);
        var taskId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            AddLocalOcrTask(setup, control, taskId, GpuMiniTaskExecutionState.Completed, now);
            var request = setup.DocumentOcrRequests.Local.Single();
            request.PipelineRecordId = deleting.RecordId;
            request.RetainedSourceRevisionId = deleting.RevisionId;
            await setup.SaveChangesAsync();
        }
        var factory = SqlTestData.CreateFactory(_fixture);
        if (publicPath)
        {
            var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
                new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId = deleting.RootId }));
            var preview = await service.PreviewAsync(mutation, "test", default);
            var refusal = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation,
                preview.ConfirmationId, "ocr-reverse:" + deleting.RootId.ToString("N"), "test", default).AsTask());
            Assert.Equal("source-delete-external-execution-owned", refusal.ReasonCode);
        }
        else
        {
            await AddDeletionAsync(deleting, now);
            var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
            var work = Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal("source-delete-external-execution-owned", (await store.PurgeAsync(work, null, default)).ReasonCode);
        }
        await using var check = CreateContext();
        Assert.True(await check.GpuMiniTasks.AnyAsync(value => value.Id == taskId && value.ParentJobId == control.JobId));
        Assert.True(await check.Jobs.AnyAsync(value => value.Id == control.JobId));
        Assert.True(await check.SourceRootConfigurations.AnyAsync(value => value.Id == deleting.RootId));
    }
}
