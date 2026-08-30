using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1.Operations;

public sealed record NativeAuditQuery(string View, Guid? RootId, Guid? JobId, int Limit, string? Cursor)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public NativeV1CursorPosition? Continuation { get; init; }
}

public sealed class NativeAuditQueryService(INativeV1ProjectionReader reader, INativeV1CursorCodec cursorCodec)
{
    public ValueTask<object> ExecuteAsync(NativeAuditQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.View?.Trim(), "events", StringComparison.OrdinalIgnoreCase)) throw new NativeOperationException("view-not-allowed");
        if (request.Limit is < 1 or > 100) throw new NativeOperationException("invalid-limit");
        var canonical = request with { View = "events", Continuation = null };
        if (canonical.Cursor is not null)
        {
            canonical = canonical with { Continuation = cursorCodec.Decode(NativeV1CursorBindings.Audit(canonical), canonical.Cursor) };
        }
        return reader.ReadAuditAsync(canonical, cancellationToken);
    }
}
