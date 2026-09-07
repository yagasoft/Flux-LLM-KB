using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Persists the restricted Codex-hook operator evidence separately from retained knowledge.</summary>
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
            CodexHookAuditOutcome.ProcessingFailed => Draft("codex_hook.processing_failed", "warning", new { reasonCode = ValidateReasonCode(auditEvent.ReasonCode) }, auditEvent.OccurredAtUtc),
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
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128 ||
            !reasonCode.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new ArgumentException("A bounded machine-readable reason code is required.", nameof(reasonCode));
        }

        return reasonCode;
    }
}
