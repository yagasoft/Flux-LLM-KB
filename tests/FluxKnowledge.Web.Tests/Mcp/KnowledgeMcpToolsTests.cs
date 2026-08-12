using System.Reflection;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class KnowledgeMcpToolsTests
{
    [Fact]
    public async Task Retained_Csharp_detail_returns_only_the_named_trusted_local_projection()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tools = CreateToolsWithCsharpReader(branchId);

        var result = await tools.RetainedCsharpDetail(branchId, CancellationToken.None);
        var payload = ParseFirstTextBlock(result);

        Assert.False(result.IsError);
        Assert.Equal("C:\\retained-detail\\sample.cs", payload.GetProperty("localPath").GetString());
        Assert.Equal("Example.Type", payload.GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.Equal("CS0001", payload.GetProperty("diagnostics")[0].GetProperty("diagnosticId").GetString());
        Assert.DoesNotContain("secret-content-sentinel", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retained_Csharp_search_returns_only_the_named_trusted_local_projection()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tools = CreateToolsWithCsharpReader(branchId);

        var result = await tools.RetainedCsharpSearch("Example", 3, CancellationToken.None);
        var payload = ParseFirstTextBlock(result);

        Assert.False(result.IsError);
        Assert.Equal("C:\\retained-detail\\sample.cs", payload.GetProperty("results")[0].GetProperty("localPath").GetString());
        Assert.Equal("Example.Type", payload.GetProperty("results")[0].GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.DoesNotContain("secret-content-sentinel", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Brief_returns_the_legacy_temporary_unavailable_content_envelope_after_three_attempts()
    {
        var tools = CreateToolsThatAlwaysThrow<TimeoutException>("backend timed out");

        var result = await tools.Brief("restart", 1200, null, null, "local_first", null, CancellationToken.None);
        var payload = ParseFirstTextBlock(result);

        Assert.False(result.IsError);
        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("temporary_unavailable", payload.GetProperty("status").GetString());
        Assert.Equal("mcp.temporary_unavailable", payload.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("mcp", payload.GetProperty("error").GetProperty("component").GetString());
        Assert.Equal("kb.brief", payload.GetProperty("error").GetProperty("stage").GetString());
        Assert.True(payload.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Equal(503, payload.GetProperty("error").GetProperty("status_code").GetInt32());
    }

    [Fact]
    public async Task Search_returns_a_non_transient_tool_error_envelope_for_a_permanent_io_failure()
    {
        var tools = CreateToolsThatAlwaysThrow<FileNotFoundException>("index file is permanently missing");

        var result = await tools.Search("restart", 5, null, null, "local_first", null, CancellationToken.None);
        var payload = ParseFirstTextBlock(result);

        Assert.False(result.IsError);
        Assert.Equal("tool_error", payload.GetProperty("status").GetString());
        Assert.Equal("mcp.tool_error", payload.GetProperty("error").GetProperty("code").GetString());
        Assert.False(payload.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Equal(500, payload.GetProperty("error").GetProperty("status_code").GetInt32());
    }

    [Theory]
    [InlineData("Search", "query", "limit", "cwd", "root_name", "scope_mode", "filters")]
    [InlineData("Brief", "query", "token_budget", "cwd", "root_name", "scope_mode", "filters")]
    public void Approved_tool_signatures_preserve_legacy_parameter_names_and_defaults(
        string methodName,
        params string[] parameterNames)
    {
        var method = typeof(KnowledgeMcpTools).GetMethod(methodName)
            ?? throw new InvalidOperationException($"{methodName} was not found.");
        var parameters = method.GetParameters();

        Assert.Equal(parameterNames, parameters.Take(parameterNames.Length).Select(parameter => parameter.Name));
        Assert.Equal(methodName == "Search" ? 5 : 1200, (int)parameters[1].DefaultValue!);
        Assert.Null(parameters[2].DefaultValue);
        Assert.Null(parameters[3].DefaultValue);
        Assert.Equal("local_first", parameters[4].DefaultValue);
        Assert.Null(parameters[5].DefaultValue);
    }

    private static KnowledgeMcpTools CreateToolsThatAlwaysThrow<TException>(string message)
        where TException : Exception
    {
        var exception = (TException)Activator.CreateInstance(typeof(TException), message)!;
        var services = new ServiceCollection();
        services.AddScoped<ISearchService>(_ => new ThrowingSearchService(exception));
        var provider = services.BuildServiceProvider();
        return new KnowledgeMcpTools(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FluxKnowledge.Application.Mcp.ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero));
    }

    private static KnowledgeMcpTools CreateToolsWithCsharpReader(Guid branchId)
    {
        var services = new ServiceCollection();
        services.AddScoped<ILocalRetainedCsharpCodeReader>(_ => new Reader(branchId));
        var provider = services.BuildServiceProvider();
        return new KnowledgeMcpTools(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FluxKnowledge.Application.Mcp.ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero));
    }

    private static JsonElement ParseFirstTextBlock(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class ThrowingSearchService(Exception exception) : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<SearchResponse>(exception);
    }

    private sealed class Reader(Guid branchId) : ILocalRetainedCsharpCodeReader
    {
        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(requestedBranchId == branchId
                ? new LocalRetainedCsharpCodeDetailProjection(
                    branchId,
                    new SourceRevisionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    12,
                    "success",
                    new string('b', 64),
                    new string('c', 64),
                    1,
                    0,
                    0,
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)],
                    [],
                    [new LocalRetainedCsharpDiagnosticProjection(0, "CS0001", 2, 0, 1, "bounded parser diagnostic", false, null, false)])
                : null);

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ]);

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));
    }
}
