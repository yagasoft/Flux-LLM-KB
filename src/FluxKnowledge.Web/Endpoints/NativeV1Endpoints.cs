using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Direct-loopback REST bindings for the native v1 Application facade.</summary>
public static class NativeV1Endpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeNativeV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/knowledge/search", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryAsync("knowledge.search", request, facade, mapper, token));
        endpoints.MapPost("/api/v1/knowledge/graph/query", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryAsync("knowledge.graph", request, facade, mapper, token));
        endpoints.MapPost("/api/v1/code/query", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryAsync("code.query", request, facade, mapper, token));
        endpoints.MapPost("/api/v1/corpus/query", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryAsync("corpus.query", request, facade, mapper, token));
        endpoints.MapGet("/api/v1/operations/status", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryStatusAsync(request, facade, mapper, token));
        endpoints.MapPost("/api/v1/operations/audit/query", (HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => QueryAsync("operations.audit", request, facade, mapper, token));
        endpoints.MapPost("/api/v1/knowledge/actions/preview", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("knowledge.write", "preview", context, facade, mapper, token));
        endpoints.MapPost("/api/v1/knowledge/actions/commit", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("knowledge.write", "commit", context, facade, mapper, token));
        endpoints.MapPost("/api/v1/code/actions/preview", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("code.write", "preview", context, facade, mapper, token));
        endpoints.MapPost("/api/v1/code/actions/commit", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("code.write", "commit", context, facade, mapper, token));
        endpoints.MapPost("/api/v1/corpus/actions/preview", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("corpus.write", "preview", context, facade, mapper, token));
        endpoints.MapPost("/api/v1/corpus/actions/commit", (HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken token) => ActionAsync("corpus.write", "commit", context, facade, mapper, token));
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(string toolName, HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await facade.ExecuteQueryAsync(Family(toolName), mapper.MapQuery(toolName, arguments), cancellationToken).ConfigureAwait(false);
            return NativeResult(McpResultFactory.NativeSuccess(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failure(exception); }
    }

    private static async Task<IResult> QueryStatusAsync(HttpRequest request, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken cancellationToken)
    {
        try
        {
            var result = await facade.ExecuteQueryAsync("operations.status", mapper.MapQuery("operations.status", mapper.FromQuery(request.Query)), cancellationToken).ConfigureAwait(false);
            return NativeResult(McpResultFactory.NativeSuccess(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failure(exception); }
    }

    private static async Task<IResult> ActionAsync(string toolName, string mode, HttpContext context, INativeV1Facade facade, NativeV1RequestMapper mapper, CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context)) return Failure("loopback-required", StatusCodes.Status403Forbidden);
        try
        {
            var arguments = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var command = mapper.MapAction(toolName, arguments);
            var family = mapper.ActionFamily(toolName, command);
            if (mode == "preview")
            {
                var preview = await facade.PreviewAsync(family, command, "rest", cancellationToken).ConfigureAwait(false);
                return NativeResult(McpResultFactory.NativeSuccess(preview));
            }

            var confirmationId = mapper.ConfirmationId(arguments);
            if (string.IsNullOrWhiteSpace(confirmationId)) return Failure("confirmation-required", StatusCodes.Status400BadRequest);
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idempotencyKey)) return Failure("idempotency-key-required", StatusCodes.Status400BadRequest);
            var receipt = await facade.CommitAsync(family, command, confirmationId, idempotencyKey, "rest", cancellationToken).ConfigureAwait(false);
            return NativeResult(McpResultFactory.NativeSuccess(receipt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failure(exception); }
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > NativeV1RequestMapper.MaximumBodyBytes) throw new NativeOperationException("body-too-large");
        try
        {
            await using var bounded = new BoundedReadStream(request.Body, NativeV1RequestMapper.MaximumBodyBytes);
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new NativeOperationException("invalid-json");
        }
    }

    private static IResult Failure(Exception exception)
    {
        var envelope = McpResultFactory.NativeFailure(exception);
        return NativeResult(envelope);
    }

    private static IResult Failure(string reasonCode, int statusCode) =>
        NativeResult(McpResultFactory.NativeFailure(reasonCode), statusCode);

    private static IResult NativeResult(NativeV1Envelope envelope, int? explicitStatusCode = null)
    {
        var bounded = McpResultFactory.NativeBytes(envelope);
        var statusCode = explicitStatusCode ?? (bounded.Envelope.Ok
            ? StatusCodes.Status200OK
            : Status(bounded.Envelope));
        return Results.Text(
            System.Text.Encoding.UTF8.GetString(bounded.Utf8),
            "application/json",
            System.Text.Encoding.UTF8,
            statusCode);
    }

    private static int Status(NativeV1Envelope envelope) => envelope switch
    {
        { Retryable: true } => StatusCodes.Status503ServiceUnavailable,
        { ReasonCode: "loopback-required" } => StatusCodes.Status403Forbidden,
        { ReasonCode: "body-too-large" } => StatusCodes.Status413PayloadTooLarge,
        { ReasonCode: "response-too-large" } => StatusCodes.Status500InternalServerError,
        { ReasonCode: "invalid-json" or "invalid-request" or "invalid-query" or "invalid-limit" or "cursor-invalid" or "confirmation-required" or "idempotency-key-required" or "invalid-mode" } => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status409Conflict
    };

    private static string Family(string toolName) => toolName switch
    {
        "knowledge.search" or "knowledge.write" => "knowledge",
        "knowledge.graph" => "graph",
        "code.query" or "code.write" => "code",
        "corpus.query" or "corpus.write" => "corpus",
        "operations.status" => "operations.status",
        "operations.audit" => "operations.audit",
        _ => throw new NativeOperationException("tool-not-allowed")
    };

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
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer, offset, Limit(count), cancellationToken).ConfigureAwait(false));
        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0) Count(1);
            return value;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Limit(int requested)
        {
            if (requested == 0) return 0;
            return Math.Min(requested, maximumBytes + 1 - _bytesRead);
        }

        private int Count(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maximumBytes) throw new NativeOperationException("body-too-large");
            return read;
        }
    }
}
