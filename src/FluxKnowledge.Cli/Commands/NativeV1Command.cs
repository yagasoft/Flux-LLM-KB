using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Thin, loopback-only CLI bindings for the nine native v1 operations.</summary>
public static class NativeV1Command
{
    public static readonly Uri LoopbackBaseAddress = new("http://127.0.0.1:5137/");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> ExecuteFromEnvironmentAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        using var handler = CreateLoopbackTransport();
        using var client = new HttpClient(handler) { BaseAddress = LoopbackBaseAddress };
        return await ExecuteAsync(args, input, client, output, error, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextReader input,
        HttpClient client,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!IsLoopbackClient(client)) return await WriteFailureAsync(output, "loopback-required").ConfigureAwait(false);

        var command = ParseCommand(args);
        if (command is null) return await WriteFailureAsync(output, "invalid-command").ConfigureAwait(false);

        JsonObject body;
        try
        {
            var requestText = await ReadBoundedTextAsync(
                input,
                NativeV1ContractLimits.MaximumRequestBytes,
                cancellationToken).ConfigureAwait(false);
            if (requestText is null) return await WriteFailureAsync(output, "body-too-large").ConfigureAwait(false);
            body = (JsonNode.Parse(requestText) as JsonObject)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return await WriteFailureAsync(output, "invalid-json").ConfigureAwait(false);
        }

        if (command.IsMutation)
        {
            var mode = MutationMode(args);
            if (mode is null) return await WriteFailureAsync(output, "invalid-mode").ConfigureAwait(false);
            if (mode == "commit")
            {
                var confirmation = Argument(args, "--confirmation-id");
                if (string.IsNullOrWhiteSpace(confirmation)) return await WriteFailureAsync(output, "confirmation-required").ConfigureAwait(false);
                var idempotency = Argument(args, "--idempotency-key");
                if (string.IsNullOrWhiteSpace(idempotency)) return await WriteFailureAsync(output, "idempotency-key-required").ConfigureAwait(false);
                body["confirmation_id"] = confirmation;
                command = command with { Method = HttpMethod.Post, Path = command.Path + "/commit", IdempotencyKey = idempotency };
            }
            else
            {
                command = command with { Method = HttpMethod.Post, Path = command.Path + "/preview" };
            }
        }

        if (command.IsStatus)
        {
            command = command with { Path = StatusPath(body) };
        }

        using var request = new HttpRequestMessage(command.Method, command.Path);
        if (command.Method != HttpMethod.Get)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }

        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", command.IdempotencyKey);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) return await WriteFailureAsync(output, "redirect-refused").ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > NativeV1ContractLimits.MaximumResponseBytes)
            {
                return await WriteFailureAsync(output, "invalid-response").ConfigureAwait(false);
            }
            var responseText = await ReadBoundedUtf8Async(
                response.Content,
                NativeV1ContractLimits.MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            if (responseText is null) return await WriteFailureAsync(output, "invalid-response").ConfigureAwait(false);
            if (!NativeV1EnvelopeProtector.TryRead(responseText, out var protectedEnvelope)) return await WriteFailureAsync(output, "invalid-response").ConfigureAwait(false);
            await output.WriteLineAsync(protectedEnvelope).ConfigureAwait(false);
            using var envelope = JsonDocument.Parse(protectedEnvelope);
            return envelope.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return await WriteFailureAsync(output, "temporary-unavailable", retryable: true).ConfigureAwait(false);
        }
    }

    private static Command? ParseCommand(string[] args)
    {
        if (args.Length < 2) return null;
        return (args[0], args[1]) switch
        {
            ("knowledge", "search") => new(HttpMethod.Post, "api/v1/knowledge/search", false, false, null),
            ("knowledge", "write") => new(HttpMethod.Post, "api/v1/knowledge/actions", true, false, null),
            ("knowledge", "graph") => new(HttpMethod.Post, "api/v1/knowledge/graph/query", false, false, null),
            ("code", "query") => new(HttpMethod.Post, "api/v1/code/query", false, false, null),
            ("code", "feedback") => new(HttpMethod.Post, "api/v1/code/actions", true, false, null),
            ("corpus", "query") => new(HttpMethod.Post, "api/v1/corpus/query", false, false, null),
            ("corpus", "write") => new(HttpMethod.Post, "api/v1/corpus/actions", true, false, null),
            ("operations", "status") => new(HttpMethod.Get, "api/v1/operations/status", false, true, null),
            ("operations", "audit") => new(HttpMethod.Post, "api/v1/operations/audit/query", false, false, null),
            _ => null
        };
    }

    private static string? MutationMode(string[] args)
    {
        var preview = args.Contains("--preview", StringComparer.Ordinal);
        var commit = args.Contains("--commit", StringComparer.Ordinal);
        return preview == commit ? null : preview ? "preview" : "commit";
    }

    private static string StatusPath(JsonObject body)
    {
        var pairs = new List<string>();
        foreach (var name in new[] { "view", "root_id", "job_id", "limit" })
        {
            if (body[name] is not JsonValue value || !value.TryGetValue<object?>(out var text) || text is null) continue;
            pairs.Add($"{name}={Uri.EscapeDataString(Convert.ToString(text, System.Globalization.CultureInfo.InvariantCulture)!)}");
        }

        return pairs.Count == 0 ? "api/v1/operations/status" : "api/v1/operations/status?" + string.Join("&", pairs);
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool IsLoopbackClient(HttpClient client) => client.BaseAddress is { } baseAddress
        && string.Equals(baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        && string.Equals(baseAddress.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal)
        && baseAddress.Port == 5137
        && baseAddress.AbsolutePath == "/";

    internal static SocketsHttpHandler CreateLoopbackTransport() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false
    };

    private static async ValueTask<string?> ReadBoundedTextAsync(
        TextReader reader,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumUtf8Bytes, 4096));
        var buffer = new char[4096];
        var utf8Bytes = 0;
        while (true)
        {
            var remainingCharacters = maximumUtf8Bytes + 1 - builder.Length;
            if (remainingCharacters <= 0) return null;
            var read = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remainingCharacters)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
            utf8Bytes = Encoding.UTF8.GetByteCount(builder.ToString());
            if (utf8Bytes > maximumUtf8Bytes) return null;
        }
        return builder.ToString();
    }

    private static async ValueTask<string?> ReadBoundedUtf8Async(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var remaining = maximumBytes + 1 - total;
            if (remaining <= 0) return null;
            var read = await source.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes) return null;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static async Task<int> WriteFailureAsync(TextWriter output, string reasonCode, bool retryable = false)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            ok = false,
            result = (object?)null,
            reasonCode,
            message = "The request could not be completed.",
            retryable
        }, SerializerOptions)).ConfigureAwait(false);
        return 1;
    }

    private sealed record Command(HttpMethod Method, string Path, bool IsMutation, bool IsStatus, string? IdempotencyKey);
}
