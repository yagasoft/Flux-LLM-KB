using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class SqlDocumentOcrHandoffTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Handoff_persists_one_source_bound_request_and_queues_its_exact_mini_task()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);

        var result = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0, 2]),
            CancellationToken.None);

        Assert.True(result.Scheduled);
        var miniTaskId = Assert.IsType<Guid>(result.MiniTaskId);
        await using var verification = await factory.CreateDbContextAsync();
        var request = await verification.DocumentOcrRequests.SingleAsync();
        Assert.Equal(miniTaskId, request.MiniTaskId);
        Assert.Equal(claim.JobId.Value, request.ParentJobId);
        Assert.Equal(claim.PipelineRecordId.Value, request.PipelineRecordId);
        Assert.Equal(claim.SourceRevision, request.SourceRevision);
        Assert.Equal(retainedSourceRevisionId.Value, request.RetainedSourceRevisionId);
        Assert.Equal(contentHash, request.ContentSha256);
        Assert.Equal("[0,2]", request.RequestedPageIndexesJson);
        Assert.Equal(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, request.ModelRuntimeKey);
        Assert.Equal(PaddleOcrVlmRuntimeContract.SettingsFingerprint, request.SettingsFingerprint);
        Assert.Null(request.ResultJson);
        Assert.Null(request.ResultDigest);
        var task = await verification.GpuMiniTasks.SingleAsync();
        Assert.Equal(miniTaskId, task.Id);
        Assert.Equal(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, task.ModelRuntimeKey);
        Assert.Equal(PaddleOcrVlmRuntimeContract.SettingsFingerprint, task.SettingsFingerprint);
    }

    [NativeSqlServerFact]
    public async Task Stored_ocr_result_requeues_only_the_original_extract_job_for_normal_consumption()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0]),
            CancellationToken.None);
        var miniTaskId = Assert.IsType<Guid>(handoff.MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var result = new DocumentOcrExecutionResult(
            true,
            "document-ocr-complete",
            [new DocumentOcrPageResult(0, 0, [new DocumentOcrBlock("text", 1, 2, 3, 4, "scanned text")])]);

        var stored = await store.StoreResultAsync(handle, miniTaskId, result, CancellationToken.None);

        Assert.True(stored.Stored);
        var lifecycle = CreateLifecycle(factory);
        Assert.True((await lifecycle.AcknowledgeAsync(
            new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await lifecycle.RecordReceiptAsync(
            new GpuExecutorResultReceipt(
                Guid.NewGuid(),
                handle,
                miniTaskId,
                GpuMiniTaskBoundaryDisposition.Completed,
                stored.ResultDigest,
                GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None)).Accepted);
        Assert.True((await lifecycle.HandleCallbackAsync(
            Guid.NewGuid(),
            new GpuBatchCallback(
                handle,
                GpuBatchCallbackKind.Completed,
                [new GpuMiniTaskBoundaryOutcome(miniTaskId, GpuMiniTaskBoundaryDisposition.Completed)],
                CapacityReleased: true),
            CancellationToken.None)).Accepted);

        Assert.True(await store.RequeueCompletedAsync(handle, miniTaskId, CancellationToken.None));

        var completed = await store.ReadCompletedAsync(claim, contentHash, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.True(completed.Succeeded);
        Assert.Equal("scanned text", Assert.Single(Assert.Single(completed.Pages).Blocks).Text);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.WorkerQueued, await verification.Jobs
            .Where(value => value.Id == claim.JobId.Value).Select(value => value.PublicState).SingleAsync());
        Assert.Equal((int)DocumentOcrRequestState.Requeued, await verification.DocumentOcrRequests
            .Where(value => value.MiniTaskId == miniTaskId).Select(value => value.State).SingleAsync());
        var outbox = await verification.OutboxMessages.SingleAsync(value => value.PipelineRecordId == claim.PipelineRecordId.Value);
        Assert.Null(outbox.DispatchedAtUtc);
        Assert.Null(outbox.LeaseOwner);
        Assert.Null(outbox.LeaseExpiresAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Local_executor_delivers_a_single_pdf_ocr_result_then_requeues_the_original_extract()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0]),
            CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executionResult = new DocumentOcrExecutionResult(
            true,
            "document-ocr-complete",
            [new DocumentOcrPageResult(0, 0, [new DocumentOcrBlock("text", 1, 2, 3, 4, "GPU OCR text")])]);
        var executor = new RecordingDocumentOcrExecutor(executionResult);
        var signal = new RecordingOutboxWakeSignal();
        var services = new ServiceCollection();
        services.AddScoped(_ => new SqlDocumentOcrStore(factory, CreateCoordinator(factory)));
        services.AddScoped<IGpuExecutorLifecycleSink>(_ => CreateLifecycle(factory));
        services.AddScoped<IRetainedSourceReader>(_ => new FixedRetainedSourceReader(retainedSourceRevisionId, contentHash));
        services.AddSingleton<IDocumentOcrExecutor>(executor);
        services.AddSingleton<IOutboxWakeSignal>(signal);
        services.AddSingleton<PaddleOcrVlmCompletionCoordinator>();
        services.AddSingleton<PaddleOcrVlmExecutionRegistry>();
        services.AddSingleton<PaddleOcrVlmCancellationCoordinator>();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
        services.AddSingleton<PaddleOcrVlmExecutorAdapter>();
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>()
            .DeliverAsync(handle, CancellationToken.None);

        Assert.Equal(PaddleOcrVlmRuntimeContract.ExecutorKey, provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>().ExecutorKey);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(retainedSourceRevisionId, call.RetainedSourceRevisionId);
        Assert.Equal([0], call.PageIndexes);
        Assert.Equal(1, signal.Notifications);
        var completed = await store.ReadCompletedAsync(claim, contentHash, CancellationToken.None);
        Assert.Equal("GPU OCR text", Assert.Single(Assert.Single(completed!.Pages).Blocks).Text);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)DocumentOcrRequestState.Requeued, await verification.DocumentOcrRequests
            .Where(value => value.MiniTaskId == handoff.MiniTaskId).Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Cancellation_of_active_local_ocr_releases_only_its_exact_batch_without_requeueing_document_work()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0]),
            CancellationToken.None);
        var miniTaskId = Assert.IsType<Guid>(handoff.MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executor = new BlockingDocumentOcrExecutor();
        var signal = new RecordingOutboxWakeSignal();
        var services = new ServiceCollection();
        services.AddScoped(_ => new SqlDocumentOcrStore(factory, CreateCoordinator(factory)));
        services.AddScoped<IGpuExecutorLifecycleSink>(_ => CreateLifecycle(factory));
        services.AddScoped<IRetainedSourceReader>(_ => new FixedRetainedSourceReader(retainedSourceRevisionId, contentHash));
        services.AddSingleton<IDocumentOcrExecutor>(executor);
        services.AddSingleton<IOutboxWakeSignal>(signal);
        services.AddSingleton<PaddleOcrVlmCompletionCoordinator>();
        services.AddSingleton<PaddleOcrVlmExecutionRegistry>();
        services.AddSingleton<PaddleOcrVlmCancellationCoordinator>();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
        services.AddSingleton<PaddleOcrVlmExecutorAdapter>();
        await using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>();

        var delivery = adapter.DeliverAsync(handle, CancellationToken.None).AsTask();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(provider.GetRequiredService<PaddleOcrVlmExecutionRegistry>().RequestCancellation(miniTaskId));
        await delivery.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, signal.Notifications);
        Assert.Null(await store.ReadCompletedAsync(claim, contentHash, CancellationToken.None));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)DocumentOcrRequestState.Pending, await verification.DocumentOcrRequests
            .Where(value => value.MiniTaskId == miniTaskId).Select(value => value.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verification.GpuMiniTasks
            .Where(value => value.Id == miniTaskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal((int)PublicJobState.GpuProcessing, await verification.Jobs
            .Where(value => value.Id == claim.JobId.Value).Select(value => value.PublicState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Separate_local_process_adapters_claim_one_dispatch_before_executing_ocr()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0]),
            CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executor = new BlockingCountingDocumentOcrExecutor();
        var firstLifetime = new TestHostApplicationLifetime();
        var secondLifetime = new TestHostApplicationLifetime();
        await using var firstProvider = CreateLocalExecutorProvider(
            factory, retainedSourceRevisionId, contentHash, executor, new RecordingOutboxWakeSignal(), firstLifetime);
        await using var secondProvider = CreateLocalExecutorProvider(
            factory, retainedSourceRevisionId, contentHash, executor, new RecordingOutboxWakeSignal(), secondLifetime);
        var firstDelivery = firstProvider.GetRequiredService<PaddleOcrVlmExecutorAdapter>()
            .DeliverAsync(handle, CancellationToken.None).AsTask();
        Task? secondDelivery = null;

        try
        {
            await executor.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondDelivery = secondProvider.GetRequiredService<PaddleOcrVlmExecutorAdapter>()
                .DeliverAsync(handle, CancellationToken.None).AsTask();
            await secondDelivery.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(1, executor.CallCount);
            Assert.False(executor.SecondExecutionStarted.Task.IsCompleted);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verification.GpuExecutorDispatches
                .Where(value => value.DispatchId == handle.DispatchId).Select(value => value.State).SingleAsync());
        }
        finally
        {
            executor.Release();
            await AwaitIgnoringFailureAsync(firstDelivery);
            if (secondDelivery is not null)
            {
                await AwaitIgnoringFailureAsync(secondDelivery);
            }
        }

        await using var completedVerification = await factory.CreateDbContextAsync();
        Assert.Equal((int)DocumentOcrRequestState.Requeued, await completedVerification.DocumentOcrRequests
            .Where(value => value.MiniTaskId == handoff.MiniTaskId).Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Application_stopping_cancels_active_local_ocr_without_publishing_a_result()
    {
        var (factory, claim, retainedSourceRevisionId, contentHash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(
            new DocumentOcrHandoffRequest(claim, retainedSourceRevisionId, contentHash, [0]),
            CancellationToken.None);
        var miniTaskId = Assert.IsType<Guid>(handoff.MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executor = new BlockingDocumentOcrExecutor();
        var lifetime = new TestHostApplicationLifetime();
        await using var provider = CreateLocalExecutorProvider(
            factory, retainedSourceRevisionId, contentHash, executor, new RecordingOutboxWakeSignal(), lifetime);
        var delivery = provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>()
            .DeliverAsync(handle, CancellationToken.None).AsTask();

        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lifetime.StopApplication();
            await delivery.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            provider.GetRequiredService<PaddleOcrVlmExecutionRegistry>().RequestCancellation(miniTaskId);
            await AwaitIgnoringFailureAsync(delivery);
        }

        Assert.Null(await store.ReadCompletedAsync(claim, contentHash, CancellationToken.None));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)DocumentOcrRequestState.Pending, await verification.DocumentOcrRequests
            .Where(value => value.MiniTaskId == miniTaskId).Select(value => value.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verification.GpuMiniTasks
            .Where(value => value.Id == miniTaskId).Select(value => value.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public Task Pause_during_inference_releases_capacity_without_replaying_the_uncertain_document() =>
        AssertResultStorageFailureReleasesCapacityAsync(pauseSource: true);

    [NativeSqlServerFact]
    public Task Terminal_result_persistence_failure_releases_capacity_without_replaying_inference() =>
        AssertResultStorageFailureReleasesCapacityAsync(pauseSource: false);

    private async Task AssertResultStorageFailureReleasesCapacityAsync(bool pauseSource)
    {
        var (factory, claim, revisionId, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(new(claim, revisionId, hash, [0]), CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executor = new BlockingCountingDocumentOcrExecutor();
        var signal = new RecordingOutboxWakeSignal();
        await using var provider = CreateLocalExecutorProvider(
            factory, revisionId, hash, executor, signal, new TestHostApplicationLifetime());
        var adapter = provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>();
        var delivery = adapter.DeliverAsync(handle, CancellationToken.None).AsTask();
        await executor.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var context = await factory.CreateDbContextAsync();
        var root = await context.SourceRootConfigurations.SingleAsync();
        if (pauseSource)
        {
            root.State = (int)SourceRootState.Paused;
            await context.SaveChangesAsync();
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE DocumentOcrRequests ADD CONSTRAINT CK_Test_RefuseOcrResult CHECK (ResultJson IS NULL)");
        }

        try
        {
            executor.Release();
            Assert.NotNull(await Record.ExceptionAsync(() => delivery.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.Equal((int)GpuCapacitySlotState.Available,
                await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
            Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain,
                await context.GpuMiniTasks.Where(value => value.Id == handoff.MiniTaskId)
                    .Select(value => value.ExecutionState).SingleAsync());
            Assert.Equal((int)DocumentOcrRequestState.Pending,
                await context.DocumentOcrRequests.Select(value => value.State).SingleAsync());
            Assert.Null(await store.ReadCompletedAsync(claim, hash, CancellationToken.None));
            Assert.Equal(0, signal.Notifications);
        }
        finally
        {
            if (pauseSource)
            {
                root.State = (int)SourceRootState.Enabled;
                await context.SaveChangesAsync();
            }
            else
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE DocumentOcrRequests DROP CONSTRAINT CK_Test_RefuseOcrResult");
            }
            executor.Release();
            await AwaitIgnoringFailureAsync(delivery);
        }

        await adapter.DeliverAsync(handle, CancellationToken.None);
        Assert.Equal(1, executor.CallCount);
        var (_, nextClaim, nextRevision, nextHash) = await CreateClaimedPdfAsync(clearData: false);
        await store.HandoffAsync(new(nextClaim, nextRevision, nextHash, [0]), CancellationToken.None);
        var nextHandle = await AdmitDocumentOcrTaskAsync(factory);
        await using var nextProvider = CreateLocalExecutorProvider(
            factory, nextRevision, nextHash, executor, signal, new TestHostApplicationLifetime());
        await nextProvider.GetRequiredService<PaddleOcrVlmExecutorAdapter>()
            .DeliverAsync(nextHandle, CancellationToken.None);
        Assert.NotNull(await store.ReadCompletedAsync(nextClaim, nextHash, CancellationToken.None));
        Assert.Equal(2, executor.CallCount);
    }

    [NativeSqlServerFact]
    public async Task Stored_result_recovery_releases_capacity_while_paused_and_requeues_only_after_resume()
    {
        var (factory, claim, revisionId, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(new(claim, revisionId, hash, [0]), CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        await CreateLifecycle(factory).AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None);
        await store.StoreResultAsync(handle, handoff.MiniTaskId!.Value,
            new(true, "document-ocr-complete", [new(0, 0, [new("text", 1, 2, 3, 4, "Retained OCR text")])]),
            CancellationToken.None);
        await using var context = await factory.CreateDbContextAsync();
        var root = await context.SourceRootConfigurations.SingleAsync();
        root.State = (int)SourceRootState.Paused;
        await context.SaveChangesAsync();
        var executor = new BlockingCountingDocumentOcrExecutor();
        var signal = new RecordingOutboxWakeSignal();
        await using var provider = CreateLocalExecutorProvider(
            factory, revisionId, hash, executor, signal, new TestHostApplicationLifetime());
        Assert.NotNull(await store.ReadPendingCompletionAsync(handle, CancellationToken.None));
        await provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>().RecoverAsync(CancellationToken.None);
        Assert.Equal((int)GpuCapacitySlotState.Available,
            await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal((int)DocumentOcrRequestState.ResultStored,
            await context.DocumentOcrRequests.Select(value => value.State).SingleAsync());
        Assert.Equal(0, signal.Notifications);
        root.State = (int)SourceRootState.Enabled;
        await context.SaveChangesAsync();
        await provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>().RecoverAsync(CancellationToken.None);
        Assert.Equal((int)DocumentOcrRequestState.Requeued,
            await context.DocumentOcrRequests.Select(value => value.State).SingleAsync());
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, signal.Notifications);
    }

    private SqlDocumentOcrStore CreateStore(IDbContextFactory<FluxKnowledgeDbContext> factory) => new(
        factory,
        CreateCoordinator(factory));

    private ServiceProvider CreateLocalExecutorProvider(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        SourceRevisionId retainedSourceRevisionId,
        string contentHash,
        IDocumentOcrExecutor executor,
        IOutboxWakeSignal signal,
        IHostApplicationLifetime applicationLifetime)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new SqlDocumentOcrStore(factory, CreateCoordinator(factory)));
        services.AddScoped<IGpuExecutorLifecycleSink>(_ => CreateLifecycle(factory));
        services.AddScoped<IRetainedSourceReader>(_ => new FixedRetainedSourceReader(retainedSourceRevisionId, contentHash));
        services.AddSingleton(executor);
        services.AddSingleton(signal);
        services.AddSingleton(applicationLifetime);
        services.AddSingleton<PaddleOcrVlmCompletionCoordinator>();
        services.AddSingleton<PaddleOcrVlmExecutionRegistry>();
        services.AddSingleton<PaddleOcrVlmCancellationCoordinator>();
        services.AddSingleton<PaddleOcrVlmExecutorAdapter>();
        return services.BuildServiceProvider();
    }

    private static async Task AwaitIgnoringFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Test cleanup must not conceal the expectation failure that triggered it.
        }
    }

    private GpuExecutorLifecycleCoordinator CreateLifecycle(IDbContextFactory<FluxKnowledgeDbContext> factory) =>
        new(new SqlGpuSchedulerStore(factory), CreateCoordinator(factory));

    private static GpuSchedulerCoordinator CreateCoordinator(IDbContextFactory<FluxKnowledgeDbContext> factory) => new(
        new SqlGpuSchedulerStore(factory),
        new NoGpuAdmissionGate(),
        new NullStatusPublisher(),
        new NullWakeSignal(),
        TimeProvider.System,
        GpuSchedulerOptions.Default);

    private static async Task<GpuExecutorBatchHandle> AdmitDocumentOcrTaskAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            if (!await arrange.GpuCapacitySlots.AnyAsync())
            {
                arrange.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
                {
                    SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                    State = (int)GpuCapacitySlotState.Available,
                    UpdatedAtUtc = DateTimeOffset.Parse("2026-09-20T12:01:00+00:00")
                });
                await arrange.SaveChangesAsync();
            }
        }

        var options = new GpuSchedulerOptions(
            maxBatchItems: 1,
            maxBatchEstimatedBytes: PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes,
            capacityDeferralCap: TimeSpan.FromMinutes(5),
            fallbackInterval: TimeSpan.FromMinutes(1),
            unresponsiveDiagnosticAge: TimeSpan.FromMinutes(10));
        var admitted = await new SqlGpuSchedulerStore(factory).RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            options,
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(
                GpuAdmissionDisposition.Admit,
                PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                PaddleOcrVlmRuntimeContract.CapacityOwnerKey,
                null,
                PaddleOcrVlmRuntimeContract.ExecutorKey)),
            CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, admitted.Disposition);
        await using var verification = await factory.CreateDbContextAsync();
        var dispatch = await verification.GpuExecutorDispatches.SingleAsync(
            value => value.State == (int)GpuExecutorDispatchState.PendingDelivery);
        return new GpuExecutorBatchHandle(
            dispatch.BatchId,
            dispatch.CapacitySlotKey,
            dispatch.ExecutorKey,
            dispatch.AdmissionGeneration,
            dispatch.DispatchId);
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, ClaimedJob Claim, SourceRevisionId RetainedSourceRevisionId, string ContentHash)>
        CreateClaimedPdfAsync(bool clearData = true)
    {
        if (clearData) await SqlTestData.ClearPhase3SourceDataAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");
        const string contentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var rootId = Guid.NewGuid();
        var retainedSourceRevisionId = SourceRevisionId.New();
        var sourceIdentityId = Guid.NewGuid();
        var pipelineRecordId = new PipelineRecordId(Guid.NewGuid());
        var jobId = new JobId(Guid.NewGuid());
        var dispatchId = new DispatchMessageId(Guid.NewGuid());
        await using (var arrange = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            arrange.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $@"E:\\OCR-test\\{rootId:N}",
                DisplayName = "OCR test",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 500_000,
                AllowedClassificationsJson = "[]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 60,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            arrange.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = retainedSourceRevisionId.Value,
                SourceRootId = rootId,
                StableSourceIdentity = "ocr-test.pdf",
                Revision = 1,
                ContentSha256 = contentHash,
                CanonicalPath = @"E:\\OCR-test\\ocr-test.pdf",
                Classification = "document",
                Extension = ".pdf",
                OriginKind = 0,
                ByteLength = 1,
                DiscoveredAtUtc = now
            });
            arrange.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId,
                SourceKind = "local file",
                StableKey = $@"E:\\OCR-test\\{rootId:N}\\ocr-test.pdf",
                CreatedAtUtc = now
            });
            arrange.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = pipelineRecordId.Value,
                SourceIdentityId = sourceIdentityId,
                SourceRevisionId = retainedSourceRevisionId.Value,
                Revision = 1,
                ContentHash = contentHash,
                RootLineageRecordId = pipelineRecordId.Value,
                CurrentStage = (int)PipelineStage.Extract,
                RegisteredAtUtc = now
            });
            arrange.Jobs.Add(new JobEntity
            {
                Id = jobId.Value,
                PipelineRecordId = pipelineRecordId.Value,
                SourceRevision = 1,
                Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractDocument,
                PublicState = (int)PublicJobState.WorkerQueued,
                DueAtUtc = now,
                AttemptCount = 0,
                LeaseGeneration = 0
            });
            arrange.OutboxMessages.Add(new OutboxMessageEntity
            {
                Id = dispatchId.Value,
                PipelineRecordId = pipelineRecordId.Value,
                SourceRevision = 1,
                Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractDocument,
                DispatchGeneration = 0,
                IdempotencyKey = $"{pipelineRecordId.Value:N}:1:document-ocr",
                DueAtUtc = now,
                CreatedAtUtc = now
            });
            await arrange.SaveChangesAsync();
        }

        var factory = SqlTestData.CreateFactory(_fixture);
        var claim = await new SqlJobClaimStore(factory).ClaimNextDueAsync(
            "document-ocr-worker",
            now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.NotNull(claim);
        return (factory, claim, retainedSourceRevisionId, contentHash);
    }

    private sealed class NoGpuAdmissionGate : IGpuAdmissionGate
    {
        public ValueTask<GpuAdmissionDecision> DecideAsync(
            GpuBatchCandidate candidate,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
    }

    private sealed class NullStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NullWakeSignal : IGpuSchedulerWakeSignal
    {
        public void Notify(GpuSchedulerWakeReason reason)
        {
        }

        public ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult((GpuSchedulerWakeReason)0);
    }

    private sealed class FixedRetainedSourceReader(SourceRevisionId revisionId, string contentHash) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(revisionId, sourceRevisionId);
            return ValueTask.FromResult(new RetainedSourceBytes(sourceRevisionId, [1, 2, 3], contentHash, 3));
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDocumentOcrExecutor(DocumentOcrExecutionResult result) : IDocumentOcrExecutor
    {
        public List<(SourceRevisionId RetainedSourceRevisionId, IReadOnlyList<int> PageIndexes)> Calls { get; } = [];

        public ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
            RetainedSourceBytes retained,
            IReadOnlyList<int> pageIndexes,
            CancellationToken cancellationToken)
        {
            Calls.Add((retained.SourceRevisionId, pageIndexes.ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingDocumentOcrExecutor : IDocumentOcrExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
            RetainedSourceBytes retained,
            IReadOnlyList<int> pageIndexes,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking OCR test executor was not cancelled.");
        }
    }

    private sealed class BlockingCountingDocumentOcrExecutor : IDocumentOcrExecutor
    {
        private readonly TaskCompletionSource<DocumentOcrExecutionResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _calls);

        public async ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
            RetainedSourceBytes retained,
            IReadOnlyList<int> pageIndexes,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstExecutionStarted.TrySetResult();
            }
            else
            {
                SecondExecutionStarted.TrySetResult();
            }

            return await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult(new DocumentOcrExecutionResult(
            true,
            "document-ocr-complete",
            [new DocumentOcrPageResult(0, 0, [new DocumentOcrBlock("text", 1, 2, 3, 4, "GPU OCR text")])]));
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();
    }

    private sealed class RecordingOutboxWakeSignal : IOutboxWakeSignal
    {
        public int Notifications { get; private set; }

        public void Notify() => Notifications++;

        public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
