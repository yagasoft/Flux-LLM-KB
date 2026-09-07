using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
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
            .Where(value => value.ActorSurface == "codex-hook" && value.IdempotencyKey == StopIdempotencyKey(sessionId, turnId))
            .ToListAsync());
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.capture_saved" && value.CorrelationId == CaptureCorrelation(sessionId, turnId))
            .ToListAsync());
        Assert.Equal("codex_hook", audit.EventFamily);
        Assert.Equal("information", audit.Severity);
        Assert.Equal("codex-hook", audit.Actor);
        Assert.DoesNotContain(summary, audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId, audit.CorrelationId, StringComparison.Ordinal);
        Assert.DoesNotContain(turnId, audit.CorrelationId, StringComparison.Ordinal);
        var projected = await new SqlOperatorEventProjectionReader(CreateRetryingFactory()).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: audit.CorrelationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Equal("codex_hook.capture_saved", visible.EventType);
        Assert.Equal("codex_hook", visible.Family);
        Assert.Equal("information", visible.Severity);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_Stop_requests_persist_exactly_one_capture_event()
    {
        var sessionId = $"codex-stop-session-{Guid.NewGuid():N}";
        var turnId = $"codex-stop-turn-{Guid.NewGuid():N}";
        var payload = Json($$"""{"session_id":"{{sessionId}}","turn_id":"{{turnId}}","last_assistant_message":"Concurrent Stop capture."}""");

        var responses = await Task.WhenAll(
            CreateService().HandleAsync("Stop", payload, CancellationToken.None).AsTask(),
            CreateService().HandleAsync("Stop", payload, CancellationToken.None).AsTask());

        Assert.All(responses, response => Assert.Equal("{\"continue\":true}", JsonSerializer.Serialize(response)));
        await using var context = await CreateRetryingFactory().CreateDbContextAsync();
        Assert.Single(await context.KnowledgeItems.Where(value => value.SafeBody == "Concurrent Stop capture.").ToListAsync());
        Assert.Single(await context.NativeOperationReceipts
            .Where(value => value.ActorSurface == "codex-hook" && value.IdempotencyKey == StopIdempotencyKey(sessionId, turnId))
            .ToListAsync());
        Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.capture_saved" && value.CorrelationId == CaptureCorrelation(sessionId, turnId))
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Stop_interruption_before_transaction_commit_persists_no_capture_event()
    {
        var factory = CreateRetryingFactory();
        using var cancellation = new CancellationTokenSource();
        var operationStore = new SqlNativeOperationStore(
            factory,
            TimeProvider.System,
            afterSaveBeforeCommitInjector: cancellation.Cancel);
        var service = CreateService(operationStore);
        var sessionId = $"codex-stop-session-{Guid.NewGuid():N}";
        var turnId = $"codex-stop-turn-{Guid.NewGuid():N}";
        const string summary = "Cancelled Stop capture.";

        var response = await service.HandleAsync(
            "Stop",
            Json($$"""{"session_id":"{{sessionId}}","turn_id":"{{turnId}}","last_assistant_message":"{{summary}}"}"""),
            cancellation.Token);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", response.SystemMessage);

        await using var context = await factory.CreateDbContextAsync();
        Assert.Empty(await context.KnowledgeItems.Where(value => value.SafeBody == summary).ToListAsync());
        Assert.Empty(await context.NativeOperationReceipts
            .Where(value => value.ActorSurface == "codex-hook" && value.IdempotencyKey == StopIdempotencyKey(sessionId, turnId))
            .ToListAsync());
        Assert.Empty(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.capture_saved" && value.CorrelationId == CaptureCorrelation(sessionId, turnId))
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task UserPromptSubmit_persists_a_metadata_only_preflight_event()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new PreflightFacade(),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var response = await service.HandleAsync(
            "UserPromptSubmit",
            Json("{\"prompt\":\"private-prompt-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.preflight_completed")
            .ToListAsync());
        Assert.Equal("codex_hook", audit.EventFamily);
        Assert.Equal("information", audit.Severity);
        Assert.Equal("codex-hook", audit.Actor);
        Assert.Contains("context_injected", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        await AssertProjectedAsync(factory, audit);
    }

    [NativeSqlServerFact]
    public async Task Invalid_hook_input_persists_a_metadata_only_projected_event()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new PreflightFacade(),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var response = await service.HandleAsync(
            "Unsupported",
            Json("{\"private-prompt-sentinel\":\"not-a-hook\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook ignored invalid input.", response.SystemMessage);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.input_rejected")
            .ToListAsync());
        Assert.Equal("codex_hook", audit.EventFamily);
        Assert.Equal("warning", audit.Severity);
        Assert.Contains("invalid_input", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        await AssertProjectedAsync(factory, audit);
    }

    [NativeSqlServerFact]
    public async Task UserPromptSubmit_failure_persists_a_sanitised_operator_event()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new FailingPreflightFacade(),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var response = await service.HandleAsync(
            "UserPromptSubmit",
            Json("{\"prompt\":\"private-prompt-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", response.SystemMessage);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed")
            .ToListAsync());
        Assert.Equal("warning", audit.Severity);
        Assert.Contains("unexpected", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        await AssertProjectedAsync(factory, audit);
    }

    private NativeCodexHookService CreateService(SqlNativeOperationStore? operationStore = null)
    {
        var factory = CreateRetryingFactory();
        operationStore ??= new SqlNativeOperationStore(factory, TimeProvider.System);
        var commands = new KnowledgeCommandService(
            operationStore,
            new SqlKnowledgeStore(factory),
            new LocalPrivateContentDisclosure());
        var facade = new NativeV1Facade(null!, null!, null!, null!, null!, null!, null!, commands);
        return new NativeCodexHookService(facade, operationStore);
    }

    private static string StopIdempotencyKey(string sessionId, string turnId) =>
        "codex-stop-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\u001f{turnId}")));

    private static string CaptureCorrelation(string sessionId, string turnId) =>
        "codex-hook:" + StopIdempotencyKey(sessionId, turnId)["codex-stop-".Length..];

    private static async Task AssertProjectedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, AuditEventEntity audit)
    {
        var projected = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: audit.CorrelationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Equal(audit.EventType, visible.EventType);
        Assert.Equal(audit.EventFamily, visible.Family);
        Assert.Equal(audit.Severity, visible.Severity);
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

    private sealed class PreflightFacade : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object>(
                new[] { new KnowledgeSearchResult(Guid.NewGuid(), "note", "Prior decision", "Safe context.", "knowledge") });

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingPreflightFacade : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            ValueTask.FromException<object>(new InvalidOperationException("private-prompt-sentinel"));

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
