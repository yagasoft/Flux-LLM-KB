using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Application.Ports;

/// <summary>Appends metadata-only Codex hook outcomes to the operator event projection.</summary>
public interface ICodexHookAuditWriter
{
    ValueTask AppendAsync(CodexHookAuditEvent auditEvent, CancellationToken cancellationToken);
}
