namespace FluxKnowledge.Domain.Common;

public sealed class DomainInvariantException(string message) : InvalidOperationException(message);
