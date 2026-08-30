using System.Reflection;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class NativeV1McpToolsTests
{
    [Fact]
    public void Native_MCP_class_advertises_exactly_the_nine_v1_tools()
    {
        var names = typeof(NativeV1McpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["code.query", "code.write", "corpus.query", "corpus.write", "knowledge.graph", "knowledge.search", "knowledge.write", "operations.audit", "operations.status"],
            names);
    }

    [Fact]
    public async Task Every_native_tool_maps_to_the_facade_through_the_shared_success_envelope()
    {
        var facade = new RecordingFacade();
        var tools = CreateTools(facade);

        var query = await tools.KnowledgeSearch("needle", 3, CancellationToken.None);
        var graph = await tools.KnowledgeGraph("node", 2, 3, CancellationToken.None);
        var code = await tools.CodeQuery("symbols", null, null, 3, null, CancellationToken.None);
        var corpus = await tools.CorpusQuery("roots", null, null, null, 3, null, CancellationToken.None);
        var status = await tools.OperationsStatus("overview", null, null, 3, CancellationToken.None);
        var audit = await tools.OperationsAudit("events", null, null, 3, null, CancellationToken.None);
        var preview = await tools.KnowledgeWrite("preview", "note_create", null, "Title", "Body", null, null, null, null, null, null, null, cancellationToken: CancellationToken.None);
        var codeWrite = await tools.CodeWrite("preview", JsonSerializer.SerializeToElement(new { rating = "useful" }), cancellationToken: CancellationToken.None);
        var corpusWrite = await tools.CorpusWrite("preview", "root_create", JsonSerializer.SerializeToElement(new { name = "Root" }), cancellationToken: CancellationToken.None);

        foreach (var result in new[] { query, graph, code, corpus, status, audit, preview, codeWrite, corpusWrite })
        {
            Assert.True(Read(result).GetProperty("ok").GetBoolean());
        }
        Assert.Equal(6, facade.QueryCalls);
        Assert.Equal(3, facade.PreviewCalls);
        Assert.Equal(0, facade.CommitCalls);
    }

    [Fact]
    public async Task Commit_rejects_missing_confirmation_or_idempotency_key_before_facade_dispatch()
    {
        var facade = new RecordingFacade();
        var tools = CreateTools(facade);

        var missingConfirmation = await tools.CodeWrite("commit", JsonSerializer.SerializeToElement(new { rating = "useful" }), null, "key-1", CancellationToken.None);
        var missingIdempotency = await tools.CodeWrite("commit", JsonSerializer.SerializeToElement(new { rating = "useful" }), "opaque-confirmation", null, CancellationToken.None);

        Assert.Equal("confirmation-required", Read(missingConfirmation).GetProperty("reasonCode").GetString());
        Assert.Equal("idempotency-key-required", Read(missingIdempotency).GetProperty("reasonCode").GetString());
        Assert.Equal(0, facade.CommitCalls);
    }

    [Fact]
    public async Task Invalid_cursor_returns_a_safe_reason_without_echoing_the_tampered_value()
    {
        var tools = CreateTools(new RecordingFacade());

        var result = await tools.CodeQuery("symbols", null, null, 3, "tampered-cursor", CancellationToken.None);
        var payload = Read(result);

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("cursor-invalid", payload.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("tampered-cursor", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_retries_are_bounded_but_an_uncertain_commit_is_not_retried()
    {
        var facade = new RetryingFacade();
        var tools = CreateTools(facade);

        var read = await tools.KnowledgeSearch("needle", 3, CancellationToken.None);
        var commit = await tools.CodeWrite("commit", JsonSerializer.SerializeToElement(new { rating = "useful" }), "opaque-confirmation", "key-1", CancellationToken.None);

        Assert.True(Read(read).GetProperty("ok").GetBoolean());
        Assert.Equal(3, facade.QueryCalls);
        Assert.False(Read(commit).GetProperty("ok").GetBoolean());
        Assert.Equal(1, facade.CommitCalls);
    }

    [Fact]
    public async Task Oversize_MCP_arguments_are_rejected_without_facade_dispatch()
    {
        var facade = new RecordingFacade();
        var tools = CreateTools(facade);

        var result = await tools.KnowledgeSearch(new string('x', NativeV1RequestMapper.MaximumBodyBytes), 3, CancellationToken.None);

        Assert.Equal("body-too-large", Read(result).GetProperty("reasonCode").GetString());
        Assert.Equal(0, facade.QueryCalls);
    }

    [Theory]
    [InlineData("knowledge", 2047, true)]
    [InlineData("knowledge", 2048, true)]
    [InlineData("knowledge", 2049, false)]
    [InlineData("graph", 2047, true)]
    [InlineData("graph", 2048, true)]
    [InlineData("graph", 2049, false)]
    [InlineData("graph", 4096, false)]
    [InlineData("code", 2047, true)]
    [InlineData("code", 2048, true)]
    [InlineData("code", 2049, false)]
    public async Task Query_character_boundaries_are_identical_at_the_MCP_mapper(
        string family,
        int characterCount,
        bool accepted)
    {
        var facade = new RecordingFacade();
        var tools = CreateTools(facade);
        var value = new string('q', characterCount);

        var result = family switch
        {
            "knowledge" => await tools.KnowledgeSearch(value, 3, CancellationToken.None),
            "graph" => await tools.KnowledgeGraph(value, 1, 3, CancellationToken.None),
            "code" => await tools.CodeQuery("matches", value, null, 3, null, CancellationToken.None),
            _ => throw new InvalidOperationException()
        };
        var envelope = Read(result);

        Assert.Equal(accepted, envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(accepted ? null : "invalid-query", envelope.GetProperty("reasonCode").GetString());
        Assert.Equal(accepted ? 1 : 0, facade.QueryCalls);
    }

    [Fact]
    public async Task MCP_accepts_the_schema_valid_maximum_knowledge_page()
    {
        var result = await CreateTools(new RecordingFacade(MaximumKnowledgePage())).KnowledgeSearch(
            "needle", 100, CancellationToken.None);
        var envelope = Read(result);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(100, envelope.GetProperty("result").GetArrayLength());
        Assert.InRange(
            System.Text.Encoding.UTF8.GetByteCount(text),
            100 * (256 + (16 * 1024)) * 6,
            NativeV1ContractLimits.MaximumResponseBytes);
    }

    [Fact]
    public async Task MCP_rejects_a_facade_result_above_the_shared_native_response_budget()
    {
        var overBudget = new string('z', NativeV1ContractLimits.MaximumResponseBytes + 1);

        var result = await CreateTools(new RecordingFacade(new { overBudget })).CodeQuery(
            "status",
            null,
            null,
            3,
            null,
            CancellationToken.None);
        var envelope = Read(result);

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("response-too-large", envelope.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(new string('z', 100), envelope.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secret-content-sentinel")]
    [InlineData("password=synthetic-value")]
    [InlineData("postgresql://synthetic-user:synthetic-password@127.0.0.1/db")]
    [InlineData("-----BEGIN PRIVATE KEY----- synthetic -----END PRIVATE KEY-----")]
    [InlineData("eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=")]
    public async Task Corpus_metadata_rejection_has_the_safe_shared_MCP_envelope(string protectedDisplayName)
    {
        var result = await CreateTools(new RecordingFacade()).CorpusWrite(
            "preview",
            "root_create",
            JsonSerializer.SerializeToElement(new { path = @"C:\native-v1-transport", displayName = protectedDisplayName }),
            cancellationToken: CancellationToken.None);
        var envelope = Read(result);

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("secret-content-withheld", envelope.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(protectedDisplayName, envelope.GetRawText(), StringComparison.Ordinal);
    }

    private static JsonElement Read(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static object MaximumKnowledgePage()
    {
        var title = new string('\u0800', 256);
        var content = new string('\u0800', 16 * 1024);
        return Enumerable.Range(0, 100)
            .Select(_ => new KnowledgeSearchResult(
                Guid.Empty,
                "note",
                title,
                content,
                "knowledge"))
            .ToArray();
    }

    private static NativeV1McpTools CreateTools(INativeV1Facade facade) => new(
        facade,
        new NativeV1RequestMapper(),
        new FluxKnowledge.Application.Mcp.ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero));

    private sealed class RecordingFacade(object? queryResult = null) : INativeV1Facade
    {
        public int QueryCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public int CommitCalls { get; private set; }

        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            QueryCalls++;
            if (request is NativeCodeQuery { Cursor: not null }) throw new NativeOperationException("cursor-invalid");
            return ValueTask.FromResult(queryResult ?? (object)new { family, value = "safe" });
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            if (command is NativeCorpusMutation corpus &&
                corpus.Payload.TryGetProperty("displayName", out var displayName) &&
                displayName.ValueKind == JsonValueKind.String &&
                new LocalPrivateContentDisclosure().Evaluate(
                    displayName.GetString()!,
                    LocalDisclosureKind.CorpusMetadata) is { Withheld: true } withheld)
            {
                throw new NativeOperationException(withheld.ReasonCode!);
            }
            PreviewCalls++;
            return ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "opaque-confirmation", "fingerprint", DateTimeOffset.UnixEpoch, [], "safe"));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
        {
            CommitCalls++;
            return ValueTask.FromResult(new NativeActionReceipt(Guid.Empty, false, "committed", null));
        }
    }

    private sealed class RetryingFacade : INativeV1Facade
    {
        public int QueryCalls { get; private set; }
        public int CommitCalls { get; private set; }

        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return QueryCalls < 3
                ? ValueTask.FromException<object>(new TimeoutException("transient"))
                : ValueTask.FromResult<object>(new { family, safe = true });
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("This test does not preview actions.");

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
        {
            CommitCalls++;
            throw new NativeOperationCommitUncertainException(new TimeoutException("uncertain"));
        }
    }
}
