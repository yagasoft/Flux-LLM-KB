using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class SourceRootProjectionReaderIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Published_current_json_metadata_counts_for_the_recording_without_a_document_selector()
    {
        var now = DateTimeOffset.Parse("2026-11-19T10:18:00+00:00");
        var rootId = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}", DisplayName = "Recordings",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 256L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var recognised = AddRecording("meeting.mp4", published: true);
            var retainedRecording = setup.SourceRevisions.Local.Single(value => value.Id == recognised);
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = Guid.NewGuid(), SourceRevisionId = recognised, ActivityKind = (int)SourceActivityKind.MediaTranscription,
                ExecutionClass = (int)ExecutionClass.DeferredCapability, State = (int)SourceActivityState.DeferredPolicy,
                ProcessorVersion = MediaTranscriptionDiscoveryOptions.ProcessorVersion,
                DescriptorFingerprint = MediaTranscriptionDiscoveryOptions.DescriptorFingerprint,
                InputFingerprint = retainedRecording.ContentSha256, Reason = "media-adjacent-transcript-unverified",
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            AddRecording("still-publishing.mp4", published: false);
            AddRecording("old-output.mp4", published: true, staleBinding: true);
            var replaced = AddRecording("newer-branch.mp4", published: true, legacy: true);
            var successor = AddChild(setup, replaced, 3, MediaMetadataRetainedProcessor.Capability, now.AddMinutes(1), "replacement");
            setup.Jobs.Add(PublishJob(successor.RecordId, PublicJobState.WorkerQueued));
            var branchWithoutPublish = AddRecording("missing-publication.mp4", published: true, legacy: true);
            AddChild(setup, branchWithoutPublish, 3, MediaMetadataRetainedProcessor.Capability, now.AddMinutes(1), "no-publish");
            await setup.SaveChangesAsync();

            Guid AddRecording(string path, bool published, bool staleBinding = false, bool legacy = false)
            {
                var owner = Revision(Guid.NewGuid(), rootId, path, now, false);
                owner.Classification = "DeferredCapability";
                setup.SourceRevisions.Add(owner);
                var capability = legacy ? MediaMetadataRetainedProcessor.Capability with
                {
                    ProcessorVersion = MediaMetadataRetainedProcessor.LegacyProcessorVersion,
                    ProcessorFingerprint = MediaMetadataRetainedProcessor.LegacyProcessorFingerprint
                } : MediaMetadataRetainedProcessor.Capability;
                var child = AddChild(setup, owner.Id, 3, capability, now, "metadata");
                var branch = setup.SourceProcessorBranches.Local.Single(value => value.Id == child.BranchId);
                setup.SourceActivities.Local.Single(value => value.Id == branch.SourceActivityId).State = (int)SourceActivityState.Pending;
                var retained = setup.SourceRevisions.Local.Single(value => value.Id == child.ChildId);
                setup.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = retained.Id, ContentSha256 = retained.ContentSha256,
                    StoreRelativePath = $"{retained.Id:N}/metadata", ByteLength = retained.ByteLength, ChecksumVerifiedAtUtc = now
                });
                var record = setup.PipelineRecords.Local.Single(value => value.Id == child.RecordId);
                record.CompletionCriteriaMet = published;
                setup.Jobs.Add(PublishJob(record.Id, published ? PublicJobState.Completed : PublicJobState.WorkerQueued));
                if (published)
                {
                    setup.Artifacts.Add(new ArtifactEntity
                    {
                        Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
                        ContentHash = new string('e', 64), ContentType = "application/json", CreatedAtUtc = now.AddSeconds(10)
                    });
                }
                if (staleBinding) branch.InputSha256 = new string('f', 64);
                return owner.Id;
            }

            JobEntity PublishJob(Guid recordId, PublicJobState state) => new()
            {
                Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
                Operation = PipelineOperations.Publish, PublicState = (int)state, DueAtUtc = now
            };
        }

        await using var context = factory.CreateDbContext();
        Assert.False(await context.DocumentPublications.AnyAsync(row => context.SourceRevisions.Any(owner => owner.Id == row.OwnerSourceRevisionId && owner.SourceRootId == rootId)));
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [rootId], default))[rootId];
        Assert.Equal(1, summary.MetadataOnly);
        Assert.Equal(4, summary.Pending);
        Assert.Equal(0, summary.Indexed);
        Assert.Equal(now.AddSeconds(10), summary.LastProgressAtUtc);
        var drawer = Assert.Single(PipelineFileSample.Project(summary.Files, [], [], [], new Dictionary<Guid, string>()),
            item => item.File.Category == "MetadataOnly");
        Assert.Equal("Metadata indexed", drawer.File.State);
        Assert.Contains("For audio and video, transcription is separate.", drawer.File.Reason);
        Assert.False(drawer.File.CanIgnore);
        var transcription = await MediaTranscriptionFileReader.ReadAsync(context,
            summary.Files.Select(file => file.SourceRevisionId).ToArray(), default);
        Assert.Equal("Transcript needs review", Assert.Single(transcription).Value.Status);
        Assert.Equal("Transcript needs review", transcription[drawer.File.SourceRevisionId].Status);
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(rootId, default));
        var recording = Assert.Single(detail.Files, file => file.FileName == "meeting.mp4");
        Assert.Equal("Metadata indexed", recording.Status);
        Assert.Equal("media-metadata-only", recording.Reason);
        Assert.All(detail.Files.Where(file => file.FileName != "meeting.mp4"), file => Assert.Equal("Pending", file.Status));

        var mediaId = drawer.File.SourceRevisionId;
        var activity = await context.SourceActivities.SingleAsync(row => row.SourceRevisionId == mediaId &&
            row.ActivityKind == (int)SourceActivityKind.MediaTranscription);
        var recordId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identityId, SourceKind = "retained local source", StableKey = $"recording:{mediaId:N}", CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId, SourceIdentityId = identityId, SourceRevisionId = mediaId, Revision = 1,
            ContentHash = activity.InputFingerprint, RootLineageRecordId = recordId, CompletionCriteriaMet = true,
            CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = now
        });
        context.Jobs.AddRange(
            CompletedJob(recordId, PipelineStage.Extract, MediaTranscriptionGpuContracts.Operation),
            CompletedJob(recordId, PipelineStage.Publish, PipelineOperations.Publish));
        activity.ResultingPipelineRecordId = recordId;
        activity.ResultingPipelineRecordRevision = 1;
        activity.State = (int)SourceActivityState.Completed;
        await context.SaveChangesAsync();

        Assert.Equal("Transcript indexing", (await MediaTranscriptionFileReader.ReadAsync(context, [mediaId], default))[mediaId].Status);
        Assert.Equal(1, (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [rootId], default))[rootId].MetadataOnly);

        context.Artifacts.Add(new ArtifactEntity
        {
            Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
            ContentHash = new string('c', 64), ContentType = "text/plain", CreatedAtUtc = now.AddMinutes(2)
        });
        await context.SaveChangesAsync();
        var indexedTranscript = (await MediaTranscriptionFileReader.ReadAsync(context, [mediaId], default))[mediaId];
        Assert.Equal("Transcript indexed", indexedTranscript.Status);
        Assert.Equal(recordId, indexedTranscript.PublishedRecordId);
        var completedSummary = (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [rootId], default))[rootId];
        Assert.Equal(1, completedSummary.Indexed);
        Assert.Equal(0, completedSummary.MetadataOnly);
        Assert.True(Assert.Single(completedSummary.Files, file => file.SourceRevisionId == mediaId).MetadataIndexed);

        var sidecarPath = (await context.SourceRevisions.SingleAsync(row => row.Id == mediaId)).CanonicalPath +
            MediaTranscriptionGpuContracts.SidecarSuffix;
        var originalSidecar = Revision(Guid.NewGuid(), rootId, "meeting.mp4.transcript.txt", now, false);
        originalSidecar.CanonicalPath = sidecarPath;
        originalSidecar.Extension = ".txt";
        context.SourceRevisions.Add(originalSidecar);
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Extract,
            ContentHash = activity.InputFingerprint, ContentType = MediaTranscriptionGpuContracts.SidecarReceiptContentType,
            DocumentMetadataJson = System.Text.Json.JsonSerializer.Serialize(
                new FluxKnowledge.Integrations.Files.TranscriptSidecarReceipt("meeting.mp4.transcript.txt", originalSidecar.ContentSha256, (int)originalSidecar.ByteLength),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedAtUtc = now.AddMinutes(2)
        });
        var originalPublication = PublishSidecar(originalSidecar);
        await context.SaveChangesAsync();
        var original = (await MediaTranscriptionFileReader.ReadAsync(context, [mediaId], default))[mediaId];
        Assert.Equal("Transcript indexed", original.Status);
        Assert.Equal(originalPublication, original.PublishedRecordId);

        var editedSidecar = Revision(Guid.NewGuid(), rootId, "meeting.mp4.transcript.txt", now.AddMinutes(3), false);
        editedSidecar.CanonicalPath = sidecarPath;
        editedSidecar.Extension = ".txt";
        editedSidecar.ContentSha256 = new string('7', 64);
        editedSidecar.ParentSourceRevisionId = originalSidecar.Id;
        editedSidecar.Revision = originalSidecar.Revision + 1;
        editedSidecar.StableSourceIdentity = originalSidecar.StableSourceIdentity;
        originalSidecar.SuppressedAtUtc = now.AddMinutes(3);
        context.SourceRevisions.Add(editedSidecar);
        await context.SaveChangesAsync();
        var pending = (await MediaTranscriptionFileReader.ReadAsync(context, [mediaId], default))[mediaId];
        Assert.Null(pending.PublishedRecordId);

        var editedPublication = PublishSidecar(editedSidecar);
        await context.SaveChangesAsync();
        var updated = (await MediaTranscriptionFileReader.ReadAsync(context, [mediaId], default))[mediaId];
        Assert.Equal("Updated transcript indexed", updated.Status);
        Assert.Equal(editedPublication, updated.PublishedRecordId);

        Guid PublishSidecar(SourceRevisionEntity owner)
        {
            var identity = Guid.NewGuid();
            var id = Guid.NewGuid();
            context.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identity, SourceKind = "retained local source", StableKey = $"sidecar-test:{owner.Id:N}", CreatedAtUtc = now
            });
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = id, SourceIdentityId = identity, SourceRevisionId = owner.Id, Revision = 1,
                ContentHash = owner.ContentSha256, RootLineageRecordId = id, CompletionCriteriaMet = true,
                CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = now
            });
            context.Artifacts.Add(new ArtifactEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = id, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
                ContentHash = owner.ContentSha256, ContentType = "text/plain", CreatedAtUtc = now
            });
            context.Jobs.Add(CompletedJob(id, PipelineStage.Extract, PipelineOperations.ExtractUtf8));
            return id;
        }

        JobEntity CompletedJob(Guid owner, PipelineStage stage, string operation) => new()
        {
            Id = Guid.NewGuid(), PipelineRecordId = owner, SourceRevision = 1, Stage = (int)stage,
            Operation = operation, PublicState = (int)PublicJobState.Completed, DueAtUtc = now
        };
    }
}
