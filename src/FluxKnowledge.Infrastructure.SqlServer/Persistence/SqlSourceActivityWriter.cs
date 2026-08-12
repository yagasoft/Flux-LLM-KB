using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Shared transactional writer for source activities and their sanitised operator evidence.</summary>
public sealed class SqlSourceActivityWriter(TimeProvider timeProvider)
{
    public async ValueTask<SourceActivity> FindOrCreateAsync(
        FluxKnowledgeDbContext context,
        SourceActivityDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        var existing = await FindExistingAsync(context, draft, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Restore(existing);
        }

        var activity = SourceActivity.Create(
            draft.SourceRevisionId,
            draft.ActivityKind,
            draft.ExecutionClass,
            draft.ProcessorVersion,
            draft.InputFingerprint,
            draft.RequiredCapability,
            draft.Reason,
            draft.InitialState,
            draft.DescriptorFingerprint);
        var now = timeProvider.GetUtcNow();
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activity.Id.Value,
            SourceRevisionId = activity.SourceRevisionId.Value,
            ActivityKind = (int)activity.Kind,
            ExecutionClass = (int)activity.ExecutionClass,
            ProcessorVersion = activity.ProcessorVersion,
            InputFingerprint = activity.InputFingerprint,
            DescriptorFingerprint = activity.DescriptorFingerprint,
            RequiredCapability = activity.RequiredCapability,
            State = (int)activity.State,
            Reason = activity.Reason,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        var rootId = context.SourceRevisions.Local
            .Where(value => value.Id == activity.SourceRevisionId.Value)
            .Select(value => (Guid?)value.SourceRootId)
            .SingleOrDefault()
            ?? await context.SourceRevisions
                .Where(value => value.Id == activity.SourceRevisionId.Value)
                .Select(value => value.SourceRootId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
        OperatorEventAppender.Add(context, OperatorEventDraft.ActivityPlanned(
            activity.Id.Value,
            activity.SourceRevisionId.Value,
            rootId,
            new { kind = activity.Kind.ToString(), executionClass = activity.ExecutionClass.ToString() },
            activity.State == SourceActivityState.DeferredUnsupported));
        return activity;
    }

    public static Task<SourceActivityEntity?> FindExistingAsync(
        FluxKnowledgeDbContext context,
        SourceActivityDraft draft,
        CancellationToken cancellationToken) =>
        context.SourceActivities.SingleOrDefaultAsync(
            value => value.SourceRevisionId == draft.SourceRevisionId.Value &&
                value.ActivityKind == (int)draft.ActivityKind &&
                value.ProcessorVersion == draft.ProcessorVersion &&
                value.DescriptorFingerprint == (draft.DescriptorFingerprint ?? SourceActivityEntity.LegacyDescriptorFingerprint) &&
                value.InputFingerprint == draft.InputFingerprint,
            cancellationToken);

    public static SourceActivity Restore(SourceActivityEntity existing) =>
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
            existing.ResultingPipelineRecordId is not null && existing.ResultingPipelineRecordRevision is not null,
            existing.DescriptorFingerprint);
}
