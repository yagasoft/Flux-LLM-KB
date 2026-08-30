using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1.Code;

public sealed record NativeCodeQuery(string View, string? Query, Guid? BranchId, int Limit, string? Cursor)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public NativeV1CursorPosition? Continuation { get; init; }
}

public sealed class NativeCodeQueryService(INativeV1ProjectionReader reader, INativeV1CursorCodec cursorCodec)
{
    private static readonly HashSet<string> Views = new(StringComparer.Ordinal) { "status", "symbols", "matches" };
    public ValueTask<object> ExecuteAsync(NativeCodeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var view = query.View?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(view) || !Views.Contains(view)) throw new NativeOperationException("view-not-allowed");
        if (query.Limit is < 1 or > 100) throw new NativeOperationException("invalid-limit");
        var canonicalQuery = view == "matches"
            ? NativeV1ContractLimits.CanonicalizeCodeQuery(query.Query)
            : NativeV1ContractLimits.CanonicalizeOptionalCodeQuery(query.Query);
        var canonical = query with
        {
            View = view,
            Query = canonicalQuery,
            Limit = query.Limit,
            Continuation = null
        };
        if (canonical.Cursor is not null)
        {
            if (view == "status") throw new NativeOperationException("cursor-invalid");
            canonical = canonical with { Continuation = cursorCodec.Decode(NativeV1CursorBindings.Code(canonical), canonical.Cursor) };
        }
        return reader.ReadCodeAsync(canonical, cancellationToken);
    }
}
