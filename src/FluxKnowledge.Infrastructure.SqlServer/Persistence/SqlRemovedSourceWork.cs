using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlRemovedSourceWork
{
    // The legacy scan writer stores one fixed value without a typed witness.
    private const string LegacyWitness = "(NCHAR(123) + N'\"reason\":\"unseen-during-authoritative-scan\"' + NCHAR(125))";

    private static readonly string RemovedPhysicalSourcesSql = $$"""
        SELECT physical.Id AS [Value]
        FROM SourceRevisions physical
        WHERE physical.OriginKind = 0 AND {{RemovalWitness("physical")}}
        """;

    private const string RemovedInputsSql = """
        SELECT physical.Id AS [Value]
        FROM OPENJSON(@removedPhysicalSources) WITH (Id uniqueidentifier '$') physical
        UNION ALL
        SELECT input.Id AS [Value]
        FROM OPENJSON(@removedPhysicalSources) WITH (Id uniqueidentifier '$') confirmed
        INNER JOIN SourceRevisions physical ON physical.Id = confirmed.Id
        INNER JOIN SourceRevisions input ON input.ParentSourceRevisionId = physical.Id
            AND input.SourceRootId = physical.SourceRootId AND input.OriginKind IN (1,2,3)
        UNION ALL
        SELECT input.Id AS [Value]
        FROM OPENJSON(@removedPhysicalSources) WITH (Id uniqueidentifier '$') confirmed
        INNER JOIN SourceRevisions physical ON physical.Id = confirmed.Id
        INNER JOIN SourceRevisions archiveMember ON archiveMember.ParentSourceRevisionId = physical.Id
            AND archiveMember.SourceRootId = physical.SourceRootId AND archiveMember.OriginKind = 1
        INNER JOIN SourceRevisions input ON input.ParentSourceRevisionId = archiveMember.Id
            AND input.SourceRootId = physical.SourceRootId AND input.OriginKind IN (2,3)
        """;

    internal static async ValueTask<string> ReadRemovedInputIdsAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        var physical = await context.Database.SqlQueryRaw<Guid>(RemovedPhysicalSourcesSql)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (physical.Length == 0) return "[]";
        var inputs = await context.Database.SqlQueryRaw<Guid>(RemovedInputsSql,
                JsonParameter("@removedPhysicalSources", JsonSerializer.Serialize(physical)))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(inputs);
    }

    internal static SqlParameter JsonParameter(string name, string document) =>
        new(name, SqlDbType.NVarChar, -1) { Value = document };

    internal const string RemovedJobBindings = """
        FROM OPENJSON(@removedInputIds) WITH (Id uniqueidentifier '$') removed
        INNER JOIN PipelineRecords record ON record.SourceRevisionId = removed.Id
        INNER JOIN Jobs job ON job.PipelineRecordId = record.Id AND job.SourceRevision = record.Revision
        """;

    private static readonly string RemovedJobFactsSql = $$"""
        SELECT job.Id,
          CASE WHEN {{HasUnconfirmedWorker("job")}}
              OR (job.PublicState IN (0,2,3) AND (job.LeaseOwner IS NOT NULL OR job.LeaseExpiresAtUtc IS NOT NULL)) THEN 1
            WHEN {{HasUnconfirmedGpuRequests("job", "record", readEvidence: true)}}
              OR EXISTS (SELECT 1 FROM GpuMiniTasks terminalTask WHERE terminalTask.ParentJobId = job.Id
                  AND terminalTask.State IN (2,4) AND {{TaskRequiresVerification("terminalTask", "job", readEvidence: true)}})
              OR (job.PublicState = 3 AND NOT EXISTS (SELECT 1 FROM GpuMiniTasks boundTask
                  WHERE boundTask.ParentJobId = job.Id AND boundTask.SourceRevision = job.SourceRevision)) THEN 2
            ELSE 0 END AS Blocker
        {{RemovedJobBindings}}
        WHERE job.PublicState <> 4
        """;

    private static readonly string RemovedGpuBlockersSql = $$"""
        SELECT task.Id AS [Value]
        {{RemovedJobBindings}}
        INNER JOIN GpuMiniTasks task ON task.ParentJobId = job.Id
        WHERE task.State IN (0,3) AND {{TaskRequiresVerification("task", "job", readEvidence: true)}}
        """;

    internal static async ValueTask<(string JobBlockers, string GpuBlockers)> ReadBlockersAsync(
        FluxKnowledgeDbContext context, string removedInputIds, string gpuReleases, bool includeJobs, bool includeGpu,
        CancellationToken cancellationToken)
    {
        if (removedInputIds == "[]") return ("[]", "[]");
        var jobs = !includeJobs ? [] : await context.Database.SqlQueryRaw<RemovedJobFact>(RemovedJobFactsSql,
                JsonParameter("@removedInputIds", removedInputIds), JsonParameter("@removedSourceGpuReleases", gpuReleases))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var gpu = !includeGpu ? [] : await context.Database.SqlQueryRaw<Guid>(RemovedGpuBlockersSql,
                JsonParameter("@removedInputIds", removedInputIds), JsonParameter("@removedSourceGpuReleases", gpuReleases))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return (JsonSerializer.Serialize(jobs.Where(job => job.Blocker != 0)), JsonSerializer.Serialize(gpu));
    }

    private sealed class RemovedJobFact
    {
        public Guid Id { get; set; }
        public int Blocker { get; set; }
    }

    internal static string RemovalWitness(string source, bool includeLegacy = true)
    {
        var json = $"CASE WHEN ISJSON({source}.RetentionEvidenceJson) = 1 THEN {source}.RetentionEvidenceJson ELSE N'[]' END";
        var legacy = includeLegacy ? $$"""
            (DATALENGTH({{source}}.RetentionEvidenceJson) = DATALENGTH({{LegacyWitness}})
             AND {{source}}.RetentionEvidenceJson COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} = {{LegacyWitness}}) OR
            """ : "";
        return $$"""
            ({{source}}.SuppressedAtUtc IS NOT NULL AND
              ({{legacy}}
                (TRY_CONVERT(uniqueidentifier, JSON_VALUE({{json}}, '$.SourceRootId')) = {{source}}.SourceRootId
                 AND TRY_CONVERT(uniqueidentifier, JSON_VALUE({{json}}, '$.SourceRevisionId')) = {{source}}.Id
                 AND TRY_CONVERT(datetimeoffset(7), JSON_VALUE({{json}}, '$.SuppressedAtUtc'), 127) = {{source}}.SuppressedAtUtc
                 AND TRY_CONVERT(bigint, JSON_VALUE({{json}}, '$.ConfigurationRevision')) > 0
                 AND LEN(JSON_VALUE({{json}}, '$.RelativePath')) > 0
                 AND ((JSON_VALUE({{json}}, '$.Version') = N'2'
                       AND {{ExactWitnessValue(json, "Kind", "guarded-path-removal")}}
                       AND DATALENGTH(JSON_VALUE({{json}}, '$.PhysicalIdentityFingerprint')) = 128
                       AND JSON_VALUE({{json}}, '$.PhysicalIdentityFingerprint') COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} NOT LIKE '%[^0-9a-f]%'
                       AND TRY_CONVERT(bigint, JSON_VALUE({{json}}, '$.PendingGeneration')) > 0
                       AND TRY_CONVERT(uniqueidentifier, JSON_VALUE({{json}}, '$.PendingId')) <> '00000000-0000-0000-0000-000000000000')
                      OR (JSON_VALUE({{json}}, '$.Version') = N'1'
                          AND {{ExactWitnessValue(json, "Category", "Deleted")}}
                          AND TRY_CONVERT(bigint, JSON_VALUE({{json}}, '$.LeaseGeneration')) > 0
                          AND TRY_CONVERT(uniqueidentifier, JSON_VALUE({{json}}, '$.SourceScanRequestId')) <> '00000000-0000-0000-0000-000000000000'
                          AND TRY_CONVERT(uniqueidentifier, JSON_VALUE({{json}}, '$.ManifestId')) <> '00000000-0000-0000-0000-000000000000')))))
            """;
    }

    private static string ExactWitnessValue(string json, string field, string expected) => $$"""
        JSON_VALUE({{json}}, '$.{{field}}') COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} = N'{{expected}}'
          AND DATALENGTH(JSON_VALUE({{json}}, '$.{{field}}')) = DATALENGTH(N'{{expected}}')
        """;

    internal static string HasUnconfirmedWorker(string job) => $$"""
        ({{job}}.PublicState = {{(int)PublicJobState.WorkerProcessing}}
         OR EXISTS (SELECT 1 FROM JobAttempts attempt WHERE attempt.JobId = {{job}}.Id AND attempt.CompletedAtUtc IS NULL)
         OR ({{job}}.PublicState = {{(int)PublicJobState.Failed}} AND {{job}}.Reason = N'source-removed'
             AND {{job}}.LeaseGeneration > 0 AND EXISTS (SELECT 1 FROM OutboxMessages delivery
                WHERE delivery.JobId = {{job}}.Id AND delivery.PipelineRecordId = {{job}}.PipelineRecordId
                  AND delivery.SourceRevision = {{job}}.SourceRevision AND delivery.Stage = {{job}}.Stage
                  AND delivery.Operation COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} =
                      {{job}}.Operation COLLATE {{SchemaConfiguration.SchedulerFenceCollation}}
                  AND delivery.DispatchedAtUtc IS NULL AND delivery.LeaseOwner IS NOT NULL)))
        """;

    internal static string NeverAdmitted(string task) => $"({task}.AdmissionGeneration = 0 AND {task}.BatchId IS NULL)";

    private const string RemovedReleases = """
        OPENJSON(@removedSourceGpuReleases) WITH (
            TaskId uniqueidentifier '$.TaskId', BatchId uniqueidentifier '$.BatchId',
            AdmissionGeneration bigint '$.AdmissionGeneration', DispatchId uniqueidentifier '$.DispatchId',
            ExecutorKey nvarchar(256) '$.ExecutorKey', CapacitySlotKey nvarchar(256) '$.CapacitySlotKey',
            SingleTaskCompletion bit '$.SingleTaskCompletion')
        """;

    private static string ReleaseBindsTask(string task) => $"""
        releaseProof.TaskId = {task}.Id AND releaseProof.BatchId = {task}.BatchId
          AND releaseProof.AdmissionGeneration = {task}.AdmissionGeneration
        """;

    // A terminal outcome and a released native reservation are separate facts.
    private static string ReleasedBatch(string task, string requestDispatch, bool readEvidence, string? executor = null)
    {
        var executorSql = executor is null ? $"{task}.RequiredExecutorKey" : $"N'{executor}'";
        return readEvidence
        ? $$"""
        EXISTS (SELECT 1 FROM {{RemovedReleases}} releaseProof
            WHERE {{ReleaseBindsTask(task)}}
              AND ({{executorSql}} IS NULL OR releaseProof.ExecutorKey COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} = {{executorSql}})
              AND ({{requestDispatch}} IS NULL OR releaseProof.DispatchId = {{requestDispatch}}))
        """
        : $$"""
        EXISTS (SELECT 1 FROM GpuBatches cleanupBatch
            INNER JOIN GpuExecutorDispatches cleanupDispatch ON cleanupDispatch.BatchId = cleanupBatch.Id
              AND cleanupDispatch.AdmissionGeneration = cleanupBatch.AdmissionGeneration
              AND cleanupDispatch.CapacitySlotKey = cleanupBatch.CapacitySlotKey
              AND cleanupDispatch.OwnerKey = cleanupBatch.OwnerKey
            WHERE cleanupBatch.Id = {{task}}.BatchId AND cleanupBatch.AdmissionGeneration = {{task}}.AdmissionGeneration
              AND cleanupBatch.ModelRuntimeKey = {{task}}.ModelRuntimeKey AND cleanupBatch.SettingsFingerprint = {{task}}.SettingsFingerprint
              AND ({{executorSql}} IS NULL OR cleanupDispatch.ExecutorKey = {{executorSql}})
              AND ({{requestDispatch}} IS NULL OR cleanupDispatch.DispatchId = {{requestDispatch}})
              AND cleanupDispatch.State IN ({{(int)GpuExecutorDispatchState.Terminal}},{{(int)GpuExecutorDispatchState.DeliveryUncertain}})
              AND (cleanupBatch.State = {{(int)GpuBatchState.Completed}} AND cleanupDispatch.State = {{(int)GpuExecutorDispatchState.Terminal}}
                   OR cleanupBatch.State = {{(int)GpuBatchState.Released}}
                   OR (cleanupBatch.State = {{(int)GpuBatchState.CapacityUncertain}}
                       AND EXISTS (SELECT 1 FROM GpuSchedulerOperationReceipts releaseReceipt
                           WHERE releaseReceipt.OperationKind = N'capacity-reconciliation' AND releaseReceipt.Accepted = 1
                             AND releaseReceipt.Committed = 1 AND releaseReceipt.BatchId = cleanupBatch.Id
                             AND releaseReceipt.AdmissionGeneration = cleanupBatch.AdmissionGeneration
                             AND releaseReceipt.CapacitySlotKey = cleanupBatch.CapacitySlotKey
                             AND releaseReceipt.OwnerKey = cleanupBatch.OwnerKey)))
              AND NOT EXISTS (SELECT 1 FROM GpuCapacitySlots slot WHERE slot.ActiveBatchId = cleanupBatch.Id))
        """;
    }

    // The status read obtains the same scheduler facts from separate statements
    // under its activity fence. Deletion verifies the live tables directly.
    private static string CompletedSingleTaskDispatches(string task, bool readEvidence) => readEvidence
        ? $$"""
        SELECT releaseProof.DispatchId, releaseProof.BatchId, releaseProof.AdmissionGeneration,
            releaseProof.ExecutorKey COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} AS ExecutorKey,
            releaseProof.CapacitySlotKey COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} AS CapacitySlotKey
        FROM {{RemovedReleases}} releaseProof
        WHERE {{ReleaseBindsTask(task)}} AND releaseProof.SingleTaskCompletion = 1
        """
        : $$"""
        SELECT dispatch.DispatchId, batch.Id AS BatchId, batch.AdmissionGeneration, dispatch.ExecutorKey, batch.CapacitySlotKey
        FROM GpuBatches batch
        INNER JOIN GpuExecutorDispatches dispatch ON dispatch.BatchId = batch.Id
            AND dispatch.AdmissionGeneration = batch.AdmissionGeneration
            AND dispatch.CapacitySlotKey = batch.CapacitySlotKey AND dispatch.OwnerKey = batch.OwnerKey
        WHERE batch.Id = {{task}}.BatchId AND batch.AdmissionGeneration = {{task}}.AdmissionGeneration
            AND batch.ModelRuntimeKey = {{task}}.ModelRuntimeKey AND batch.SettingsFingerprint = {{task}}.SettingsFingerprint
            AND batch.State = {{(int)GpuBatchState.Completed}} AND batch.ItemCount = 1
            AND dispatch.State = {{(int)GpuExecutorDispatchState.Terminal}}
            AND NOT EXISTS (SELECT 1 FROM GpuCapacitySlots slot WHERE slot.ActiveBatchId = batch.Id)
            AND NOT EXISTS (SELECT 1 FROM GpuMiniTasks other WHERE other.BatchId = batch.Id AND other.Id <> {{task}}.Id)
        """;

    private static string EmbeddingCleanup(string request, string task, string job, bool readEvidence) => $$"""
        ({{request}}.MiniTaskId = {{task}}.Id AND {{task}}.ParentJobId = {{job}}.Id AND {{task}}.SourceRevision = {{job}}.SourceRevision
         AND {{request}}.ParentJobId = {{job}}.Id
         AND {{request}}.PipelineRecordId = {{job}}.PipelineRecordId AND {{request}}.SourceRevision = {{task}}.SourceRevision
         AND {{request}}.ExecutorInstanceId IS NOT NULL AND {{request}}.DispatchId IS NOT NULL
         AND (({{request}}.NativeCleanupConfirmed = 1 AND {{request}}.CleanupConfirmedAtUtc IS NOT NULL)
           OR EXISTS (SELECT 1 FROM GpuModelDetachmentReceipts detach WHERE detach.DispatchId = {{request}}.ResidentDetachmentDispatchId
             AND detach.DispatchId = {{request}}.DispatchId AND detach.ExecutionInstanceId = {{request}}.DispatchId
             AND detach.BatchId = {{task}}.BatchId AND detach.AdmissionGeneration = {{task}}.AdmissionGeneration
             AND detach.ExecutorKey = N'{{EmbeddingGpuExecutor.Name}}'))
         AND {{ReleasedBatch(task, request + ".DispatchId", readEvidence, EmbeddingGpuExecutor.Name)}})
        """;

    private static string OcrCleanup(string request, string task, string job, bool readEvidence) => $$"""
        ({{request}}.MiniTaskId = {{task}}.Id AND {{task}}.ParentJobId = {{job}}.Id AND {{task}}.SourceRevision = {{job}}.SourceRevision
         AND {{request}}.ParentJobId = {{job}}.Id
         AND {{request}}.PipelineRecordId = {{job}}.PipelineRecordId AND {{request}}.SourceRevision = {{task}}.SourceRevision
         AND {{request}}.ExecutionInstanceId IS NOT NULL AND {{request}}.ExecutionDispatchId IS NOT NULL
         AND {{request}}.ExecutionBatchId = {{task}}.BatchId AND {{request}}.ExecutionAdmissionGeneration = {{task}}.AdmissionGeneration
         AND {{request}}.ModelRuntimeKey = {{task}}.ModelRuntimeKey AND {{request}}.SettingsFingerprint = {{task}}.SettingsFingerprint
         AND (({{request}}.NativeCleanupConfirmedAtUtc IS NOT NULL
               AND {{request}}.ExecutionPhase IN ({{(int)DocumentOcrExecutionPhase.CleanupConfirmed}},{{(int)DocumentOcrExecutionPhase.Settled}})
               AND (({{request}}.CleanupProofKind = {{(int)DocumentOcrCleanupKind.VerifiedUnstarted}} AND {{request}}.NativeProcessId IS NULL)
                 OR ({{request}}.CleanupProofKind = {{(int)DocumentOcrCleanupKind.OwnedJobEmpty}} AND {{request}}.NativeProcessId IS NOT NULL
                   AND {{request}}.NativeProcessStartedAtUtc IS NOT NULL)
                 OR ({{request}}.CleanupProofKind = {{(int)DocumentOcrCleanupKind.StoppedMaintenance}}
                   AND LEN({{request}}.PhysicalCleanupEvidenceSha256) = 64
                   AND {{request}}.PhysicalCleanupEvidenceSha256 COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} NOT LIKE '%[^0-9a-f]%')))
             OR {{OcrDetached(request, task, job, readEvidence)}})
         AND {{ReleasedBatch(task, request + ".ExecutionDispatchId", readEvidence, PaddleOcrVlmRuntimeContract.ExecutorKey)}})
        """;

    // Detachment proves that this source owns no active OCR command. The
    // resident allocation may retain its native GPU capacity independently.
    internal static string OcrDetached(string request, string task, string job, bool readEvidence = false) => $$"""
        ({{request}}.MiniTaskId = {{task}}.Id AND {{task}}.ParentJobId = {{job}}.Id
         AND {{request}}.ParentJobId = {{job}}.Id AND {{request}}.PipelineRecordId = {{job}}.PipelineRecordId
         AND {{request}}.SourceRevision = {{job}}.SourceRevision AND {{request}}.SourceRevision = {{task}}.SourceRevision
         AND {{request}}.ExecutionPhase = {{(int)DocumentOcrExecutionPhase.Settled}}
         AND {{request}}.NativeCleanupConfirmedAtUtc IS NULL AND {{request}}.CleanupProofKind IS NULL
         AND {{request}}.NativeProcessId IS NOT NULL AND {{request}}.NativeProcessStartedAtUtc IS NOT NULL
         AND {{request}}.ModelRuntimeKey = {{task}}.ModelRuntimeKey AND {{request}}.SettingsFingerprint = {{task}}.SettingsFingerprint
         AND {{task}}.State = {{(int)GpuMiniTaskExecutionState.Completed}}
         AND EXISTS (SELECT 1 FROM GpuModelDetachmentReceipts detached
            INNER JOIN GpuModelResidencies resident ON resident.ResidencyId = detached.ResidencyId
            INNER JOIN ({{CompletedSingleTaskDispatches(task, readEvidence)}}) ocrDispatch ON ocrDispatch.DispatchId = detached.DispatchId
                AND ocrDispatch.BatchId = detached.BatchId AND ocrDispatch.AdmissionGeneration = detached.AdmissionGeneration
                AND ocrDispatch.ExecutorKey = detached.ExecutorKey
            INNER JOIN GpuExecutorResultReceipts ocrResult ON ocrResult.DispatchId = ocrDispatch.DispatchId
                AND ocrResult.BatchId = ocrDispatch.BatchId AND ocrResult.AdmissionGeneration = ocrDispatch.AdmissionGeneration
                AND ocrResult.ExecutorKey = ocrDispatch.ExecutorKey AND ocrResult.MiniTaskId = {{task}}.Id
            WHERE detached.DispatchId = {{request}}.ExecutionDispatchId AND detached.ExecutionInstanceId = {{request}}.ExecutionInstanceId
                AND detached.BatchId = {{request}}.ExecutionBatchId AND detached.BatchId = {{task}}.BatchId
                AND detached.AdmissionGeneration = {{request}}.ExecutionAdmissionGeneration
                AND detached.AdmissionGeneration = {{task}}.AdmissionGeneration
                AND detached.ExecutorKey = N'{{PaddleOcrVlmRuntimeContract.ExecutorKey}}'
                AND resident.SlotKey = ocrDispatch.CapacitySlotKey AND resident.ChildJobId = resident.ResidencyId
                AND resident.RuntimeKey = {{request}}.ModelRuntimeKey AND resident.SettingsFingerprint = {{request}}.SettingsFingerprint
                AND resident.OwnerProcessId = {{request}}.SupervisorProcessId
                AND resident.OwnerStartedAtUtc = {{request}}.SupervisorProcessStartedAtUtc
                AND resident.MachineFingerprint = {{request}}.ExecutionMachineFingerprint
                AND resident.ChildProcessId = {{request}}.NativeProcessId AND resident.ChildStartedAtUtc = {{request}}.NativeProcessStartedAtUtc
                AND ocrResult.Disposition = {{(int)GpuMiniTaskBoundaryDisposition.Completed}}
                AND ocrResult.EvidenceClass = {{(int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed}}
                AND ocrResult.OpaqueResultDigest = {{request}}.ResultDigest))
        """;

    internal static string MediaCleanup(string request, string task, string job, bool readEvidence = false) => $$"""
        ({{request}}.MiniTaskId = {{task}}.Id AND {{task}}.ParentJobId = {{job}}.Id
         AND {{request}}.ParentJobId = {{job}}.Id AND {{request}}.PipelineRecordId = {{job}}.PipelineRecordId
         AND {{request}}.SourceRevision = {{job}}.SourceRevision AND {{request}}.SourceRevision = {{task}}.SourceRevision
         AND {{job}}.Stage = {{(int)Domain.Pipeline.PipelineStage.Extract}}
         AND {{job}}.Operation COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} = N'{{MediaTranscriptionGpuContracts.Operation}}'
         AND EXISTS (SELECT 1 FROM PipelineRecords mediaRecord
                WHERE mediaRecord.Id = {{request}}.PipelineRecordId AND mediaRecord.SourceRevisionId = {{request}}.RetainedSourceRevisionId)
         AND {{task}}.PriorityLane = {{(int)MediaTranscriptionGpuRuntime.PriorityLane}}
         AND {{request}}.ModelRuntimeKey = {{task}}.ModelRuntimeKey AND {{request}}.SettingsFingerprint = {{task}}.SettingsFingerprint
         AND {{request}}.ExecutionPhase = {{(int)DocumentOcrExecutionPhase.Settled}}
         AND {{request}}.ExecutionInstanceId IS NOT NULL AND {{request}}.ExecutionDispatchId IS NOT NULL
         AND {{request}}.ExecutionBatchId = {{task}}.BatchId AND {{request}}.ExecutionAdmissionGeneration = {{task}}.AdmissionGeneration
         AND {{task}}.State = {{(int)GpuMiniTaskExecutionState.Completed}}
         AND EXISTS (SELECT 1 FROM ({{CompletedSingleTaskDispatches(task, readEvidence)}}) mediaDispatch
            INNER JOIN GpuExecutorResultReceipts mediaResult ON mediaResult.DispatchId = mediaDispatch.DispatchId
                AND mediaResult.BatchId = mediaDispatch.BatchId AND mediaResult.AdmissionGeneration = mediaDispatch.AdmissionGeneration
                AND mediaResult.ExecutorKey = mediaDispatch.ExecutorKey AND mediaResult.MiniTaskId = {{task}}.Id
            WHERE mediaDispatch.DispatchId = {{request}}.ExecutionDispatchId
                AND (({{request}}.CleanupConfirmedAtUtc IS NOT NULL
                        AND (({{request}}.CleanupKind = {{(int)DocumentOcrCleanupKind.VerifiedUnstarted}} AND {{request}}.NativeProcessId IS NULL)
                            OR ({{request}}.CleanupKind = {{(int)DocumentOcrCleanupKind.OwnedJobEmpty}} AND {{request}}.NativeProcessId IS NOT NULL
                                AND {{request}}.NativeProcessStartedAtUtc IS NOT NULL))
                        AND EXISTS (SELECT 1 FROM GpuExecutorEvidence mediaProof
                            WHERE mediaProof.DispatchId = mediaDispatch.DispatchId AND mediaProof.BatchId = mediaDispatch.BatchId
                                AND mediaProof.AdmissionGeneration = mediaDispatch.AdmissionGeneration
                                AND mediaProof.ExecutorKey = mediaDispatch.ExecutorKey AND mediaProof.CapacitySlotKey = mediaDispatch.CapacitySlotKey
                                AND mediaProof.VerifierKey COLLATE {{SchemaConfiguration.SchedulerFenceCollation}} IN (
                                    N'media-transcription-owned-job-v1', N'media-transcription-resident-job-empty-v1')
                                AND mediaProof.EvidenceClass = {{(int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed}}
                                AND mediaProof.ObservedAtUtc = {{request}}.CleanupConfirmedAtUtc))
                     OR ({{request}}.CleanupConfirmedAtUtc IS NULL AND {{request}}.CleanupKind IS NULL
                        AND {{request}}.NativeProcessId IS NOT NULL AND {{request}}.NativeProcessStartedAtUtc IS NOT NULL
                        AND EXISTS (SELECT 1 FROM GpuModelDetachmentReceipts detached
                            INNER JOIN GpuModelResidencies resident ON resident.ResidencyId = detached.ResidencyId
                            WHERE detached.DispatchId = mediaDispatch.DispatchId AND detached.BatchId = mediaDispatch.BatchId
                                AND detached.AdmissionGeneration = mediaDispatch.AdmissionGeneration
                                AND detached.ExecutorKey = mediaDispatch.ExecutorKey
                                AND detached.ExecutionInstanceId = {{request}}.ExecutionInstanceId
                                AND resident.Version >= detached.Version AND resident.SlotKey = mediaDispatch.CapacitySlotKey
                                AND resident.RuntimeKey = {{request}}.ModelRuntimeKey AND resident.SettingsFingerprint = {{request}}.SettingsFingerprint
                                AND resident.ChildJobId = detached.ResidencyId AND resident.ChildProcessId = {{request}}.NativeProcessId
                                AND resident.ChildStartedAtUtc = {{request}}.NativeProcessStartedAtUtc
                                AND resident.MachineFingerprint = {{request}}.MachineFingerprint)))
                AND mediaResult.EvidenceClass = {{(int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed}}
                AND mediaResult.Disposition = {{(int)GpuMiniTaskBoundaryDisposition.Completed}}
                AND mediaResult.OpaqueResultDigest = {{request}}.ResultDigest))
        """;

    internal static string TaskRequiresVerification(string task, string job, bool readEvidence = false) => $$"""
        ({{task}}.SourceRevision <> {{job}}.SourceRevision
         OR ({{task}}.State = {{(int)GpuMiniTaskExecutionState.Ready}} AND NOT {{NeverAdmitted(task)}})
         OR ({{task}}.State IN ({{(int)GpuMiniTaskExecutionState.Completed}},{{(int)GpuMiniTaskExecutionState.Cancelled}})
             AND NOT {{NeverAdmitted(task)}} AND NOT {{ReleasedBatch(task, "NULL", readEvidence)}})
         OR (NOT {{NeverAdmitted(task)}} AND EXISTS (SELECT 1 FROM MediaTranscriptionGpuRequests media
                WHERE media.MiniTaskId = {{task}}.Id AND NOT EXISTS (SELECT 1 WHERE {{MediaCleanup("media", task, job, readEvidence)}})))
         OR ({{task}}.State = {{(int)GpuMiniTaskExecutionState.OutcomeUncertain}}
             AND NOT EXISTS (SELECT 1 FROM EmbeddingGpuRequests embedded WHERE {{EmbeddingCleanup("embedded", task, job, readEvidence)}})
             AND NOT EXISTS (SELECT 1 FROM DocumentOcrRequests ocr WHERE {{OcrCleanup("ocr", task, job, readEvidence)}})))
        """;

    private static string RelatedRequests(string table, string job, string? ocrRecord = null)
    {
        var orphan = """
            NOT EXISTS (SELECT 1 FROM Jobs requestJob WHERE requestJob.Id = request.ParentJobId
              AND requestJob.PipelineRecordId = request.PipelineRecordId AND requestJob.SourceRevision = request.SourceRevision)
            """;
        var ocrSource = ocrRecord is null ? "" : $$"""
            UNION ALL
            SELECT request.MiniTaskId FROM {{table}} request
            WHERE request.RetainedSourceRevisionId = {{ocrRecord}}.SourceRevisionId AND request.SourceRevision = {{job}}.SourceRevision
              AND {{orphan}}
            """;
        return $$"""
            SELECT request.MiniTaskId FROM {{table}} request WHERE request.ParentJobId = {{job}}.Id
            UNION ALL
            SELECT request.MiniTaskId
            FROM GpuMiniTasks ownedTask INNER JOIN {{table}} request ON request.MiniTaskId = ownedTask.Id
            WHERE ownedTask.ParentJobId = {{job}}.Id
            UNION ALL
            SELECT request.MiniTaskId FROM {{table}} request
            WHERE request.PipelineRecordId = {{job}}.PipelineRecordId AND request.SourceRevision = {{job}}.SourceRevision
              AND {{orphan}}
            {{ocrSource}}
            """;
    }

    internal static string HasUnconfirmedGpuRequests(string job, string record, bool readEvidence = false) => $$"""
        (EXISTS (SELECT 1 FROM ({{RelatedRequests("EmbeddingGpuRequests", job)}}) candidate
            INNER JOIN EmbeddingGpuRequests embedded ON embedded.MiniTaskId = candidate.MiniTaskId
            LEFT JOIN GpuMiniTasks linked ON linked.Id = embedded.MiniTaskId
            WHERE 1 = 1
              AND NOT EXISTS (SELECT 1 WHERE {{EmbeddingCleanup("embedded", "linked", job, readEvidence)}})
              AND (embedded.ExecutorInstanceId IS NOT NULL OR embedded.ClaimOperationId IS NOT NULL
                   OR embedded.DispatchId IS NOT NULL OR embedded.OwnerProcessId IS NOT NULL))
         OR EXISTS (SELECT 1 FROM ({{RelatedRequests("DocumentOcrRequests", job, record)}}) candidate
            INNER JOIN DocumentOcrRequests ocr ON ocr.MiniTaskId = candidate.MiniTaskId
            LEFT JOIN GpuMiniTasks linked ON linked.Id = ocr.MiniTaskId
            WHERE 1 = 1
              AND NOT EXISTS (SELECT 1 WHERE {{OcrCleanup("ocr", "linked", job, readEvidence)}})
              AND (ocr.ExecutionPhase <> {{(int)DocumentOcrExecutionPhase.Unclaimed}} OR ocr.ExecutionInstanceId IS NOT NULL
                   OR ocr.ExecutionDispatchId IS NOT NULL OR ocr.ExecutionBatchId IS NOT NULL OR ocr.ExecutionAdmissionGeneration IS NOT NULL
                   OR ocr.ExecutionMachineFingerprint IS NOT NULL OR ocr.SupervisorInstanceId IS NOT NULL OR ocr.SupervisorProcessId IS NOT NULL
                   OR ocr.SupervisorProcessStartedAtUtc IS NOT NULL OR ocr.ExecutionClaimedAtUtc IS NOT NULL OR ocr.ExecutionDeadlineAtUtc IS NOT NULL
                   OR ocr.NativeProcessId IS NOT NULL OR ocr.NativeProcessStartedAtUtc IS NOT NULL OR ocr.NativeCleanupConfirmedAtUtc IS NOT NULL
                   OR ocr.CleanupProofKind IS NOT NULL OR ocr.PhysicalCleanupEvidenceSha256 IS NOT NULL))
         OR EXISTS (SELECT 1 FROM ({{RelatedRequests("MediaTranscriptionGpuRequests", job, record)}}) candidate
            INNER JOIN MediaTranscriptionGpuRequests media ON media.MiniTaskId = candidate.MiniTaskId
            LEFT JOIN GpuMiniTasks linkedMedia ON linkedMedia.Id = media.MiniTaskId
            WHERE (media.ExecutionPhase <> {{(int)DocumentOcrExecutionPhase.Unclaimed}} OR media.ExecutionInstanceId IS NOT NULL
                   OR media.ExecutionDispatchId IS NOT NULL OR media.ExecutionBatchId IS NOT NULL OR media.ExecutionAdmissionGeneration IS NOT NULL
                   OR media.ClaimedAtUtc IS NOT NULL OR media.DeadlineAtUtc IS NOT NULL OR media.MachineFingerprint IS NOT NULL
                   OR media.SupervisorInstanceId IS NOT NULL OR media.SupervisorProcessId IS NOT NULL OR media.SupervisorProcessStartedAtUtc IS NOT NULL
                   OR media.NativeProcessId IS NOT NULL OR media.NativeProcessStartedAtUtc IS NOT NULL
                   OR media.CleanupConfirmedAtUtc IS NOT NULL OR media.CleanupKind IS NOT NULL)
              AND NOT EXISTS (SELECT 1 WHERE {{MediaCleanup("media", "linkedMedia", job, readEvidence)}})))
        """;

    private static readonly string UnconfirmedJobIdsSql = $$"""
        SELECT removedJob.Id AS Value
        FROM Jobs removedJob
        INNER JOIN PipelineRecords removedRecord ON removedRecord.Id = removedJob.PipelineRecordId
        WHERE {{HasUnconfirmedWorker("removedJob")}} OR {{HasUnconfirmedGpuRequests("removedJob", "removedRecord")}}
           OR EXISTS (SELECT 1 FROM GpuMiniTasks task WHERE task.ParentJobId = removedJob.Id
                AND {{TaskRequiresVerification("task", "removedJob")}})
        """;

    internal static IQueryable<Guid> UnconfirmedJobIds(FluxKnowledgeDbContext db) =>
        db.Database.SqlQueryRaw<Guid>(UnconfirmedJobIdsSql);
}
