using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Owns one explicitly configured child process. Process observations are evidence only: this
/// service never derives completion, capacity release, retry, requeue or replacement from them.
/// </summary>
public sealed class NativeWorkerSupervisorService : IHostedService
{
    private readonly NativeWorkerOptions _options;
    private readonly INativeWorkerInstanceStore? _store;
    private readonly IGpuExecutorLifecycleSink? _lifecycleSink;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly INativeWorkerProcessLauncher _processLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly INativeWorkerTestLifecycleScriptSource? _testLifecycleScriptSource;
    private readonly object _sync = new();
    private NativeWorkerPipeSession? _session;
    private GpuExecutorBatchHandle? _activeHandle;
    private Task? _workerTask;
    private CancellationTokenSource? _observationCancellation;
    private bool _readyObserved;
    private bool _gracefulStopRequested;
    private bool _gracefulStopAttested;
    private NativeWorkerReceiptAndCompleteScript? _completionScript;

    [ActivatorUtilitiesConstructor]
    public NativeWorkerSupervisorService(
        NativeWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        INativeWorkerProcessLauncher processLauncher,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal NativeWorkerSupervisorService(
        NativeWorkerOptions options,
        INativeWorkerInstanceStore store,
        IGpuExecutorLifecycleSink lifecycleSink,
        INativeWorkerProcessLauncher processLauncher,
        TimeProvider timeProvider,
        INativeWorkerTestLifecycleScriptSource? testLifecycleScriptSource = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lifecycleSink = lifecycleSink ?? throw new ArgumentNullException(nameof(lifecycleSink));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _testLifecycleScriptSource = testLifecycleScriptSource;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _options.Validate();
        if (!_options.Enabled)
        {
            return;
        }

        if (!await ReconcilePriorCandidatesAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var instanceId = Guid.NewGuid();
        var executableFingerprint = CreateExecutableFingerprint(_options.ExecutablePath!);
        var observedAtUtc = _timeProvider.GetUtcNow();
        var create = await CreateAsync(
            Guid.NewGuid(),
            new NativeWorkerLaunchRequest(instanceId, _options.ExecutorKey!, executableFingerprint, _options.ProtocolVersion),
            cancellationToken).ConfigureAwait(false);
        if (!create.Accepted)
        {
            return;
        }

        var pipeName = $"FluxKnowledge.NativeWorker.{instanceId:N}";
        var pipe = new NativeWorkerPipeServer(pipeName);
        Process process;
        try
        {
            process = _processLauncher.Start(CreateStartInfo(_options.ExecutablePath!, pipeName, instanceId, _options.ProtocolVersion, _options.PostReadyReadSignalName));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            await AppendEvidenceAsync(instanceId, NativeWorkerLifecycleClass.LaunchFailed, observedAtUtc, 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        NativeWorkerInstanceHandle instance;
        try
        {
            instance = NativeWorkerInstanceHandle.Create(
                instanceId,
                _options.ExecutorKey!,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                _options.ProtocolVersion);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            process.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            await AppendEvidenceAsync(instanceId, NativeWorkerLifecycleClass.LaunchFailed, observedAtUtc, 2, cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            _observationCancellation = new CancellationTokenSource();
            _workerTask = ObserveWorkerAsync(pipe, process, instance, executableFingerprint, _observationCancellation.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? workerTask;
        NativeWorkerPipeSession? session;
        lock (_sync)
        {
            workerTask = _workerTask;
            session = _session;
        }

        if (session is not null && _activeHandle is null)
        {
            try
            {
                var requested = await AppendEvidenceAsync(session.Instance.InstanceId, NativeWorkerLifecycleClass.GracefulStopRequested, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                if (!requested.Accepted || !requested.Committed)
                {
                    return;
                }
                lock (_sync)
                {
                    if (_session != session || _activeHandle is not null)
                    {
                        return;
                    }

                    _gracefulStopRequested = true;
                }
                await session.WriteAsync(new NativeWorkerFrame(
                    NativeWorkerFrameKind.StopRequested,
                    session.Instance.ProtocolVersion,
                    session.Instance.InstanceId,
                    session.SessionNonce), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        if (workerTask is not null)
        {
            await workerTask.WaitAsync(_options.IdleStopTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (workerTask?.IsCompleted == true)
            {
                _observationCancellation?.Cancel();
            }
        }
    }

    internal ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        return new ValueTask(DeliverBoundAsync(handle, cancellationToken));
    }

    private async Task DeliverBoundAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        NativeWorkerReceiptAndCompleteScript? completionScript = null;
        if (_options.TestInstruction == NativeWorkerTestInstruction.ReceiptAndComplete)
        {
            completionScript = _testLifecycleScriptSource is null
                ? null
                : await _testLifecycleScriptSource.GetReceiptAndCompleteAsync(handle, cancellationToken).ConfigureAwait(false);
            if (completionScript is null)
            {
                throw new InvalidOperationException("Receipt-and-complete delivery requires a parent-owned lifecycle script.");
            }

            completionScript.ValidateFor(handle);
        }

        NativeWorkerPipeSession session;
        lock (_sync)
        {
            session = _session ?? throw new InvalidOperationException("The native worker is not ready for dispatch delivery.");
            if (!_readyObserved)
            {
                throw new InvalidOperationException("The native worker has not completed its attested ready handshake.");
            }
            if (_activeHandle is not null)
            {
                if (_activeHandle == handle)
                {
                    return;
                }

                throw new InvalidOperationException("The native worker already owns an active durable dispatch.");
            }
        }

        var bound = await BindExactActiveDispatchAsync(
            CreateDeterministicOperationId("delivery-bind", session.Instance.InstanceId, handle.DispatchId),
            session.Instance,
            handle,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!bound.Accepted || !bound.Committed)
        {
            throw new InvalidOperationException("The native worker dispatch could not be durably bound.");
        }

        lock (_sync)
        {
            if (_session != session || _activeHandle is not null)
            {
                throw new InvalidOperationException("The native worker session changed before durable delivery.");
            }

            _activeHandle = handle;
            _completionScript = completionScript;
        }

        try
        {
            await DeliverToSessionAsync(session, handle, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await MakeActiveHandleUncertainAsync(session.Instance, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DeliverToSessionAsync(NativeWorkerPipeSession session, GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        await session.WriteAsync(new NativeWorkerFrame(
            NativeWorkerFrameKind.Dispatch,
            session.Instance.ProtocolVersion,
            session.Instance.InstanceId,
            session.SessionNonce,
            handle), cancellationToken).ConfigureAwait(false);
        if (_options.TestInstruction is { } instruction)
        {
            await session.WriteAsync(new NativeWorkerFrame(
                NativeWorkerFrameKind.TestInstruction,
                session.Instance.ProtocolVersion,
                session.Instance.InstanceId,
                session.SessionNonce,
                TestInstruction: instruction), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObserveWorkerAsync(
        NativeWorkerPipeServer pipe,
        Process process,
        NativeWorkerInstanceHandle instance,
        string executableFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            using (process)
            {
                var pipeName = pipe.PipeName;
                NativeWorkerPipeServer? nextServer = pipe;
                while (!cancellationToken.IsCancellationRequested)
                {
                    await using var server = nextServer!;
                    nextServer = null;
                    var accepting = server.AcceptAsync(instance, cancellationToken);
                    var timeout = Task.Delay(_options.ConnectTimeout, _timeProvider, cancellationToken);
                    var exited = process.WaitForExitAsync(cancellationToken);
                    var connectionObservation = await Task.WhenAny(accepting, timeout, exited).ConfigureAwait(false);
                    if (connectionObservation == exited)
                    {
                        var uncertainty = await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                        if (uncertainty is { Accepted: false } or { Committed: false })
                        {
                            return;
                        }

                        await RecordExitAsync(CreateDeterministicOperationId("exit", instance.InstanceId, Guid.Empty), instance.InstanceId, _timeProvider.GetUtcNow(), process.ExitCode, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    if (connectionObservation != accepting)
                    {
                        var uncertainty = await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                        if (uncertainty is { Accepted: false } or { Committed: false })
                        {
                            return;
                        }

                        await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.Lost, _timeProvider.GetUtcNow(), null, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    var session = await accepting.ConfigureAwait(false);
                    if (session is null)
                    {
                        await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.IdentityMismatch, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var reconnectAfterSessionClose = false;
                    await using (session.ConfigureAwait(false))
                    {
                        var connected = await RecordConnectionAsync(CreateDeterministicOperationId("connection", instance.InstanceId, Guid.Empty), new NativeWorkerConnectionAttestation(instance, executableFingerprint), cancellationToken).ConfigureAwait(false);
                        if (!connected.Accepted)
                        {
                            return;
                        }

                        lock (_sync)
                        {
                            _session = session;
                            _readyObserved = false;
                        }
                        try
                        {
                            while (!cancellationToken.IsCancellationRequested)
                            {
                                using var frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                var nextFrame = session.ReadAsync(frameCancellation.Token);
                                var heartbeatDeadline = Task.Delay(_options.HeartbeatTimeout, _timeProvider, cancellationToken);
                                var observation = await Task.WhenAny(nextFrame, heartbeatDeadline, session.Disposed).ConfigureAwait(false);
                                if (observation == session.Disposed)
                                {
                                    frameCancellation.Cancel();
                                    try { await nextFrame.ConfigureAwait(false); } catch (Exception exception) when (exception is IOException or OperationCanceledException or EndOfStreamException or ObjectDisposedException) { }
                                    throw new IOException("The native worker session was disposed while awaiting a frame.");
                                }

                                if (observation != nextFrame)
                                {
                                    frameCancellation.Cancel();
                                    try { await nextFrame.ConfigureAwait(false); } catch (OperationCanceledException) { }
                                    await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.Unresponsive, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                                    var uncertainty = await MakeActiveHandleUncertainAsync(instance, cancellationToken).ConfigureAwait(false);
                                    if (uncertainty is { Accepted: true, Committed: true })
                                    {
                                        await ForceTerminateForControlledTestAsync(process, instance.InstanceId, cancellationToken).ConfigureAwait(false);
                                    }
                                    return;
                                }

                                frameCancellation.Cancel();
                                var frame = await nextFrame.ConfigureAwait(false);
                                if (!await ApplyFrameAsync(instance, frame, cancellationToken).ConfigureAwait(false))
                                {
                                    if (IsGracefulStopAttested())
                                    {
                                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                                        await RecordExitAsync(CreateDeterministicOperationId("exit", instance.InstanceId, Guid.Empty), instance.InstanceId, _timeProvider.GetUtcNow(), process.ExitCode, cancellationToken).ConfigureAwait(false);
                                    }

                                    return;
                                }
                            }
                        }
                        catch (Exception exception) when (exception is IOException or EndOfStreamException or ObjectDisposedException)
                        {
                            lock (_sync)
                            {
                                _session = null;
                                _readyObserved = false;
                            }
                            if (process.HasExited)
                            {
                                var uncertainty = await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                                if (uncertainty is { Accepted: false } or { Committed: false })
                                {
                                    return;
                                }

                                await RecordExitAsync(CreateDeterministicOperationId("exit", instance.InstanceId, Guid.Empty), instance.InstanceId, _timeProvider.GetUtcNow(), process.ExitCode, CancellationToken.None).ConfigureAwait(false);
                                return;
                            }

                            reconnectAfterSessionClose = true;
                        }
                    }

                    if (reconnectAfterSessionClose)
                    {
                        nextServer = new NativeWorkerPipeServer(pipeName);
                        continue;
                    }
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var uncertainty = await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
            if (uncertainty is { Accepted: false } or { Committed: false })
            {
                return;
            }

            await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.Exited, _timeProvider.GetUtcNow(), 3, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _session = null;
                _readyObserved = false;
            }
        }
    }

    private async Task<bool> ApplyFrameAsync(NativeWorkerInstanceHandle instance, NativeWorkerFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case NativeWorkerFrameKind.Ready:
                var readyObserved = false;
                lock (_sync)
                {
                    if (!_readyObserved)
                    {
                        _readyObserved = true;
                        readyObserved = true;
                    }
                }
                if (readyObserved)
                {
                    await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.Ready, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                }
                return true;
            case NativeWorkerFrameKind.Heartbeat:
                await RecordHeartbeatAsync(Guid.NewGuid(), instance.InstanceId, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return true;
            case NativeWorkerFrameKind.Acknowledgement when frame.Handle is not null:
                if (!IsExactActiveHandle(frame.Handle)) return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
                await AcknowledgeAndBindAsync(instance, frame.Handle, cancellationToken).ConfigureAwait(false);
                return true;
            case NativeWorkerFrameKind.Receipt when frame.Handle is not null && frame.Disposition == NativeWorkerTaskDisposition.Completed:
                if (!IsExactActiveHandle(frame.Handle) || _completionScript is null) return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
                foreach (var receipt in _completionScript.Receipts)
                {
                    var recorded = await RecordReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
                    if (!recorded.Accepted || !recorded.Committed)
                    {
                        await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                        return false;
                    }
                }
                return true;
            case NativeWorkerFrameKind.Callback:
                if (_completionScript is null || !HasActiveHandle()) return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
                var callback = await HandleCallbackAsync(_completionScript.CallbackOperationId, _completionScript.Callback, cancellationToken).ConfigureAwait(false);
                if (!callback.Accepted || !callback.Committed)
                {
                    await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                var cleared = await ClearExactActiveDispatchAsync(CreateDeterministicOperationId("completion-clear", instance.InstanceId, _completionScript.Callback.Handle.DispatchId), instance, _completionScript.Callback.Handle, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (!cleared.Accepted || !cleared.Committed)
                {
                    await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                lock (_sync) { _activeHandle = null; _completionScript = null; }
                return true;
            case NativeWorkerFrameKind.Stopped:
                if (!IsGracefulStopRequested()) return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
                var confirmed = await AppendEvidenceAsync(instance.InstanceId, NativeWorkerLifecycleClass.GracefulStopConfirmed, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                if (!confirmed.Accepted || !confirmed.Committed) return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
                lock (_sync) { _gracefulStopAttested = true; }
                return false;
            default:
                return await RejectFrameAndMakeActiveHandleUncertainAsync(instance).ConfigureAwait(false);
        }
    }

    private async Task<bool> RejectFrameAndMakeActiveHandleUncertainAsync(NativeWorkerInstanceHandle instance)
    {
        var uncertainty = await MakeActiveHandleUncertainAsync(instance, CancellationToken.None).ConfigureAwait(false);
        if (uncertainty is null)
        {
            return false;
        }

        // A protocol-rejected frame may end this observation only after the exact active
        // dispatch is durably fenced as uncertain. Otherwise retain the observation fence.
        return uncertainty is not { Accepted: true, Committed: true };
    }

    private bool IsGracefulStopRequested()
    {
        lock (_sync)
        {
            return _gracefulStopRequested && _activeHandle is null;
        }
    }

    private bool IsGracefulStopAttested()
    {
        lock (_sync)
        {
            return _gracefulStopAttested;
        }
    }

    private bool IsExactActiveHandle(GpuExecutorBatchHandle handle)
    {
        lock (_sync)
        {
            return _activeHandle == handle;
        }
    }

    private bool HasActiveHandle()
    {
        lock (_sync)
        {
            return _activeHandle is not null;
        }
    }

    private async Task<NativeWorkerStoreMutationResult?> MakeActiveHandleUncertainAsync(NativeWorkerInstanceHandle instance, CancellationToken cancellationToken)
    {
        GpuExecutorBatchHandle? active;
        lock (_sync)
        {
            active = _activeHandle;
        }

        if (active is not null)
        {
            var uncertainty = await MarkExactHandleUncertainAsync(
                CreateDeterministicOperationId("uncertain", instance.InstanceId, active.DispatchId),
                instance,
                active,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (uncertainty.Accepted && uncertainty.Committed)
            {
                lock (_sync)
                {
                    if (_activeHandle == active)
                    {
                        _activeHandle = null;
                    }
                }
            }

            return uncertainty;
        }

        return null;
    }

    private async Task<bool> ReconcilePriorCandidatesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NativeWorkerRecoveryCandidate> candidates;
        try
        {
            candidates = await ReadRecoveryCandidatesAsync(_options.ExecutorKey!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (candidates.Count == 0)
        {
            return true;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                candidate.Validate(_options.ExecutorKey!);
                if (candidate.ActiveHandle is not null)
                {
                    if (candidate.AttestedInstance is null)
                    {
                        return false;
                    }

                    var uncertain = await MarkExactHandleUncertainAsync(
                        CreateDeterministicOperationId("recovery-uncertain", candidate.InstanceId, candidate.ActiveHandle.DispatchId),
                        candidate.AttestedInstance,
                        candidate.ActiveHandle,
                        _timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    if (!uncertain.Accepted)
                    {
                        return false;
                    }
                }
                else if (candidate.State != NativeWorkerLifecycleClass.Lost)
                {
                    var lost = await AppendEvidenceAsync(new NativeWorkerLifecycleEvidence(
                        CreateDeterministicOperationId("recovery-lost", candidate.InstanceId, Guid.Empty),
                        candidate.InstanceId,
                        NativeWorkerLifecycleClass.Lost,
                        _timeProvider.GetUtcNow(),
                        null,
                        CreateFingerprint("recovery-lost", candidate.InstanceId.ToString("N"))), cancellationToken).ConfigureAwait(false);
                    if (!lost.Accepted)
                    {
                        return false;
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        // A prior potentially-live row remains a durable recovery fence even after its exact
        // uncertainty/lost observation is appended. This host neither adopts nor replaces it.
        return false;
    }

    private async Task ForceTerminateForControlledTestAsync(Process process, Guid instanceId, CancellationToken cancellationToken)
    {
        if (!_options.AllowForcedTerminationForControlledTests)
        {
            return;
        }

        await AppendEvidenceAsync(instanceId, NativeWorkerLifecycleClass.TerminationRequested, _timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendEvidenceAsync(instanceId, NativeWorkerLifecycleClass.TerminationConfirmed, _timeProvider.GetUtcNow(), process.ExitCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await AppendEvidenceAsync(instanceId, NativeWorkerLifecycleClass.TerminationFailed, _timeProvider.GetUtcNow(), 4, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask<NativeWorkerStoreMutationResult> AppendEvidenceAsync(Guid instanceId, NativeWorkerLifecycleClass lifecycleClass, DateTimeOffset observedAtUtc, int? outcomeCode, CancellationToken cancellationToken) =>
        await AppendEvidenceAsync(new NativeWorkerLifecycleEvidence(
            Guid.NewGuid(), instanceId, lifecycleClass, observedAtUtc, outcomeCode,
            CreateFingerprint(lifecycleClass.ToString(), instanceId.ToString("N"), outcomeCode?.ToString() ?? string.Empty)), cancellationToken).ConfigureAwait(false);

    private async ValueTask<NativeWorkerStoreMutationResult> CreateAsync(Guid operationId, NativeWorkerLaunchRequest launch, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.CreateAsync(operationId, launch, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().CreateAsync(operationId, launch, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<NativeWorkerRecoveryCandidate>> ReadRecoveryCandidatesAsync(string executorKey, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.ReadRecoveryCandidatesAsync(executorKey, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().ReadRecoveryCandidatesAsync(executorKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> AppendEvidenceAsync(NativeWorkerLifecycleEvidence evidence, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.AppendEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().AppendEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> RecordConnectionAsync(Guid operationId, NativeWorkerConnectionAttestation attestation, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.RecordConnectionAsync(operationId, attestation, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().RecordConnectionAsync(operationId, attestation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> RecordHeartbeatAsync(Guid operationId, Guid instanceId, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.RecordHeartbeatAsync(operationId, instanceId, observedAtUtc, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().RecordHeartbeatAsync(operationId, instanceId, observedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> RecordExitAsync(Guid operationId, Guid instanceId, DateTimeOffset observedAtUtc, int? exitCode, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.RecordExitAsync(operationId, instanceId, observedAtUtc, exitCode, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().RecordExitAsync(operationId, instanceId, observedAtUtc, exitCode, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> BindExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.BindExactActiveDispatchAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().BindExactActiveDispatchAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcknowledgeAndBindAsync(NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            await _lifecycleSink!.AcknowledgeAsync(new GpuExecutorAcknowledgement(CreateDeterministicOperationId("acknowledgement", instance.InstanceId, handle.DispatchId), handle), cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var scope = _scopeFactory!.CreateAsyncScope();
        var lifecycleSink = scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>();
        var store = scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>();
        await lifecycleSink.AcknowledgeAsync(new GpuExecutorAcknowledgement(CreateDeterministicOperationId("acknowledgement", instance.InstanceId, handle.DispatchId), handle), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt receipt, CancellationToken cancellationToken)
    {
        if (_lifecycleSink is not null) return await _lifecycleSink.RecordReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>().RecordReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken)
    {
        if (_lifecycleSink is not null) return await _lifecycleSink.HandleCallbackAsync(operationId, callback, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>().HandleCallbackAsync(operationId, callback, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> ClearExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.ClearExactActiveDispatchAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().ClearExactActiveDispatchAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> MarkExactHandleUncertainAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (_store is not null) return await _store.MarkExactHandleUncertainAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
        await using var scope = _scopeFactory!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INativeWorkerInstanceStore>().MarkExactHandleUncertainAsync(operationId, instance, handle, observedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string pipeName, Guid instanceId, string protocolVersion, string? postReadyReadSignalName)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // The deterministic child has no configuration surface. Do not inherit ambient settings,
        // credentials, or other host process data into its environment.
        startInfo.Environment.Clear();
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(instanceId.ToString("D"));
        startInfo.ArgumentList.Add("--protocol-version");
        startInfo.ArgumentList.Add(protocolVersion);
        if (!string.IsNullOrWhiteSpace(postReadyReadSignalName))
        {
            startInfo.Environment["FLUX_NATIVE_WORKER_TEST_POST_READY_READ_EVENT"] = postReadyReadSignalName;
        }
        return startInfo;
    }

    private static string CreateExecutableFingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CreateFingerprint(params string[] fields) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", fields)))).ToLowerInvariant();

    private static Guid CreateDeterministicOperationId(string kind, Guid instanceId, Guid dispatchId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{instanceId:N}|{dispatchId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public interface INativeWorkerProcessLauncher
{
    Process Start(ProcessStartInfo startInfo);
}

public sealed class NativeWorkerProcessLauncher : INativeWorkerProcessLauncher
{
    public Process Start(ProcessStartInfo startInfo) =>
        Process.Start(startInfo) ?? throw new InvalidOperationException("The deterministic native worker process could not be started.");
}
