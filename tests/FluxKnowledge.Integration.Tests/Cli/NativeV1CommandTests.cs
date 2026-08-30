using System.Net;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Cli.Commands;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

public sealed class NativeV1CommandTests
{
    [Theory]
    [InlineData("knowledge search", "POST", "/api/v1/knowledge/search", "{\"query\":\"needle\",\"limit\":3}")]
    [InlineData("knowledge graph", "POST", "/api/v1/knowledge/graph/query", "{\"node\":\"n\",\"max_depth\":1,\"max_results\":3}")]
    [InlineData("code query", "POST", "/api/v1/code/query", "{\"view\":\"status\",\"limit\":3}")]
    [InlineData("corpus query", "POST", "/api/v1/corpus/query", "{\"view\":\"roots\",\"limit\":3}")]
    [InlineData("operations status", "GET", "/api/v1/operations/status?view=overview&limit=3", "{\"view\":\"overview\",\"limit\":3}")]
    [InlineData("operations audit", "POST", "/api/v1/operations/audit/query", "{\"view\":\"events\",\"limit\":3}")]
    public async Task Query_commands_forward_the_stable_json_body_to_the_matching_loopback_v1_route(
        string command,
        string method,
        string pathAndQuery,
        string input)
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{\"safe\":true},\"reasonCode\":null,\"message\":null,\"retryable\":false}");

        var result = await ExecuteAsync(command.Split(' '), input, handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(method, handler.Request!.Method.Method);
        Assert.Equal(pathAndQuery, handler.Request.RequestUri!.PathAndQuery);
        Assert.Equal("{\"ok\":true,\"result\":{\"safe\":true},\"reasonCode\":null,\"message\":null,\"retryable\":false}", result.Output.Trim());
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("knowledge write", "knowledge/actions")]
    [InlineData("code feedback", "code/actions")]
    [InlineData("corpus write", "corpus/actions")]
    public async Task Preview_mutations_use_only_preview_routes_and_never_commit(string command, string route)
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{\"confirmationId\":\"opaque\"},\"reasonCode\":null,\"message\":null,\"retryable\":false}");

        var result = await ExecuteAsync([.. command.Split(' '), "--preview"], "{\"action\":\"note_create\",\"title\":\"title\",\"body\":\"body\",\"payload\":{\"rating\":\"useful\"}}", handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"/api/v1/{route}/preview", handler.Request!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("commit", handler.Request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("knowledge search", "{\"query\":\"needle\",\"limit\":3}")]
    [InlineData("knowledge write --preview", "{\"action\":\"note_create\",\"title\":\"title\",\"body\":\"body\"}")]
    [InlineData("knowledge graph", "{\"node\":\"n\",\"max_depth\":1,\"max_results\":3}")]
    [InlineData("code query", "{\"view\":\"status\",\"limit\":3}")]
    [InlineData("code feedback --preview", "{\"payload\":{\"rating\":\"useful\"}}")]
    [InlineData("corpus query", "{\"view\":\"roots\",\"limit\":3}")]
    [InlineData("corpus write --preview", "{\"action\":\"root_create\",\"payload\":{\"name\":\"root\"}}")]
    [InlineData("operations status", "{\"view\":\"overview\",\"limit\":3}")]
    [InlineData("operations audit", "{\"view\":\"events\",\"limit\":3}")]
    public async Task Every_native_command_preserves_a_canonical_failure_envelope_and_nonzero_exit(string command, string input)
    {
        var handler = new RecordingHandler("{\"ok\":false,\"result\":null,\"reasonCode\":\"invalid-request\",\"message\":\"The request could not be completed.\",\"retryable\":false}");

        var result = await ExecuteAsync(command.Split(' '), input, handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("{\"ok\":false,\"result\":null,\"reasonCode\":\"invalid-request\",\"message\":\"The request could not be completed.\",\"retryable\":false}", result.Output.Trim());
    }

    [Theory]
    [InlineData("knowledge write")]
    [InlineData("code feedback")]
    [InlineData("corpus write")]
    public async Task Commit_mutations_reject_missing_confirmation_and_idempotency_without_an_http_request(string command)
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{},\"reasonCode\":null,\"message\":null,\"retryable\":false}");

        var result = await ExecuteAsync([.. command.Split(' '), "--commit"], "{\"action\":\"note_create\"}", handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Null(handler.Request);
        using var document = JsonDocument.Parse(result.Output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("confirmation-required", document.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("note_create", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("knowledge write")]
    [InlineData("code feedback")]
    [InlineData("corpus write")]
    public async Task Commit_mutations_reject_missing_idempotency_after_a_confirmation_is_supplied(string command)
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{},\"reasonCode\":null,\"message\":null,\"retryable\":false}");

        var result = await ExecuteAsync([.. command.Split(' '), "--commit", "--confirmation-id", "opaque-confirmation"], "{\"payload\":{}}", handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Null(handler.Request);
        Assert.Contains("idempotency-key-required", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-confirmation", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_mutations_send_an_opaque_confirmation_and_idempotency_key_without_echoing_them()
    {
        var handler = new RecordingHandler("{\"ok\":false,\"result\":null,\"reasonCode\":\"confirmation-expired\",\"message\":\"The request could not be completed.\",\"retryable\":false}");

        var result = await ExecuteAsync(
            ["code", "feedback", "--commit", "--confirmation-id", "opaque-confirmation", "--idempotency-key", "idempotency-1"],
            "{\"payload\":{\"rating\":\"useful\"}}",
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("opaque-confirmation", handler.Body, StringComparison.Ordinal);
        var request = Assert.IsType<HttpRequestMessage>(handler.Request);
        Assert.Equal("idempotency-1", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.DoesNotContain("opaque-confirmation", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("idempotency-1", result.Output, StringComparison.Ordinal);
        Assert.Contains("confirmation-expired", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_output_rejects_protected_values_without_disclosing_them()
    {
        var handler = new RecordingHandler("{\"ok\":false,\"result\":{\"sourceOriginalPath\":\"C:\\\\private\\\\raw.txt\",\"secretValue\":\"secret-sentinel\",\"decodedCursor\":\"decoded\"},\"reasonCode\":\"cursor-invalid\",\"message\":\"The request could not be completed.\",\"retryable\":false}");

        var result = await ExecuteAsync(["code", "query"], "{\"view\":\"status\",\"limit\":3}", handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid-response", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private\\\\raw.txt", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-sentinel", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("decoded", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_input_stops_at_max_plus_one_without_materialising_the_remainder()
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{},\"reasonCode\":null,\"message\":null,\"retryable\":false}");
        using var client = new HttpClient(handler) { BaseAddress = NativeV1Command.LoopbackBaseAddress };
        var input = new GuardedTextReader(
            NativeV1ContractLimits.MaximumRequestBytes + 100,
            NativeV1ContractLimits.MaximumRequestBytes + 1);
        var output = new StringWriter();

        var exitCode = await NativeV1Command.ExecuteAsync(
            ["knowledge", "search"],
            input,
            client,
            output,
            TextWriter.Null);

        Assert.Equal(1, exitCode);
        Assert.Equal(NativeV1ContractLimits.MaximumRequestBytes + 1, input.CharactersRead);
        Assert.Contains("body-too-large", output.ToString(), StringComparison.Ordinal);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Response_input_stops_at_max_plus_one_without_materialising_the_remainder()
    {
        var source = new GuardedReadStream(
            NativeV1ContractLimits.MaximumResponseBytes + 100L,
            NativeV1ContractLimits.MaximumResponseBytes + 1L);
        using var client = new HttpClient(new StreamResponseHandler(source))
        {
            BaseAddress = NativeV1Command.LoopbackBaseAddress
        };
        var output = new StringWriter();

        var exitCode = await NativeV1Command.ExecuteAsync(
            ["code", "query"],
            new StringReader("{\"view\":\"status\",\"limit\":3}"),
            client,
            output,
            TextWriter.Null);

        Assert.Equal(1, exitCode);
        Assert.Equal(NativeV1ContractLimits.MaximumResponseBytes + 1L, source.BytesRead);
        Assert.Contains("invalid-response", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CLI_accepts_the_schema_valid_maximum_knowledge_page()
    {
        var response = JsonSerializer.Serialize(new
        {
            ok = true,
            result = MaximumKnowledgePage(),
            reasonCode = (string?)null,
            message = (string?)null,
            retryable = false
        });

        var result = await ExecuteAsync(
            ["knowledge", "search"],
            "{\"query\":\"needle\",\"limit\":100}",
            new RecordingHandler(response));

        Assert.Equal(0, result.ExitCode);
        using var envelope = JsonDocument.Parse(result.Output);
        Assert.Equal(100, envelope.RootElement.GetProperty("result").GetArrayLength());
        Assert.InRange(
            Encoding.UTF8.GetByteCount(result.Output),
            100 * (256 + (16 * 1024)) * 6,
            NativeV1ContractLimits.MaximumResponseBytes);
    }

    [Theory]
    [InlineData("secret-content-sentinel")]
    [InlineData("password=synthetic-value")]
    [InlineData("postgresql://synthetic-user:synthetic-password@127.0.0.1/db")]
    [InlineData("-----BEGIN PRIVATE KEY----- synthetic -----END PRIVATE KEY-----")]
    [InlineData("eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=")]
    public async Task Corpus_metadata_rejection_has_the_safe_shared_CLI_envelope(string protectedDisplayName)
    {
        var handler = new RecordingHandler("{\"ok\":false,\"result\":null,\"reasonCode\":\"secret-content-withheld\",\"message\":\"The request could not be completed.\",\"retryable\":false}");
        var input = JsonSerializer.Serialize(new
        {
            action = "root_create",
            payload = new { path = @"C:\native-v1-transport", displayName = protectedDisplayName }
        });

        var result = await ExecuteAsync(["corpus", "write", "--preview"], input, handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("secret-content-withheld", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedDisplayName, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"result\":{\"secretValue\":\"leaked\"},\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    [InlineData("{\"ok\":true,\"result\":{\"accessToken\":\"value\"},\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    [InlineData("{\"ok\":true,\"result\":{\"nested\":{\"password\":\"value\"}},\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    [InlineData("{\"ok\":true,\"result\":\"Authorization: Bearer value\",\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    [InlineData("{\"ok\":true,\"result\":\"Bearer leaked-token\",\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    [InlineData("{\"ok\":true,\"result\":\"eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=\",\"reasonCode\":null,\"message\":null,\"retryable\":false}")]
    public async Task Noncanonical_or_credential_bearing_envelopes_fail_closed(string response)
    {
        var handler = new RecordingHandler(response);

        var result = await ExecuteAsync(["code", "query"], "{\"view\":\"status\",\"limit\":3}", handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid-response", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secretValue", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("password", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer value", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-token", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_response_is_refused_without_a_follow_up_remote_request()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler) { BaseAddress = NativeV1Command.LoopbackBaseAddress };
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await NativeV1Command.ExecuteAsync(["code", "query"], new StringReader("{\"view\":\"status\",\"limit\":3}"), client, output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("redirect-refused", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.25", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_loopback_transport_disables_proxy_resolution_and_redirects()
    {
        using var transport = NativeV1Command.CreateLoopbackTransport();

        Assert.False(transport.UseProxy);
        Assert.False(transport.AllowAutoRedirect);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string[] args, string input, RecordingHandler handler)
    {
        using var client = new HttpClient(handler) { BaseAddress = NativeV1Command.LoopbackBaseAddress };
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await NativeV1Command.ExecuteAsync(args, new StringReader(input), client, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static object MaximumKnowledgePage()
    {
        var title = new string('\u0800', 256);
        var content = new string('\u0800', 16 * 1024);
        return Enumerable.Range(0, 100)
            .Select(_ => new
            {
                id = Guid.Empty,
                kind = "note",
                title,
                content,
                provenance = "knowledge",
                confidence = (decimal?)null,
                sourceIdentity = (string?)null,
                sourceRevision = (long?)null
            })
            .ToArray();
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://192.0.2.25:5137/api/v1/code/query") }
            });
        }
    }

    private sealed class StreamResponseHandler(Stream source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(source)
            });
    }

    private sealed class GuardedTextReader(int length, int maximumReadable) : TextReader
    {
        public int CharactersRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CharactersRead >= maximumReadable)
            {
                throw new Xunit.Sdk.XunitException("The CLI read beyond the request max+1 guard.");
            }

            var count = Math.Min(buffer.Length, Math.Min(length - CharactersRead, maximumReadable - CharactersRead));
            buffer.Span[..count].Fill('x');
            CharactersRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class GuardedReadStream(long length, long maximumReadable) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The CLI must use asynchronous response reads.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead >= maximumReadable)
            {
                throw new Xunit.Sdk.XunitException("The CLI read beyond the response max+1 guard.");
            }

            var count = (int)Math.Min(buffer.Length, Math.Min(length - BytesRead, maximumReadable - BytesRead));
            buffer.Span[..count].Fill((byte)'x');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
