using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Models;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, "after-claim")]
    [InlineData(true, "after-claim")]
    [InlineData(false, "during-handoff")]
    [InlineData(true, "during-handoff")]
    [InlineData(false, "before-native")]
    [InlineData(true, "before-native")]
    public async Task Confirmed_Git_unstarted_recovery_retains_current_capture_fence_after_claim_and_handoff(bool segmented, string boundary)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Disposable bootstrap.",
            embed: false, clock: scenario.Clock, segmented: segmented, configureServices: services =>
            {
                services.AddSingleton(ReplayRuntime);
                services.AddScoped<IRetainedSourceReader>(_ => new SqlRetainedSourceReader(scenario.Factory, scenario.RetainedRoot));
            });
        await SqlTestData.ClearPipelineAsync(fixture);
        await using (var format = scenario.Factory.CreateDbContext())
        {
            var state = await format.IndexState.SingleAsync(); state.RequiredIndexFormat = segmented ? 2 : 1;
            state.ProjectionChangeFloorVersion = state.CorpusVersion; await format.SaveChangesAsync();
        }
        await scenario.ScanAsync("pending-handoff-authority"); await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        Assert.Equal(1, await new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock,
            sourceAdmission: new FixtureSourceAdmission(scenario.Factory)).OfferUnlinkedInProcessActivitiesAsync(default));
        await pipeline.PumpAsync();
        var admission = new FixtureSourceAdmission(scenario.Factory);
        var checkpoint = new SqlEmbeddingCheckpointStore(scenario.Factory, scenario.Clock, ReplayRuntime, admission);
        var original = await ClaimRepositoryEmbedAsync(scenario);
        var batch = await checkpoint.ReadNextAsync(original, ReplayRuntime.Profile, default);
        var interrupted = new SqlEmbeddingGpuRequestStore(scenario.Factory,
            new SqlGpuSchedulerStore(scenario.Factory, beforeIdempotencyRead: _ => throw new InvalidOperationException("injected-before-handoff"),
                timeProvider: scenario.Clock, sourceAdmission: admission), new ChannelGpuSchedulerWakeSignal(), ReplayRuntime, scenario.Clock, sourceAdmission: admission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.QueueAsync(original, batch, default).AsTask());
        await new SqlStageTransitionStore(scenario.Factory, null, scenario.Clock, ReplayRuntime, admission).FailAsync(
            new(original.DispatchMessage, original.Job, "stage worker failed non-retryably", "injected-before-handoff", "test"), default);
        var service = new NativeCorpusCommandService(new SqlNativeOperationStore(scenario.Factory, scenario.Clock, embeddingRuntime: ReplayRuntime),
            new SqlNativeCorpusActionStore(scenario.Factory,
                new SourceRootPathPolicy(new LocalIngressOptions([Path.GetDirectoryName(scenario.Path)!])), new LocalPrivateContentDisclosure(), ReplayRuntime));
        var command = new NativeCorpusMutation("embedding_retry", JsonSerializer.SerializeToElement(new { jobId = original.Job.JobId.Value }));
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, "qualified-git-pending-handoff-" + original.Job.JobId.Value.ToString("N"), "test", default);
        await using (var marker = scenario.Factory.CreateDbContext())
        {
            var json = JsonDocument.Parse((await marker.PipelineRecords.SingleAsync(x => x.Id == original.Job.PipelineRecordId.Value)).RepositoryRecoveryBindingJson!);
            Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
            var receipt = JsonDocument.Parse((await marker.AuditEvents.SingleAsync(x => x.PipelineRecordId == original.Job.PipelineRecordId.Value &&
                x.EventType == "embedding recovery queued")).DetailsJson);
            Assert.True(receipt.RootElement.GetProperty("requireCurrentGpuCapture").GetBoolean());
        }
        var resumed = await ClaimRepositoryEmbedAsync(scenario);
        await using (var claimed = scenario.Factory.CreateDbContext())
            Assert.Null((await claimed.Jobs.SingleAsync(x => x.Id == resumed.Job.JobId.Value)).Reason);
        async ValueTask Invalidate(CancellationToken _)
        {
            await using var changed = scenario.Factory.CreateDbContext();
            (await changed.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).BaselineEpoch++;
            await changed.SaveChangesAsync();
        }
        if (boundary == "after-claim")
        {
            await Invalidate(default);
            await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => checkpoint.ReadNextAsync(resumed, ReplayRuntime.Profile, default).AsTask());
        }
        else
        {
            var next = await checkpoint.ReadNextAsync(resumed, ReplayRuntime.Profile, default);
            var scheduler = new SqlGpuSchedulerStore(scenario.Factory, timeProvider: scenario.Clock, sourceAdmission: admission,
                beforeIdempotencyRead: boundary == "during-handoff" ? Invalidate : null);
            var wake = new ChannelGpuSchedulerWakeSignal();
            var requests = new SqlEmbeddingGpuRequestStore(scenario.Factory, scheduler, wake, ReplayRuntime, scenario.Clock, sourceAdmission: admission);
            if (boundary == "during-handoff")
                await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => requests.QueueAsync(resumed, next, default).AsTask());
            else
            {
                await using (var setup = scenario.Factory.CreateDbContext())
                {
                    setup.GpuCapacitySlots.Add(new() { SlotKey = "repository-replay-gpu", UpdatedAtUtc = scenario.Clock.Now });
                    await setup.SaveChangesAsync();
                }
                await requests.QueueAsync(resumed, next, default);
                var handle = await AdmitRepositoryGpuAsync(scheduler);
                await Invalidate(default);
                var models = new BgeGpuInferenceTests.RecordingModels();
                await new EmbeddingGpuExecutor(requests, new RepositoryReplayLifecycle(scheduler), scheduler,
                    new BgeGpuInferenceSession(models), new WindowsInteractiveGpuOwnerProbe(), ReplayRuntime,
                    new ChannelOutboxWakeSignal(), wake, scenario.Clock).DeliverAsync(handle, default);
                Assert.Empty(models.Events);
            }
        }
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.Empty(await verify.Vectors.ToArrayAsync());
        Assert.False((await verify.PipelineRecords.SingleAsync(x => x.Id == original.Job.PipelineRecordId.Value)).CompletionCriteriaMet);
        if (boundary != "before-native") Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
    }
}
