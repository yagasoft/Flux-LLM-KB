using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Web.Mcp;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Loopback-only HTTP adapter for Codex command hooks.</summary>
public static class NativeCodexHookEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeNativeCodexHooks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/native/v1/codex/hooks/{eventName}", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string eventName,
        HttpContext context,
        NativeCodexHookService service,
        CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            var payload = await ReadPayloadAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var response = await service.HandleAsync(eventName, payload, cancellationToken).ConfigureAwait(false);
            return Results.Json(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return Results.Json(NativeCodexHookService.InvalidInput());
        }
        catch (NativeCodexHookBodyTooLargeException)
        {
            return Results.Json(NativeCodexHookService.InvalidInput());
        }
    }

    private static async Task<JsonElement> ReadPayloadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > NativeV1ContractLimits.MaximumRequestBytes) throw new NativeCodexHookBodyTooLargeException();
        await using var bounded = new BoundedReadStream(request.Body, NativeV1ContractLimits.MaximumRequestBytes);
        using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private sealed class BoundedReadStream(Stream inner, int maximumBytes) : Stream
    {
        private int _bytesRead;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Limit(count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken).ConfigureAwait(false));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Limit(int requested) => requested == 0 ? 0 : Math.Min(requested, maximumBytes + 1 - _bytesRead);
        private int Count(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maximumBytes) throw new NativeCodexHookBodyTooLargeException();
            return read;
        }
    }

    private sealed class NativeCodexHookBodyTooLargeException : Exception;
}
