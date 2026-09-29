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
        catch
        {
            try
            {
                if (session is not null)
                {
                    session.Dispose();
                    allocation?.ConfirmReleased();
                }
            }
            finally { lease.Dispose(); }
            throw;
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
            try { _session.Dispose(); _allocation?.ConfirmReleased(); }
            finally { _lease.Dispose(); }
        }
    }
}
