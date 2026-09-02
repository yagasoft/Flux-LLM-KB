using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web.Mcp;

public sealed record NativeCodexHookSpecificOutput(string HookEventName, string AdditionalContext);

/// <summary>Codex-compatible, fail-open response for local hook invocations.</summary>
public sealed record NativeCodexHookResponse(
    bool Continue,
    NativeCodexHookSpecificOutput? HookSpecificOutput = null,
    string? SystemMessage = null);

/// <summary>Maps the three supported Codex hooks onto the native v1 knowledge boundary.</summary>
public sealed class NativeCodexHookService(INativeV1Facade facade, INativeOperationStore operationStore)
{
    private const int SearchLimit = 5;
    private const int MaximumSummaryCharacters = 8_000;
    private const int MaximumContextCharacters = 4_096;
    private const string ActorSurface = "codex-hook";
    private readonly INativeV1Facade _facade = facade ?? throw new ArgumentNullException(nameof(facade));
    private readonly INativeOperationStore _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));

    public async ValueTask<NativeCodexHookResponse> HandleAsync(
        string? eventName,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            if (payload.ValueKind != JsonValueKind.Object) return InvalidInput();

            return eventName switch
            {
                "UserPromptSubmit" => await HandleUserPromptSubmitAsync(payload, cancellationToken).ConfigureAwait(false),
                "PreCompact" => new NativeCodexHookResponse(true),
                "Stop" => await HandleStopAsync(payload, cancellationToken).ConfigureAwait(false),
                _ => InvalidInput()
            };
        }
        catch (NativeCodexHookInputException)
        {
            return InvalidInput();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new NativeCodexHookResponse(true, SystemMessage: "Native Codex hook could not access local knowledge; continuing.");
        }
    }

    public static NativeCodexHookResponse InvalidInput() =>
        new(true, SystemMessage: "Native Codex hook ignored invalid input.");

    private async ValueTask<NativeCodexHookResponse> HandleUserPromptSubmitAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var prompt = RequiredText(payload, "prompt", NativeV1ContractLimits.MaximumKnowledgeQueryCharacters);
        var results = await _facade.ExecuteQueryAsync(
            "knowledge",
            new NativeKnowledgeQuery(prompt, SearchLimit),
            cancellationToken).ConfigureAwait(false);
        var context = FormatContext(results);
        return string.IsNullOrEmpty(context)
            ? new NativeCodexHookResponse(true)
            : new NativeCodexHookResponse(true, new NativeCodexHookSpecificOutput("UserPromptSubmit", context));
    }

    private async ValueTask<NativeCodexHookResponse> HandleStopAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var sessionId = RequiredText(payload, "session_id", 256);
        var turnId = RequiredText(payload, "turn_id", 256);
        var summary = RequiredText(payload, "last_assistant_message", MaximumSummaryCharacters);
        var idempotencyKey = IdempotencyKey(sessionId, turnId);
        if (await _operationStore.FindReceiptAsync(idempotencyKey, ActorSurface, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new NativeCodexHookResponse(true);
        }
        var mutation = new KnowledgeMutation(
            "note_create",
            null,
            CaptureTitle(sessionId, turnId),
            summary,
            null, null, null, null, null, null);
        var preview = await _facade.PreviewAsync("knowledge", mutation, ActorSurface, cancellationToken).ConfigureAwait(false);
        await _facade.CommitAsync(
            "knowledge",
            mutation,
            preview.ConfirmationId,
            idempotencyKey,
            ActorSurface,
            cancellationToken).ConfigureAwait(false);
        return new NativeCodexHookResponse(true);
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

}
