namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Metadata-only outcomes that may be projected to local operator Events.</summary>
public enum CodexHookAuditOutcome
{
    PreflightContextInjected,
    PreflightNoContext,
    InputRejected,
    ProcessingFailed
}

/// <summary>Contains no prompt, assistant-message, session, turn, or retrieved-context text.</summary>
public sealed record CodexHookAuditEvent(
    CodexHookAuditOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    string? ReasonCode = null)
{
    public static CodexHookAuditEvent Preflight(bool contextInjected, DateTimeOffset occurredAtUtc) =>
        new(contextInjected ? CodexHookAuditOutcome.PreflightContextInjected : CodexHookAuditOutcome.PreflightNoContext, occurredAtUtc);

    public static CodexHookAuditEvent InputRejected(DateTimeOffset occurredAtUtc) =>
        new(CodexHookAuditOutcome.InputRejected, occurredAtUtc);

    public static CodexHookAuditEvent ProcessingFailed(string reasonCode, DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(CodexHookAuditOutcome.ProcessingFailed, occurredAtUtc, reasonCode);
    }
}
