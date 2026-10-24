using System.Data;
using FluxKnowledge.Application.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Read-only evidence using the same rebuild admission predicates as the worker.</summary>
public static class SqlPipelineActivityQuery
{
    public static IQueryable<PipelineJobActivityRow> Jobs(FluxKnowledgeDbContext context, DateTimeOffset now,
        DeploymentHoldAdmission admission, SqlSelectedBranchReadScope selectedBranches, SqlGpuActivityReadEvidence gpuEvidence)
    {
        selectedBranches.EnsureFor(context);
        selectedBranches.EnsureJobFactsFor(context, now, admission, gpuEvidence);
        var sql = selectedBranches.HasJobFacts ? MaterializedJobsSql : JobsSql(selectedBranches.HasGpuFacts);
        return context.Database.SqlQueryRaw<PipelineJobActivityRow>(sql, Parameters(now, admission, gpuEvidence));
    }

    private static string JobsSql(bool facts)
    {
        var active = facts ? "COALESCE(gpu.HasActiveGpu, 0) = 1" : $$"""
            EXISTS (SELECT 1 FROM GpuMiniTasks t WHERE t.ParentJobId = j.Id
                AND t.SourceRevision = j.SourceRevision AND {{OwnedTask("t", false)}})
            """;
        var ready = facts ? "COALESCE(gpu.HasReadyGpu, 0) = 1" : """
            EXISTS (SELECT 1 FROM GpuMiniTasks t WHERE t.ParentJobId = j.Id
                AND t.SourceRevision = j.SourceRevision AND t.State = 0 AND (t.DeferredUntilUtc IS NULL OR t.DeferredUntilUtc <= @nowUtc))
            """;
        var uncertain = facts ? "COALESCE(gpu.HasUncertainGpu, 0) = 1" : """
            EXISTS (SELECT 1 FROM GpuMiniTasks t WHERE t.ParentJobId = j.Id
                AND t.SourceRevision = j.SourceRevision AND t.State = 3)
            """;
        var waiting = facts ? "COALESCE(gpu.HasWaitingGpu, 0) = 1" : $$"""
            EXISTS (SELECT 1 FROM GpuMiniTasks t WHERE t.ParentJobId = j.Id
                AND t.SourceRevision = j.SourceRevision AND
                ((t.State = 0 AND t.DeferredUntilUtc > @nowUtc) OR {{OwnedTask("t", true)}}))
            """;
        var sql = $$"""
            SELECT j.Id, j.PipelineRecordId, s.SourceRootId, j.Stage, j.PublicState, j.SourceRevision,
              p.CurrentStage,
              CAST(CASE WHEN {{DirectBranchMismatch}} THEN 0 ELSE 1 END AS bit) AS MatchesSelectedFileBranch,
              CASE WHEN s.OriginKind IN (2,3) THEN s.ParentSourceRevisionId ELSE s.Id END AS LogicalSourceRevisionId,
              COALESCE((CASE WHEN s.OriginKind IN (2,3) THEN owner.CanonicalPath ELSE s.CanonicalPath END) COLLATE Latin1_General_100_BIN2,
                CASE WHEN identitySource.SourceKind = N'local file' THEN identitySource.StableKey
                  ELSE N'Local ingress (' + identitySource.SourceKind + N')' END COLLATE Latin1_General_100_BIN2) AS FullPath,
              CASE WHEN s.OriginKind = 1 THEN owner.CanonicalPath
                WHEN s.OriginKind IN (2,3) AND owner.OriginKind = 1 THEN archiveOwner.CanonicalPath END AS ArchivePath,
              (SELECT MAX(currentAttempt.StartedAtUtc) FROM JobAttempts currentAttempt
                WHERE currentAttempt.JobId = j.Id AND currentAttempt.LeaseGeneration = j.LeaseGeneration
                  AND currentAttempt.LeaseOwner COLLATE Latin1_General_100_BIN2 = j.LeaseOwner COLLATE Latin1_General_100_BIN2
                  AND currentAttempt.CompletedAtUtc IS NULL
                  AND j.LeaseExpiresAtUtc > @nowUtc) AS AttemptStartedAtUtc,
              CAST(CASE WHEN {{HistoricalBinding}} THEN 1 ELSE 0 END AS bit) AS IsHistorical,
              CAST(CASE WHEN (s.Id IS NULL OR r.State = 0 OR {{SqlCorpusRebuildEligibility.ActiveJob("j.Id")}})
                AND {{SqlCorpusRebuildEligibility.Admission("j.Id")}}
                AND {{SqlCorpusRebuildEligibility.DeploymentAdmission("j.Id")}} THEN 1 ELSE 0 END AS bit) AS SourceAdmitted,
              CAST(CASE WHEN j.LeaseOwner IS NOT NULL AND j.LeaseGeneration > 0 AND j.LeaseExpiresAtUtc > @nowUtc
                THEN 1 ELSE 0 END AS bit) AS HasWorkerLease,
              CAST(CASE WHEN {{active}} THEN 1 ELSE 0 END AS bit) AS HasActiveGpu,
              CAST(CASE WHEN {{ready}} THEN 1 ELSE 0 END AS bit) AS HasReadyGpu,
              CAST(CASE WHEN {{uncertain}} THEN 1 ELSE 0 END AS bit) AS HasUncertainGpu,
              CAST(CASE WHEN {{waiting}} THEN 1 ELSE 0 END AS bit) AS HasWaitingGpu,
              CAST(CASE WHEN EXISTS (SELECT 1 FROM OutboxMessages dispatch WHERE dispatch.JobId = j.Id
                AND dispatch.PipelineRecordId = j.PipelineRecordId AND dispatch.SourceRevision = j.SourceRevision
                AND dispatch.Stage = j.Stage AND dispatch.Operation COLLATE Latin1_General_100_BIN2 = j.Operation COLLATE Latin1_General_100_BIN2
                AND dispatch.DispatchedAtUtc IS NULL) THEN 1 ELSE 0 END AS bit) AS HasPendingDispatch,
              CAST(CASE WHEN j.DueAtUtc <= @nowUtc THEN 1 ELSE 0 END AS bit) AS IsDue,
              j.Reason,
              COALESCE((SELECT MAX(attempt.StartedAtUtc) FROM JobAttempts attempt WHERE attempt.JobId = j.Id),
                p.RegisteredAtUtc) AS RecordedAtUtc
            FROM Jobs j
            INNER JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
            INNER JOIN SourceIdentities identitySource ON identitySource.Id = p.SourceIdentityId
            LEFT JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
            LEFT JOIN SourceRootConfigurations r ON r.Id = s.SourceRootId
            LEFT JOIN SourceRevisions owner ON owner.Id = s.ParentSourceRevisionId
            LEFT JOIN SourceRevisions archiveOwner ON archiveOwner.Id = owner.ParentSourceRevisionId
            {{(facts ? "LEFT JOIN #FluxPipelineGpuFacts gpu ON gpu.ParentJobId = j.Id AND gpu.SourceRevision = j.SourceRevision" : "")}}
            WHERE j.PublicState <> 4
            """;
        return sql;
    }

    private const string JobFactColumns = """
        Id, PipelineRecordId, SourceRootId, LogicalSourceRevisionId, SourceRevision, CurrentStage,
        MatchesSelectedFileBranch, Stage, PublicState, IsHistorical, SourceAdmitted, HasWorkerLease,
        HasActiveGpu, HasReadyGpu, HasUncertainGpu, HasWaitingGpu, HasPendingDispatch, IsDue, Reason
        """;

    internal static Task PopulateJobFactsAsync(FluxKnowledgeDbContext context, DateTimeOffset now,
        DeploymentHoldAdmission admission, SqlGpuActivityReadEvidence evidence, CancellationToken cancellationToken)
    {
        // Interpolated fragments are fixed SQL definitions; runtime values remain
        // SqlParameters, as in the original composed job query.
        var sql = $$"""
            INSERT INTO #FluxPipelineJobFacts ({{JobFactColumns}})
            SELECT {{JobFactColumns}} FROM (
            {{JobsSql(true)}}
            ) source;
            """;
        return context.Database.ExecuteSqlRawAsync(sql, Parameters(now, admission, evidence), cancellationToken);
    }

    // Only identity/classification is retained. Paths and attempt metadata are read
    // from the same Snapshot image when a consumer actually projects those fields.
    private static string MaterializedJobsSql => $$"""
        SELECT {{string.Join(", ", JobFactColumns.Split(',').Select(column => "facts." + column.Trim()))}},
          COALESCE((CASE WHEN s.OriginKind IN (2,3) THEN owner.CanonicalPath ELSE s.CanonicalPath END) COLLATE Latin1_General_100_BIN2,
            CASE WHEN identitySource.SourceKind = N'local file' THEN identitySource.StableKey
              ELSE N'Local ingress (' + identitySource.SourceKind + N')' END COLLATE Latin1_General_100_BIN2) AS FullPath,
          CASE WHEN s.OriginKind = 1 THEN owner.CanonicalPath
            WHEN s.OriginKind IN (2,3) AND owner.OriginKind = 1 THEN archiveOwner.CanonicalPath END AS ArchivePath,
          (SELECT MAX(currentAttempt.StartedAtUtc) FROM JobAttempts currentAttempt
            WHERE currentAttempt.JobId = j.Id AND currentAttempt.LeaseGeneration = j.LeaseGeneration
              AND currentAttempt.LeaseOwner COLLATE Latin1_General_100_BIN2 = j.LeaseOwner COLLATE Latin1_General_100_BIN2
              AND currentAttempt.CompletedAtUtc IS NULL AND j.LeaseExpiresAtUtc > @nowUtc) AS AttemptStartedAtUtc,
          COALESCE((SELECT MAX(attempt.StartedAtUtc) FROM JobAttempts attempt WHERE attempt.JobId = j.Id),
            p.RegisteredAtUtc) AS RecordedAtUtc
        FROM #FluxPipelineJobFacts facts
        INNER JOIN Jobs j ON j.Id = facts.Id
        INNER JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
        INNER JOIN SourceIdentities identitySource ON identitySource.Id = p.SourceIdentityId
        LEFT JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
        LEFT JOIN SourceRevisions owner ON owner.Id = s.ParentSourceRevisionId
        LEFT JOIN SourceRevisions archiveOwner ON archiveOwner.Id = owner.ParentSourceRevisionId
        """;

    public static IQueryable<GpuTaskActivityRow> GpuTasks(FluxKnowledgeDbContext context, DateTimeOffset now,
        DeploymentHoldAdmission admission, SqlSelectedBranchReadScope selectedBranches, SqlGpuActivityReadEvidence gpuEvidence)
    {
        selectedBranches.EnsureFor(context);
        var sql = $$"""
            SELECT t.Id, j.PipelineRecordId, s.SourceRootId, t.CreatedAtUtc,
              CASE
                WHEN t.State = 3 THEN N'Outcome uncertain'
                WHEN t.State = 1 AND {{OwnedTask("t", false)}} THEN N'Active'
                WHEN t.State = 1 AND {{OwnedTask("t", true)}} THEN N'Awaiting dispatch'
                WHEN t.State = 1 THEN N'Ownership unconfirmed'
                WHEN t.ParentJobId IS NOT NULL AND ({{HistoricalBinding}}) THEN N'Historical unresolved'
                WHEN t.ParentJobId IS NULL AND (t.InteractiveCancellationRequested = 1 OR t.QueueDeadlineUtc <= @nowUtc)
                  THEN N'Ownership unconfirmed'
                WHEN t.ParentJobId IS NOT NULL AND (j.Id IS NULL OR j.SourceRevision <> t.SourceRevision OR j.PublicState NOT IN (2,3))
                  THEN N'Ownership unconfirmed'
                WHEN t.DeferredUntilUtc > @nowUtc THEN N'Deferred'
                WHEN (t.ParentJobId IS NOT NULL AND ((r.State <> 0 AND NOT ({{SqlCorpusRebuildEligibility.ActiveJob("j.Id")}})) OR NOT ({{SqlCorpusRebuildEligibility.Admission("j.Id")}})))
                  OR NOT ({{SqlCorpusRebuildEligibility.DeploymentAdmission("j.Id")}}) THEN N'Held'
                ELSE N'Ready'
              END AS State,
              CAST(CASE WHEN t.ParentJobId IS NULL THEN 1 ELSE 0 END AS bit) AS IsInteractive
            FROM GpuMiniTasks t
            LEFT JOIN Jobs j ON j.Id = t.ParentJobId
            LEFT JOIN PipelineRecords p ON p.Id = j.PipelineRecordId
            LEFT JOIN SourceRevisions s ON s.Id = p.SourceRevisionId
            LEFT JOIN SourceRootConfigurations r ON r.Id = s.SourceRootId
            LEFT JOIN SourceRevisions owner ON owner.Id = s.ParentSourceRevisionId
            WHERE t.State IN (0,1,3)
            """;
        return context.Database.SqlQueryRaw<GpuTaskActivityRow>(sql, Parameters(now, admission, gpuEvidence));
    }

    // Every condition here proves applicability; timestamps are deliberately not used.
    private static string HistoricalBinding => $$"""
        p.IsDeleted = 1 OR p.Revision <> j.SourceRevision
        OR ((p.CompletionCriteriaMet = 1 OR p.CurrentStage <> j.Stage) AND NOT ({{SqlCorpusRebuildEligibility.ActiveJob("j.Id")}}))
        OR s.SuppressedAtUtc IS NOT NULL OR (s.OriginKind <> 0 AND owner.SuppressedAtUtc IS NOT NULL) OR r.State = 2
        OR EXISTS (SELECT 1 FROM CorpusRebuildSupersededJobs superseded WHERE superseded.JobId = j.Id)
        OR (NOT ({{SqlCorpusRebuildEligibility.ActiveJob("j.Id")}}) AND ({{DirectBranchMismatch}}))
        OR (s.OriginKind IN (2,3) AND EXISTS (
          SELECT 1 FROM #FluxPipelineSelectedBranches selected
          WHERE selected.SourceRevisionId = s.ParentSourceRevisionId
            AND NOT EXISTS (SELECT 1 FROM SourceProcessorBranchMembers member
              INNER JOIN SourceActivities activity ON activity.Id = member.ChildSourceActivityId
              WHERE member.BranchId = selected.Id AND member.ChildSourceRevisionId = s.Id
                AND activity.SourceRevisionId = s.Id AND activity.ResultingPipelineRecordId = p.Id
                AND activity.ResultingPipelineRecordRevision = p.Revision)))
        """;

    private static string DirectBranchMismatch => """
        s.OriginKind IN (0,1) AND EXISTS (
          SELECT 1 FROM #FluxPipelineSelectedBranches selected
          WHERE selected.SourceRevisionId = s.Id
            AND NOT EXISTS (SELECT 1 FROM SourceProcessorBranches branch
              INNER JOIN SourceActivities activity ON activity.Id = branch.SourceActivityId
              WHERE branch.Id = selected.Id AND activity.SourceRevisionId = s.Id
                AND activity.ResultingPipelineRecordId = p.Id
                AND activity.ResultingPipelineRecordRevision = p.Revision))
        """;

    /// <summary>
    /// Conservatively fences the read against application writes without snapshot settings or range locks.
    /// Immutable evidence inserts/deletes share a transaction with a versioned record, branch, lifecycle or index row.
    /// This relies on those writer invariants; it is not snapshot isolation for arbitrary direct SQL.
    /// </summary>
    public static async ValueTask<ActivityReadFence> ReadFenceAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @version binary(8) = @@DBTS;
            SELECT CONVERT(bigint, @version) AS Version,
              CAST(CASE WHEN MIN_ACTIVE_ROWVERSION() > @version THEN 1 ELSE 0 END AS bit) AS IsStable;
            """;
        var rows = await context.Database.SqlQueryRaw<ActivityReadFence>(sql).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    private static string OwnedTask(string task, bool pendingDelivery) => $$"""
        {{task}}.State = 1 AND EXISTS (
          SELECT 1 FROM OPENJSON(@ownedGpuTasks) WITH (
            Id uniqueidentifier '$.Id', BatchId uniqueidentifier '$.BatchId', AdmissionGeneration bigint '$.AdmissionGeneration',
            HasActive bit '$.HasActive', HasPending bit '$.HasPending') proof
          WHERE proof.Id = {{task}}.Id AND proof.BatchId = {{task}}.BatchId
            AND proof.AdmissionGeneration = {{task}}.AdmissionGeneration
            AND proof.{{(pendingDelivery ? "HasPending" : "HasActive")}} = 1)
        """;

    private static object[] Parameters(DateTimeOffset now, DeploymentHoldAdmission admission,
        SqlGpuActivityReadEvidence gpuEvidence) =>
    [
        new SqlParameter("@nowUtc", SqlDbType.DateTimeOffset) { Value = now },
        new SqlParameter("@deploymentHeld", SqlDbType.Bit) { Value = admission.IsHeld },
        new SqlParameter("@permittedRebuildOperationId", SqlDbType.UniqueIdentifier)
            { Value = (object?)admission.PermittedCorpusRebuildOperationId ?? DBNull.Value },
        new SqlParameter("@ownedGpuTasks", SqlDbType.NVarChar, -1) { Value = gpuEvidence.OwnedTasksJson }
    ];
}

public sealed class ActivityReadFence
{
    public long Version { get; set; }
    public bool IsStable { get; set; }
    public bool Accepts(ActivityReadFence after) => IsStable && after.IsStable && Version == after.Version;
}

public sealed class PipelineJobActivityRow
{
    public Guid Id { get; set; }
    public Guid PipelineRecordId { get; set; }
    public Guid? SourceRootId { get; set; }
    public Guid? LogicalSourceRevisionId { get; set; }
    public long SourceRevision { get; set; }
    public int CurrentStage { get; set; }
    public bool MatchesSelectedFileBranch { get; set; } = true;
    public string FullPath { get; set; } = "";
    public string? ArchivePath { get; set; }
    public DateTimeOffset? AttemptStartedAtUtc { get; set; }
    public int Stage { get; set; }
    public int PublicState { get; set; }
    public bool IsHistorical { get; set; }
    public bool SourceAdmitted { get; set; }
    public bool HasWorkerLease { get; set; }
    public bool HasActiveGpu { get; set; }
    public bool HasReadyGpu { get; set; }
    public bool HasUncertainGpu { get; set; }
    public bool HasWaitingGpu { get; set; }
    public bool HasPendingDispatch { get; set; }
    public bool IsDue { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class GpuTaskActivityRow
{
    public Guid Id { get; set; }
    public Guid? PipelineRecordId { get; set; }
    public Guid? SourceRootId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string State { get; set; } = "";
    public bool IsInteractive { get; set; }
}
