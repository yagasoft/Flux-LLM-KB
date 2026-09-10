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
using Microsoft.Data.SqlClient;
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

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "UserPromptSubmit",
            Json("{\"prompt\":\"private-prompt-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", response.SystemMessage);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Equal("warning", audit.Severity);
        Assert.Contains("unexpected", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("preflight-exception-detail-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        await AssertProjectedAsync(factory, audit);
    }

    [NativeSqlServerFact]
    public async Task Stop_preview_failure_persists_exception_text_without_capture_content()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new PreviewFailingFacade(new InvalidOperationException("exception-detail-sentinel")),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"private-session-sentinel\",\"turn_id\":\"private-turn-sentinel\",\"last_assistant_message\":\"private-summary-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", response.SystemMessage);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Contains("\"phase\":\"preview\"", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"exceptionType\":\"InvalidOperationException\"", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"exceptionText\"", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("exception-detail-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlErrorNumber", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-summary-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        var projected = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: audit.CorrelationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Contains("exception-detail-sentinel", visible.Details, StringComparison.Ordinal);
        Assert.Contains("exception-detail-sentinel", visible.Message, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Stop_preview_failure_redacts_hook_values_echoed_by_the_exception()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new EchoingPreviewFailureFacade("private-session-sentinel", "private-turn-sentinel"),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"private-session-sentinel\",\"turn_id\":\"private-turn-sentinel\",\"last_assistant_message\":\"private-summary-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Contains("preview failure", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-summary-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        var projected = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: audit.CorrelationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Contains("preview failure", visible.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-sentinel", visible.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-sentinel", visible.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-summary-sentinel", visible.Message, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Stop_preview_failure_truncates_an_oversized_caught_exception_text()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new PreviewFailingFacade(new InvalidOperationException(new string('x', CodexHookFailureMetadata.MaximumExceptionTextCharacters + 1))),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"session\",\"turn_id\":\"turn\",\"last_assistant_message\":\"summary\"}"),
            CancellationToken.None);

        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(
            CodexHookFailureMetadata.MaximumExceptionTextCharacters,
            details.RootElement.GetProperty("exceptionText").GetString()!.Length);
    }

    [NativeSqlServerFact]
    public async Task Stop_preview_failure_uses_a_bounded_fallback_when_exception_text_throws()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new PreviewFailingFacade(new ThrowingToStringException()),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"session\",\"turn_id\":\"turn\",\"last_assistant_message\":\"summary\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Contains("ThrowingToStringException", audit.DetailsJson, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task UserPromptSubmit_failure_redacts_the_prompt_echoed_by_the_exception()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new EchoingPreflightFailureFacade(),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "UserPromptSubmit",
            Json("{\"prompt\":\"private-prompt-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Contains("query failure", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt-sentinel", audit.DetailsJson, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Stop_preview_sql_failure_persists_the_safe_sql_error_number_without_capture_content()
    {
        var factory = CreateRetryingFactory();
        var service = new NativeCodexHookService(
            new SqlPreviewFailingFacade(_fixture.ConnectionString),
            new SqlNativeOperationStore(factory, TimeProvider.System),
            auditWriter: new SqlCodexHookAuditWriter(factory));

        var startedAt = DateTimeOffset.UtcNow;
        var response = await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"private-session-sentinel\",\"turn_id\":\"private-turn-sentinel\",\"last_assistant_message\":\"private-summary-sentinel\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Contains("\"phase\":\"preview\"", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"exceptionType\":\"SqlException\"", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"sqlErrorNumber\":208", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-summary-sentinel", audit.DetailsJson, StringComparison.Ordinal);
        await AssertProjectedAsync(factory, audit);
    }

    [NativeSqlServerFact]
    public async Task Processing_failure_writer_persists_only_the_declared_bounded_metadata()
    {
        var factory = CreateRetryingFactory();
        var writer = new SqlCodexHookAuditWriter(factory);
        var startedAt = DateTimeOffset.UtcNow;

        await writer.AppendAsync(
            CodexHookAuditEvent.ProcessingFailed(
                "unexpected",
                "preview",
                "SqlException",
                208,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using var context = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await context.AuditEvents
            .Where(value => value.EventType == "codex_hook.processing_failed" &&
                            value.OccurredAtUtc >= startedAt)
            .ToListAsync());
        Assert.Equal("{\"reasonCode\":\"unexpected\",\"phase\":\"preview\",\"exceptionType\":\"SqlException\",\"sqlErrorNumber\":208}", audit.DetailsJson);
        var projected = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: audit.CorrelationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Equal("reason: unexpected; phase: preview; exception: SqlException; SQL error: 208", visible.Message);
        Assert.Equal(audit.DetailsJson, visible.Details);
        await AssertProjectedAsync(factory, audit);
    }

    [NativeSqlServerFact]
    public async Task Processing_failure_writer_rejects_oversized_exception_text()
    {
        var factory = CreateRetryingFactory();
        var audit = new CodexHookAuditEvent(
            CodexHookAuditOutcome.ProcessingFailed,
            DateTimeOffset.UtcNow,
            "unexpected",
            "preview",
            "InvalidOperationException",
            null,
            new string('x', CodexHookFailureMetadata.MaximumExceptionTextCharacters + 1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SqlCodexHookAuditWriter(factory).AppendAsync(audit, CancellationToken.None).AsTask());
    }

    [NativeSqlServerFact]
    public async Task Processing_failure_rejects_and_hides_unapproved_metadata()
    {
        var factory = CreateRetryingFactory();
        var occurredAt = DateTimeOffset.UtcNow;
        var unapproved = new CodexHookAuditEvent(
            CodexHookAuditOutcome.ProcessingFailed,
            occurredAt,
            "private-summary-sentinel",
            "private-summary-sentinel",
            "private-summary-sentinel",
            208);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SqlCodexHookAuditWriter(factory).AppendAsync(unapproved, CancellationToken.None).AsTask());

        const string correlationId = "codex-hook:hostile-metadata";
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.AuditEvents.Add(new AuditEventEntity
            {
                Actor = "test",
                CorrelationId = correlationId,
                EventFamily = "codex_hook",
                Severity = "warning",
                EventType = "codex_hook.processing_failed",
                DetailsJson = "{\"reasonCode\":\"private-summary-sentinel\",\"phase\":\"private-summary-sentinel\",\"exceptionType\":\"SqlException\",\"sqlErrorNumber\":208}",
                OccurredAtUtc = occurredAt
            });
            await context.SaveChangesAsync();
        }

        var projected = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(
            new OperatorEventQuery(new OperatorEventFilters(Family: "codex_hook", CorrelationId: correlationId)),
            CancellationToken.None);
        var visible = Assert.Single(projected.Items);
        Assert.Equal("{\"exceptionType\":\"SqlException\",\"sqlErrorNumber\":208}", visible.Details);
        Assert.Equal("exception: SqlException; SQL error: 208", visible.Message);
        Assert.DoesNotContain("private-summary-sentinel", visible.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("private-summary-sentinel", visible.Message, StringComparison.Ordinal);
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
            ValueTask.FromException<object>(new InvalidOperationException("preflight-exception-detail-sentinel"));

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PreviewFailingFacade(Exception exception) : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            ValueTask.FromException<NativeActionPreview>(exception);

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EchoingPreviewFailureFacade(string sessionId, string turnId) : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            var mutation = Assert.IsType<KnowledgeMutation>(command);
            return ValueTask.FromException<NativeActionPreview>(
                new InvalidOperationException($"preview failure: {sessionId}; {turnId}; {mutation.Title}; {mutation.Body}"));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EchoingPreflightFailureFacade : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            var query = Assert.IsType<NativeKnowledgeQuery>(request);
            return ValueTask.FromException<object>(new InvalidOperationException($"query failure: {query.Query}"));
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("exception-text-fallback-sentinel");
    }

    private sealed class SqlPreviewFailingFacade(string connectionString) : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var query = new SqlCommand("SELECT * FROM dbo.__FluxKnowledgeStopHookDiagnosticMissingTable;", connection);
            await query.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("The diagnostic SQL query unexpectedly succeeded.");
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
