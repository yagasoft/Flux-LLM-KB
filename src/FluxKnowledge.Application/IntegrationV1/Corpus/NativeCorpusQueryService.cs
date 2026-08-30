using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1.Corpus;

public sealed record NativeCorpusQuery(string View, Guid? RootId, Guid? BranchId, Guid? JobId, int Limit, string? Cursor)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public NativeV1CursorPosition? Continuation { get; init; }
}

public sealed class NativeCorpusQueryService(INativeV1ProjectionReader reader, INativeV1CursorCodec cursorCodec)
{
    private static readonly HashSet<string> Views = new(StringComparer.Ordinal)
    {
        "roots", "assets", "branches", "processors", "jobs", "detail"
    };

    public ValueTask<object> ExecuteAsync(NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateView(query.View, query.Limit);
        var canonical = query with { View = query.View.Trim().ToLowerInvariant(), Continuation = null };
        if (canonical.Cursor is not null)
        {
            if (!NativeV1CursorBindings.IsPageableCorpusView(canonical.View)) throw new NativeOperationException("cursor-invalid");
            canonical = canonical with { Continuation = cursorCodec.Decode(NativeV1CursorBindings.Corpus(canonical), canonical.Cursor) };
        }
        return reader.ReadCorpusAsync(canonical, cancellationToken);
    }

    internal static void ValidateView(string? view, int limit)
    {
        if (string.IsNullOrWhiteSpace(view) || !Views.Contains(view.Trim().ToLowerInvariant())) throw new NativeOperationException("view-not-allowed");
        if (limit is < 1 or > 100) throw new NativeOperationException("invalid-limit");
    }
}
