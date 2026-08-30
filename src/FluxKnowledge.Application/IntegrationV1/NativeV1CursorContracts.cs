using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Canonical query identity authenticated by a native-v1 continuation.</summary>
public sealed record NativeV1CursorBinding(
    string Family,
    string View,
    string CanonicalFilters,
    string Ordering,
    int PageLimit);

/// <summary>Exclusive keyset position carried only inside a protected native-v1 continuation.</summary>
public sealed record NativeV1CursorPosition(
    Guid? Id = null,
    DateTimeOffset? Timestamp = null,
    int? Ordinal = null,
    string? Text = null,
    string? SecondaryText = null,
    string? TertiaryText = null,
    long? Sequence = null);

/// <summary>Protects and validates query-bound native-v1 continuation state without persistence access.</summary>
public interface INativeV1CursorCodec
{
    string Encode(NativeV1CursorBinding binding, NativeV1CursorPosition position);
    NativeV1CursorPosition Decode(NativeV1CursorBinding binding, string cursor);
}

public static class NativeV1CursorBindings
{
    public static NativeV1CursorBinding Code(Code.NativeCodeQuery query) => new(
        "code",
        query.View,
        JsonSerializer.Serialize(new
        {
            queryFingerprint = query.Query is null
                ? null
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    NativeV1ContractLimits.CanonicalizeOptionalCodeQuery(query.Query)!))),
            branchId = GuidText(query.BranchId)
        }),
        "document-id:asc,ordinal:asc",
        query.Limit);

    public static NativeV1CursorBinding Corpus(Corpus.NativeCorpusQuery query) => new(
        "corpus",
        query.View,
        JsonSerializer.Serialize(new
        {
            rootId = GuidText(query.RootId),
            branchId = GuidText(query.BranchId),
            jobId = GuidText(query.JobId)
        }),
        query.View switch
        {
            "roots" => "id:asc",
            "assets" => "discovered-at:desc,id:desc",
            "branches" => "updated-at:desc,id:desc",
            "processors" => "id:asc",
            "jobs" => "updated-at:desc,id:desc",
            _ => "not-pageable"
        },
        query.Limit);

    public static NativeV1CursorBinding Audit(Operations.NativeAuditQuery query) => new(
        "operations.audit",
        query.View,
        JsonSerializer.Serialize(new
        {
            rootId = GuidText(query.RootId),
            jobId = GuidText(query.JobId)
        }),
        "occurred-at:desc,id:desc",
        query.Limit);

    public static bool IsPageableCorpusView(string view) => view is "roots" or "assets" or "branches" or "processors" or "jobs";

    private static string? GuidText(Guid? value) => value?.ToString("D", CultureInfo.InvariantCulture);
}
