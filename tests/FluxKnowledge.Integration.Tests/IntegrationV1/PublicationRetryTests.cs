using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

[Collection("sql-full-text")]
public sealed class PublicationRetryTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_retry_preserves_the_failed_attempt_and_same_delivery_then_normally_publishes_current_cited_text(bool checkpointBinding)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Initial synthetic passage.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Retained recovery acceptance passage.");
        var failed = await FailPublishAsync(environment, recordId);
        if (checkpointBinding)
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            var embed = await db.Artifacts.SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed);
            var generation = await db.IndexGenerations.SingleAsync(value => value.Id == Guid.Parse(embed.SearchText));
            generation.EmbeddingJobId = (await db.Jobs.SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed)).Id;
            var state = await db.IndexState.SingleAsync(); generation.CorpusEpoch = state.CorpusEpoch; generation.CorpusVersion = state.CorpusVersion;
            var vectors = await db.Vectors.Include(value => value.TextChunk).Where(value => value.IndexGenerationId == generation.Id).ToArrayAsync();
            foreach (var vector in vectors) vector.SearchInputHash = vector.TextChunk.SearchInputHash;
            await db.SaveChangesAsync();
        }
        var wake = new WakeSignal();
        var service = Service(environment, wake);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        var key = "publication-recovery-first-" + checkpointBinding;
        var first = await service.CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        Assert.False(first.WasReplay);
        Assert.Equal(1, wake.Count);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value);
            var dispatch = await db.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
            Assert.Equal(failed.Job.AttemptCount, job.AttemptCount);
            Assert.Equal(failed.Job.LeaseGeneration + 1, job.LeaseGeneration);
            Assert.Equal(failed.DispatchMessage.DispatchMessageId.Value, dispatch.Id);
            Assert.Equal(failed.DispatchMessage.DispatchGeneration, dispatch.DispatchGeneration);
            Assert.Equal(failed.DispatchMessage.IdempotencyKey, dispatch.IdempotencyKey);
            Assert.Equal(failed.DispatchMessage.LeaseGeneration + 1, dispatch.LeaseGeneration);
            Assert.Null(dispatch.DispatchedAtUtc);
            var activity = await db.SourceActivities.SingleAsync(value => value.ResultingPipelineRecordId == recordId);
            Assert.Equal((int)SourceActivityState.FailedRetryable, activity.State);
            Assert.Contains("synthetic-publish-timeout", (await db.AuditEvents.SingleAsync(value => value.EventType == "publication recovery queued")).DetailsJson);
            Assert.Contains("synthetic-error-details", (await db.AuditEvents.SingleAsync(value => value.EventType == "publication recovery queued")).DetailsJson);
        }
        await AssertOldLeaseRefusedAsync(environment, failed);
        var recovered = await ClaimAsync(environment, recordId);
        await AssertOldLeaseRefusedAsync(environment, failed);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new StatusPublisher(), wake, TimeProvider.System);
        await new PublishStageWorker(environment.Store, environment.Store, environment.Builder, transitions, TimeProvider.System)
            .ExecuteAsync(recovered, CancellationToken.None);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            Assert.True((await db.PipelineRecords.SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
            Assert.Equal((int)PublicJobState.Completed, (await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value)).PublicState);
            Assert.Equal((int)SourceActivityState.Completed, (await db.SourceActivities.SingleAsync(value => value.ResultingPipelineRecordId == recordId)).State);
            var chunk = await db.TextChunks.SingleAsync(value => value.Artifact.PipelineRecordId == recordId);
            var record = await db.PipelineRecords.SingleAsync(value => value.Id == recordId);
            var source = await db.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId);
            var artifact = await db.Artifacts.SingleAsync(value => value.Id == chunk.ArtifactId);
            var epoch = (await db.IndexState.SingleAsync()).CorpusEpoch;
            var identityHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.CanonicalPath)));
            var binding = new CorpusEvidenceBinding(1, source.SourceRootId, source.Id, identityHash, recordId, record.Revision,
                artifact.Id, artifact.ContentHash, chunk.Id, chunk.ContentHash, chunk.StartOffset, chunk.Length, epoch);
            var read = await new SqlCorpusRetrievalReader(environment.Factory).ReadAsync(binding, 0, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Contains("Retained recovery acceptance passage", read.Text);
            Assert.Single(await db.OutboxMessages.Where(value => value.JobId == failed.Job.JobId.Value).ToListAsync());
        }
        var replay = await service.CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.OperationId, replay.OperationId);
    }

    [NativeSqlServerTheory]
    [InlineData("queued")]
    [InlineData("processing")]
    [InlineData("completed")]
    [InlineData("wrong-stage")]
    [InlineData("job-lease")]
    [InlineData("dispatch-lease")]
    [InlineData("unfinished-delivery")]
    [InlineData("completed-artifact")]
    [InlineData("publish-artifact")]
    [InlineData("document-publication")]
    [InlineData("record-complete")]
    [InlineData("record-deleted")]
    [InlineData("root-paused")]
    [InlineData("root-deleting")]
    [InlineData("source-suppressed")]
    [InlineData("activity-complete")]
    [InlineData("ambiguous-activity")]
    [InlineData("competing-job")]
    [InlineData("ambiguous-delivery")]
    [InlineData("missing-generation")]
    [InlineData("wrong-embed-checksum")]
    [InlineData("wrong-generation-profile")]
    [InlineData("obsolete-generation-epoch")]
    [InlineData("deleted-vector")]
    [InlineData("wrong-vector-payload")]
    [InlineData("case-only-vector-profile")]
    [InlineData("altered-chunk-content")]
    [InlineData("altered-chunk-context")]
    [InlineData("checkpoint-missing-stamp")]
    [InlineData("checkpoint-placed")]
    [InlineData("checkpoint-validated")]
    [InlineData("checkpoint-checksum")]
    [InlineData("checkpoint-missing-vector-hash")]
    [InlineData("rebuild")]
    public async Task Ineligible_or_contradictory_state_refuses_preview_and_stale_commit_without_scheduling(string change)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic refusal acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        var service = Service(environment);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value);
            var dispatch = await db.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            var record = await db.PipelineRecords.SingleAsync(value => value.Id == recordId);
            var source = await db.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId);
            var root = await db.SourceRootConfigurations.SingleAsync(value => value.Id == source.SourceRootId);
            var activity = await db.SourceActivities.SingleAsync(value => value.ResultingPipelineRecordId == recordId);
            var embed = await db.Artifacts.SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed);
            var generation = await db.IndexGenerations.SingleAsync(value => value.Id == Guid.Parse(embed.SearchText));
            var vector = await db.Vectors.FirstAsync(value => value.IndexGenerationId == generation.Id);
            if (change.StartsWith("checkpoint-", StringComparison.Ordinal))
            {
                generation.EmbeddingJobId = (await db.Jobs.SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed)).Id;
                var state = await db.IndexState.SingleAsync(); generation.CorpusEpoch = state.CorpusEpoch; generation.CorpusVersion = state.CorpusVersion;
                var currentChunk = await db.TextChunks.SingleAsync(value => value.Id == vector.TextChunkId);
                vector.SearchInputHash = currentChunk.SearchInputHash;
            }
            switch (change)
            {
                case "queued": job.PublicState = (int)PublicJobState.WorkerQueued; break;
                case "processing": job.PublicState = (int)PublicJobState.WorkerProcessing; break;
                case "completed": job.PublicState = (int)PublicJobState.Completed; break;
                case "wrong-stage": job.Stage = (int)PipelineStage.Embed; job.Operation = PipelineOperations.Embed; break;
                case "job-lease": job.LeaseOwner = "obsolete-or-live-owner"; job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1); break;
                case "dispatch-lease": dispatch.LeaseOwner = "obsolete-or-live-owner"; dispatch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "unfinished-delivery": dispatch.DispatchedAtUtc = null; break;
                case "completed-artifact": dispatch.CompletedArtifactId = embed.Id; break;
                case "publish-artifact": db.Artifacts.Add(new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = record.Revision,
                    Stage = (int)PipelineStage.Publish, ContentType = "text/plain", ContentHash = new string('a', 64), SearchText = "contradictory success", CreatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "document-publication":
                    var branch = new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = activity.Id, SourceRevisionId = source.Id,
                        InputSha256 = source.ContentSha256, ProcessorVersion = "synthetic-processor", ProcessorFingerprint = new string('a', 64), State = 2,
                        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    db.SourceProcessorBranches.Add(branch);
                    db.DocumentPublications.Add(new DocumentPublicationEntity { OwnerSourceRevisionId = source.Id, DocumentInputSourceRevisionId = source.Id,
                        SourceProcessorBranchId = branch.Id, PipelineRecordId = recordId, PipelineRecordRevision = record.Revision,
                        ProcessorFingerprint = branch.ProcessorFingerprint, PublishedAtUtc = DateTimeOffset.UtcNow }); break;
                case "record-complete": record.CompletionCriteriaMet = true; break;
                case "record-deleted": record.IsDeleted = true; break;
                case "root-paused": root.State = (int)SourceRootState.Paused; break;
                case "root-deleting": root.State = (int)SourceRootState.Deleting; break;
                case "source-suppressed": source.SuppressedAtUtc = DateTimeOffset.UtcNow; break;
                case "activity-complete": activity.State = (int)SourceActivityState.Completed; break;
                case "ambiguous-activity": db.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = source.Id,
                    ActivityKind = activity.ActivityKind, ExecutionClass = activity.ExecutionClass, ProcessorVersion = "synthetic-other-version",
                    InputFingerprint = activity.InputFingerprint, State = (int)SourceActivityState.FailedTerminal, ResultingPipelineRecordId = recordId,
                    ResultingPipelineRecordRevision = record.Revision, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "competing-job": db.Jobs.Add(new JobEntity { Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = record.Revision,
                    Stage = job.Stage, Operation = job.Operation, PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = DateTimeOffset.UtcNow }); break;
                case "ambiguous-delivery": db.OutboxMessages.Add(new OutboxMessageEntity { Id = Guid.NewGuid(), JobId = job.Id, PipelineRecordId = recordId,
                    SourceRevision = record.Revision, Stage = job.Stage, Operation = job.Operation, DispatchGeneration = dispatch.DispatchGeneration + 1,
                    IdempotencyKey = Guid.NewGuid().ToString("N"), DueAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "missing-generation": embed.SearchText = Guid.NewGuid().ToString("D"); break;
                case "wrong-embed-checksum": embed.ContentHash = new string('b', 64); break;
                case "wrong-generation-profile": generation.ModelFingerprint = "incompatible-profile"; break;
                case "obsolete-generation-epoch": generation.CorpusEpoch = Guid.NewGuid(); generation.CorpusVersion = 0; break;
                case "deleted-vector": vector.IsDeleted = true; break;
                case "wrong-vector-payload": vector.Values = new byte[vector.Values.Length]; break;
                case "case-only-vector-profile": vector.ModelFingerprint = vector.ModelFingerprint.ToUpperInvariant(); break;
                case "altered-chunk-content": (await db.TextChunks.SingleAsync(value => value.Id == vector.TextChunkId)).Content = "Altered content without updating its stored hashes."; break;
                case "altered-chunk-context": (await db.TextChunks.SingleAsync(value => value.Id == vector.TextChunkId)).ContextHeader = "Altered context without updating its stored hashes."; break;
                case "checkpoint-missing-stamp": generation.CorpusEpoch = null; generation.CorpusVersion = null; break;
                case "checkpoint-placed": generation.IndexPath = "C:\\synthetic\\contradictory-placed"; break;
                case "checkpoint-validated": generation.ValidatedAtUtc = DateTimeOffset.UtcNow; break;
                case "checkpoint-checksum": generation.MetadataChecksum = new string('c', 64); break;
                case "checkpoint-missing-vector-hash": vector.SearchInputHash = null; break;
                case "rebuild":
                    var operation = new CorpusRebuildOperationEntity { Id = Guid.NewGuid(), TargetEpoch = Guid.NewGuid(), ManifestHash = new string('a', 64), ManifestJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow };
                    db.CorpusRebuildOperations.Add(operation); (await db.IndexState.SingleAsync()).CorpusRebuildOperationId = operation.Id; break;
                default: throw new InvalidOperationException(change);
            }
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(environment, failed.Job.JobId.Value);
        await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId, "refusal-" + change, "test", CancellationToken.None).AsTask());
        Assert.Equal(before, await SnapshotAsync(environment, failed.Job.JobId.Value));
    }

    [NativeSqlServerTheory]
    [InlineData("before")]
    [InlineData("saved")]
    [InlineData("committed")]
    public async Task Crashes_keep_scheduling_audit_and_receipt_atomic_and_replay_only_the_committed_attempt(string boundary)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic crash acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        Action crash = () => throw new InvalidOperationException("synthetic-native-crash");
        var service = Service(environment, beforeCommit: boundary == "before" ? crash : null,
            afterSave: boundary == "saved" ? crash : null, afterCommit: boundary == "committed" ? crash : null);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        var before = await SnapshotAsync(environment, failed.Job.JobId.Value);
        var error = await Record.ExceptionAsync(() => service.CommitAsync(command, preview.ConfirmationId, "crash-" + boundary, "test", CancellationToken.None).AsTask());
        Assert.NotNull(error);
        if (boundary == "before") Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<NativeOperationCommitUncertainException>(error);
        if (boundary != "committed") Assert.Equal(before, await SnapshotAsync(environment, failed.Job.JobId.Value));
        var receipt = await Service(environment).CommitAsync(command, preview.ConfirmationId, "crash-" + boundary, "test", CancellationToken.None);
        Assert.Equal(boundary == "committed", receipt.WasReplay);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await db.NativeOperationReceipts.Where(value => value.IdempotencyKey == "crash-" + boundary).ToListAsync());
        Assert.Single(await db.AuditEvents.Where(value => value.EventType == "publication recovery queued").ToListAsync());
        Assert.Equal(failed.Job.LeaseGeneration + 1, (await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value)).LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_confirmations_schedule_one_attempt_and_replayed_receipt_after_later_failure_never_requeues()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic concurrent acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        var service = Service(environment);
        var command = Mutation(failed.Job.JobId.Value);
        var previews = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.PreviewAsync(command, "test", CancellationToken.None).AsTask()));
        var outcomes = await Task.WhenAll(previews.Select(async (preview, index) =>
        {
            try { return (Receipt: await service.CommitAsync(command, preview.ConfirmationId, "concurrent-" + index, "test", CancellationToken.None), Error: (NativeOperationException?)null, Index: index); }
            catch (NativeOperationException exception) { return (Receipt: (NativeActionReceipt?)null, Error: exception, Index: index); }
        }));
        var winner = Assert.Single(outcomes, value => value.Receipt is not null);
        Assert.Single(outcomes, value => value.Error is not null);
        var work = await ClaimAsync(environment, recordId);
        await new SqlStageTransitionStore(environment.Factory).FailAsync(new(work.DispatchMessage, work.Job, "second-synthetic-failure", "second-details", "test"), CancellationToken.None);
        var before = await SnapshotAsync(environment, failed.Job.JobId.Value);
        var replay = await service.CommitAsync(command, previews[winner.Index].ConfirmationId, "concurrent-" + winner.Index, "test", CancellationToken.None);
        Assert.True(replay.WasReplay);
        Assert.Equal(winner.Receipt!.OperationId, replay.OperationId);
        Assert.Equal(before, await SnapshotAsync(environment, failed.Job.JobId.Value));
        var fresh = await service.PreviewAsync(command, "test", CancellationToken.None);
        await service.CommitAsync(command, fresh.ConfirmationId, "explicit-second-recovery", "test", CancellationToken.None);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.AuditEvents.CountAsync(value => value.EventType == "publication recovery queued"));
        Assert.Single(await db.OutboxMessages.Where(value => value.JobId == failed.Job.JobId.Value).ToListAsync());
        Assert.Equal(work.Job.AttemptCount, (await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value)).AttemptCount);
    }

    [NativeSqlServerFact]
    public async Task Configuration_version_changes_and_deployment_hold_fence_a_confirmed_retry()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic hold acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        var service = Service(environment);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var sourceId = (await db.PipelineRecords.SingleAsync(value => value.Id == recordId)).SourceRevisionId;
            var rootId = (await db.SourceRevisions.SingleAsync(value => value.Id == sourceId)).SourceRootId;
            var root = await db.SourceRootConfigurations.SingleAsync(value => value.Id == rootId); root.ConfigurationRevision++; root.DisplayName = "Changed configuration";
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(environment, failed.Job.JobId.Value);
        Assert.Equal("operation-fenced", (await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId,
            "stale-config", "test", CancellationToken.None).AsTask())).ReasonCode);
        environment.PermitRebuild(Guid.NewGuid());
        Assert.Equal("deployment-validation-held", (await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask())).ReasonCode);
        Assert.Equal("deployment-validation-held", (await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId,
            "held", "test", CancellationToken.None).AsTask())).ReasonCode);
        Assert.Equal(before, await SnapshotAsync(environment, failed.Job.JobId.Value));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_after_commit_keeps_the_attempt_atomic_and_replay_fenced(bool afterCommit)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic cancellation acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        using var cancellation = new CancellationTokenSource();
        var service = Service(environment, beforeCommit: afterCommit ? null : cancellation.Cancel, afterCommit: afterCommit ? cancellation.Cancel : null);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        var before = await SnapshotAsync(environment, failed.Job.JobId.Value);
        var key = "cancel-" + afterCommit;
        var error = await Record.ExceptionAsync(() => service.CommitAsync(command, preview.ConfirmationId, key, "test", cancellation.Token).AsTask());
        if (afterCommit) Assert.IsType<NativeOperationCommitUncertainException>(error);
        else { Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.Equal(before, await SnapshotAsync(environment, failed.Job.JobId.Value)); }
        var receipt = await Service(environment).CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        Assert.Equal(afterCommit, receipt.WasReplay);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await db.NativeOperationReceipts.Where(value => value.IdempotencyKey == key).ToListAsync());
        Assert.Single(await db.AuditEvents.Where(value => value.EventType == "publication recovery queued").ToListAsync());
        Assert.Equal(failed.Job.LeaseGeneration + 1, (await db.Jobs.SingleAsync(value => value.Id == failed.Job.JobId.Value)).LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Recovery_preserves_independent_code_outcomes_and_normal_worker_rebuilds_a_changed_publication_snapshot()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Synthetic stamp and code acceptance.");
        var failed = await FailPublishAsync(environment, recordId);
        var codeId = Guid.NewGuid();
        string beforeCode;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var text = await db.SourceActivities.SingleAsync(value => value.ResultingPipelineRecordId == recordId);
            db.SourceActivities.Add(new SourceActivityEntity { Id = codeId, SourceRevisionId = text.SourceRevisionId, ActivityKind = (int)SourceActivityKind.CodeParsing,
                ExecutionClass = text.ExecutionClass, ProcessorVersion = "synthetic-code", InputFingerprint = text.InputFingerprint, State = (int)SourceActivityState.Completed,
                Reason = "code-index-complete", AttemptCount = 1, ResultingPipelineRecordId = recordId, ResultingPipelineRecordRevision = text.ResultingPipelineRecordRevision,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            beforeCode = JsonSerializer.Serialize(await db.SourceActivities.AsNoTracking().SingleAsync(value => value.Id == codeId));
        }
        var service = Service(environment);
        var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await service.CommitAsync(command, preview.ConfirmationId, "stamp-recovery", "test", CancellationToken.None);
        var work = await ClaimAsync(environment, recordId);
        var publisher = new ChangingSnapshotPublisher(environment);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new StatusPublisher(), new WakeSignal(), TimeProvider.System);
        await new PublishStageWorker(environment.Store, environment.Store, publisher, transitions, TimeProvider.System).ExecuteAsync(work, CancellationToken.None);
        Assert.Equal(2, publisher.Placements);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(beforeCode, JsonSerializer.Serialize(await verification.SourceActivities.AsNoTracking().SingleAsync(value => value.Id == codeId)));
        Assert.True((await verification.PipelineRecords.SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
        var state = await verification.IndexState.SingleAsync();
        var active = await verification.IndexGenerations.SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        Assert.Equal(state.CorpusEpoch, active.CorpusEpoch);
        Assert.Equal(state.CorpusVersion, active.CorpusVersion);
    }

    private sealed class ChangingSnapshotPublisher(SqlToUsearchRebuildTests.PipelineEnvironment environment) : IIndexGenerationPublisher
    {
        public int Placements { get; private set; }
        public async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(Guid generation, CancellationToken ct)
        {
            var candidate = await environment.Builder.BuildAndPlaceAsync(generation, ct);
            if (++Placements == 1)
            {
                await using var db = await environment.Factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct);
                await SqlPublishedPassageSelection.AdvanceVersionAsync(db, DateTimeOffset.UtcNow, ct);
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            return candidate;
        }
        public ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(Guid generation, CancellationToken ct) => environment.Builder.RebuildFromSqlAsync(generation, ct);
    }

    [NativeSqlServerTheory]
    [InlineData("{\"jobIds\":[]}")]
    [InlineData("{\"jobId\":\"invalid\"}")]
    [InlineData("{\"jobId\":[],\"force\":true}")]
    [InlineData("{\"jobId\":\"00000000-0000-0000-0000-000000000001\",\"force\":true}")]
    public async Task Recovery_accepts_only_a_single_job_id_and_never_creates_an_intent_for_invalid_payloads(string payload)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic original.", publish: false);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var intents = await db.NativeOperationIntents.CountAsync();
        using var document = JsonDocument.Parse(payload);
        var command = new NativeCorpusMutation("publication_retry", document.RootElement.Clone());
        Assert.Equal("invalid-payload", (await Assert.ThrowsAsync<NativeOperationException>(() => Service(environment)
            .PreviewAsync(command, "test", CancellationToken.None).AsTask())).ReasonCode);
        Assert.Equal(intents, await db.NativeOperationIntents.CountAsync());
    }

    private static async Task<string> SnapshotAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, Guid jobId)
    {
        await using var db = await environment.Factory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking().SingleAsync(value => value.Id == jobId);
        return JsonSerializer.Serialize(new { job, dispatch = await db.OutboxMessages.AsNoTracking().Where(value => value.JobId == jobId).OrderBy(value => value.Id).ToArrayAsync(),
            activity = await db.SourceActivities.AsNoTracking().Where(value => value.ResultingPipelineRecordId == job.PipelineRecordId).OrderBy(value => value.Id).ToArrayAsync(),
            receipts = await db.NativeOperationReceipts.AsNoTracking().OrderBy(value => value.OperationId).ToArrayAsync(),
            audits = await db.AuditEvents.AsNoTracking().Where(value => value.EventType == "publication recovery queued").OrderBy(value => value.Id).ToArrayAsync() });
    }

    private static NativeCorpusMutation Mutation(Guid jobId) => new("publication_retry", JsonSerializer.SerializeToElement(new { jobId }));

    private static NativeCorpusCommandService Service(SqlToUsearchRebuildTests.PipelineEnvironment environment, WakeSignal? wake = null,
        Action? beforeCommit = null, Action? afterSave = null, Action? afterCommit = null) =>
        new(new SqlNativeOperationStore(environment.Factory, TimeProvider.System, afterCommit, beforeCommit, afterSave),
            new SqlNativeCorpusActionStore(environment.Factory, new UnusedPathPolicy(), new LocalPrivateContentDisclosure()),
            wake, deploymentValidationHold: environment.DeploymentHold);

    private static async Task<StageWorkItem> FailPublishAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, Guid recordId)
    {
        // The fixture has an initial unretained Publish delivery; leave it completed
        // so only the selected retained delivery is available for this worker.
        await using (var db = await environment.Factory.CreateDbContextAsync())
            await db.OutboxMessages.Where(value => value.PipelineRecordId != recordId && value.Stage == (int)PipelineStage.Publish)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.DispatchedAtUtc, DateTimeOffset.UtcNow));
        var work = await ClaimAsync(environment, recordId);
        await new SqlStageTransitionStore(environment.Factory).FailAsync(new(work.DispatchMessage, work.Job,
            "synthetic-publish-timeout", "synthetic-error-details", "test"), CancellationToken.None);
        return work;
    }

    private static async Task<StageWorkItem> ClaimAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, Guid recordId)
    {
        var dispatch = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("recovery-dispatcher", DateTimeOffset.UtcNow.AddSeconds(1),
            TimeSpan.FromMinutes(2), [PipelineOperations.Publish], CancellationToken.None);
        Assert.NotNull(dispatch);
        Assert.Equal(recordId, dispatch.PipelineRecordId.Value);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "recovery-worker", DateTimeOffset.UtcNow.AddSeconds(1),
            TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(job);
        return new(dispatch, job);
    }

    private static async Task AssertOldLeaseRefusedAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, StageWorkItem old)
    {
        var store = new SqlStageTransitionStore(environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FailAsync(new(old.DispatchMessage, old.Job,
            "obsolete-failure", "obsolete-details", "test"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TransitionAsync(new(old.DispatchMessage, old.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.Publish, new string('a', 64), "text/plain", "obsolete", DateTimeOffset.UtcNow),
            null, null, "test"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlOutboxStore(environment.Factory)
            .ReleaseAsync(old.DispatchMessage, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None).AsTask());
    }

    private sealed class UnusedPathPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new NotSupportedException();
    }
    private sealed class WakeSignal : IOutboxWakeSignal
    {
        public int Count { get; private set; }
        public void Notify() => Count++;
        public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class StatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
