using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlIndexSegmentOwnerStore(IDbContextFactory<FluxKnowledgeDbContext> contexts,
    TimeProvider clock, IGpuInteractiveOwnerProbe probe) : IIndexSegmentOwnerStore
{
    public async ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instanceId, CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("segment-owner-instance-invalid");
        var identity = probe.Current;
        identity.Validate();
        // Stable identity makes an acknowledgement-lost transaction retry reuse its own owner row.
        var id = Guid.NewGuid();
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await SqlSegmentedIndexStore.AcquireSharedTransactionAsync(context, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            var current = await context.IndexSegments.AsNoTracking().SingleOrDefaultAsync(value => value.Id == segment.Id, cancellationToken);
            if (current is null || current.State != 1 || SqlSegmentedIndexStore.Describe(current) != segment)
                throw new InvalidOperationException("segment-retiring-or-proof-changed");
            var existing = await context.IndexSegmentOwners.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (existing is null)
                context.IndexSegmentOwners.Add(new()
                {
                    Id = id, SegmentId = segment.Id, OwnerInstanceId = instanceId, OwnerProcessId = identity.ProcessId,
                    OwnerStartedAtUtc = identity.StartedAtUtc, OwnerMachineFingerprint = identity.MachineFingerprint,
                    State = 0, CreatedAtUtc = clock.GetUtcNow()
                });
            else ValidateOwner(existing);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        });
    }

    public async ValueTask OpenedAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var owner = await context.IndexSegmentOwners.SingleAsync(value => value.Id == ownerId, cancellationToken);
        ValidateOwner(owner);
        if (owner.State is not (0 or 1)) throw new InvalidOperationException("segment-owner-state-invalid");
        owner.State = 1;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask ReleasedAfterDisposalAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var owner = await context.IndexSegmentOwners.SingleOrDefaultAsync(value => value.Id == ownerId, cancellationToken);
        if (owner is null) return;
        ValidateOwner(owner);
        context.IndexSegmentOwners.Remove(owner);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> segmentIds, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(segmentIds, StringComparer.Ordinal);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        foreach (var page in segmentIds.Chunk(256))
        {
            var sealedIds = await context.IndexSegments.AsNoTracking().Where(value => page.Contains(value.Id) && value.State == 1)
                .Select(value => value.Id).ToArrayAsync(cancellationToken);
            result.ExceptWith(sealedIds);
        }
        return result;
    }

    private void ValidateOwner(IndexSegmentOwnerEntity value)
    {
        var owner = probe.Current;
        if (value.OwnerProcessId != owner.ProcessId || value.OwnerStartedAtUtc != owner.StartedAtUtc || value.OwnerMachineFingerprint != owner.MachineFingerprint)
            throw new InvalidOperationException("segment-owner-incarnation-mismatch");
    }
}
