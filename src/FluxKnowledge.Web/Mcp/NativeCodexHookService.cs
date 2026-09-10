using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Web.Mcp;

public sealed record NativeCodexHookSpecificOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);

/// <summary>Codex-compatible, fail-open response for local hook invocations.</summary>
public sealed record NativeCodexHookResponse
{
    public NativeCodexHookResponse(
        bool Continue,
        NativeCodexHookSpecificOutput? HookSpecificOutput = null,
        string? SystemMessage = null)
    {
        this.Continue = Continue;
        this.HookSpecificOutput = HookSpecificOutput;
        this.SystemMessage = string.IsNullOrWhiteSpace(SystemMessage) ? null : SystemMessage;
    }

    [JsonPropertyName("continue")]
    public bool Continue { get; }

    [JsonPropertyName("hookSpecificOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeCodexHookSpecificOutput? HookSpecificOutput { get; }

    [JsonPropertyName("systemMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemMessage { get; }
}

/// <summary>Maps the three supported Codex hooks onto the native v1 knowledge boundary.</summary>
public sealed class NativeCodexHookService(
    INativeV1Facade facade,
    INativeOperationStore operationStore,
    ILogger<NativeCodexHookService>? logger = null,
    ICodexHookAuditWriter? auditWriter = null,
    IStatusEventPublisher? statusPublisher = null,
    TimeProvider? timeProvider = null)
{
    private const int SearchLimit = 5;
    private const int MaximumSummaryCharacters = 8_000;
    private const int MaximumContextCharacters = 4_096;
    private const string ActorSurface = "codex-hook";
    private readonly INativeV1Facade _facade = facade ?? throw new ArgumentNullException(nameof(facade));
    private readonly INativeOperationStore _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
    private readonly ILogger<NativeCodexHookService> _logger = logger ?? NullLogger<NativeCodexHookService>.Instance;
    private readonly ICodexHookAuditWriter? _auditWriter = auditWriter;
    private readonly IStatusEventPublisher? _statusPublisher = statusPublisher;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<NativeCodexHookResponse> HandleAsync(
        string? eventName,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return await HandleInvalidInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return eventName switch
            {
                "UserPromptSubmit" => await HandleUserPromptSubmitAsync(payload, cancellationToken).ConfigureAwait(false),
                "PreCompact" => new NativeCodexHookResponse(true),
                "Stop" => await HandleStopAsync(payload, cancellationToken).ConfigureAwait(false),
                _ => await HandleInvalidInputAsync(cancellationToken).ConfigureAwait(false)
            };
        }
        catch (NativeCodexHookInputException)
        {
            return await HandleInvalidInputAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = DescribeFailure(exception);
            await RecordAuditAsync(
                CodexHookAuditEvent.ProcessingFailed(
                    failure.Classification,
                    failure.Phase,
                    failure.ExceptionType,
                    failure.SqlErrorNumber,
                    _timeProvider.GetUtcNow(),
                    failure.ExceptionText),
                cancellationToken).ConfigureAwait(false);
            return new NativeCodexHookResponse(true, SystemMessage: "Native Codex hook could not access local knowledge; continuing.");
        }
    }

    public static NativeCodexHookResponse InvalidInput() =>
        new(true, SystemMessage: "Native Codex hook ignored invalid input.");

    /// <summary>Records an invalid loopback-hook request without retaining its payload, then returns the fail-open envelope.</summary>
    public async ValueTask<NativeCodexHookResponse> HandleInvalidInputAsync(CancellationToken cancellationToken)
    {
        await RecordAuditAsync(CodexHookAuditEvent.InputRejected(_timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return InvalidInput();
    }

    private async ValueTask<NativeCodexHookResponse> HandleUserPromptSubmitAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var prompt = RequiredText(payload, "prompt", NativeV1ContractLimits.MaximumKnowledgeQueryCharacters);
        try
        {
            var results = await _facade.ExecuteQueryAsync(
                "knowledge",
                new NativeKnowledgeQuery(prompt, SearchLimit),
                cancellationToken).ConfigureAwait(false);
            var context = FormatContext(results);
            await RecordAuditAsync(CodexHookAuditEvent.Preflight(!string.IsNullOrEmpty(context), _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(context)
                ? new NativeCodexHookResponse(true)
                : new NativeCodexHookResponse(true, new NativeCodexHookSpecificOutput("UserPromptSubmit", context));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new NativeCodexHookFailureException("user_prompt", [prompt], exception);
        }
    }

    private async ValueTask<NativeCodexHookResponse> HandleStopAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var phase = "input_validation";
        string? sessionId = null;
        string? turnId = null;
        string? summary = null;
        try
        {
            sessionId = RequiredText(payload, "session_id", 256);
            turnId = RequiredText(payload, "turn_id", 256);
            summary = RequiredText(payload, "last_assistant_message", MaximumSummaryCharacters);
            var idempotencyKey = IdempotencyKey(sessionId, turnId);
            phase = "receipt_lookup";
            if (await _operationStore.FindReceiptAsync(idempotencyKey, ActorSurface, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new NativeCodexHookResponse(true);
            }
            phase = "mutation_build";
            var mutation = new KnowledgeMutation(
                "note_create",
                null,
                CaptureTitle(sessionId, turnId),
                summary,
                null, null, null, null, null, null);
            phase = "preview";
            var preview = await _facade.PreviewAsync("knowledge", mutation, ActorSurface, cancellationToken).ConfigureAwait(false);
            phase = "commit";
            await _facade.CommitAsync(
                "knowledge",
                mutation,
                preview.ConfirmationId,
                idempotencyKey,
                ActorSurface,
                cancellationToken).ConfigureAwait(false);
            await PublishEventsAsync(cancellationToken).ConfigureAwait(false);
            return new NativeCodexHookResponse(true);
        }
        catch (NativeCodexHookInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Native Codex Stop persistence failed at {Phase} ({Classification}).",
                phase,
                FailureClassification(exception));
            throw new NativeCodexHookFailureException(phase, [sessionId, turnId, summary], exception);
        }
    }

    private static HookFailure DescribeFailure(Exception exception)
    {
        var phase = "user_prompt";
        IReadOnlyList<string> sensitiveValues = [];
        if (exception is NativeCodexHookFailureException hookFailure)
        {
            phase = hookFailure.Phase;
            sensitiveValues = hookFailure.SensitiveValues;
            exception = hookFailure.InnerException!;
        }

        return new HookFailure(
            phase,
            FailureClassification(exception),
            ExceptionType(exception),
            SqlErrorNumber(exception),
            ExceptionText(exception, sensitiveValues));
    }

    private static string FailureClassification(Exception exception) => exception switch
    {
        NativeOperationCommitUncertainException => "commit-uncertain",
        NativeOperationException { ReasonCode: "confirmation-mismatch" } => "confirmation-mismatch",
        NativeOperationException { ReasonCode: "operation-fenced" } => "operation-fenced",
        NativeOperationException { ReasonCode: "operation-conflict" } => "operation-conflict",
        NativeOperationException => "native-operation",
        TimeoutException => "timeout",
        _ => "unexpected"
    };

    private static string ExceptionType(Exception exception) => exception switch
    {
        SqlException => "SqlException",
        NativeOperationCommitUncertainException => "NativeOperationCommitUncertainException",
        NativeOperationException => "NativeOperationException",
        TimeoutException => "TimeoutException",
        OperationCanceledException => "OperationCanceledException",
        InvalidOperationException => "InvalidOperationException",
        ArgumentException => "ArgumentException",
        _ => "UnexpectedException"
    };

    private static int? SqlErrorNumber(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException) return sqlException.Number;
        }

        return null;
    }

    private static string ExceptionText(Exception exception, IReadOnlyList<string> sensitiveValues)
    {
        try
        {
            var text = exception.ToString();
            foreach (var sensitiveValue in sensitiveValues
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(value => value.Length))
            {
                text = text.Replace(sensitiveValue, "[hook-input-redacted]", StringComparison.Ordinal);
                var jsonEscapedValue = JsonSerializer.Serialize(sensitiveValue);
                text = text.Replace(jsonEscapedValue[1..^1], "[hook-input-redacted]", StringComparison.Ordinal);
            }

            return Truncate(text, CodexHookFailureMetadata.MaximumExceptionTextCharacters);
        }
        catch (Exception)
        {
            return Truncate(exception.GetType().FullName ?? "Exception", CodexHookFailureMetadata.MaximumExceptionTextCharacters);
        }
    }

    private async ValueTask RecordAuditAsync(CodexHookAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (_auditWriter is null) return;
        try
        {
            await _auditWriter.AppendAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            await PublishEventsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Native Codex hook audit persistence failed.");
        }
    }

    private async ValueTask PublishEventsAsync(CancellationToken cancellationToken)
    {
        if (_statusPublisher is null) return;
        try
        {
            await _statusPublisher.PublishAsync(
                new StatusChanged(null, "events", _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Native Codex hook event refresh notification failed.");
        }
    }

    private static string RequiredText(JsonElement payload, string propertyName, int maximumCharacters)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new NativeCodexHookInputException();
        }

        var value = Normalise(property.GetString());
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
        {
            throw new NativeCodexHookInputException();
        }

        return value;
    }

    private static string FormatContext(object result)
    {
        if (result is not IEnumerable<KnowledgeSearchResult> rows) return string.Empty;
        var builder = new StringBuilder("Relevant local knowledge:");
        foreach (var row in rows)
        {
            var title = Normalise(row.Title);
            var content = Normalise(row.Content);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) continue;
            var next = $"\n- {title}: {content}";
            if (builder.Length + next.Length > MaximumContextCharacters) break;
            builder.Append(next);
        }

        return builder.Length == "Relevant local knowledge:".Length ? string.Empty : builder.ToString();
    }

    private static string Normalise(string? value) => new string((value ?? string.Empty)
        .Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t')
        .ToArray())
        .Trim()
        .Normalize(NormalizationForm.FormC);

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string IdempotencyKey(string sessionId, string turnId) =>
        "codex-stop-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\u001f{turnId}")));

    private static string CaptureTitle(string sessionId, string turnId) =>
        $"Codex turn {Truncate(turnId, 220)} [{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..16]}]";

    private sealed class NativeCodexHookInputException : Exception;

    private sealed class NativeCodexHookFailureException(string phase, IEnumerable<string?> sensitiveValues, Exception innerException) : Exception(null, innerException)
    {
        public string Phase { get; } = phase;
        public IReadOnlyList<string> SensitiveValues { get; } = sensitiveValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private sealed record HookFailure(string Phase, string Classification, string ExceptionType, int? SqlErrorNumber, string ExceptionText);

}
