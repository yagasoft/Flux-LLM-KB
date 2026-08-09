using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceActivityStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : ISourceActivityStore, ISourceCapabilityStore
{
    public async ValueTask<SourceActivity> FindOrCreateAsync(
        SourceActivityDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var activity = SourceActivity.Create(
            draft.SourceRevisionId,
            draft.ActivityKind,
            draft.ExecutionClass,
            draft.ProcessorVersion,
            draft.InputFingerprint,
            draft.RequiredCapability,
            draft.Reason,
            draft.InitialState);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await FindExistingAsync(context, draft, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Restore(existing);
        }

        var now = timeProvider.GetUtcNow();
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activity.Id.Value,
            SourceRevisionId = activity.SourceRevisionId.Value,
            ActivityKind = (int)activity.Kind,
            ExecutionClass = (int)activity.ExecutionClass,
            ProcessorVersion = activity.ProcessorVersion,
            InputFingerprint = activity.InputFingerprint,
            RequiredCapability = activity.RequiredCapability,
            State = (int)activity.State,
            Reason = activity.Reason,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            var concurrent = await FindExistingAsync(context, draft, cancellationToken).ConfigureAwait(false);
            if (concurrent is null)
            {
                throw;
            }

            return Restore(concurrent);
        }

        return activity;
    }

    private static Task<SourceActivityEntity?> FindExistingAsync(
        FluxKnowledgeDbContext context,
        SourceActivityDraft draft,
        CancellationToken cancellationToken) =>
        context.SourceActivities.SingleOrDefaultAsync(
            value => value.SourceRevisionId == draft.SourceRevisionId.Value &&
                value.ActivityKind == (int)draft.ActivityKind &&
                value.ProcessorVersion == draft.ProcessorVersion &&
                value.InputFingerprint == draft.InputFingerprint,
            cancellationToken);

    private static SourceActivity Restore(SourceActivityEntity existing) =>
        SourceActivity.Restore(
            new SourceActivityId(existing.Id),
            new SourceRevisionId(existing.SourceRevisionId),
            (SourceActivityKind)existing.ActivityKind,
            (ExecutionClass)existing.ExecutionClass,
            existing.ProcessorVersion,
            existing.InputFingerprint,
            existing.RequiredCapability,
            (SourceActivityState)existing.State,
            existing.Reason,
            existing.ResultingPipelineRecordId is not null && existing.ResultingPipelineRecordRevision is not null);

    public async ValueTask<RegisteredSourceCapability> RegisterAsync(
        RegisteredSourceCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var runnable = capability.ExecutionClass == ExecutionClass.InProcess && capability.IsRunnable;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.SourceCapabilities.SingleOrDefaultAsync(value => value.Id == capability.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.ProcessorKind, capability.ProcessorKind, StringComparison.Ordinal) ||
                !string.Equals(existing.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal) ||
                existing.ExecutionClass != (int)capability.ExecutionClass ||
                !string.Equals(existing.ProcessorFingerprint, capability.ProcessorFingerprint, StringComparison.Ordinal) ||
                !string.Equals(existing.OutputContract, capability.OutputContract, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A source capability id cannot be rebound to a different descriptor.");
            }

            return ToCapability(existing);
        }

        var now = timeProvider.GetUtcNow();
        context.SourceCapabilities.Add(new SourceCapabilityEntity
        {
            Id = capability.Id,
            ProcessorKind = capability.ProcessorKind,
            ProcessorVersion = capability.ProcessorVersion,
            ExecutionClass = (int)capability.ExecutionClass,
            AcceptedClassificationsJson = capability.AcceptedClassification == "AcceptedUtf8Text" ? "[\"text/plain\"]" : "[]",
            OutputContract = capability.OutputContract,
            ProcessorFingerprint = capability.ProcessorFingerprint,
            IsRunnable = runnable,
            RegisteredBy = "local-process",
            RegisteredAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return capability with { IsRunnable = runnable };
    }

    public async ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var capability = await context.SourceCapabilities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == capabilityId, cancellationToken).ConfigureAwait(false);
        return capability is null ? null : ToCapability(capability);
    }

    private static RegisteredSourceCapability ToCapability(SourceCapabilityEntity value) => new(
        value.Id,
        value.ProcessorKind,
        value.ProcessorVersion,
        (ExecutionClass)value.ExecutionClass,
        value.ProcessorFingerprint,
        value.IsRunnable,
        SourceActivityKind.TextExtraction,
        value.AcceptedClassificationsJson == "[\"text/plain\"]" ? "AcceptedUtf8Text" : "",
        value.OutputContract);
}
