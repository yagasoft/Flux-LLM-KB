using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Restart_after_final_batch_and_cancelled_graph_commit_preserves_targets_until_file_receipt_is_durable()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("graph-boundary",SourceRootState.Deleting,now);
        var operationId = await AddDeletionAsync(source,now);
        var (vector,_) = await SeedMembershipsAsync(source,1,now);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlSourceDeletionStore(factory,TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operationId,stopAtCutover:true);
        var batch = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        await store.PurgeAsync(batch,null,default);
        // A fresh owner after the final batch must retain the canonical graph
        // until the distinct graph transaction commits.
        store = new SqlSourceDeletionStore(factory,TimeProvider.System);
        var finalCheck = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records",(await store.PurgeAsync(finalCheck,null,default)).Phase);
        var graph = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var interrupted = new SqlSourceDeletionStore(new CancelCommitFactory(_fixture.ConnectionString),TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.PurgeAsync(graph,null,default).AsTask());

        await using (var rolledBack = CreateContext())
        {
            Assert.True(await rolledBack.Vectors.AnyAsync(v => v.VectorId == vector.VectorId));
            Assert.True(await rolledBack.PipelineRecords.AnyAsync(p => p.Id == source.RecordId));
            Assert.False(await rolledBack.IndexGenerationVectors.AnyAsync());
            var operation = await rolledBack.SourceDeletionOperations.SingleAsync();
            Assert.Equal("purge-records",operation.Phase); Assert.Equal(1,operation.MembershipRowsDeleted);
            Assert.Equal(vector.VectorId,operation.MembershipVectorCursor);
            operation.LeaseExpiresAtUtc = now.AddMinutes(-1); await rolledBack.SaveChangesAsync();
        }
        store = new SqlSourceDeletionStore(factory,TimeProvider.System);
        var recovered = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("cleanup-files",(await store.PurgeAsync(recovered,null,default)).Phase);
        var files = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var target = Assert.Single(await store.ReadPendingCleanupAsync(files,default));
        await using (var graphCommitted = CreateContext())
        {
            Assert.False(await graphCommitted.Vectors.AnyAsync(v => v.VectorId == vector.VectorId));
            Assert.True(await graphCommitted.SourceRootConfigurations.AnyAsync(r => r.Id == source.RootId && r.State == 2));
        }
        // This SQL receipt test does not claim native cleanup. Real Windows IO
        // and independently verified native absence have separate staging cases.
        var ambiguousReceipt = new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString),TimeProvider.System);
        await ambiguousReceipt.RecordCleanupResultAsync(files,target.CleanupItemId,new(true),default);
        await ambiguousReceipt.RecordCleanupResultAsync(files,target.CleanupItemId,new(true),default);
        store = new SqlSourceDeletionStore(factory,TimeProvider.System);
        var final = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.True((await store.PurgeAsync(final,null,default)).Completed);
        await using var completed = CreateContext();
        Assert.Equal(3,await completed.SourceDeletionOperations.Select(o => o.State).SingleAsync());
        Assert.Equal(1,await completed.SourceDeletionOperations.Select(o => o.MembershipRowsDeleted).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Additive_progress_migration_preserves_legacy_attention_and_cleanup_targets()
    {
        var operationId = Guid.NewGuid(); var rootId = Guid.NewGuid(); var itemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var previous = CreateContext())
        {
            await previous.GetService<IMigrator>().MigrateAsync("20261004175522_AddRepositoryRecoveryAuthority");
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO SourceDeletionOperations (Id,SourceRootId,State,Phase,ReasonCode,CreatedAtUtc,UpdatedAtUtc,
                    SourceArtifactCount,SharedArtifactCount,PipelineRecordCount)
                VALUES ({operationId},{rootId},4,N'attention',N'source-delete-cleanup-failed',{now},{now},1,0,11);
                INSERT INTO SourceDeletionCleanupItems (Id,SourceDeletionOperationId,StorageKind,RelativePath,State,CreatedAtUtc,UpdatedAtUtc)
                VALUES ({itemId},{operationId},2,N'00000000000000000000000000000001',0,{now},{now});
                """);
            await previous.Database.MigrateAsync();
        }
        await using var check = CreateContext();
        var operation = await check.SourceDeletionOperations.SingleAsync();
        Assert.Equal(operationId,operation.Id); Assert.Equal(rootId,operation.SourceRootId);
        Assert.Equal(4,operation.State); Assert.Equal("attention",operation.Phase);
        Assert.Equal("source-delete-cleanup-failed",operation.ReasonCode);
        Assert.Equal(11,operation.PipelineRecordCount); Assert.Equal(1,operation.SourceArtifactCount);
        Assert.Equal(0,operation.MembershipRowsDeleted); Assert.Null(operation.MembershipVectorCursor);
        Assert.Null(operation.LastCommittedLeaseId); Assert.Null(operation.LastProgressAtUtc); Assert.Null(operation.ResumePhase);
        Assert.Equal(itemId,await check.SourceDeletionCleanupItems.Select(i => i.Id).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("cleanup-memberships", false, "cleanup-memberships")]
    [InlineData("purge-records", false, "purge-records")]
    [InlineData("cleanup-files", true, "cleanup-files")]
    [InlineData(null, true, "cleanup-files")]
    [InlineData(null, false, "accepted")]
    public async Task Public_retry_restores_exact_or_legacy_phase_and_replays_one_receipt(string? resumePhase,
        bool failedTarget, string expectedPhase)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("public-resume", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var expectedCount = resumePhase is null ? 0 : 37L;
        await using (var setup = CreateContext())
        {
            var operation = await setup.SourceDeletionOperations.SingleAsync();
            operation.State = 4; operation.Phase = "attention"; operation.ResumePhase = resumePhase;
            operation.MembershipRowsDeleted = expectedCount;
            operation.MembershipVectorCursor = resumePhase is null ? null : 2;
            operation.ReasonCode = "source-delete-sql-timeout";
            if (failedTarget) setup.SourceDeletionCleanupItems.Add(new() { Id = Guid.NewGuid(),
                SourceDeletionOperationId = operationId, StorageKind = 2, RelativePath = Guid.NewGuid().ToString("N"),
                State = 2, ReasonCode = "source-delete-io-failed", CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var factory = SqlTestData.CreateFactory(_fixture);
        var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
            new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId = source.RootId }));
        var preview = await service.PreviewAsync(mutation, "test", default);
        var key = "resume:" + operationId.ToString("N");

        var receipt = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);

        Assert.Equal(receipt.OperationId, replay.OperationId);
        Assert.True(replay.WasReplay);
        await using var check = CreateContext();
        var resumed = await check.SourceDeletionOperations.SingleAsync();
        Assert.Equal(operationId, resumed.Id);
        Assert.Equal(expectedPhase, resumed.Phase);
        Assert.Equal(expectedCount, resumed.MembershipRowsDeleted);
        Assert.Equal(resumePhase is null ? null : (long?)2, resumed.MembershipVectorCursor);
        Assert.Null(resumed.ResumePhase);
        Assert.Equal(0, resumed.State);
        Assert.False(await check.SourceDeletionCleanupItems.AnyAsync(row => row.State != 0));
    }

    [NativeSqlServerFact]
    public async Task Native_io_already_started_can_finish_but_stale_receipt_cannot_win_and_late_publication_survives()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("in-flight-delete", SourceRootState.Deleting, now);
        var unrelated = await SeedRootAsync("late-publisher", SourceRootState.Enabled, now);
        var operationId = await AddDeletionAsync(source, now);
        var fileRoot = Path.Combine(Path.GetTempPath(), "FluxKnowledgeDeletionOverlap_" + Guid.NewGuid().ToString("N"));
        var retained = Path.Combine(fileRoot, "retained");
        var indexes = Path.Combine(fileRoot, "indexes");
        var bytes = "native overlap fixture"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
        var blob = Path.Combine(retained, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        Directory.CreateDirectory(indexes);
        await File.WriteAllBytesAsync(blob, bytes);
        var files = new HeldFileStore(new WindowsSourceDeletionFileStore(retained, indexes));
        try
        {
            await using (var setup = CreateContext())
            {
                setup.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = source.RevisionId,
                    ContentSha256 = hash, StoreRelativePath = relative, ByteLength = bytes.Length,
                    ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
                await setup.SaveChangesAsync();
            }
            var factory = SqlTestData.CreateFactory(_fixture);
            var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
            var gate = new SqlSourceArtifactPublicationGate(factory);
            await AdvanceSqlToFilesAsync(store, operationId);
            var oldRun = new SourceDeletionCoordinator(store, fileStore: files, artifactPublicationGate: gate).RunOneAsync(default).AsTask();
            await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using (var expiry = CreateContext())
                await expiry.SourceDeletionOperations.ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUtc, now.AddMinutes(-1)));
            var nextOwner = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Null(await gate.TryAcquireExclusiveAsync(hash, default));
            var publication = gate.AcquireSharedAsync(hash, default).AsTask();
            Assert.False(publication.IsCompleted);

            files.Release.TrySetResult();
            Assert.True(await oldRun.WaitAsync(TimeSpan.FromSeconds(10)));
            await using (var published = await publication.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.False(File.Exists(blob));
                await File.WriteAllBytesAsync(blob, bytes);
                await using var writer = CreateContext();
                writer.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = unrelated.RevisionId,
                    ContentSha256 = hash, StoreRelativePath = relative, ByteLength = bytes.Length,
                    ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
                await writer.SaveChangesAsync();
            }
            await using (var staleCheck = CreateContext())
            {
                Assert.Equal(nextOwner.LeaseId, await staleCheck.SourceDeletionOperations.Select(row => row.LeaseId).SingleAsync());
                Assert.Equal(0, await staleCheck.SourceDeletionCleanupItems.Select(row => row.State).SingleAsync());
            }
            await store.DeferAsync(nextOwner, "source-delete-artifact-publication-active", default);
            Assert.True(await RunDeletionUntilStoppedAsync(new SourceDeletionCoordinator(store,
                fileStore: new WindowsSourceDeletionFileStore(retained, indexes), artifactPublicationGate: gate)));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(blob));
            await using var final = CreateContext();
            Assert.Equal(3, await final.SourceDeletionOperations.Select(row => row.State).SingleAsync());
            Assert.True(await final.SourceArtifacts.AnyAsync(row => row.SourceRevisionId == unrelated.RevisionId));
        }
        finally { files.Release.TrySetResult(); if (Directory.Exists(fileRoot)) Directory.Delete(fileRoot, recursive: true); }
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_membership_delete_rolls_back_rows_and_progress_then_a_new_owner_resumes()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("cancel-memberships", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var (vector, generations) = await SeedMembershipsAsync(source, 2, now);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId, stopAtCutover: true);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var interrupted = new SqlSourceDeletionStore(new MembershipCancellationFactory(_fixture.ConnectionString), TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.PurgeAsync(work, null, default).AsTask());

        await using (var rolledBack = CreateContext())
        {
            Assert.Equal(2, await rolledBack.IndexGenerationVectors.CountAsync(row => row.VectorId == vector.VectorId));
            var operation = await rolledBack.SourceDeletionOperations.SingleAsync();
            Assert.Equal(0, operation.MembershipRowsDeleted);
            Assert.Null(operation.MembershipVectorCursor);
            operation.LeaseExpiresAtUtc = now.AddMinutes(-1);
            await rolledBack.SaveChangesAsync();
        }
        var newOwner = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        await store.FailAsync(work, "stale-failure-must-not-win", default);
        await store.RecordCleanupResultAsync(work, Guid.NewGuid(), new(true), default);
        await store.PurgeAsync(newOwner, null, default);
        await using var resumed = CreateContext();
        Assert.Empty(await resumed.IndexGenerationVectors.ToListAsync());
        var receipt = await resumed.SourceDeletionOperations.SingleAsync();
        Assert.Equal(2, receipt.MembershipRowsDeleted);
        Assert.Equal(vector.VectorId, receipt.MembershipVectorCursor);
        Assert.Equal(newOwner.LeaseId, receipt.LastCommittedLeaseId);
        Assert.Null(receipt.ReasonCode);
        Assert.Equal(2, generations.Length);
    }

    [NativeSqlServerFact]
    public async Task Late_membership_behind_the_cursor_blocks_graph_purge_without_removing_canonical_data()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("late-membership", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var (vector, generations) = await SeedMembershipsAsync(source, 1, now);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId, stopAtCutover: true);
        var batch = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        await store.PurgeAsync(batch, null, default);
        await using (var lateWriter = CreateContext())
        {
            lateWriter.IndexGenerationVectors.Add(new() { GenerationId = generations[0], VectorId = vector.VectorId });
            await lateWriter.SaveChangesAsync();
        }
        var next = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));

        var refused = await store.PurgeAsync(next, null, default);

        Assert.Equal("source-delete-membership-changed", refused.ReasonCode);
        await using var check = CreateContext();
        Assert.True(await check.Vectors.AnyAsync(row => row.VectorId == vector.VectorId));
        Assert.True(await check.PipelineRecords.AnyAsync(row => row.Id == source.RecordId));
        Assert.Equal("cleanup-memberships", await check.SourceDeletionOperations.Select(row => row.ResumePhase).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task No_survivor_cutover_preserves_another_roots_generation_memberships_and_unplaced_draft()
    {
        var now = DateTimeOffset.UtcNow;
        var owned = await SeedRootAsync("narrow-delete", SourceRootState.Deleting, now);
        var unrelated = await SeedRootAsync("unrelated-deleting", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(owned, now);
        var (ownedVector, _) = await SeedMembershipsAsync(owned, 1, now);
        var (unrelatedVector, unrelatedGenerations) = await SeedMembershipsAsync(unrelated, 1, now);
        var draftId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.IndexGenerations.Add(new() { Id = draftId, EmbeddingJobId = unrelated.JobId,
                ModelFingerprint = "source-deletion-test:1", Dimensions = 1, IndexPath = string.Empty,
                MetadataChecksum = new string('a', 64), CreatedAtUtc = now });
            setup.Artifacts.Add(new() { Id = Guid.NewGuid(),PipelineRecordId = unrelated.RecordId,SourceRevision = 1,
                Stage = (int)FluxKnowledge.Domain.Pipeline.PipelineStage.Embed,ContentType = FluxKnowledge.Application.Indexing.EmbedDraftDefaults.ArtifactContentType,
                ContentHash = new string('d',64),SearchText = draftId.ToString("D"),CreatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);

        await AdvanceSqlToFilesAsync(store, operationId);

        await using var check = CreateContext();
        Assert.False(await check.Vectors.AnyAsync(row => row.VectorId == ownedVector.VectorId));
        Assert.True(await check.Vectors.AnyAsync(row => row.VectorId == unrelatedVector.VectorId && !row.IsDeleted));
        Assert.True(await check.IndexGenerationVectors.AnyAsync(row => row.VectorId == unrelatedVector.VectorId));
        Assert.Null(await check.IndexGenerations.Where(row => row.Id == draftId).Select(row => row.RetiredAtUtc).SingleAsync());
        Assert.True(await check.IndexGenerations.AnyAsync(row => row.Id == draftId && row.IndexPath == string.Empty && row.EmbeddingJobId == unrelated.JobId));
        Assert.True(await check.Artifacts.AnyAsync(row => row.PipelineRecordId == unrelated.RecordId && row.SearchText == draftId.ToString("D")));
        Assert.Null(await check.IndexGenerations.Where(row => row.Id == unrelatedGenerations[0]).Select(row => row.RetiredAtUtc).SingleAsync());
        Assert.DoesNotContain(await check.SourceDeletionCleanupItems.Select(row => row.RelativePath).ToListAsync(),
            path => path == draftId.ToString("N") || path == unrelatedGenerations[0].ToString("N"));
    }

    [NativeSqlServerFact]
    public async Task Two_deleting_sources_capture_their_shared_blob_until_the_last_reference_is_removed()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await SeedRootAsync("shared-first", SourceRootState.Deleting, now);
        var second = await SeedRootAsync("shared-second", SourceRootState.Deleting, now);
        var firstOperation = await AddDeletionAsync(first, now);
        var secondOperation = await AddDeletionAsync(second, now.AddSeconds(1));
        var fileRoot = Path.Combine(Path.GetTempPath(), "FluxKnowledgeDeletionShared_" + Guid.NewGuid().ToString("N"));
        var retained = Path.Combine(fileRoot, "retained");
        var indexes = Path.Combine(fileRoot, "indexes");
        var bytes = "shared staging fixture"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
        var blob = Path.Combine(retained, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        Directory.CreateDirectory(indexes);
        await File.WriteAllBytesAsync(blob, bytes);
        var original = Path.Combine(fileRoot, "original.txt");
        await File.WriteAllBytesAsync(original, bytes);
        try
        {
            await using (var setup = CreateContext())
            {
                foreach (var source in new[] { first, second }) setup.SourceArtifacts.Add(new()
                {
                    Id = Guid.NewGuid(), SourceRevisionId = source.RevisionId, ContentSha256 = hash,
                    StoreRelativePath = relative, ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                });
                await setup.SaveChangesAsync();
            }
            var factory = SqlTestData.CreateFactory(_fixture);
            var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
            await AdvanceSqlToFilesAsync(store, firstOperation, stopAtCutover: true);
            var firstHeld = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal(firstOperation, firstHeld.OperationId);
            await AdvanceSqlToFilesAsync(store, secondOperation, stopAtCutover: true);
            await using (var captured = CreateContext())
                Assert.Equal(2, await captured.SourceDeletionCleanupItems.CountAsync(row => row.StorageKind == 1 &&
                    (row.SourceDeletionOperationId == firstOperation || row.SourceDeletionOperationId == secondOperation)));
            await store.PurgeAsync(firstHeld, null, default);
            var coordinator = new SourceDeletionCoordinator(store,
                fileStore: new WindowsSourceDeletionFileStore(retained, indexes),
                artifactPublicationGate: new SqlSourceArtifactPublicationGate(factory));
            await RunDeletionUntilStoppedAsync(coordinator);

            await using var check = CreateContext();
            Assert.Equal(2, await check.SourceDeletionOperations.CountAsync(row => row.State == 3));
            Assert.Empty(await check.SourceArtifacts.ToListAsync());
            Assert.False(File.Exists(blob));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
            Assert.Equal(1, await check.SourceDeletionOperations.SumAsync(row => row.SharedArtifactCount));
        }
        finally { if (Directory.Exists(fileRoot)) Directory.Delete(fileRoot, recursive: true); }
    }

    [NativeSqlServerFact]
    public async Task Reading_a_shared_target_is_read_only_and_preservation_is_one_leased_receipt()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("shared-read-only", SourceRootState.Deleting, now);
        var survivor = await SeedRootAsync("shared-survivor", SourceRootState.Enabled, now);
        var operationId = await AddDeletionAsync(deleting, now);
        var hash = new string('c', 64);
        await using (var setup = CreateContext())
        {
            foreach (var source in new[] { deleting, survivor }) setup.SourceArtifacts.Add(new()
            {
                Id = Guid.NewGuid(), SourceRevisionId = source.RevisionId, ContentSha256 = hash,
                StoreRelativePath = Path.Combine("sha256", hash[..2], hash + ".bin"), ByteLength = 4,
                ChecksumVerifiedAtUtc = now, ReferenceCount = 1
            });
            await setup.SaveChangesAsync();
        }
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));

        var pending = await store.ReadPendingCleanupAsync(work, default);

        var target = Assert.Single(pending);
        await using (var readCheck = CreateContext())
        {
            Assert.Equal(0, await readCheck.SourceDeletionCleanupItems.Select(row => row.State).SingleAsync());
            Assert.Equal(0, await readCheck.SourceDeletionOperations.Select(row => row.SharedArtifactCount).SingleAsync());
        }
        await store.RecordCleanupResultAsync(work, target.CleanupItemId,
            new(true, "source-delete-shared-artifact-preserved"), default);
        await store.RecordCleanupResultAsync(work, target.CleanupItemId,
            new(true, "source-delete-shared-artifact-preserved"), default);
        await using var receipt = CreateContext();
        Assert.Equal(1, await receipt.SourceDeletionCleanupItems.Select(row => row.State).SingleAsync());
        Assert.Equal(1, await receipt.SourceDeletionOperations.Select(row => row.SharedArtifactCount).SingleAsync());
        Assert.Equal(work.LeaseId, await receipt.SourceDeletionOperations.Select(row => row.LastCommittedLeaseId).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task A_generation_target_is_revalidated_after_the_native_gate_before_starting_file_io()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("native-takeover", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        await using (var setup = CreateContext())
        {
            setup.IndexGenerations.Add(new()
            {
                Id = Guid.NewGuid(), EmbeddingJobId = source.JobId, ModelFingerprint = "source-deletion-test:1", Dimensions = 1,
                IndexPath = "owned-native-takeover", MetadataChecksum = new string('a', 64), VectorCount = 0,
                CreatedAtUtc = now, ValidatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId);
        var takeover = new TakeoverAfterFirstTargetRead(store, factory);
        var files = new RecordingFileStore();
        var coordinator = new SourceDeletionCoordinator(takeover, fileStore: files,
            queryCleanupGate: new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System));

        Assert.False(await coordinator.RunOneAsync(default));

        Assert.Equal(0, files.DeleteCalls);
        Assert.NotNull(takeover.NewOwner);
        await using var check = CreateContext();
        var operation = await check.SourceDeletionOperations.SingleAsync();
        Assert.Equal(1, operation.State);
        Assert.Equal(takeover.NewOwner!.LeaseId, operation.LeaseId);
        Assert.Null(operation.ReasonCode);
        Assert.Equal(0, await check.SourceDeletionCleanupItems.Select(row => row.State).SingleAsync());
    }

    private static async Task AdvanceSqlToFilesAsync(SqlSourceDeletionStore store, Guid operationId, bool stopAtCutover = false)
    {
        for (var pass = 0; pass < 32; pass++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal(operationId, work.OperationId);
            var result = await store.PurgeAsync(work, null, default);
            if (result.Phase == (stopAtCutover ? "cleanup-memberships" : "cleanup-files")) return;
            Assert.True(result.ContinueImmediately, result.ReasonCode);
        }
        throw new InvalidOperationException("The small fixture did not reach the expected SQL phase.");
    }

    private async Task<(VectorEntity vector, Guid[] generations)> SeedMembershipsAsync(SeededRoot source, int count, DateTimeOffset now)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await using (var setup = CreateContext())
        {
            setup.IndexGenerations.AddRange(ids.Select(id => new IndexGenerationEntity { Id = id,
                ModelFingerprint = "source-deletion-test:1", Dimensions = 1, IndexPath = "owned-recovery-test",
                MetadataChecksum = new string('a', 64), VectorCount = 1, CreatedAtUtc = now, ValidatedAtUtc = now }));
            await setup.SaveChangesAsync();
        }
        var vector = await AddVectorAsync(source, ids[0], "recovery", now);
        await using var members = CreateContext();
        members.IndexGenerationVectors.AddRange(ids.Select(id => new IndexGenerationVectorEntity { GenerationId = id, VectorId = vector.VectorId }));
        await members.SaveChangesAsync();
        return (vector, ids);
    }

    private sealed class MembershipCancellationFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(new CancelAfterMembershipDelete()).Options);
    }

    private sealed class CancelCommitFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(new CancelBeforeCommit()).Options);
    }

    private sealed class CancelBeforeCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData eventData,
            InterceptionResult result,CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("Simulated cancellation before the graph commit.");
    }

    private sealed class HeldFileStore(ISourceDeletionFileStore inner) : ISourceDeletionFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SourceDeletionFileResult> DeleteAsync(SourceDeletionFileTarget target, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await inner.DeleteAsync(target, ct);
        }
    }

    private sealed class CancelAfterMembershipDelete : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default) => command.CommandText.Contains("DELETE TOP", StringComparison.Ordinal)
                ? throw new OperationCanceledException("Simulated cancellation after the transactional mutation.")
                : ValueTask.FromResult(result);
    }

    private static async Task<bool> RunDeletionUntilStoppedAsync(SourceDeletionCoordinator coordinator)
    {
        var progressed = false;
        for (var quantum = 0; quantum < 64; quantum++)
        {
            if (!await coordinator.RunOneAsync(default)) return progressed;
            progressed = true;
        }
        throw new InvalidOperationException("The small deletion fixture did not finish its bounded quanta.");
    }

    private sealed class TakeoverAfterFirstTargetRead(SqlSourceDeletionStore inner,
        IDbContextFactory<FluxKnowledgeDbContext> factory) : ISourceDeletionStore
    {
        private bool _takenOver;
        public SourceDeletionWorkItem? NewOwner { get; private set; }
        public ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken ct) => inner.ClaimNextAsync(ct);
        public ValueTask<SourceDeletionRunResult> PurgeAsync(SourceDeletionWorkItem work,
            IndexGenerationCandidateSnapshot? candidate, CancellationToken ct) => inner.PurgeAsync(work, candidate, ct);
        public async ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(SourceDeletionWorkItem work, CancellationToken ct)
        {
            var pending = await inner.ReadPendingCleanupAsync(work, ct);
            if (!_takenOver && pending.Count != 0)
            {
                _takenOver = true;
                await using var context = await factory.CreateDbContextAsync(ct);
                await context.SourceDeletionOperations.Where(row => row.Id == work.OperationId && row.LeaseId == work.LeaseId)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
                NewOwner = Assert.IsType<SourceDeletionWorkItem>(await inner.ClaimNextAsync(ct));
                Assert.NotEqual(work.LeaseId, NewOwner.LeaseId);
            }
            return pending;
        }
        public ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem work, Guid id, SourceDeletionFileResult result, CancellationToken ct) =>
            inner.RecordCleanupResultAsync(work, id, result, ct);
        public ValueTask FailAsync(SourceDeletionWorkItem work, string reason, CancellationToken ct) => inner.FailAsync(work, reason, ct);
        public ValueTask DeferAsync(SourceDeletionWorkItem work, string reason, CancellationToken ct) => inner.DeferAsync(work, reason, ct);
    }
}
