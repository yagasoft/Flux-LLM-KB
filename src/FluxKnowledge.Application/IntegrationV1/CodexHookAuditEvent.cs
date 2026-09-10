namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Metadata-only outcomes that may be projected to local operator Events.</summary>
public enum CodexHookAuditOutcome
{
    PreflightContextInjected,
    PreflightNoContext,
    InputRejected,
    ProcessingFailed
}

/// <summary>Contains no hook payload text; a bounded exception text is retained only for operator diagnosis.</summary>
public sealed record CodexHookAuditEvent(
    CodexHookAuditOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    string? ReasonCode = null,
    string? FailurePhase = null,
    string? ExceptionType = null,
    int? SqlErrorNumber = null,
    string? ExceptionText = null)
{
    public static CodexHookAuditEvent Preflight(bool contextInjected, DateTimeOffset occurredAtUtc) =>
        new(contextInjected ? CodexHookAuditOutcome.PreflightContextInjected : CodexHookAuditOutcome.PreflightNoContext, occurredAtUtc);

    public static CodexHookAuditEvent InputRejected(DateTimeOffset occurredAtUtc) =>
        new(CodexHookAuditOutcome.InputRejected, occurredAtUtc);

    public static CodexHookAuditEvent ProcessingFailed(
        string reasonCode,
        string failurePhase,
        string exceptionType,
        int? sqlErrorNumber,
        DateTimeOffset occurredAtUtc,
        string? exceptionText = null)
    {
        if (!CodexHookFailureMetadata.IsReasonCode(reasonCode)) throw new ArgumentException("A known Codex hook failure reason code is required.", nameof(reasonCode));
        if (!CodexHookFailureMetadata.IsPhase(failurePhase)) throw new ArgumentException("A known Codex hook failure phase is required.", nameof(failurePhase));
        if (!CodexHookFailureMetadata.IsExceptionType(exceptionType)) throw new ArgumentException("A known Codex hook exception category is required.", nameof(exceptionType));
        if (sqlErrorNumber is { } number && !CodexHookFailureMetadata.IsSqlErrorNumber(number)) throw new ArgumentOutOfRangeException(nameof(sqlErrorNumber));
        if (!CodexHookFailureMetadata.IsExceptionText(exceptionText)) throw new ArgumentException("Codex hook exception text exceeds the diagnostic limit.", nameof(exceptionText));
        return new(CodexHookAuditOutcome.ProcessingFailed, occurredAtUtc, reasonCode, failurePhase, exceptionType, sqlErrorNumber, exceptionText);
    }
}

/// <summary>Closed metadata vocabulary permitted for a failed Codex-hook event.</summary>
public static class CodexHookFailureMetadata
{
    public const int MaximumExceptionTextCharacters = 4_096;
    public const int MaximumFailureDetailsJsonCharacters = 32_768;

    public static bool IsReasonCode(string? value) => value is
        "commit-uncertain" or "confirmation-mismatch" or "operation-fenced" or
        "operation-conflict" or "native-operation" or "timeout" or "unexpected";

    public static bool IsPhase(string? value) => value is
        "user_prompt" or "input_validation" or "receipt_lookup" or
        "mutation_build" or "preview" or "commit";

    public static bool IsExceptionType(string? value) => value is
        "SqlException" or "NativeOperationCommitUncertainException" or
        "NativeOperationException" or "TimeoutException" or
        "OperationCanceledException" or "InvalidOperationException" or
        "ArgumentException" or "UnexpectedException";

    public static bool IsSqlErrorNumber(int value) => value is >= -32768 and <= 65535;

    public static bool IsExceptionText(string? value) => value is null ||
        (!string.IsNullOrWhiteSpace(value) && value.Length <= MaximumExceptionTextCharacters);
}
