using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>A historical digest, never by itself permission to reuse or publish a revision.</summary>
public sealed record SourceVerificationDigest(string Sha256, long BytesRead,
    SourceFileObservation Observation, DateTimeOffset AttestedAtUtc);

public interface ISourceVerificationContentReader
{
    ValueTask<SourceVerificationDigest> ReadAsync(SourceRootConfiguration root, string relativePath,
        ISourceVerificationReadBudget budget, CancellationToken cancellationToken);
}

public interface ISourceVerificationRetainedStore
{
    ValueTask<IRetainedFileObservationLease> OpenVerificationObservationAsync(string relativePath, string sha256,
        long byteLength, ISourceVerificationReadBudget budget, CancellationToken cancellationToken);
}
