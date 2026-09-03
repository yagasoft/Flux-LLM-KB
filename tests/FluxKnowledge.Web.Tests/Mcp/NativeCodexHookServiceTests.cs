using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Mcp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class NativeCodexHookServiceTests
{
    [Fact]
    public async Task UserPromptSubmit_returns_a_Codex_context_envelope_from_native_knowledge_results()
    {
        var facade = new RecordingFacade(
            [new KnowledgeSearchResult(Guid.Empty, "note", "Prior decision", "Use the native loopback boundary.", "knowledge")]);
        var service = new NativeCodexHookService(facade, new RecordingOperationStore());

        var response = await service.HandleAsync("UserPromptSubmit", Json("{\"prompt\":\"Continue the native activation work using previous decisions.\"}"), CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("UserPromptSubmit", response.HookSpecificOutput!.HookEventName);
        Assert.Contains("Prior decision", response.HookSpecificOutput.AdditionalContext, StringComparison.Ordinal);
        Assert.Equal(["knowledge"], facade.QueryFamilies);
    }

    [Fact]
    public async Task PreCompact_continues_without_dispatching_a_native_mutation()
    {
        var facade = new RecordingFacade([]);
        var service = new NativeCodexHookService(facade, new RecordingOperationStore());

        var response = await service.HandleAsync("PreCompact", Json("{\"trigger\":\"manual\"}"), CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Null(response.HookSpecificOutput);
        Assert.Equal(0, facade.PreviewCalls);
        Assert.Equal(0, facade.CommitCalls);
    }

    [Fact]
    public async Task Stop_uses_the_native_command_idempotency_key_for_the_same_turn_identity()
    {
        var captures = new RecordingOperationStore();
        var facade = new RecordingFacade([], captures);
        var service = new NativeCodexHookService(facade, captures);
        var payload = Json("{\"session_id\":\"session-1\",\"turn_id\":\"turn-9\",\"last_assistant_message\":\"Implemented the native hook boundary and ran focused verification.\"}");

        var first = await service.HandleAsync("Stop", payload, CancellationToken.None);
        var second = await new NativeCodexHookService(facade, captures).HandleAsync(
            "Stop",
            Json("{\"session_id\":\"session-1\",\"turn_id\":\"turn-9\",\"last_assistant_message\":\"A changed summary must not create another capture.\"}"),
            CancellationToken.None);

        Assert.True(first.Continue);
        Assert.True(second.Continue);
        Assert.Equal(1, facade.PreviewCalls);
        Assert.Equal(1, facade.CommitCalls);
        Assert.StartsWith("codex-stop-", Assert.Single(facade.IdempotencyKeys.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Stop_captures_matching_turns_from_distinct_sessions_separately()
    {
        var captures = new RecordingOperationStore();
        var facade = new RecordingFacade([], captures);
        var first = await new NativeCodexHookService(facade, captures).HandleAsync(
            "Stop", Json("{\"session_id\":\"session-a\",\"turn_id\":\"turn-9\",\"last_assistant_message\":\"The final native result.\"}"), CancellationToken.None);
        var second = await new NativeCodexHookService(facade, captures).HandleAsync(
            "Stop", Json("{\"session_id\":\"session-b\",\"turn_id\":\"turn-9\",\"last_assistant_message\":\"The final native result.\"}"), CancellationToken.None);

        Assert.True(first.Continue);
        Assert.True(second.Continue);
        Assert.Equal(2, facade.PreviewCalls);
        Assert.Equal(2, facade.CommitCalls);
        Assert.Equal(2, facade.IdempotencyKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, facade.SavedTitles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Invalid_input_and_backend_errors_fail_open_with_a_sanitised_diagnostic()
    {
        var invalid = await new NativeCodexHookService(new RecordingFacade([]), new RecordingOperationStore()).HandleAsync(
            "Stop", Json("{\"session_id\":\"session-1\",\"last_assistant_message\":\"summary\"}"), CancellationToken.None);
        var failed = await new NativeCodexHookService(new RecordingFacade([], throwOnQuery: true), new RecordingOperationStore()).HandleAsync(
            "UserPromptSubmit", Json("{\"prompt\":\"secret-content-sentinel\"}"), CancellationToken.None);

        Assert.True(invalid.Continue);
        Assert.Equal("Native Codex hook ignored invalid input.", invalid.SystemMessage);
        Assert.True(failed.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", failed.SystemMessage);
        Assert.DoesNotContain("secret-content-sentinel", failed.SystemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_commit_failure_logs_only_its_safe_phase_and_classification()
    {
        var logger = new RecordingLogger();
        var service = new NativeCodexHookService(new CommitFailingFacade(), new RecordingOperationStore(), logger);

        var response = await service.HandleAsync(
            "Stop",
            Json("{\"session_id\":\"session-sensitive\",\"turn_id\":\"turn-sensitive\",\"last_assistant_message\":\"summary-sensitive\"}"),
            CancellationToken.None);

        Assert.True(response.Continue);
        Assert.Equal("Native Codex hook could not access local knowledge; continuing.", response.SystemMessage);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("commit", entry.Message, StringComparison.Ordinal);
        Assert.Contains("confirmation-mismatch", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("session-sensitive", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("turn-sensitive", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("summary-sensitive", entry.Message, StringComparison.Ordinal);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class RecordingFacade(IReadOnlyList<KnowledgeSearchResult> results, RecordingOperationStore? captures = null, bool throwOnQuery = false) : INativeV1Facade
    {
        public List<string> QueryFamilies { get; } = [];
        public int PreviewCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public List<string> IdempotencyKeys { get; } = [];
        public List<string> SavedTitles { get; } = [];

        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            QueryFamilies.Add(family);
            return throwOnQuery
                ? ValueTask.FromException<object>(new InvalidOperationException("secret-content-sentinel"))
                : ValueTask.FromResult<object>(results);
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            return ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "confirmation", "fingerprint", DateTimeOffset.MaxValue, [], "saved"));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
        {
            CommitCalls++;
            IdempotencyKeys.Add(idempotencyKey);
            if (command is KnowledgeMutation { Title: { } title, Body: { } body })
            {
                SavedTitles.Add(title);
            }
            captures?.CompletedKeys.Add(idempotencyKey);
            return ValueTask.FromResult(new NativeActionReceipt(Guid.Empty, CommitCalls > 1, "completed", null));
        }
    }

    private sealed class RecordingOperationStore : INativeOperationStore
    {
        public HashSet<string> CompletedKeys { get; } = new(StringComparer.Ordinal);
        public ValueTask<NativeActionReceipt?> FindReceiptAsync(string idempotencyKey, string actorSurface, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NativeActionReceipt?>(CompletedKeys.Contains(idempotencyKey)
                ? new NativeActionReceipt(Guid.Empty, true, "completed", null)
                : null);
        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CommitFailingFacade : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "confirmation", "fingerprint", DateTimeOffset.MaxValue, [], "saved"));

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            ValueTask.FromException<NativeActionReceipt>(new NativeOperationException("confirmation-mismatch"));
    }

    private sealed class RecordingLogger : ILogger<NativeCodexHookService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
