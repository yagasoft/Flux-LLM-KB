using System.Collections.Immutable;
using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

public sealed record DerivedIndexRecoverySqlSnapshot(
    Guid? ActiveGenerationId,
    IndexGenerationDescriptor? Generation,
    ImmutableArray<CanonicalVector> Membership,
    ImmutableHashSet<Guid> ReferencedGenerationIds,
    ImmutableHashSet<string> ReferencedIndexPaths);

public interface IDerivedIndexRecoveryLease : IAsyncDisposable;

public interface IDerivedIndexRecoveryStore
{
    ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(
        CancellationToken cancellationToken);

    ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
        TimeSpan lockTimeout,
        CancellationToken cancellationToken);

    ValueTask AppendAuditAsync(
        DerivedIndexRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken);
}
