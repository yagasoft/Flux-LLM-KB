namespace FluxKnowledge.Integrations.Files;

/// <summary>A source path policy refusal, distinct from an operating-system access denial.</summary>
internal sealed class SourcePathSecurityException(string reasonCode, string message) : UnauthorizedAccessException(message)
{
    public string ReasonCode { get; } = reasonCode;
}
