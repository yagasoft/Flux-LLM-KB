using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Mcp;

public sealed class NativeCodexHookPersistenceIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Stop_persists_one_note_and_receipt_then_replays_the_same_turn_without_duplicates()
    {
        var sessionId = $"codex-stop-session-{Guid.NewGuid():N}";
        var turnId = $"codex-stop-turn-{Guid.NewGuid():N}";
        const string summary = "The native Stop integration test captured this completed turn.";
        var payload = Json($$"""{"session_id":"{{sessionId}}","turn_id":"{{turnId}}","last_assistant_message":"{{summary}}"}""");

        var first = await CreateService().HandleAsync("Stop", payload, CancellationToken.None);
        var replay = await CreateService().HandleAsync("Stop", payload, CancellationToken.None);

        Assert.Equal("{\"continue\":true}", JsonSerializer.Serialize(first));
        Assert.Equal("{\"continue\":true}", JsonSerializer.Serialize(replay));

        await using var context = await CreateRetryingFactory().CreateDbContextAsync();
        var note = Assert.Single(await context.KnowledgeItems
            .Where(value => value.SafeBody == summary)
            .ToListAsync());
        Assert.Equal(summary, note.SafeBody);
        Assert.Single(await context.NativeOperationReceipts
            .Where(value => value.ActorSurface == "codex-hook")
            .ToListAsync());
    }

    private NativeCodexHookService CreateService()
    {
        var factory = CreateRetryingFactory();
        var operationStore = new SqlNativeOperationStore(factory, TimeProvider.System);
        var commands = new KnowledgeCommandService(
            operationStore,
            new SqlKnowledgeStore(factory),
            new LocalPrivateContentDisclosure());
        var facade = new NativeV1Facade(null!, null!, null!, null!, null!, null!, null!, commands);
        return new NativeCodexHookService(facade, operationStore);
    }

    private IDbContextFactory<FluxKnowledgeDbContext> CreateRetryingFactory() =>
        new RetryingDbContextFactory(_fixture.ConnectionString);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class RetryingDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure())
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
