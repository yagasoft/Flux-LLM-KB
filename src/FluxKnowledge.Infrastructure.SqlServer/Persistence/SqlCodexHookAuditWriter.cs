using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Persists bounded Codex-hook diagnostic evidence separately from retained knowledge.</summary>
public sealed class SqlCodexHookAuditWriter(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : ICodexHookAuditWriter
{
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async ValueTask AppendAsync(CodexHookAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var draft = auditEvent.Outcome switch
        {
            CodexHookAuditOutcome.PreflightContextInjected => Draft("codex_hook.preflight_completed", "information", new { state = "context_injected" }, auditEvent.OccurredAtUtc),
            CodexHookAuditOutcome.PreflightNoContext => Draft("codex_hook.preflight_completed", "information", new { state = "no_context" }, auditEvent.OccurredAtUtc),
            CodexHookAuditOutcome.InputRejected => Draft("codex_hook.input_rejected", "warning", new { reasonCode = "invalid_input" }, auditEvent.OccurredAtUtc),
            CodexHookAuditOutcome.ProcessingFailed => Draft("codex_hook.processing_failed", "warning", FailureDetails(auditEvent), auditEvent.OccurredAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(auditEvent))
        };

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        OperatorEventAppender.Add(context, draft);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private OperatorEventDraft Draft(string eventType, string severity, object details, DateTimeOffset occurredAtUtc) =>
        new(
            eventType,
            "codex_hook",
            severity,
            "codex-hook",
            occurredAtUtc.ToUniversalTime(),
            CorrelationId: "codex-hook:" + Guid.NewGuid().ToString("N"),
            Details: details);

    private static string ValidateReasonCode(string? reasonCode)
    {
        if (!CodexHookFailureMetadata.IsReasonCode(reasonCode))
        {
            throw new ArgumentException("A known Codex hook failure reason code is required.", nameof(reasonCode));
        }

        return reasonCode!;
    }

    private static object FailureDetails(CodexHookAuditEvent auditEvent)
    {
        var reasonCode = ValidateReasonCode(auditEvent.ReasonCode);
        var phase = ValidatePhase(auditEvent.FailurePhase);
        var exceptionType = ValidateExceptionType(auditEvent.ExceptionType);
        var exceptionText = ValidateExceptionText(auditEvent.ExceptionText);
        if (auditEvent.SqlErrorNumber is { } number && !CodexHookFailureMetadata.IsSqlErrorNumber(number))
        {
            throw new ArgumentOutOfRangeException(nameof(auditEvent.SqlErrorNumber));
        }
        if (exceptionText is not null)
        {
            return auditEvent.SqlErrorNumber is { } sqlErrorNumberWithText
                ? new { reasonCode, phase, exceptionType, sqlErrorNumber = sqlErrorNumberWithText, exceptionText }
                : new { reasonCode, phase, exceptionType, exceptionText };
        }
        return auditEvent.SqlErrorNumber is { } sqlErrorNumber
            ? new { reasonCode, phase, exceptionType, sqlErrorNumber }
            : new { reasonCode, phase, exceptionType };
    }

    private static string ValidatePhase(string? phase)
    {
        if (!CodexHookFailureMetadata.IsPhase(phase))
        {
            throw new ArgumentException("A known Codex hook failure phase is required.", nameof(phase));
        }

        return phase!;
    }

    private static string ValidateExceptionType(string? exceptionType)
    {
        if (!CodexHookFailureMetadata.IsExceptionType(exceptionType))
        {
            throw new ArgumentException("A known Codex hook exception category is required.", nameof(exceptionType));
        }

        return exceptionType!;
    }

    private static string? ValidateExceptionText(string? exceptionText)
    {
        if (!CodexHookFailureMetadata.IsExceptionText(exceptionText))
        {
            throw new ArgumentException("Codex hook exception text exceeds the diagnostic limit.", nameof(exceptionText));
        }

        return exceptionText;
    }
}
