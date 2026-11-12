using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal sealed partial record StagingNormalRuntimeBinding
{
    // Retain positively settled trial history, rather than resetting canonical staging data for each attempt.
    private static async Task<StagingNormalRuntimeBinding> VerifyContinuationAsync(StagingHostSettings settings,string resetPath,
        JsonElement reset,JsonElement plan,string path,CancellationToken ct)
    {
        const string packet=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110";
        path=Path.GetFullPath(path);
        if(!path.StartsWith(packet+@"\window-",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path)!="continuation-proof.json")
            throw new InvalidOperationException("owned-native-continuation-proof-required");
        using var proof=Read(path,262144);var value=proof.RootElement;
        if(value.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidOperationException("continuation-schema-refused");
        var fixtureOutput=Path.GetFullPath(value.GetProperty("fixtureOutput").GetString()!);
        if(!fixtureOutput.StartsWith(packet+@"\indexing-fixture-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("owned-continuation-fixture-required");
        using var intake=Read(Path.Combine(fixtureOutput,"0001-before-intake.json"),131072);
        var priorWindow=Path.GetFullPath(value.GetProperty("priorWindow").GetString()!);
        StagingIndexFixtureExitProof.Verify(priorWindow,intake.RootElement);
        var cleanupHostPath=Path.GetFullPath(value.GetProperty("cleanupHostReceipt").GetString()!);
        if(!cleanupHostPath.StartsWith(priorWindow+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("native-cleanup-host-scope");
        using var cleanupHost=Read(cleanupHostPath,16384);var host=cleanupHost.RootElement;
        if(!host.GetProperty("exitObserved").GetBoolean() || !host.GetProperty("jobEmpty").GetBoolean() || host.GetProperty("exitCode").GetInt32()!=0 ||
            host.GetProperty("forced").GetBoolean() || !host.GetProperty("modelsAndHttpNotStartedByMode").GetBoolean() ||
            host.GetProperty("fixture").GetString()!=fixtureOutput || !Exited(host.GetProperty("id").GetInt32(),host.GetProperty("startedUtc").GetDateTimeOffset()))
            throw new InvalidOperationException("native-cleanup-host-exit-unproved");
        var cleanupPath=Path.GetFullPath(value.GetProperty("cleanupReceipt").GetString()!);
        if(!cleanupPath.StartsWith(fixtureOutput+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("native-cleanup-receipt-scope");
        using var cleanup=Read(cleanupPath,16384);var completed=cleanup.RootElement;
        var root=completed.GetProperty("rootId").GetGuid();var deletion=completed.GetProperty("deletionOperation").GetGuid();
        if(!completed.GetProperty("foreignBaselineUnchanged").GetBoolean() || Directory.Exists(intake.RootElement.GetProperty("directory").GetString()))
            throw new InvalidOperationException("native-cleanup-incomplete");
        var expected=value.GetProperty("canonical");ValidateFrozenContinuation(plan.GetProperty("canonical"),expected);
        await using(var connection=new SqlConnection(settings.RuntimeConnectionString))
        {
            await connection.OpenAsync(ct);await using var command=new SqlCommand(ContinuationCanonicalSql,connection){CommandTimeout=10};
            await using var reader=await command.ExecuteReaderAsync(ct);var json=new StringBuilder();
            while(await reader.ReadAsync(ct)){json.Append(reader.GetString(0));if(json.Length>1048576)throw new InvalidOperationException("continuation-canonical-bound");}
            using var current=JsonDocument.Parse(json.ToString());
            if(!JsonNode.DeepEquals(CanonicalNode(expected),CanonicalNode(current.RootElement)))throw new InvalidOperationException("closed-continuation-canonical-state-changed");
        }
        var owners=new HashSet<(int,DateTimeOffset)>();
        if(value.GetProperty("historyOwners").GetArrayLength()>32)throw new InvalidOperationException("continuation-owner-bound");
        foreach(var owner in value.GetProperty("historyOwners").EnumerateArray())
        {
            if(owners.Count>=32)throw new InvalidOperationException("continuation-owner-bound");
            var window=Path.GetFullPath(owner.GetProperty("window").GetString()!);
            if(!window.StartsWith(packet+@"\window-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("continuation-owner-window");
            using var start=Read(Path.Combine(window,"owned-host-started.json"),16384);using var end=Read(Path.Combine(window,"owned-host-settlement.json"),16384);
            var pid=start.RootElement.GetProperty("id").GetInt32();var birth=start.RootElement.GetProperty("startedUtc").GetDateTimeOffset();
            if(!start.RootElement.GetProperty("assignedOwnedJob").GetBoolean() || !start.RootElement.GetProperty("noBreakaway").GetBoolean() ||
                start.RootElement.GetProperty("activeProcessLimit").GetInt32()!=1 || !end.RootElement.GetProperty("actualExitObserved").GetBoolean() ||
                !end.RootElement.GetProperty("ownedJobEmpty").GetBoolean() || end.RootElement.GetProperty("id").GetInt32()!=pid ||
                end.RootElement.GetProperty("startedUtc").GetDateTimeOffset()!=birth || !Exited(pid,birth))throw new InvalidOperationException("continuation-gpu-owner-exit-unproved");
            owners.Add((pid,birth));
        }
        IDbContextFactory<FluxKnowledgeDbContext> factory=new ContinuationFactory(settings.RuntimeConnectionString);
        var machine=new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe().Current.MachineFingerprint;
        await using(var db=await factory.CreateDbContextAsync(ct))
        {
            var removed=await db.SourceDeletionOperations.AsNoTracking().SingleAsync(o=>o.Id==deletion,ct);
            if(removed.SourceRootId!=root || removed.State!=3 || removed.LeaseId is not null || removed.LeaseExpiresAtUtc is not null ||
                await db.SourceRootConfigurations.AnyAsync(r=>r.Id==root,ct) || await db.SourceRevisions.AnyAsync(r=>r.SourceRootId==root,ct) ||
                await db.EmbeddingGpuRequests.AnyAsync(ct) || await db.DocumentOcrRequests.AnyAsync(ct) || await db.NativeWorkerInstances.AnyAsync(ct) || await db.NativeWorkerLifecycleEvidence.AnyAsync(ct))
                throw new InvalidOperationException("native-continuation-cleanup-state-unproved");
            var slot=await db.GpuCapacitySlots.AsNoTracking().SingleAsync(ct);
            if(slot.State!=(int)GpuCapacitySlotState.Available || slot.ActiveBatchId is not null || slot.OwnerKey is not null)throw new InvalidOperationException("continuation-capacity-not-released");
            var tasks=await db.GpuMiniTasks.AsNoTracking().OrderBy(t=>t.Id).Take(513).ToArrayAsync(ct);
            if(tasks.Length>512)throw new InvalidOperationException("continuation-history-bound");
            var completedCount=0;
            foreach(var task in tasks)
            {
                if(task.ParentJobId is not null || task.SourceRevision!=0 || task.CreatedAtUtc<reset.GetProperty("completedUtc").GetDateTimeOffset() ||
                    task.ModelRuntimeKey!=BgeExpandedGpuRankBackend.Profile.RuntimeKey || task.SettingsFingerprint!=BgeExpandedGpuRankBackend.Profile.SettingsFingerprint ||
                    task.InteractiveOwnerMachineFingerprint!=machine ||
                    task.InteractiveOwnerProcessId is not {} pid || task.InteractiveOwnerStartedAtUtc is not {} birth || !owners.Contains((pid,birth)))
                    throw new InvalidOperationException("foreign-continuation-history");
                if(task.ExecutionState==(int)GpuMiniTaskExecutionState.Cancelled && task.BatchId is null && task.InteractiveCancellationRequested)continue;
                if(task.ExecutionState!=(int)GpuMiniTaskExecutionState.Completed || task.BatchId is null || task.InteractiveExecutorInstanceId is not {} executor ||
                    task.RequiredExecutorKey!=$"retrieval-gpu:{executor:N}")throw new InvalidOperationException("unsettled-continuation-history");
                var batch=await db.GpuBatches.AsNoTracking().SingleAsync(b=>b.Id==task.BatchId,ct);
                var dispatch=await db.GpuExecutorDispatches.AsNoTracking().SingleAsync(d=>d.BatchId==batch.Id,ct);
                if(batch.State!=(int)GpuBatchState.Completed || batch.ItemCount!=1 || batch.ModelRuntimeKey!=task.ModelRuntimeKey || batch.SettingsFingerprint!=task.SettingsFingerprint ||
                    batch.AdmissionGeneration!=task.AdmissionGeneration || dispatch.State!=(int)GpuExecutorDispatchState.Terminal || dispatch.ExecutorKey!=task.RequiredExecutorKey ||
                    dispatch.AdmissionGeneration!=batch.AdmissionGeneration || dispatch.CapacitySlotKey!=batch.CapacitySlotKey || dispatch.OwnerKey!=batch.OwnerKey ||
                    !await db.GpuExecutorResultReceipts.AnyAsync(r=>r.BatchId==batch.Id && r.DispatchId==dispatch.DispatchId && r.MiniTaskId==task.Id &&
                        r.ExecutorKey==dispatch.ExecutorKey && r.AdmissionGeneration==batch.AdmissionGeneration && r.Disposition==(int)GpuMiniTaskBoundaryDisposition.Completed &&
                        r.EvidenceClass==(int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed,ct))throw new InvalidOperationException("continuation-terminal-binding-unproved");
                completedCount++;
            }
            if(await db.GpuBatches.CountAsync(ct)!=completedCount || await db.GpuExecutorDispatches.CountAsync(ct)!=completedCount)throw new InvalidOperationException("orphan-continuation-gpu-history");
        }
        var store=new SqlGpuSchedulerStore(factory,interactiveExecutionTimeout:TimeSpan.FromSeconds(20));
        var runtime=new EmbeddingGpuRuntime(BgeOfflineModels.GpuRuntimeKey,BgeOfflineModels.GpuSettingsFingerprint,new(BgeOfflineModels.EmbeddingFingerprint,1024),8L*1024*1024*1024);
        var embedding=new SqlEmbeddingGpuRequestStore(factory,store,new ChannelGpuSchedulerWakeSignal(),runtime,TimeProvider.System);
        if((await store.ReadInteractiveRecoveryAsync(ct)).Count!=0 || (await store.ReadPendingDispatchesAsync(ct)).Count!=0 || (await embedding.ReadRecoveryAsync(ct)).Count!=0)
            throw new InvalidOperationException("continuation-has-recoverable-gpu-work");
        var publication=expected.GetProperty("Publication")[0];
        return new(resetPath,publication.GetProperty("Id").GetGuid(),publication.GetProperty("CorpusVersion").GetInt64());
    }

    internal static void ValidateFrozenContinuation(JsonElement original,JsonElement candidate)
    {
        var a=original.GetProperty("Publication")[0];var b=candidate.GetProperty("Publication")[0];
        if(candidate.GetProperty("Catalogue").GetString()!=Catalogue || candidate.GetProperty("Incarnation").GetGuid()!=Guid.Parse(Incarnation) ||
            a.GetProperty("CorpusEpoch").GetGuid()!=b.GetProperty("CorpusEpoch").GetGuid() || a.GetProperty("VectorCount").GetInt32()!=b.GetProperty("VectorCount").GetInt32() ||
            b.GetProperty("CorpusVersion").GetInt64()<a.GetProperty("CorpusVersion").GetInt64() ||
            a.GetProperty("SegmentCount").GetInt32()!=b.GetProperty("SegmentCount").GetInt32() || b.GetProperty("CorpusRebuildOperationId").ValueKind!=JsonValueKind.Null ||
            !JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty("Roots").GetRawText()),JsonNode.Parse(candidate.GetProperty("Roots").GetRawText())) ||
            !JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty("Segments").GetRawText()),JsonNode.Parse(candidate.GetProperty("Segments").GetRawText())))
            throw new InvalidOperationException("frozen-corpus-changed-during-trial-cleanup");
    }
    private static JsonNode CanonicalNode(JsonElement value)
    {
        var node=JsonNode.Parse(value.GetRawText())!;
        foreach(var file in node["DatabaseFiles"]!.AsArray())file!.AsObject().Remove("Bytes");
        return node;
    }
    private static bool Exited(int pid,DateTimeOffset birth)
    {
        try{using var process=Process.GetProcessById(pid);return process.HasExited || process.StartTime.ToUniversalTime()!=birth.UtcDateTime;}
        catch(ArgumentException){return true;}
    }
    private sealed class ContinuationFactory(string connection):IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection,sql=>sql.EnableRetryOnFailure()).Options);}

    private const string ContinuationCanonicalSql="""
        SELECT DB_NAME() AS Catalogue,
        (SELECT CONVERT(varchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID()) AS Incarnation,
        JSON_QUERY((SELECT t.name AS [Name],SUM(p.rows) AS [Rows] FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN(0,1)
         WHERE t.name NOT IN('GpuExecutorEvidence','GpuExecutorResultReceipts','GpuExecutorDispatches','EmbeddingGpuRequests','DocumentOcrRequests','GpuMiniTasks','GpuBatches','GpuSchedulerOperationReceipts','GpuCapacitySlots','GpuSchedulerState')
         GROUP BY t.name ORDER BY t.name FOR JSON PATH)) AS PreservedTableRowMetadata,
        JSON_QUERY((SELECT TOP(65) Id,State,ConfigurationRevision,CONVERT(varchar(16),CONVERT(binary(8),RowVersion),2) AS Version FROM SourceRootConfigurations ORDER BY Id FOR JSON PATH)) AS Roots,
        JSON_QUERY((SELECT g.Id,g.VectorCount,g.SegmentCount,g.IndexPath,s.CorpusEpoch,s.CorpusVersion,s.CorpusRebuildOperationId,
         CONVERT(varchar(16),CONVERT(binary(8),s.RowVersion),2) AS Version FROM IndexState s JOIN IndexGenerations g ON g.Id=s.ActiveIndexGenerationId WHERE s.Id=1 FOR JSON PATH,INCLUDE_NULL_VALUES)) AS Publication,
        JSON_QUERY((SELECT m.Ordinal,m.SegmentId,s.DirectoryPath,s.MembershipChecksum,s.FileChecksum,s.VectorCount FROM IndexState state JOIN IndexGenerationSegments m ON m.GenerationId=state.ActiveIndexGenerationId
         JOIN IndexSegments s ON s.Id=m.SegmentId WHERE state.Id=1 ORDER BY m.Ordinal FOR JSON PATH)) AS Segments,
        JSON_QUERY((SELECT physical_name AS [Path],CONVERT(bigint,size)*8192 AS Bytes FROM sys.database_files ORDER BY file_id FOR JSON PATH)) AS DatabaseFiles
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
        """;
}
