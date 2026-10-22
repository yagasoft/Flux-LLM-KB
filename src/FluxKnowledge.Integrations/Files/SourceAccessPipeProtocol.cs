using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class SourceAccessPipeProtocol
{
    internal const int MaximumFrameLength = 256 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    // Windows also requires ReadAttributes when opening a client pipe, even
    // when CreateFile's explicit desired-access mask omits that bit.
    internal const PipeAccessRights ClientRights = PipeAccessRights.ReadData | PipeAccessRights.WriteData |
        PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    internal sealed record Request(int Version, long DeadlineUtcTicks, SourceAccessRepairRequest Item);
    internal sealed record Response(int Version, SourceAccessRepairOutcome Outcome);

    internal static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128 ||
            !pipeName.StartsWith("FluxKnowledge.SourceAccess.", StringComparison.Ordinal) ||
            pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("A local FluxKnowledge source-access pipe name is required.", nameof(pipeName));
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length is 0 or > MaximumFrameLength) throw new InvalidDataException("SourceAccessFrameInvalid");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameLength) throw new InvalidDataException("SourceAccessFrameInvalid");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("SourceAccessFrameInvalid");
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("SourceAccessDuplicateProperty");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
