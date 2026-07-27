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

public sealed class DerivedIndexRecoverySqlSchemaException : Exception
{
    public DerivedIndexRecoverySqlSchemaException(Exception innerException)
        : base("The recovery SQL schema is incompatible with this application.", innerException) { }
}

public sealed class DerivedIndexRecoverySqlPermissionException : Exception
{
    public DerivedIndexRecoverySqlPermissionException(Exception innerException)
        : base("The recovery SQL principal cannot access required recovery data.", innerException) { }
}

public interface IDerivedIndexRecoveryStore
{
    ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(
        CancellationToken cancellationToken);

    ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
        TimeSpan lockTimeout,
        CancellationToken cancellationToken);

    ValueTask UpdateRecoveryPathAsync(
        Guid generationId,
        string indexPath,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken);

    ValueTask AppendAuditAsync(
        DerivedIndexRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken);
}
