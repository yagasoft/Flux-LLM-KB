using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_selected_recovery_removes_only_earlier_matched_failure_events_before_pagination(bool pdf)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, "document-ocr-runtime-failed");
        var recovery = await CreateCommittedRecoveryAsync(seed, pdf);
        var publication = await PrepareRecoveryPublicationAsync(seed, recovery.ChildId);
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        var activityFailure = RecoveryFailureEvent(seed, "activity.failed", earlier);
        var pipelineFailure = RecoveryFailureEvent(seed, "pipeline.failed", earlier.AddTicks(1));
        var independent = new AuditEventEntity { EventType = "source.access_denied", EventFamily = "source",
            Severity = "error", SourceRootId = seed.RootId, SourceRevisionId = seed.ParentId,
            OccurredAtUtc = earlier.AddMinutes(-1), Actor = "test", DetailsJson = "{}" };
        await using (var arrange = await seed.Factory.CreateDbContextAsync())
        {
            arrange.AuditEvents.AddRange(activityFailure, pipelineFailure, independent);
            await arrange.SaveChangesAsync();
        }
        var reader = new SqlOperatorEventProjectionReader(seed.Factory);
        var query = new OperatorEventQuery(new OperatorEventFilters(Severity: "error"), PageSize: 1);
        Assert.Equal(pipelineFailure.Id, Assert.Single((await reader.ReadPageAsync(query, CancellationToken.None)).Items).Id);

        await new SqlStageTransitionStore(seed.Factory).TransitionAsync(publication, CancellationToken.None);
        await using (var pause = await seed.Factory.CreateDbContextAsync())
        {
            // Pausing a root does not turn an already proved success back into a failure.
            (await pause.SourceRootConfigurations.SingleAsync(value => value.Id == seed.RootId)).State = (int)SourceRootState.Paused;
            await pause.SaveChangesAsync();
        }
        var page = await reader.ReadPageAsync(query, CancellationToken.None);
        Assert.Equal(independent.Id, Assert.Single(page.Items).Id);
        Assert.Null(page.NextCursor);
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Equal(3, await verify.AuditEvents.CountAsync(value => value.Id == activityFailure.Id || value.Id == pipelineFailure.Id || value.Id == independent.Id));
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
    }

    private static AuditEventEntity RecoveryFailureEvent(ImageFailureSeed seed, string eventType, DateTimeOffset occurred) => new()
    {
        EventType = eventType, EventFamily = eventType.Split('.')[0], Severity = "error", Actor = "test", DetailsJson = "{}",
        PipelineRecordId = seed.RecordId, OccurredAtUtc = occurred,
        SourceRootId = eventType == "activity.failed" ? seed.RootId : null,
        SourceRevisionId = eventType == "activity.failed" ? seed.ChildId : null,
        SourceActivityId = eventType == "activity.failed" ? seed.FailedChildActivityId : null,
        CorrelationId = eventType == "activity.failed" ? $"source:{seed.ChildId:N}" : $"pipeline:{seed.RecordId:N}:1"
    };
}
