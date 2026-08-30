using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1.Operations;

public sealed record NativeOperationsStatus(string View, Guid? RootId, Guid? JobId, int Limit);

public sealed class NativeOperationsStatusService(INativeV1ProjectionReader reader)
{
    private static readonly HashSet<string> Views = new(StringComparer.Ordinal)
    {
        "overview", "sources", "jobs", "workers", "processors", "recovery"
    };

    public ValueTask<object> ExecuteAsync(NativeOperationsStatus request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = request.View?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(view) || !Views.Contains(view)) throw new NativeOperationException("view-not-allowed");
        if (request.Limit is < 1 or > 100) throw new NativeOperationException("invalid-limit");
        return reader.ReadStatusAsync(request with { View = view }, cancellationToken);
    }
}
