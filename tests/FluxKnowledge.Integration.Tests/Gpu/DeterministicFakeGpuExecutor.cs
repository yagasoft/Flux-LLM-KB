using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Integration.Tests.Gpu;

public enum DeterministicFakeGpuExecutorMode
{
    Acknowledge,
    Drop,
    Unresponsive
}

public abstract record DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorAcknowledgementStep(
    GpuExecutorAcknowledgement Request) : DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorDeliveryUncertaintyStep(
    GpuExecutorDeliveryUncertainty Request) : DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorReceiptStep(
    GpuExecutorResultReceipt Request) : DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorTrustedEvidenceStep(
    GpuExecutorTrustedEvidence Request) : DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorCallbackStep(
    Guid OperationId,
    GpuBatchCallback Callback) : DeterministicFakeGpuExecutorStep;

public sealed record DeterministicFakeGpuExecutorScriptResult(
    DeterministicFakeGpuExecutorStep Step,
    bool Accepted,
    bool Committed);

/// <summary>
/// Test-only adapter that exercises the private lifecycle boundary without any process,
/// runtime, file, network, model, GPU, or direct persistence access.
/// </summary>
public sealed class DeterministicFakeGpuExecutor : IGpuExecutorAdapter
{
    private readonly object _sync = new();
    private readonly IGpuExecutorLifecycleSink _lifecycleSink;
    private readonly IReadOnlyList<DeterministicFakeGpuExecutorStep>? _script;
    private readonly DeterministicFakeGpuExecutorMode? _mode;
    private readonly Func<Guid>? _operationIdFactory;
    private readonly List<GpuExecutorBatchHandle> _deliveredHandles = [];
    private readonly List<DeterministicFakeGpuExecutorScriptResult> _scriptResults = [];

    public DeterministicFakeGpuExecutor(
        string executorKey,
        IGpuExecutorLifecycleSink lifecycleSink,
        IReadOnlyList<DeterministicFakeGpuExecutorStep> script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executorKey);
        ArgumentNullException.ThrowIfNull(lifecycleSink);
        ArgumentNullException.ThrowIfNull(script);
        ExecutorKey = executorKey;
        _lifecycleSink = lifecycleSink;
        _script = script.ToArray();
    }

    public DeterministicFakeGpuExecutor(
        string executorKey,
        IGpuExecutorLifecycleSink lifecycleSink,
        DeterministicFakeGpuExecutorMode mode,
        Func<Guid> operationIdFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executorKey);
        ArgumentNullException.ThrowIfNull(lifecycleSink);
        ArgumentNullException.ThrowIfNull(operationIdFactory);
        ExecutorKey = executorKey;
        _lifecycleSink = lifecycleSink;
        _mode = mode;
        _operationIdFactory = operationIdFactory;
    }

    public TaskCompletionSource DeliveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DeliveryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ExecutorKey { get; }

    public IReadOnlyList<GpuExecutorBatchHandle> DeliveredHandles
    {
        get
        {
            lock (_sync)
            {
                return _deliveredHandles.ToArray();
            }
        }
    }

    public IReadOnlyList<DeterministicFakeGpuExecutorScriptResult> ScriptResults
    {
        get
        {
            lock (_sync)
            {
                return _scriptResults.ToArray();
            }
        }
    }

    public async ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        handle.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _deliveredHandles.Add(handle);
        }
        DeliveryStarted.TrySetResult();

        if (_script is not null)
        {
            foreach (var step in _script)
            {
                var result = await ExecuteStepAsync(step, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _scriptResults.Add(result);
                }
            }

            return;
        }

        switch (_mode)
        {
            case DeterministicFakeGpuExecutorMode.Acknowledge:
                await _lifecycleSink
                    .AcknowledgeAsync(new GpuExecutorAcknowledgement(_operationIdFactory!(), handle), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case DeterministicFakeGpuExecutorMode.Drop:
                return;
            case DeterministicFakeGpuExecutorMode.Unresponsive:
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DeliveryCancelled.TrySetResult();
                    throw;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(_mode));
        }
    }

    private async ValueTask<DeterministicFakeGpuExecutorScriptResult> ExecuteStepAsync(
        DeterministicFakeGpuExecutorStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step switch
        {
            DeterministicFakeGpuExecutorAcknowledgementStep acknowledgement =>
                ToResult(step, await _lifecycleSink.AcknowledgeAsync(acknowledgement.Request, cancellationToken).ConfigureAwait(false)),
            DeterministicFakeGpuExecutorDeliveryUncertaintyStep uncertainty =>
                ToResult(step, await _lifecycleSink.MarkDeliveryUncertainAsync(uncertainty.Request, cancellationToken).ConfigureAwait(false)),
            DeterministicFakeGpuExecutorReceiptStep receipt =>
                ToResult(step, await _lifecycleSink.RecordReceiptAsync(receipt.Request, cancellationToken).ConfigureAwait(false)),
            DeterministicFakeGpuExecutorTrustedEvidenceStep evidence =>
                ToResult(step, await _lifecycleSink.RecordTrustedEvidenceAsync(evidence.Request, cancellationToken).ConfigureAwait(false)),
            DeterministicFakeGpuExecutorCallbackStep callback =>
                ToResult(step, await _lifecycleSink.HandleCallbackAsync(callback.OperationId, callback.Callback, cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
    }

    private static DeterministicFakeGpuExecutorScriptResult ToResult(
        DeterministicFakeGpuExecutorStep step,
        GpuExecutorDispatchMutationResult result) => new(step, result.Accepted, result.Committed);

    private static DeterministicFakeGpuExecutorScriptResult ToResult(
        DeterministicFakeGpuExecutorStep step,
        GpuBatchCallbackResult result) => new(step, result.Accepted, result.Committed);
}
