using FluxKnowledge.Application.Gpu;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Singleton native adapters call the existing scoped lifecycle without capturing a scope.</summary>
public sealed class ScopedGpuExecutorLifecycleSink(IServiceScopeFactory scopes) : IGpuExecutorLifecycleSink
{
    private async ValueTask<T> InvokeAsync<T>(Func<IGpuExecutorLifecycleSink, ValueTask<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>()).ConfigureAwait(false);
    }
    public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement request, CancellationToken ct) =>
        InvokeAsync(sink => sink.AcknowledgeAsync(request, ct));
    public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty request, CancellationToken ct) =>
        InvokeAsync(sink => sink.MarkDeliveryUncertainAsync(request, ct));
    public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt request, CancellationToken ct) =>
        InvokeAsync(sink => sink.RecordReceiptAsync(request, ct));
    public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence request, CancellationToken ct) =>
        InvokeAsync(sink => sink.RecordTrustedEvidenceAsync(request, ct));
    public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operationId, GpuBatchCallback request, CancellationToken ct) =>
        InvokeAsync(sink => sink.HandleCallbackAsync(operationId, request, ct));
}
