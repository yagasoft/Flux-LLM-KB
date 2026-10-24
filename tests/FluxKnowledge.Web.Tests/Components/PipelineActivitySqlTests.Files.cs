using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerFact]
    public async Task File_pages_reconcile_current_inventory_scope_conflicts_and_retained_archive_members()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var otherRoot = await AddActivityRootAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            setup.SourceRevisions.AddRange(Enumerable.Range(0, 30).Select(i => Revision(Guid.NewGuid(), root, $"pending-{i:D2}.txt", now, false)));
            var archive = Revision(Guid.NewGuid(), root, "container.zip", now, false);
            var member = Revision(Guid.NewGuid(), root, "member.txt", now, false);
            member.OriginKind = 1; member.ParentSourceRevisionId = archive.Id;
            var memberIdentity = ArchiveMemberIdentity.Create(archive.StableSourceIdentity, "folder/member.txt");
            member.CanonicalPath = memberIdentity.SyntheticLocator;
            member.StableSourceIdentity = memberIdentity.StableSourceIdentity;
            setup.SourceRevisions.AddRange(archive, member);
            var conflict = Revision(Guid.NewGuid(), root, "conflict.txt", now, false);
            var conflict2 = Revision(Guid.NewGuid(), root, "conflict.txt", now, false);
            conflict2.ContentSha256 = new string('b', 64); conflict2.Revision = 2;
            var stable1 = Revision(Guid.NewGuid(), root, "stable-one.txt", now, false);
            var stable2 = Revision(Guid.NewGuid(), root, "stable-two.txt", now, false);
            stable2.StableSourceIdentity = stable1.StableSourceIdentity;
            stable2.Revision = 2;
            setup.SourceRevisions.AddRange(conflict, conflict2, stable1, stable2);
            var withdrawnArchive = Revision(Guid.NewGuid(), root, "withdrawn.zip", now, true);
            var withdrawnMember = Revision(Guid.NewGuid(), root, "withdrawn-member.txt", now, false);
            withdrawnMember.OriginKind = 1; withdrawnMember.ParentSourceRevisionId = withdrawnArchive.Id;
            var generated = Revision(Guid.NewGuid(), root, "generated-text.txt", now, false);
            generated.OriginKind = 2; generated.ParentSourceRevisionId = archive.Id;
            setup.SourceRevisions.AddRange(withdrawnArchive, withdrawnMember, generated);
            setup.SourceRevisions.Add(Revision(Guid.NewGuid(), otherRoot, "other.txt", now, false));
            await setup.SaveChangesAsync();
        }
        var reader = SnapshotReader();
        var sample = await reader.ReadOverviewAsync(default);
        var source = Assert.Single(sample.Activity.Sources, s => s.Id == root);
        Assert.Equal(35, source.KnownFiles);
        Assert.Equal(3, source.ProvisionalFiles);
        Assert.Equal(source.KnownFiles, PipelineFilePresentation.Categories(source).Sum(c => c.Count));
        Assert.Equal(source.PendingFiles, sample.Activity.FileStages.Where(s => s.SourceRootId == root).Sum(s => s.Count));
        var first = await reader.ReadPipelineFilesAsync(new(), root, 0, default);
        var second = await reader.ReadPipelineFilesAsync(new(), root, 1, default);
        Assert.Equal(35, first.TotalCount); Assert.Equal(25, first.Items.Count); Assert.Equal(10, second.Items.Count);
        var all = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(35, all.Select(f => f.SourceRevisionId).Distinct().Count());
        Assert.All(all, file => Assert.Equal(root, file.SourceRootId));
        Assert.EndsWith("container.zip", Assert.Single(all, f => f.FullPath.StartsWith("retained-archive-member:", StringComparison.Ordinal)).ArchivePath);
        Assert.DoesNotContain(all, f => f.FullPath.Contains("withdrawn", StringComparison.Ordinal) || f.FullPath.Contains("generated-text", StringComparison.Ordinal));
        var unconfirmed = await reader.ReadPipelineFilesAsync(new("Unconfirmed"), root, 0, default);
        Assert.Equal(3, unconfirmed.TotalCount);
        Assert.All(unconfirmed.Items, f => Assert.Equal("Stage unconfirmed", f.Stage));
        var other = await reader.ReadPipelineFilesAsync(new(), otherRoot, 0, default);
        Assert.Equal(1, other.TotalCount);
    }

    [NativeSqlServerFact]
    public async Task File_embedding_detail_counts_compatible_current_inventory_once_and_distinguishes_continuation_retry()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var record = await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null);
        Guid jobId;
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var pipeline = await setup.PipelineRecords.SingleAsync(p => p.Id == record);
            pipeline.CurrentStage = (int)PipelineStage.Embed;
            var job = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == record);
            jobId = job.Id; job.Stage = (int)PipelineStage.Embed; job.Operation = PipelineOperations.Embed;
            job.Reason = "embedding-batch-persisted"; job.AttemptCount = 70;
            var dispatch = await setup.OutboxMessages.SingleAsync(d => d.JobId == jobId);
            dispatch.Stage = job.Stage; dispatch.Operation = job.Operation;
            setup.SourceActivities.Add(Activity(pipeline.SourceRevisionId!.Value, SourceActivityState.Pending, record));
            var artifact = new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record, SourceRevision = 1,
                Stage = (int)PipelineStage.CanonicalIndex, ContentHash = new string('c', 64), ContentType = "text/plain",
                SearchText = "Private payload must not be selected by status counters.", CreatedAtUtc = now };
            setup.Artifacts.Add(artifact);
            var chunks = Enumerable.Range(0, 6).Select(i => new TextChunkEntity { ArtifactId = artifact.Id,
                SourceRevision = 1, Ordinal = i, Length = 4, Content = "text", ContentHash = new string('d', 64),
                PassagePolicyFingerprint = new string('e', 64), SearchInputHash = new string('f', 64) }).ToArray();
            setup.TextChunks.AddRange(chunks);
            var state = await setup.IndexState.SingleAsync();
            var epoch = state.CorpusEpoch;
            var draft = new IndexGenerationEntity { Id = Guid.NewGuid(), EmbeddingJobId = jobId, CorpusEpoch = epoch,
                CorpusVersion = state.CorpusVersion, ModelFingerprint = "synthetic-profile", Dimensions = 2, MetadataChecksum = new string('a', 64), CreatedAtUtc = now };
            var copy = new IndexGenerationEntity { Id = Guid.NewGuid(), CorpusEpoch = epoch,
                CorpusVersion = state.CorpusVersion, ModelFingerprint = draft.ModelFingerprint, Dimensions = 2, MetadataChecksum = new string('b', 64), CreatedAtUtc = now };
            setup.IndexGenerations.AddRange(draft, copy);
            await setup.SaveChangesAsync();
            setup.Vectors.AddRange(Vector(chunks[0], draft, now.AddMinutes(-3)), Vector(chunks[1], draft, now.AddMinutes(-2)),
                Vector(chunks[0], copy, now), Vector(chunks[1], copy, now));
            var incompatible = Vector(chunks[2], draft, now);
            incompatible.SearchInputHash = new string('1', 64);
            var deleted = Vector(chunks[3], draft, now); deleted.IsDeleted = true;
            var wrongCase = Vector(chunks[4], draft, now); wrongCase.ModelFingerprint = draft.ModelFingerprint.ToUpperInvariant();
            var wrongDimensions = Vector(chunks[5], draft, now); wrongDimensions.Dimensions = 3; wrongDimensions.Values = new byte[12];
            setup.Vectors.AddRange(incompatible, deleted, wrongCase, wrongDimensions);
            await setup.SaveChangesAsync();
        }
        var commands = new FileCommandTrace();
        var reader = SnapshotReader(commands);
        var page = await reader.ReadPipelineFilesAsync(new("Pending", "Embedding"), root, 0, default);
        var file = Assert.Single(page.Items);
        Assert.Equal("Queued continuation", file.State);
        var progress = Assert.Single(file.Embedding);
        Assert.Equal(2, progress.SavedPassages); Assert.Equal(6, progress.TotalPassages); Assert.Equal(4, progress.RemainingPassages);
        Assert.InRange(progress.LastSavedAtUtc!.Value, now.AddMinutes(-2).AddMilliseconds(-1), now.AddMinutes(-2).AddMilliseconds(1));
        Assert.DoesNotContain(commands.Commands, sql => sql.Contains("vector.Values", StringComparison.OrdinalIgnoreCase) || sql.Contains("chunk.Content,", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(commands.Commands.Count, 1, 40);
        await using (var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
            await update.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Reason, "embedding-gpu-retry"));
        var retry = await reader.ReadPipelineFilesAsync(new("Pending"), root, 0, default);
        Assert.Equal("Retrying", Assert.Single(retry.Items).State);
        Assert.Equal(2, Assert.Single(Assert.Single(retry.Items).Embedding).SavedPassages);
        await using (var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
            await update.IndexGenerations.Where(g => g.EmbeddingJobId == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.CorpusEpoch, Guid.NewGuid()));
        var unavailable = Assert.Single(Assert.Single((await reader.ReadPipelineFilesAsync(new("Pending"), root, 0, default)).Items).Embedding);
        Assert.Null(unavailable.SavedPassages); Assert.Null(unavailable.TotalPassages); Assert.Null(unavailable.RemainingPassages);

        static VectorEntity Vector(TextChunkEntity chunk, IndexGenerationEntity generation, DateTimeOffset saved) => new()
        {
            TextChunkId = chunk.Id, SourceRevision = 1, IndexGenerationId = generation.Id, ModelFingerprint = generation.ModelFingerprint,
            Dimensions = generation.Dimensions, Values = new byte[8], TextChunkContentHash = chunk.ContentHash,
            SearchInputHash = chunk.SearchInputHash, PayloadChecksum = new string('2', 64), CreatedAtUtc = saved
        };
    }

    [NativeSqlServerFact]
    public async Task Bound_OCR_demand_deduplicates_requested_pages_and_later_publication_does_not_settle_native_history()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedGpuReadLockAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var task = await setup.GpuMiniTasks.SingleAsync(t => t.Id == seed.Task);
            var job = await setup.Jobs.SingleAsync(j => j.Id == task.ParentJobId);
            var record = await setup.PipelineRecords.SingleAsync(p => p.Id == job.PipelineRecordId);
            setup.SourceActivities.Add(Activity(record.SourceRevisionId!.Value, SourceActivityState.Pending, record.Id));
            setup.DocumentOcrRequests.Add(new() { MiniTaskId = task.Id, ParentJobId = job.Id, PipelineRecordId = record.Id,
                SourceRevision = 1, RetainedSourceRevisionId = record.SourceRevisionId.Value, ContentSha256 = record.ContentHash,
                RequestedPageIndexesJson = "[0,1]", ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic",
                CreatedAtUtc = now, UpdatedAtUtc = now });
            var secondTask = Guid.NewGuid();
            setup.GpuMiniTasks.Add(new() { Id = secondTask, ParentJobId = job.Id, SourceRevision = 1,
                ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", IdempotencyKey = $"file-ocr:{secondTask:N}",
                EstimatedBytes = 1, CreatedAtUtc = now });
            setup.DocumentOcrRequests.Add(new() { MiniTaskId = secondTask, ParentJobId = job.Id, PipelineRecordId = record.Id,
                SourceRevision = 1, RetainedSourceRevisionId = record.SourceRevisionId.Value, ContentSha256 = record.ContentHash,
                RequestedPageIndexesJson = "[1,2]", ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic",
                CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var reader = SnapshotReader();
        var current = await reader.ReadOverviewAsync(default);
        var demand = Assert.Single(current.Activity.OcrDemand, o => o.SourceRootId == seed.Root);
        Assert.Equal(1, demand.ConfirmedFiles); Assert.Equal(1, demand.RunningFiles); Assert.Equal(3, demand.RequestedPages);
        Assert.Equal("OCR running", Assert.Single(current.Activity.FileStages, s => s.SourceRootId == seed.Root).Stage);
        await using (var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var task = await update.GpuMiniTasks.SingleAsync(t => t.Id == seed.Task);
            task.ExecutionState = 3;
            var job = await update.Jobs.SingleAsync(j => j.Id == task.ParentJobId);
            job.PublicState = (int)PublicJobState.Completed;
            var record = await update.PipelineRecords.SingleAsync(p => p.Id == job.PipelineRecordId);
            record.CurrentStage = (int)PipelineStage.Publish; record.CompletionCriteriaMet = true;
            var activity = await update.SourceActivities.SingleAsync(a => a.SourceRevisionId == record.SourceRevisionId);
            activity.State = (int)SourceActivityState.Completed;
            await update.SaveChangesAsync();
        }
        var published = await reader.ReadOverviewAsync(default);
        Assert.Equal(1, Assert.Single(published.Activity.Sources, s => s.Id == seed.Root).PublishedFiles);
        Assert.DoesNotContain(published.Activity.FileCauses, c => c.SourceRootId == seed.Root);
        Assert.Equal(1, published.Activity.GpuTasks.Where(g => g.SourceRootId == seed.Root && g.State == "Outcome uncertain").Sum(g => g.Count));
    }

    private sealed class FileCommandTrace : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
