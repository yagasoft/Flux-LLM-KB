namespace FluxKnowledge.Application.Ports;

/// <summary>A historical digest, never by itself permission to reuse or publish a revision.</summary>
public sealed record SourceVerificationDigest(string Sha256, long BytesRead,
    SourceFileObservation Observation, DateTimeOffset AttestedAtUtc);
