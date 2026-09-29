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
    string? ExceptionText = null,
    string? PolicyVersion = null,
    int? ExaminedCount = null,
    int? InjectedCount = null,
    long? ElapsedMilliseconds = null)
{
    public static CodexHookAuditEvent Preflight(bool contextInjected, DateTimeOffset occurredAtUtc) =>
        new(contextInjected ? CodexHookAuditOutcome.PreflightContextInjected : CodexHookAuditOutcome.PreflightNoContext, occurredAtUtc);

    public static CodexHookAuditEvent Preflight(CodexPromptContextResult result, DateTimeOffset occurredAtUtc)
    {
        if (!CodexHookPreflightMetadata.IsReasonCode(result.ReasonCode))
            throw new ArgumentException("A known preflight reason is required.", nameof(result));
        if (result.ExaminedCount is < 0 or > 10 || result.InjectedCount is < 0 or > 3 ||
            result.ElapsedMilliseconds is < 0 or > 60_000 ||
            (result.AdditionalContext is null) != (result.InjectedCount == 0) ||
            (result.ReasonCode == "context-injected") != (result.AdditionalContext is not null))
            throw new ArgumentOutOfRangeException(nameof(result));
        return new(result.AdditionalContext is null ? CodexHookAuditOutcome.PreflightNoContext :
            CodexHookAuditOutcome.PreflightContextInjected, occurredAtUtc, result.ReasonCode,
            PolicyVersion: CodexPromptContextPolicy.Version,
            ExaminedCount: result.ExaminedCount, InjectedCount: result.InjectedCount,
            ElapsedMilliseconds: result.ElapsedMilliseconds);
    }

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

public static class CodexHookPreflightMetadata
{
    public static bool IsReasonCode(string? value) => value is
        "context-injected" or "context-disabled" or "workspace-missing" or
        "scope-unavailable" or "query-insufficient" or "no-matching-evidence" or
        "evidence-unavailable" or "context-budget" or "retrieval-unavailable" or
        "retrieval-timeout";
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
