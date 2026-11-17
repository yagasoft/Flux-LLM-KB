using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class NativeCorpusConnectorCommandTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Encoded_MCP_object_uses_real_closed_validation_confirmation_and_idempotent_native_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeMcpPayload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new ContextFactory(fixture.ConnectionString);
            var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([root])), new LocalPrivateContentDisclosure()));
            var tools = new NativeV1McpTools(new CorpusFacade(service), new NativeV1RequestMapper(), new ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero));
            var payload = JsonSerializer.SerializeToElement(new { path = root, displayName = "Connector payload fixture", recursive = false });
            var encoded = JsonSerializer.SerializeToElement(payload.GetRawText());
            var preview = Read(await tools.CorpusWrite("preview", "root_create", encoded));
            Assert.True(preview.GetProperty("ok").GetBoolean());
            var confirmation = preview.GetProperty("result").GetProperty("confirmationId").GetString();
            var key = "connector-root-" + Guid.NewGuid().ToString("N");
            var committed = Read(await tools.CorpusWrite("commit", "root_create", encoded, confirmation, key));
            var replayed = Read(await tools.CorpusWrite("commit", "root_create", payload, confirmation, key));
            Assert.True(committed.GetProperty("ok").GetBoolean());
            Assert.True(replayed.GetProperty("ok").GetBoolean());
            Assert.Equal(committed.GetProperty("result").GetProperty("operationId").GetGuid(), replayed.GetProperty("result").GetProperty("operationId").GetGuid());
            Assert.True(replayed.GetProperty("result").GetProperty("wasReplay").GetBoolean());
            var extra = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new { path = root, displayName = "Fixture", unexpected = true }));
            var refused = Read(await tools.CorpusWrite("preview", "root_create", extra));
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Equal("invalid-payload", refused.GetProperty("reasonCode").GetString());
            await using var db = factory.CreateDbContext();
            Assert.Single(await db.SourceRootConfigurations.ToArrayAsync());
        }
        finally { Directory.Delete(root); }
    }

    private static JsonElement Read(CallToolResult result)
    {
        using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        return json.RootElement.Clone();
    }

    private sealed class CorpusFacade(NativeCorpusCommandService service) : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken ct) =>
            service.PreviewAsync((NativeCorpusMutation)command, surface, ct);
        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmation, string key, string surface, CancellationToken ct) =>
            service.CommitAsync((NativeCorpusMutation)command, confirmation, key, surface, ct);
    }
    private sealed class ContextFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
}
