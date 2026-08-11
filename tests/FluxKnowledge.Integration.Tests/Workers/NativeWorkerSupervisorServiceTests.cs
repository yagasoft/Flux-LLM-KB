using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

[Collection("native-worker-process")]
public sealed class NativeWorkerSupervisorServiceTests
{
    [Fact]
    public void Options_are_disabled_by_default()
    {
        var options = new NativeWorkerOptions();

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Enabled_options_require_a_canonical_executor_key_and_existing_executable()
    {
        var options = new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a ",
            ExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Scheduler_registers_native_worker_components_only_when_explicitly_enabled()
    {
        var disabled = new ServiceCollection();
        disabled.AddFluxKnowledgeGpuScheduler();

        Assert.DoesNotContain(disabled, descriptor => descriptor.ServiceType == typeof(NativeWorkerSupervisorService));
        Assert.DoesNotContain(disabled, descriptor => descriptor.ServiceType == typeof(IGpuExecutorAdapter));

        var enabled = new ServiceCollection();
        enabled.AddFluxKnowledgeGpuScheduler(new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a",
            ExecutablePath = Environment.ProcessPath!
        });

        Assert.Contains(enabled, descriptor => descriptor.ServiceType == typeof(NativeWorkerSupervisorService));
        Assert.Contains(enabled, descriptor => descriptor.ServiceType == typeof(IGpuExecutorAdapter));
        Assert.Contains(enabled, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Enabled_composition_validates_scopes_without_capturing_scheduler_scoped_services()
    {
        var services = new ServiceCollection();
        services.AddFluxKnowledgeGpuScheduler(new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a",
            ExecutablePath = Environment.ProcessPath!
        });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        _ = provider.GetRequiredService<NativeWorkerSupervisorService>();
        _ = provider.GetRequiredService<IGpuExecutorAdapter>();
    }

    [Fact]
    public async Task Pipe_rejects_a_stale_client_identity_before_issuing_a_session_nonce()
    {
        var pipeName = $"fluxknowledge-test-{Guid.NewGuid():N}";
        var instance = NativeWorkerInstanceHandle.Create(
            Guid.NewGuid(), "executor-a", 100, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), NativeWorkerProtocol.SupportedVersion);
        await using var server = new NativeWorkerPipeServer(pipeName, new FixedClientProcessIdentityReader(101));
        var accepting = server.AcceptAsync(instance, CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(CancellationToken.None);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(new NativeWorkerFrame(
            NativeWorkerFrameKind.Hello, NativeWorkerProtocol.SupportedVersion, instance.InstanceId)));

        Assert.Null(await accepting);
    }

    [Fact]
    public async Task Disposing_an_active_session_cancels_its_pending_read_so_the_observer_can_create_a_successor_pipe()
    {
        var pipeName = $"fluxknowledge-test-{Guid.NewGuid():N}";
        using var process = Process.GetCurrentProcess();
        var instance = NativeWorkerInstanceHandle.Create(
            Guid.NewGuid(), "executor-a", process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), NativeWorkerProtocol.SupportedVersion);
        await using var server = new NativeWorkerPipeServer(pipeName, new FixedClientProcessIdentityReader((uint)process.Id));
        var accepting = server.AcceptAsync(instance, CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(CancellationToken.None);
        using var clientReader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await using var clientWriter = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await clientWriter.WriteLineAsync(NativeWorkerFrameCodec.Serialize(new NativeWorkerFrame(NativeWorkerFrameKind.Hello, NativeWorkerProtocol.SupportedVersion, instance.InstanceId)));
        var welcome = clientReader.ReadLineAsync();
        var session = Assert.IsType<NativeWorkerPipeSession>(await accepting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(await welcome.WaitAsync(TimeSpan.FromSeconds(10)));
        var pendingRead = session.ReadAsync(CancellationToken.None);
        var clientEof = clientReader.ReadLineAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => pendingRead.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Null(await clientEof.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Launch_failure_is_bounded_and_happens_after_durable_launching_without_scheduler_mutation()
    {
        var executable = Environment.ProcessPath!;
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = executable },
            store,
            lifecycle,
            new ThrowingProcessLauncher(),
            TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(["launching", "launch-failed"], store.Operations);
        Assert.Equal(0, lifecycle.MutationCount);
    }

    [Fact]
    public async Task Launch_clears_the_inherited_environment_and_passes_only_the_approved_test_signal()
    {
        const string sensitiveSettingName = "FLUX_NATIVE_WORKER_TEST_SENSITIVE_SETTING";
        const string sensitiveSettingValue = "must-not-reach-child";
        var priorValue = Environment.GetEnvironmentVariable(sensitiveSettingName);
        Environment.SetEnvironmentVariable(sensitiveSettingName, sensitiveSettingValue);
        try
        {
            var launcher = new CapturingThrowingProcessLauncher();
            var supervisor = new NativeWorkerSupervisorService(
                new NativeWorkerOptions
                {
                    Enabled = true,
                    ExecutorKey = "executor-a",
                    ExecutablePath = Environment.ProcessPath!,
                    PostReadyReadSignalName = "approved-test-signal"
                },
                new RecordingWorkerStore(),
                new RecordingLifecycleSink(),
                launcher,
                TimeProvider.System);

            await supervisor.StartAsync(CancellationToken.None);

            var startInfo = Assert.IsType<ProcessStartInfo>(launcher.StartInfo);
            Assert.DoesNotContain(sensitiveSettingName, startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            var environment = Assert.Single(startInfo.Environment);
            Assert.Equal("FLUX_NATIVE_WORKER_TEST_POST_READY_READ_EVENT", environment.Key);
            Assert.Equal("approved-test-signal", environment.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sensitiveSettingName, priorValue);
        }
    }

    [Fact]
    public async Task Restart_with_an_active_recovery_candidate_makes_only_its_exact_handle_uncertain_and_never_launches_a_replacement()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), "executor-a", 42, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), "v1");
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        store.Candidates.Add(new NativeWorkerRecoveryCandidate(instance.InstanceId, NativeWorkerLifecycleClass.Connected, instance, handle));
        var launcher = new CountingProcessLauncher();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! },
            store,
            lifecycle,
            launcher,
            TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(handle, store.UncertainHandle);
        Assert.Contains("uncertain", store.Operations);
        Assert.Equal(0, launcher.Calls);
        Assert.Equal(0, lifecycle.MutationCount);
        Assert.DoesNotContain("launching", store.Operations);
        Assert.DoesNotContain(NativeWorkerLifecycleClass.TerminationRequested.ToString(), store.Operations);
    }

    [Theory]
    [InlineData(NativeWorkerLifecycleClass.Connected)]
    [InlineData(NativeWorkerLifecycleClass.Lost)]
    [InlineData(NativeWorkerLifecycleClass.LaunchRequested)]
    public async Task Restart_candidate_without_an_active_handle_blocks_launch_without_probing_or_replacing(NativeWorkerLifecycleClass state)
    {
        var store = new RecordingWorkerStore();
        var instanceId = Guid.NewGuid();
        store.Candidates.Add(new NativeWorkerRecoveryCandidate(instanceId, state, null, null));
        var launcher = new CountingProcessLauncher();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! },
            store,
            new RecordingLifecycleSink(),
            launcher,
            TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(0, launcher.Calls);
        Assert.DoesNotContain("launching", store.Operations);
        if (state == NativeWorkerLifecycleClass.Lost)
        {
            Assert.DoesNotContain("Lost", store.Operations);
        }
        else
        {
            Assert.Contains("Lost", store.Operations);
        }
    }

    [Fact]
    public async Task Real_attested_child_reaches_ready_and_stops_gracefully_when_idle()
    {
        var store = new RecordingWorkerStore();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions
            {
                Enabled = true,
                ExecutorKey = "executor-a",
                ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe")
            },
            store,
            new RecordingLifecycleSink(),
            new NativeWorkerProcessLauncher(),
            TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await supervisor.StopAsync(CancellationToken.None);
        await store.Exited.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("connected", store.Operations);
        Assert.Contains("Ready", store.Operations);
        Assert.Contains("GracefulStopRequested", store.Operations);
        Assert.Contains("GracefulStopConfirmed", store.Operations);
        Assert.Contains("Exited", store.Operations);

        var restarted = new NativeWorkerSupervisorService(
            new NativeWorkerOptions
            {
                Enabled = true,
                ExecutorKey = "executor-a",
                ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe")
            },
            store,
            new RecordingLifecycleSink(),
            new NativeWorkerProcessLauncher(),
            TimeProvider.System);
        await restarted.StartAsync(CancellationToken.None);
        await store.SecondReady.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, store.Operations.Count(operation => operation == "launching"));
        await restarted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Real_child_reconnects_after_controlled_eof_without_relaunch_or_duplicate_ready_artifacts()
    {
        var store = new RecordingWorkerStore();
        var postReadyReadEventName = $"FluxKnowledge.NativeWorker.PostReadyRead.{Guid.NewGuid():N}";
        using var postReadyRead = new EventWaitHandle(false, EventResetMode.ManualReset, postReadyReadEventName);
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions
            {
                Enabled = true,
                ExecutorKey = "executor-a",
                ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"),
                PostReadyReadSignalName = postReadyReadEventName
            },
            store,
            new RecordingLifecycleSink(),
            new NativeWorkerProcessLauncher(),
            TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(postReadyRead.WaitOne(TimeSpan.FromSeconds(10)));
        var firstConnection = Assert.Single(store.Connections);
        var firstSession = GetCurrentSession(supervisor);

        await firstSession.DisposeAsync();
        await store.Reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await store.SecondReady.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, store.Connections.Count);
        Assert.Equal(firstConnection.Instance.InstanceId, store.Connections[1].Instance.InstanceId);
        Assert.Equal(firstConnection.Instance.ProcessId, store.Connections[1].Instance.ProcessId);
        Assert.Equal(firstConnection.Instance.ProcessStartedAtUtc, store.Connections[1].Instance.ProcessStartedAtUtc);
        Assert.Single(store.ConnectionOperationIds.Distinct());
        Assert.Equal(1, store.Operations.Count(operation => operation == "launching"));
        Assert.Equal(2, store.Operations.Count(operation => operation == NativeWorkerLifecycleClass.Ready.ToString()));
        Assert.DoesNotContain("bound", store.Operations);

        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_requires_ready_from_its_current_pipe_session()
    {
        var store = new RecordingWorkerStore();
        using var process = Process.GetCurrentProcess();
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), "executor-a", process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), NativeWorkerProtocol.SupportedVersion);
        var pipeName = $"fluxknowledge-test-{Guid.NewGuid():N}";
        await using var server = new NativeWorkerPipeServer(pipeName, new FixedClientProcessIdentityReader((uint)process.Id));
        var accepting = server.AcceptAsync(instance, CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(CancellationToken.None);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(new NativeWorkerFrame(NativeWorkerFrameKind.Hello, NativeWorkerProtocol.SupportedVersion, instance.InstanceId)));
        _ = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var session = Assert.IsType<NativeWorkerPipeSession>(await accepting.WaitAsync(TimeSpan.FromSeconds(10)));
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! };
        var supervisor = new NativeWorkerSupervisorService(options, store, new RecordingLifecycleSink(), new ThrowingProcessLauncher(), TimeProvider.System);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        SetSession(supervisor, session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeWorkerExecutorAdapter(options, supervisor).DeliverAsync(handle, CancellationToken.None).AsTask());
        Assert.DoesNotContain("bound", store.Operations);
    }

    private static NativeWorkerPipeSession GetCurrentSession(NativeWorkerSupervisorService supervisor) =>
        (NativeWorkerPipeSession?)typeof(NativeWorkerSupervisorService)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(supervisor)
        ?? throw new InvalidOperationException("The supervisor did not retain its attested pipe session.");

    private static GpuExecutorBatchHandle? GetActiveHandle(NativeWorkerSupervisorService supervisor) =>
        (GpuExecutorBatchHandle?)typeof(NativeWorkerSupervisorService)
            .GetField("_activeHandle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(supervisor);

    private static void SetActiveHandle(NativeWorkerSupervisorService supervisor, GpuExecutorBatchHandle handle) =>
        typeof(NativeWorkerSupervisorService)
            .GetField("_activeHandle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(supervisor, handle);

    private static void SetSession(NativeWorkerSupervisorService supervisor, NativeWorkerPipeSession session) =>
        typeof(NativeWorkerSupervisorService)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(supervisor, session);

    private static async Task<bool> ApplyFrameAsync(NativeWorkerSupervisorService supervisor, NativeWorkerInstanceHandle instance, NativeWorkerFrame frame) =>
        await Assert.IsType<Task<bool>>(typeof(NativeWorkerSupervisorService)
            .GetMethod("ApplyFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(supervisor, [instance, frame, CancellationToken.None]));

    [Fact]
    public async Task Child_exit_before_acknowledgement_marks_only_its_original_handle_uncertain()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var options = new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a",
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"),
            TestInstruction = NativeWorkerTestInstruction.ExitBeforeAcknowledgement
        };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System);
        var adapter = new NativeWorkerExecutorAdapter(options, supervisor);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.DeliverAsync(handle, CancellationToken.None);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await store.Exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(handle, store.UncertainHandle);
        Assert.Equal(0, lifecycle.MutationCount);
        Assert.Contains("Exited", store.Operations);
        Assert.Contains("uncertain", store.Operations);
    }

    [Fact]
    public async Task Rejected_exact_uncertainty_after_active_child_loss_retains_the_handle_and_prevents_idle_stop()
    {
        var store = new RecordingWorkerStore { UncertaintyResult = new NativeWorkerStoreMutationResult(false, false) };
        var options = new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a",
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"),
            TestInstruction = NativeWorkerTestInstruction.ExitBeforeAcknowledgement
        };
        var supervisor = new NativeWorkerSupervisorService(options, store, new RecordingLifecycleSink(), new NativeWorkerProcessLauncher(), TimeProvider.System);
        var adapter = new NativeWorkerExecutorAdapter(options, supervisor);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.DeliverAsync(handle, CancellationToken.None);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(handle, GetActiveHandle(supervisor));
        await supervisor.StopAsync(CancellationToken.None);
        Assert.DoesNotContain(NativeWorkerLifecycleClass.GracefulStopRequested.ToString(), store.Operations);
    }

    [Theory]
    [InlineData(NativeWorkerFrameKind.Acknowledgement)]
    [InlineData(NativeWorkerFrameKind.Receipt)]
    [InlineData(NativeWorkerFrameKind.Callback)]
    public async Task Protocol_rejected_frame_with_an_active_handle_makes_that_exact_handle_uncertain(NativeWorkerFrameKind frameKind)
    {
        var store = new RecordingWorkerStore();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! },
            store,
            new RecordingLifecycleSink(),
            new ThrowingProcessLauncher(),
            TimeProvider.System);
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), "executor-a", 42, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), NativeWorkerProtocol.SupportedVersion);
        var activeHandle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        var rejectedHandle = activeHandle with { DispatchId = Guid.NewGuid() };
        SetActiveHandle(supervisor, activeHandle);
        var frame = new NativeWorkerFrame(
            frameKind,
            NativeWorkerProtocol.SupportedVersion,
            instance.InstanceId,
            Guid.NewGuid(),
            frameKind == NativeWorkerFrameKind.Callback ? null : rejectedHandle,
            Disposition: frameKind == NativeWorkerFrameKind.Receipt ? NativeWorkerTaskDisposition.Completed : null);

        var accepted = await ApplyFrameAsync(supervisor, instance, frame);

        Assert.False(accepted);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(activeHandle, store.UncertainHandle);
    }

    [Fact]
    public async Task Protocol_rejected_frame_with_uncommitted_exact_uncertainty_keeps_the_observation_open()
    {
        var store = new RecordingWorkerStore { UncertaintyResult = new NativeWorkerStoreMutationResult(false, false) };
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! },
            store,
            new RecordingLifecycleSink(),
            new ThrowingProcessLauncher(),
            TimeProvider.System);
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), "executor-a", 42, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), NativeWorkerProtocol.SupportedVersion);
        var activeHandle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        SetActiveHandle(supervisor, activeHandle);

        var continueObserving = await ApplyFrameAsync(supervisor, instance, new NativeWorkerFrame(
            NativeWorkerFrameKind.ProtocolRejected,
            NativeWorkerProtocol.SupportedVersion,
            instance.InstanceId,
            Guid.NewGuid()));

        Assert.True(continueObserving);
        Assert.Equal(activeHandle, store.UncertainHandle);
    }

    [Fact]
    public async Task Protocol_rejected_idle_frame_closes_the_session_immediately()
    {
        var store = new RecordingWorkerStore();
        var supervisor = new NativeWorkerSupervisorService(
            new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Environment.ProcessPath! },
            store,
            new RecordingLifecycleSink(),
            new ThrowingProcessLauncher(),
            TimeProvider.System);
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), "executor-a", 42, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), NativeWorkerProtocol.SupportedVersion);

        var continueObserving = await ApplyFrameAsync(supervisor, instance, new NativeWorkerFrame(
            NativeWorkerFrameKind.ProtocolRejected,
            NativeWorkerProtocol.SupportedVersion,
            instance.InstanceId,
            Guid.NewGuid()));

        Assert.False(continueObserving);
        Assert.Null(store.UncertainHandle);
    }

    [Fact]
    public async Task Parent_script_maps_real_completion_to_its_exact_mini_task_not_the_batch()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        var miniTaskId = Guid.NewGuid();
        var script = new NativeWorkerReceiptAndCompleteScript(
            [new GpuExecutorResultReceipt(Guid.NewGuid(), handle, miniTaskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed)],
            Guid.NewGuid(),
            new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed, [new GpuMiniTaskBoundaryOutcome(miniTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true));
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"), TestInstruction = NativeWorkerTestInstruction.ReceiptAndComplete };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System, new FixedCompletionScriptSource(script));
        var adapter = new NativeWorkerExecutorAdapter(options, supervisor);

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.DeliverAsync(handle, CancellationToken.None);
        await lifecycle.Callback.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await store.Cleared.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(miniTaskId, Assert.Single(lifecycle.Receipts).MiniTaskId);
        Assert.NotEqual(handle.BatchId, Assert.Single(lifecycle.Receipts).MiniTaskId);
        Assert.Contains("cleared", store.Operations);
    }

    [Fact]
    public async Task Rejected_parent_owned_completion_receipt_marks_only_its_exact_handle_uncertain_without_callback_or_clear()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink { ReceiptResult = new GpuExecutorDispatchMutationResult(false, false) };
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        var miniTaskId = Guid.NewGuid();
        var script = new NativeWorkerReceiptAndCompleteScript(
            [new GpuExecutorResultReceipt(Guid.NewGuid(), handle, miniTaskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed)],
            Guid.NewGuid(),
            new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed, [new GpuMiniTaskBoundaryOutcome(miniTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true));
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"), TestInstruction = NativeWorkerTestInstruction.ReceiptAndComplete };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System, new FixedCompletionScriptSource(script));

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await new NativeWorkerExecutorAdapter(options, supervisor).DeliverAsync(handle, CancellationToken.None);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(handle, store.UncertainHandle);
        Assert.Single(lifecycle.Receipts);
        Assert.DoesNotContain("cleared", store.Operations);
        Assert.Equal(2, lifecycle.MutationCount);
    }

    [Fact]
    public async Task Generic_observer_exception_with_rejected_uncertainty_retains_a_nonterminal_recovery_fence_and_blocks_replacement()
    {
        var store = new RecordingWorkerStore { UncertaintyResult = new NativeWorkerStoreMutationResult(false, false) };
        var lifecycle = new RecordingLifecycleSink { ReceiptException = new InvalidOperationException("scripted generic observer failure") };
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        var miniTaskId = Guid.NewGuid();
        var script = new NativeWorkerReceiptAndCompleteScript(
            [new GpuExecutorResultReceipt(Guid.NewGuid(), handle, miniTaskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed)],
            Guid.NewGuid(),
            new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed, [new GpuMiniTaskBoundaryOutcome(miniTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true));
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"), TestInstruction = NativeWorkerTestInstruction.ReceiptAndComplete };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System, new FixedCompletionScriptSource(script));

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await new NativeWorkerExecutorAdapter(options, supervisor).DeliverAsync(handle, CancellationToken.None);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var instance = Assert.Single(store.Connections).Instance;
        store.Candidates.Add(new NativeWorkerRecoveryCandidate(instance.InstanceId, NativeWorkerLifecycleClass.Connected, instance, handle));
        var replacementLauncher = new CountingProcessLauncher();
        var recreated = new NativeWorkerSupervisorService(options, store, new RecordingLifecycleSink(), replacementLauncher, TimeProvider.System);
        await recreated.StartAsync(CancellationToken.None);

        Assert.Equal(handle, GetActiveHandle(supervisor));
        Assert.DoesNotContain(NativeWorkerLifecycleClass.Exited.ToString(), store.Operations);
        Assert.Equal(0, replacementLauncher.Calls);
    }

    [Fact]
    public async Task Missing_completion_script_rejects_before_binding_or_lifecycle_mutation()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"), TestInstruction = NativeWorkerTestInstruction.ReceiptAndComplete };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System);
        var adapter = new NativeWorkerExecutorAdapter(options, supervisor);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.DeliverAsync(handle, CancellationToken.None).AsTask());

        Assert.DoesNotContain("bound", store.Operations);
        Assert.Equal(0, lifecycle.MutationCount);
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_completion_script_rejects_before_binding_or_lifecycle_mutation()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());
        var invalid = new NativeWorkerReceiptAndCompleteScript(
            [new GpuExecutorResultReceipt(Guid.NewGuid(), handle with { DispatchId = Guid.NewGuid() }, Guid.NewGuid(), GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed)],
            Guid.NewGuid(), new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed, [new GpuMiniTaskBoundaryOutcome(Guid.NewGuid(), GpuMiniTaskBoundaryDisposition.Completed)], true));
        var options = new NativeWorkerOptions { Enabled = true, ExecutorKey = "executor-a", ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"), TestInstruction = NativeWorkerTestInstruction.ReceiptAndComplete };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System, new FixedCompletionScriptSource(invalid));
        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ArgumentException>(() => new NativeWorkerExecutorAdapter(options, supervisor).DeliverAsync(handle, CancellationToken.None).AsTask());
        Assert.DoesNotContain("bound", store.Operations);
        Assert.Equal(0, lifecycle.MutationCount);
        await supervisor.StopAsync(CancellationToken.None);
    }

    private sealed class FixedCompletionScriptSource(NativeWorkerReceiptAndCompleteScript? script) : INativeWorkerTestLifecycleScriptSource
    {
        public ValueTask<NativeWorkerReceiptAndCompleteScript?> GetReceiptAndCompleteAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken) => ValueTask.FromResult(script);
    }

    [Fact]
    public async Task Missing_heartbeat_marks_only_its_original_handle_uncertain_without_lifecycle_completion()
    {
        var store = new RecordingWorkerStore();
        var lifecycle = new RecordingLifecycleSink();
        var options = new NativeWorkerOptions
        {
            Enabled = true,
            ExecutorKey = "executor-a",
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "deterministic-worker", "FluxKnowledge.DeterministicWorker.exe"),
            TestInstruction = NativeWorkerTestInstruction.Unresponsive,
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            AllowForcedTerminationForControlledTests = true
        };
        var supervisor = new NativeWorkerSupervisorService(options, store, lifecycle, new NativeWorkerProcessLauncher(), TimeProvider.System);
        var adapter = new NativeWorkerExecutorAdapter(options, supervisor);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

        await supervisor.StartAsync(CancellationToken.None);
        await store.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.DeliverAsync(handle, CancellationToken.None);
        await store.Uncertain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await store.Termination.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(handle, store.UncertainHandle);
        Assert.Equal(0, lifecycle.MutationCount);
        Assert.Contains("Unresponsive", store.Operations);
        Assert.Contains("TerminationRequested", store.Operations);
        Assert.Contains("TerminationConfirmed", store.Operations);
    }

    private sealed class FixedClientProcessIdentityReader(uint processId) : INativeWorkerClientProcessIdentityReader
    {
        public uint GetClientProcessId(SafePipeHandle pipeHandle) => processId;
    }

    private sealed class ThrowingProcessLauncher : INativeWorkerProcessLauncher
    {
        public Process Start(ProcessStartInfo startInfo) => throw new InvalidOperationException("scripted launch failure");
    }

    private sealed class CapturingThrowingProcessLauncher : INativeWorkerProcessLauncher
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public Process Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            throw new InvalidOperationException("scripted launch failure");
        }
    }

    private sealed class CountingProcessLauncher : INativeWorkerProcessLauncher
    {
        public int Calls { get; private set; }
        public Process Start(ProcessStartInfo startInfo)
        {
            Calls++;
            throw new InvalidOperationException("A blocked recovery must not start a replacement.");
        }
    }

    private sealed class RecordingWorkerStore : INativeWorkerInstanceStore
    {
        public List<string> Operations { get; } = [];
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Uncertain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Termination { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GpuExecutorBatchHandle? UncertainHandle { get; private set; }
        public NativeWorkerStoreMutationResult UncertaintyResult { get; init; } = new(true, true);
        public List<NativeWorkerRecoveryCandidate> Candidates { get; } = [];
        public List<NativeWorkerConnectionAttestation> Connections { get; } = [];
        public List<Guid> ConnectionOperationIds { get; } = [];
        public ValueTask<IReadOnlyList<NativeWorkerRecoveryCandidate>> ReadRecoveryCandidatesAsync(string executorKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NativeWorkerRecoveryCandidate>>(Candidates);
        public ValueTask<NativeWorkerStoreMutationResult> CreateAsync(Guid operationId, NativeWorkerLaunchRequest launch, CancellationToken cancellationToken)
        {
            Operations.Add("launching");
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> AppendEvidenceAsync(NativeWorkerLifecycleEvidence evidence, CancellationToken cancellationToken)
        {
            Operations.Add(evidence.Class == NativeWorkerLifecycleClass.LaunchFailed ? "launch-failed" : evidence.Class.ToString());
            if (evidence.Class == NativeWorkerLifecycleClass.Ready)
            {
                Ready.TrySetResult();
                if (Operations.Count(operation => operation == NativeWorkerLifecycleClass.Ready.ToString()) == 2)
                {
                    SecondReady.TrySetResult();
                }
            }
            if (evidence.Class == NativeWorkerLifecycleClass.TerminationConfirmed)
            {
                Termination.TrySetResult();
            }
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> RecordConnectionAsync(Guid operationId, NativeWorkerConnectionAttestation attestation, CancellationToken cancellationToken)
        {
            Operations.Add("connected");
            Connections.Add(attestation);
            ConnectionOperationIds.Add(operationId);
            Connected.TrySetResult();
            if (Connections.Count == 2)
            {
                Reconnected.TrySetResult();
            }
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> BindExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            Operations.Add("bound");
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> ClearExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            Operations.Add("cleared");
            Cleared.TrySetResult();
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> RecordHeartbeatAsync(Guid operationId, Guid instanceId, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeWorkerStoreMutationResult> RecordExitAsync(Guid operationId, Guid instanceId, DateTimeOffset observedAtUtc, int? exitCode, CancellationToken cancellationToken)
        {
            Operations.Add("Exited");
            Exited.TrySetResult();
            return ValueTask.FromResult(new NativeWorkerStoreMutationResult(true, true));
        }
        public ValueTask<NativeWorkerStoreMutationResult> MarkExactHandleUncertainAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            UncertainHandle = handle;
            Operations.Add("uncertain");
            Uncertain.TrySetResult();
            return ValueTask.FromResult(UncertaintyResult);
        }
    }

    private sealed class RecordingLifecycleSink : IGpuExecutorLifecycleSink
    {
        public int MutationCount { get; private set; }
        public List<GpuExecutorResultReceipt> Receipts { get; } = [];
        public TaskCompletionSource Callback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GpuExecutorDispatchMutationResult ReceiptResult { get; init; } = new(true, true);
        public Exception? ReceiptException { get; init; }
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement acknowledgement, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty uncertainty, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt receipt, CancellationToken cancellationToken)
        {
            if (ReceiptException is not null) throw ReceiptException;
            Receipts.Add(receipt);
            MutationCount++;
            return ValueTask.FromResult(ReceiptResult);
        }
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence evidence, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken)
        {
            MutationCount++;
            Callback.TrySetResult();
            return ValueTask.FromResult(new GpuBatchCallbackResult(true, true));
        }
        private ValueTask<GpuExecutorDispatchMutationResult> Mutate()
        {
            MutationCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(true, true));
        }
    }
}
