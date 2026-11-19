using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(1, 1, 1)]
    [InlineData(512, 256, 64)]
    public Task Native_deletion_admission_and_ordinary_cleanup_keep_large_root_ids_on_the_server(int revisions, int records, int tasks) =>
        DeleteSyntheticRootAsync(revisions, records, tasks);

    [SqlServerScaleFact]
    [Trait("Category", "Scale")]
    public Task Production_sized_native_deletion_completes_owned_history_and_cleanup() =>
        DeleteSyntheticRootAsync(35_000, 15_539, 676);

    [SqlServerScaleFact]
    [Trait("Category", "Scale")]
    public Task Million_vector_native_deletion_completes_withdrawal_memberships_graph_and_files() =>
        DeleteSyntheticRootAsync(35_000,15_539,676,1_300_000);

    private async Task DeleteSyntheticRootAsync(int revisions, int records, int tasks, int vectors = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("synthetic-scale-delete", SourceRootState.Enabled, now);
        var control = await SeedRootAsync("synthetic-scale-control", SourceRootState.Enabled, now);
        var runtime = new EmbeddingGpuRuntime("source-deletion-scale", new string('d', 64), new("source-deletion-scale:1", 1), 4);
        var foreignGeneration = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            seed.Database.SetCommandTimeout(120);
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                CREATE TABLE #DeletionScale ([n] int PRIMARY KEY,[source] uniqueidentifier,[identity] uniqueidentifier,
                    [record] uniqueidentifier,[job] uniqueidentifier,[generation] uniqueidentifier,[task] uniqueidentifier);
                INSERT #DeletionScale
                SELECT TOP({revisions}) ROW_NUMBER() OVER(ORDER BY (SELECT NULL))-1,NEWID(),NEWID(),NEWID(),NEWID(),NEWID(),NEWID()
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT SourceRevisions (Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,
                    Extension,ByteLength,DiscoveredAtUtc,OriginKind,SuppressedAtUtc,RetainUntilUtc)
                SELECT [source],{deleting.RootId},CONCAT('deletion-scale:',[n]),1,
                    LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varchar(20),[n])),2)),
                    CONCAT(N'C:\deletion-scale\',[source]),'AcceptedUtf8Text','.txt',4,{now},0,
                    CASE WHEN [n]%5=0 THEN {now} ELSE NULL END,CASE WHEN [n]%5=0 THEN {now.AddDays(30)} ELSE NULL END
                FROM #DeletionScale;
                INSERT SourceArtifacts (Id,SourceRevisionId,ContentSha256,StoreRelativePath,ByteLength,ChecksumVerifiedAtUtc,ReferenceCount)
                SELECT NEWID(),[source],LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varchar(20),[n])),2)),
                    CONCAT('deletion-scale/',[n]),4,{now},1 FROM #DeletionScale;
                INSERT SourceIdentities (Id,SourceKind,StableKey,CreatedAtUtc)
                SELECT [identity],'local file',CONCAT('deletion-scale:',[identity]),{now} FROM #DeletionScale WHERE [n]<{records};
                INSERT PipelineRecords (Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,CurrentStage,
                    CompletionCriteriaMet,IsDeleted,RegisteredAtUtc)
                SELECT [record],[identity],[source],1,LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varchar(20),[n])),2)),
                    [record],5,1,0,{now} FROM #DeletionScale WHERE [n]<{records};
                INSERT Jobs (Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,LeaseGeneration)
                SELECT CASE WHEN [stage]=4 THEN [job] ELSE NEWID() END,[record],1,[stage],
                    CASE [stage] WHEN 1 THEN 'extract utf-8' WHEN 2 THEN 'normalise text' WHEN 3 THEN 'canonical index' ELSE 'embed' END,
                    4,{now},1,1 FROM #DeletionScale CROSS JOIN (VALUES(1),(2),(3),(4)) s([stage]) WHERE [n]<{records};
                INSERT IndexGenerations (Id,FormatVersion,SegmentCount,EmbeddingJobId,CorpusEpoch,CorpusVersion,ModelFingerprint,
                    Dimensions,IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc)
                SELECT [generation],1,0,[job],state.CorpusEpoch,state.CorpusVersion,{runtime.Profile.ModelFingerprint},1,'',
                    {EmbedDraftDefaults.MetadataChecksum},0,{now} FROM #DeletionScale CROSS JOIN IndexState state WHERE [n]<{tasks} AND state.Id=1;
                INSERT GpuMiniTasks (Id,ParentJobId,SourceRevision,PriorityLane,ModelRuntimeKey,SettingsFingerprint,EstimatedBytes,
                    AdmissionGeneration,IdempotencyKey,State,ReservationAttemptCount,InteractiveCancellationRequested,CreatedAtUtc)
                SELECT [task],[job],1,1,{runtime.RuntimeKey},{runtime.SettingsFingerprint},4,0,CONCAT('deletion-scale:',[task]),2,0,0,{now}
                FROM #DeletionScale WHERE [n]<{tasks};
                INSERT EmbeddingGpuRequests (MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,GenerationId,CorpusEpoch,ModelFingerprint,
                    Dimensions,InputsJson,InputDigest,State,NativeCleanupConfirmed,CleanupConfirmedAtUtc,CreatedAtUtc,UpdatedAtUtc)
                SELECT [task],[job],[record],1,[generation],state.CorpusEpoch,{runtime.Profile.ModelFingerprint},1,'[]',
                    {new string('a', 64)},2,1,{now},{now},{now} FROM #DeletionScale CROSS JOIN IndexState state WHERE [n]<{tasks} AND state.Id=1;
                DROP TABLE #DeletionScale;
                """);
            var foreignJob = Guid.NewGuid();
            var epoch = (await seed.IndexState.SingleAsync()).CorpusEpoch;
            seed.Jobs.Add(new JobEntity
            {
                Id = foreignJob, PipelineRecordId = control.RecordId, SourceRevision = 1, Stage = 4,
                Operation = "embed", PublicState = 5, DueAtUtc = now
            });
            seed.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = foreignGeneration, EmbeddingJobId = foreignJob, CorpusEpoch = epoch, CorpusVersion = 0,
                ModelFingerprint = runtime.Profile.ModelFingerprint, Dimensions = 1, MetadataChecksum = EmbedDraftDefaults.MetadataChecksum,
                CreatedAtUtc = now, RetiredAtUtc = now
            });
            // Even another root's unstarted request retains its exact generation
            // proof; empty projection cleanup cannot dispose of that metadata.
            seed.EmbeddingGpuRequests.Add(new EmbeddingGpuRequestEntity
            {
                MiniTaskId = Guid.NewGuid(), ParentJobId = foreignJob, PipelineRecordId = control.RecordId, SourceRevision = 1,
                GenerationId = foreignGeneration, CorpusEpoch = epoch, ModelFingerprint = runtime.Profile.ModelFingerprint,
                Dimensions = 1, InputsJson = "[]", InputDigest = new string('a', 64), CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await seed.SaveChangesAsync();
        }
        if (vectors>0) await SeedDistinctMembershipVectorsAsync(deleting,vectors,now);
        var commands = new RootDeletionScaleCommands();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(commands).Options);
        var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System, embeddingRuntime: runtime),
            new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId = deleting.RootId }));
        var preview = await service.PreviewAsync(mutation, "test", default);
        commands.EnforceParameterBudget = true;
        var timer = Stopwatch.StartNew();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(vectors>0 ? 20 : 10));
        await service.CommitAsync(mutation, preview.ConfirmationId, "scale-delete:" + deleting.RootId.ToString("N"), "test", budget.Token);
        output.WriteLine($"native_deletion revisions={revisions} records={records} tasks={tasks} admission_ms={timer.Elapsed.TotalMilliseconds:F1} max_guid_parameters={commands.MaximumGuidParameters}");
        commands.EnforceParameterBudget = false;
        var files = new ScaleDeletionFiles();
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System, embeddingRuntime: runtime);
        for (var page=0;page<=(vectors+4095)/4096;page++)
        {
            var first = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(budget.Token));
            var result=await store.PurgeAsync(first,null,budget.Token);
            if (result.Phase=="rebuild-index") break;
            Assert.True(result.ContinueImmediately,result.ReasonCode);
            if (page%64==0) output.WriteLine($"native_withdraw page={page} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
            store=new SqlSourceDeletionStore(factory,TimeProvider.System,embeddingRuntime:runtime);
        }
        store = new SqlSourceDeletionStore(factory, TimeProvider.System, embeddingRuntime: runtime);
        var second = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(budget.Token));
        Assert.Equal("cleanup-memberships", (await store.PurgeAsync(second, null, budget.Token)).Phase);
        await using (var boundary = CreateContext())
        {
            Assert.Equal(tasks, await boundary.IndexGenerations.CountAsync(generation => generation.EmbeddingJobId.HasValue &&
                boundary.Jobs.Any(job => job.Id == generation.EmbeddingJobId && job.PipelineRecord.SourceRevisionId.HasValue &&
                    job.PipelineRecord.SourceRevision!.SourceRootId == deleting.RootId), budget.Token));
            Assert.True(await boundary.IndexGenerations.AnyAsync(generation => generation.Id == foreignGeneration, budget.Token));
        }
        // Resume in another store and coordinator after the durable cutover.
        store = new SqlSourceDeletionStore(factory, TimeProvider.System, embeddingRuntime: runtime);
        var coordinator = new SourceDeletionCoordinator(store, fileStore: files,
            queryCleanupGate: new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System),
            artifactPublicationGate: new SqlSourceArtifactPublicationGate(factory));
        for (var pass = 0; pass < (revisions + tasks + ((vectors+999)/1000)*4) / 8 + 128; pass++)
        {
            var progressed = await coordinator.RunOneAsync(budget.Token);
            if (progressed && pass % 64 != 0) continue;
            await using var progress = CreateContext();
            var operation = await progress.SourceDeletionOperations.SingleAsync(value => value.SourceRootId == deleting.RootId, budget.Token);
            output.WriteLine($"native_deletion pass={pass} phase={operation.Phase} resume={operation.ResumePhase} state={operation.State} reason={operation.ReasonCode} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1} files={files.Count}");
            if (operation.State == 3) break;
            Assert.NotEqual(4, operation.State);
            Assert.True(progressed, "Ordinary deletion must continue making progress.");
        }
        await using var check = CreateContext();
        Assert.False(await check.SourceRootConfigurations.AnyAsync(value => value.Id == deleting.RootId));
        Assert.False(await check.SourceRevisions.AnyAsync(value => value.SourceRootId == deleting.RootId));
        Assert.False(await check.PipelineRecords.AnyAsync(value => value.SourceRevisionId == deleting.RevisionId));
        Assert.True(await check.SourceRootConfigurations.AnyAsync(value => value.Id == control.RootId));
        Assert.True(await check.SourceRevisions.AnyAsync(value => value.Id == control.RevisionId));
        Assert.True(await check.IndexGenerations.AnyAsync(value => value.Id == foreignGeneration));
        Assert.Single(await check.EmbeddingGpuRequests.ToArrayAsync());
        Assert.Empty(await check.GpuMiniTasks.ToArrayAsync());
        Assert.Equal(revisions, files.RetainedCount);
        Assert.Equal(3, (await check.SourceDeletionOperations.SingleAsync(value => value.SourceRootId == deleting.RootId)).State);
    }

    private sealed class RootDeletionScaleFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class ScaleDeletionFiles : ISourceDeletionFileStore
    {
        public int Count { get; private set; }
        public int RetainedCount { get; private set; }
        public ValueTask<SourceDeletionFileResult> DeleteAsync(SourceDeletionFileTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++; if (target.StorageKind == 1) RetainedCount++;
            return ValueTask.FromResult(new SourceDeletionFileResult(true));
        }
    }

    private sealed class RootDeletionScaleCommands : DbCommandInterceptor
    {
        public bool EnforceParameterBudget { get; set; }
        public int MaximumGuidParameters { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (EnforceParameterBudget)
            {
                var count = 0;
                foreach (DbParameter parameter in command.Parameters)
                    if (parameter.Value is Guid) count++;
                    else if (parameter.Value is IEnumerable<Guid> ids) count += ids.Count();
                    else if (parameter.Value is string { Length: > 0 } value && value[0] == '[')
                    {
                        using var json = JsonDocument.Parse(value);
                        count += json.RootElement.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String && item.TryGetGuid(out _));
                    }
                MaximumGuidParameters = Math.Max(MaximumGuidParameters, count);
                Assert.True(count <= 256, "Native deletion must retain root ID sets on the SQL server.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
