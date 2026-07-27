namespace FluxKnowledge.Application.Pipeline;

public sealed class IndexGenerationStaleException(string message) : InvalidOperationException(message);
