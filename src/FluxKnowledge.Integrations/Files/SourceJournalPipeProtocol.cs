using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

internal static class SourceJournalPipeProtocol
{
    internal const int MaximumFrameBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, MaxDepth = 12
    };
    internal sealed record Request(int Version, long DeadlineUtcTicks, string Operation, JournalCursor Cursor, int MaximumBytes);
    internal sealed record Response(int Version, JournalCapability? Capability, JournalPage? Page, string? Error);

    internal static bool IsAllowed(Request request, string approvedVolume) => request.Version == 1 &&
        request.Operation is "query" or "read" && request.Cursor.VolumeIdentity.Equals(approvedVolume, StringComparison.OrdinalIgnoreCase) &&
        request.Cursor.NextUsn >= 0 && request.MaximumBytes is >= 4096 and <= 1024 * 1024 &&
        request.DeadlineUtcTicks > DateTimeOffset.UtcNow.UtcTicks && request.DeadlineUtcTicks <= DateTimeOffset.UtcNow.Add(Timeout).UtcTicks;

    internal static void ValidatePipeName(string name)
    {
        if (!name.StartsWith("FluxKnowledge.SourceJournal.", StringComparison.Ordinal) || name.Length > 128 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')) throw new ArgumentException("SourceJournalPipeInvalid");
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("SourceJournalFrameInvalid");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("SourceJournalFrameInvalid");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("SourceJournalFrameInvalid");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("SourceJournalDuplicateProperty");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
