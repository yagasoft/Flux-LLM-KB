using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Gpu;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FluxKnowledge.Infrastructure.Inference.Search;

// GPU sessions require an executor-issued ownership context. Session disposal
// precedes verified-file release, including on failure. No provider acquisition.
internal sealed class BgeOnnxTensorRunner : IBgeTensorRunner
{
    private readonly object _sync = new();
    private readonly VerifiedLocalModelLease _lease;
    private readonly InferenceSession _session;
    private readonly string _output;
    private readonly GpuOwnedWorkContext? _ownership;
    private readonly GpuOwnedWorkContext.NativeAllocation? _allocation;
    private bool _disposed;

    private BgeOnnxTensorRunner(VerifiedLocalModelLease lease, InferenceSession session, string output, GpuOwnedWorkContext? ownership,
        GpuOwnedWorkContext.NativeAllocation? allocation)
    {
        _lease = lease; _session = session; _output = output; _ownership = ownership; _allocation = allocation;
    }

    internal static BgeOnnxTensorRunner Open(VerifiedLocalModelLease lease, bool embedding, CancellationToken cancellationToken,
        GpuOwnedWorkContext? ownership = null, int cpuIntraOpThreads = 4)
    {
        InferenceSession? session = null;
        GpuOwnedWorkContext.NativeAllocation? allocation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownership?.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
            var graph = lease.GetVerifiedLocalPath("model.onnx");
            var data = lease.GetVerifiedLocalPath("model.onnx_data");
            if (!string.Equals(Path.GetDirectoryName(graph), Path.GetDirectoryName(data), StringComparison.OrdinalIgnoreCase))
                throw new BgeInferenceException("bge-external-data-directory-mismatch");
            using var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = ownership is null ? cpuIntraOpThreads : 4,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (ownership is not null)
            {
                options.EnableMemoryPattern = false;
                allocation = ownership.BeginNativeAllocation();
                options.AppendExecutionProvider_DML(0);
            }
            session = new InferenceSession(graph, options);
            cancellationToken.ThrowIfCancellationRequested();
            ownership?.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
            var output = embedding ? "sentence_embedding" : "logits";
            if (!session.InputMetadata.Keys.Order().SequenceEqual(new[] { "attention_mask", "input_ids" }) ||
                session.InputMetadata.Values.Any(static m => m.ElementType != typeof(long) || m.Dimensions.Length != 2) ||
                !session.OutputMetadata.TryGetValue(output, out var meta) || meta.ElementType != typeof(float) || meta.Dimensions.Length != 2)
                throw new BgeInferenceException("bge-model-tensor-contract-mismatch");
            return new(lease, session, output, ownership, allocation);
        }
        catch (Exception failure)
        {
            DisposeFailedOpen(failure, session, lease, ownership is null, allocation);
            throw;
        }
    }

    internal static void DisposeFailedOpen(Exception failure, IDisposable? session,
        VerifiedLocalModelLease lease, bool cpu, GpuOwnedWorkContext.NativeAllocation? allocation = null)
    {
        if (session is null)
        {
            lease.Dispose();
            return;
        }
        try { DisposeSession(session, lease, cpu, allocation); }
        catch (CpuNativeReleaseUnconfirmedException cleanup)
        {
            // Preserve both the opening failure and the unconfirmed close outcome.
            throw new CpuNativeReleaseUnconfirmedException(failure, cleanup);
        }
    }

    public BgeTensorOutput Run(BgeInputBatch batch, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _ownership?.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
            using var run = new RunOptions();
            using var registration = cancellationToken.Register(() => run.Terminate = true);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(batch.InputIds, [batch.Count, batch.SequenceLength])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(batch.AttentionMask, [batch.Count, batch.SequenceLength]))
            };
            try
            {
                using var outputs = _session.Run(inputs, [_output], run);
                cancellationToken.ThrowIfCancellationRequested();
                var tensor = outputs.Single().AsTensor<float>();
                return new(tensor.ToArray(), tensor.Dimensions.ToArray());
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeSession(_session, _lease, _ownership is null, _allocation);
        }
    }

    internal static void DisposeSession(IDisposable session, VerifiedLocalModelLease lease,
        bool cpu, GpuOwnedWorkContext.NativeAllocation? allocation = null)
    {
        var retained = false;
        try { session.Dispose(); allocation?.ConfirmReleased(); }
        catch (Exception failure) when (cpu)
        {
            retained = true;
            CpuResidentPassageLoad.RetainUncertainOwner(lease);
            throw new CpuNativeReleaseUnconfirmedException(failure);
        }
        finally { if (!retained) lease.Dispose(); }
    }
}
