namespace FluxKnowledge.Application.Pipeline;

/// <summary>The candidate is valid, but searchable membership changed before publication.</summary>
public sealed class PublicationSnapshotConflictException(string message) : InvalidOperationException(message);
