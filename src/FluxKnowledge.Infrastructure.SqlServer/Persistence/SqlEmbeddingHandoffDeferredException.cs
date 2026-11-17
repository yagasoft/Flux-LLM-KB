namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>The Embed handoff transaction timed out before its first scheduler mutation.</summary>
internal sealed class SqlEmbeddingHandoffDeferredException()
    // EF translates provider TimeoutException before the handoff-specific pump catch.
    : Exception("GPU embedding handoff is waiting for the scheduler mutation fence.");
