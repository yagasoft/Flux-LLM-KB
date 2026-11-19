using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Media;
using FluxKnowledge.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData("private-spool")]
    [InlineData("legacy-published")]
    [InlineData("verified-native-file")]
    public async Task Media_transcription_recovers_new_and_published_results_without_copying_full_text_to_a_second_index(string storage)
    {
        var (factory, original, revision, _) = await CreateClaimedPdfAsync();
        using var files = new CloudSourceFixture(@"I:\Codex Workspaces\Staging\FluxKnowledge");
        var recording = files.File("recording.mp4", "synthetic recording");
        var runtimeFolder = files.Directory("Runtime");
        var layout = LiveRootLayout.CreateForIsolatedTests(runtimeFolder);
        var output = new MediaTranscriptionLocalExecutor(layout, null!);
        var rootFingerprint = PhysicalFileIdentity.GetDirectory(files.Root).IdentityFingerprint;
        var rootConfig = SourceRootConfiguration.Create(files.Root, "Sidecar test", true, false, SourcePolicyDefaults.MaximumTextBytes);
        SourceFileObservation observation;
        using (var source = new WindowsSourceObservationReader().Open(rootConfig, "recording.mp4"))
            observation = source.Observation;
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(recording)));
        var claim = original with { Operation = MediaTranscriptionGpuContracts.Operation, LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        var runtime = new MediaTranscriptionGpuRuntime("synthetic-media", "synthetic-settings", "synthetic-media-executor", 10);
        var scheduler = new SqlGpuSchedulerStore(factory, sourceAdmission: new FixtureSourceAdmission(factory));
        var store = new SqlMediaTranscriptionGpuStore(factory, CreateCoordinator(factory), runtime, TimeProvider.System,
            new FixtureSourceAdmission(factory), nativeOutput: output);
        await using var arrange = await factory.CreateDbContextAsync();
        var root = await arrange.SourceRootConfigurations.SingleAsync();
        root.CanonicalPath = files.Root;
        root.HealthEvidenceJson = JsonSerializer.Serialize(new { physicalIdentity = new { identityFingerprint = rootFingerprint } });
        root.AllowedClassificationsJson = """["text/plain"]""";
        var media = await arrange.SourceRevisions.AsNoTracking().SingleAsync();
        var capture = JsonSerializer.Serialize(new
        {
            kind = "verified-physical-capture",
            sourceRevisionId = media.Id,
            configurationRevision = root.ConfigurationRevision,
            rootIdentityFingerprint = rootFingerprint,
            stableSourceIdentity = observation.Identity,
            contentSha256 = hash,
            byteLength = observation.ByteLength,
            lastWriteAtUtc = DateTimeOffset.FromFileTime(observation.LastWriteTicks).ToUniversalTime(),
            observedAt = DateTimeOffset.UtcNow
        });
        await arrange.SourceRevisions.ExecuteUpdateAsync(update => update.SetProperty(row => row.CanonicalPath, recording)
            .SetProperty(row => row.Extension, ".mp4").SetProperty(row => row.ByteLength, observation.ByteLength)
            .SetProperty(row => row.ContentSha256, hash).SetProperty(row => row.CurrentDiscoveryEvidenceJson, capture));
        var pipelineRecord = await arrange.PipelineRecords.AsNoTracking().SingleAsync();
        await arrange.PipelineRecords.ExecuteUpdateAsync(update => update.SetProperty(row => row.ContentHash, hash));
        arrange.SourceActivities.Add(new()
        {
            Id = Guid.NewGuid(),
            SourceRevisionId = media.Id,
            ActivityKind = (int)SourceActivityKind.MediaTranscription,
            ProcessorVersion = MediaTranscriptionDiscoveryOptions.ProcessorVersion,
            DescriptorFingerprint = MediaTranscriptionDiscoveryOptions.DescriptorFingerprint,
            InputFingerprint = hash,
            ResultingPipelineRecordId = pipelineRecord.Id,
            ResultingPipelineRecordRevision = pipelineRecord.Revision,
            State = (int)SourceActivityState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await arrange.SaveChangesAsync();
        await arrange.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Operation, claim.Operation).SetProperty(j => j.LeaseExpiresAtUtc, claim.LeaseExpiresAtUtc));
        await arrange.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.Operation, claim.Operation));
        arrange.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
        {
            SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey,
            State = (int)GpuCapacitySlotState.Available,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await arrange.SaveChangesAsync();
        var rootId = await arrange.SourceRootConfigurations.Select(root => root.Id).SingleAsync();

        var optedOut = new SqlMediaTranscriptionGpuStore(factory, CreateCoordinator(factory), runtime, TimeProvider.System,
            new FixtureSourceAdmission(factory), MediaTranscriptionDiscoveryOptions.Disabled);
        Assert.False((await optedOut.QueueAsync(claim, revision, hash, default)).Scheduled);

        var queued = await store.QueueAsync(claim, revision, hash, default);
        Assert.True(queued.Scheduled, queued.ReasonCode);
        Assert.Equal(queued, await store.QueueAsync(claim, revision, hash, default));
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            runtime.WorkloadProfile]);
        var gate = new SharedGpuAdmissionGate(policy, "unused-retrieval", 10, runtime);
        var options = new GpuSchedulerOptions(1, 10, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), policy);
        var result = await scheduler.RunAdmissionRoundAsync(GpuSchedulerWakeReason.WorkReady, options, gate.DecideAsync, default);
        Assert.Equal(GpuAdmissionDisposition.Admit, result.Disposition);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));

        var owner = SyntheticSupervisor();
        var execution = Guid.NewGuid();
        var owned = await store.TryClaimExecutionAsync(handle, execution, owner, default);
        var lease = Assert.IsType<DocumentOcrExecutionLease>(owned.Lifecycle.Lease);
        Assert.Equal(revision, owned.SourceRevisionId);
        Assert.False((await store.TryClaimExecutionAsync(handle, Guid.NewGuid(), owner, default)).Lifecycle.Accepted);
        var process = new DocumentOcrProcessIdentity(owner.Process.ProcessId + 1, DateTimeOffset.UtcNow, owner.Process.MachineFingerprint);
        Assert.Equal(DocumentOcrLifecycleDisposition.SourceWithdrawn,
            (await optedOut.PermitNativeStartAsync(lease, process, default)).Disposition);
        Assert.True((await store.PermitNativeStartAsync(lease, process, default)).Accepted);
        Assert.True(await SqlSourceDeletionOwnership.HasUnconfirmedMediaWorkAsync(arrange, rootId, default));
        Assert.Contains(claim.JobId.Value, await SqlRemovedSourceWork.UnconfirmedJobIds(arrange).ToArrayAsync());
        var recovering = Assert.Single(await store.ReadRecoveryAsync(default));
        Assert.Equal(lease, recovering.Lease);
        Assert.Equal(process, recovering.Process);
        Assert.False(recovering.NativeCleanupConfirmed);
        // This fits the private payload limit with Arabic written as Unicode, but not as escaped ASCII.
        var nativeJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            succeeded = true, result = new
            {
                profile = "egyptian-ar-en", selectionReason = "arabic-speech", language = "ar", languageConfidence = 0.98,
                durationMillis = 1200, languageEvidence = new[] { new { language = "ar", confidence = 0.98 } },
                cues = new[] { new { startMillis = 0, endMillis = 1200, text = new string('م', 352_000) } }
            }
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var transcription = MediaTranscriptionLocalExecutor.ParseResult(nativeJson);
        Assert.True(transcription.Succeeded);
        Assert.False((await store.StoreResultAsync(lease, transcription, default)).Accepted);
        Assert.False((await store.ConfirmCleanupAsync(lease,
            new(execution, DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), default)).Accepted);

        await arrange.SourceRootConfigurations.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, (int)SourceRootState.Deleting));
        Assert.Equal(DocumentOcrLifecycleDisposition.SourceWithdrawn, (await store.HeartbeatAsync(lease, default)).Disposition);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await arrange.GpuCapacitySlots.Select(s => s.State).SingleAsync());

        await arrange.SourceRootConfigurations.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, (int)SourceRootState.Paused));
        Assert.True((await store.HeartbeatAsync(lease, default)).Accepted);
        Assert.Equal(lease, Assert.Single(await optedOut.ReadRecoveryAsync(default)).Lease);
        Assert.True((await optedOut.ConfirmCleanupAsync(lease,
            new(execution, DocumentOcrCleanupKind.OwnedJobEmpty, process, DateTimeOffset.UtcNow), default)).Accepted);

        var secondHostObservation = new ConfirmedCleanupOwnerStillWriting();
        await using (var services = new ServiceCollection().AddSingleton(optedOut).BuildServiceProvider())
        {
            var secondHost = new MediaTranscriptionGpuExecutor(services.GetRequiredService<IServiceScopeFactory>(), runtime,
                new SyntheticTranscriptionOwnerProbe(owner.Process), secondHostObservation, new RecordingOutboxWakeSignal(),
                new ChannelGpuSchedulerWakeSignal(), TimeProvider.System, new TestHostApplicationLifetime());
            await secondHost.RecoverAsync(default);
        }
        Assert.Equal(lease, secondHostObservation.Lease);
        Assert.True(secondHostObservation.CleanupConfirmed);
        Assert.Null(await store.ReadPendingCompletionAsync(handle, default));
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await arrange.GpuCapacitySlots.Select(s => s.State).SingleAsync());

        var useNativeResult = storage == "verified-native-file";
        var nativeDirectory = Path.Combine(layout.TempRoot, "media-transcription", $"{execution:N}");
        if (useNativeResult)
        {
            Directory.CreateDirectory(nativeDirectory);
            await File.WriteAllBytesAsync(Path.Combine(nativeDirectory, "output.json"), nativeJson);
        }
        else await output.PersistTranscriptAsync(execution, transcription.Transcript!, default);
        Assert.True((await store.StoreResultAsync(lease, transcription, default, useNativeResult)).Accepted);
        var pending = Assert.IsType<MediaTranscriptionGpuCompletion>(await store.ReadPendingCompletionAsync(handle, default));
        Assert.True(pending.NativeCleanupConfirmed);
        Assert.True(await SqlSourceDeletionOwnership.HasUnconfirmedMediaWorkAsync(arrange, rootId, default));
        Assert.True((await optedOut.SettleAsync(handle, lease.MiniTaskId, pending.ResultDigest, default)).Accepted);
        Assert.False(await store.RequeueSettledAsync(handle, lease.MiniTaskId, default));
        Assert.Equal((int)GpuCapacitySlotState.Available, await arrange.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        Assert.False(await SqlSourceDeletionOwnership.HasUnconfirmedMediaWorkAsync(arrange, rootId, default));
        Assert.DoesNotContain(claim.JobId.Value, await SqlRemovedSourceWork.UnconfirmedJobIds(arrange).ToArrayAsync());
        Assert.Empty(await store.ReadRecoveryAsync(default));
        await using (var transaction = await arrange.Database.BeginTransactionAsync())
        {
            var ownership = await SqlSourceDeletionOwnership.ReadAsync(arrange, rootId, runtime: null, default);
            Assert.True(ownership.IsOwned);
            Assert.Equal(lease.MiniTaskId, Assert.Single(ownership.TaskIds));
        }

        // A terminal SQL state alone cannot substitute for the exact native-cleanup receipt.
        Assert.Equal(1, await arrange.GpuExecutorEvidence.ExecuteUpdateAsync(s => s.SetProperty(e => e.VerifierKey, "foreign-verifier")));
        Assert.True(await SqlSourceDeletionOwnership.HasUnconfirmedMediaWorkAsync(arrange, rootId, default));
        Assert.Contains(claim.JobId.Value, await SqlRemovedSourceWork.UnconfirmedJobIds(arrange).ToArrayAsync());
        Assert.Equal(1, await arrange.GpuExecutorEvidence.ExecuteUpdateAsync(s => s.SetProperty(e => e.VerifierKey, "media-transcription-owned-job-v1")));

        await arrange.SourceRootConfigurations.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, (int)SourceRootState.Enabled));
        Assert.False(await optedOut.RequeueSettledAsync(handle, lease.MiniTaskId, default));
        Assert.True(await store.RequeueSettledAsync(handle, lease.MiniTaskId, default));
        Assert.False(await store.RequeueSettledAsync(handle, lease.MiniTaskId, default));
        Assert.Null(await optedOut.ReadCompletedAsync(claim, revision, hash, default));
        var completed = Assert.IsType<MediaTranscriptionCompletedResult>(await store.ReadCompletedAsync(claim, revision, hash, default));
        Assert.True(completed.Result.Succeeded);
        Assert.Null(completed.Result.Transcript);
        Assert.Null(completed.Result.MetadataJson);
        Assert.Equal(execution, completed.ExecutionId);
        Assert.Equal(useNativeResult, completed.UseNativeResult);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(transcription.Transcript!), completed.TranscriptByteLength);
        var savedJson = await arrange.MediaTranscriptionGpuRequests.Select(row => row.ResultJson).SingleAsync();
        Assert.DoesNotContain(transcription.Transcript![..100], savedJson!);

        if (storage == "legacy-published")
        {
            await ExerciseLegacyConversionAsync();
            return;
        }

        var admission = new FixtureSourceAdmission(factory);
        var now = DateTimeOffset.UtcNow;
        var delivery = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(factory, sourceAdmission: admission)
            .ClaimNextDueAsync("resumed-asr", now, TimeSpan.FromMinutes(1), [claim.Operation], default));
        var parent = Assert.IsType<FluxKnowledge.Application.Ports.ClaimedJob>(await new SqlJobClaimStore(factory, sourceAdmission: admission)
            .ClaimForDispatchAsync(delivery, "resumed-asr", now, TimeSpan.FromMinutes(1), default));
        Assert.Equal(claim.JobId, parent.JobId);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(factory, sourceAdmission: admission),
            new NullStatusPublisher(), new RecordingOutboxWakeSignal(), TimeProvider.System);

        var extraction = new TranscribeMediaStageWorker(new SqlPipelineStore(factory), store, transitions, TimeProvider.System,
            () => throw new Xunit.Sdk.XunitException("A proven saved transcript must not start the native runtime again."), store);
        await extraction.ExecuteAsync(new(delivery, parent), default);

        var artifact = await arrange.Artifacts.AsNoTracking().SingleAsync(row => row.PipelineRecordId == parent.PipelineRecordId.Value &&
            row.SourceRevision == parent.SourceRevision && row.Stage == (int)PipelineStage.Extract);
        Assert.Equal("", artifact.SearchText);
        Assert.Equal(MediaTranscriptionGpuContracts.SidecarReceiptContentType, artifact.ContentType);
        Assert.NotNull(artifact.DocumentMetadataJson);
        Assert.Equal((int)SourceActivityState.Completed, await arrange.SourceActivities.AsNoTracking().Select(row => row.State).SingleAsync());
        Assert.False(await arrange.Jobs.AnyAsync(row => row.PipelineRecordId == parent.PipelineRecordId.Value &&
            row.SourceRevision == parent.SourceRevision && row.Operation == PipelineOperations.NormaliseText));

        var sidecar = Path.Combine(files.Root, "recording.mp4.transcript.txt");
        Assert.Equal(transcription.Transcript, await File.ReadAllTextAsync(sidecar));
        Assert.False(File.Exists(Path.Combine(layout.SpoolRoot, "PendingTranscripts", $"{execution:N}.txt")));
        Assert.False(Directory.Exists(nativeDirectory));
        var discovered = new List<SourceDiscoveredFile>();
        await foreach (var item in new LocalSourceEnumerator().EnumerateAsync(rootConfig, default))
            discovered.Add(item);
        Assert.Equal(transcription.Transcript, Assert.Single(discovered, item => item.CanonicalPath == sidecar).Classification.Text);

        async Task ExerciseLegacyConversionAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var text = transcription.Transcript!;
            var json = JsonSerializer.Serialize(transcription, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var legacyDigest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            await arrange.MediaTranscriptionGpuRequests.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ResultDigest, legacyDigest).SetProperty(row => row.ResultJson, json));
            await arrange.GpuExecutorResultReceipts.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.OpaqueResultDigest, legacyDigest));
            await arrange.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PublicState, (int)PublicJobState.Completed));
            await arrange.PipelineRecords.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CurrentStage, (int)PipelineStage.Publish)
                .SetProperty(row => row.CompletionCriteriaMet, true));
            var oldExtract = new ArtifactEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = pipelineRecord.Id, SourceRevision = pipelineRecord.Revision,
                Stage = (int)PipelineStage.Extract, ContentHash = hash, ContentType = "text/plain",
                SearchText = text, DocumentMetadataJson = transcription.MetadataJson, CreatedAtUtc = now
            };
            arrange.Artifacts.Add(oldExtract);
            await arrange.SaveChangesAsync();

            var scopedOptions = new MediaTranscriptionDiscoveryOptions([root.Id], now, [revision.Value]);
            var migration = new SqlMediaTranscriptionGpuStore(factory, CreateCoordinator(factory), runtime, TimeProvider.System,
                new FixtureSourceAdmission(factory), scopedOptions);
            Assert.False(await migration.ConvertPublishedSidecarAsync(Guid.NewGuid(), default));
            Assert.False(await migration.ConvertPublishedSidecarAsync(revision.Value, default));
            var expectedPath = recording + MediaTranscriptionGpuContracts.SidecarSuffix;
            Assert.Equal(text, await File.ReadAllTextAsync(expectedPath));
            Assert.Equal(json, await arrange.MediaTranscriptionGpuRequests.AsNoTracking().Select(row => row.ResultJson).SingleAsync());

            var scans = new List<SourceDiscoveredFile>();
            await foreach (var item in new LocalSourceEnumerator().EnumerateAsync(rootConfig, default))
                scans.Add(item);
            var observed = Assert.Single(scans, item => item.CanonicalPath == expectedPath);
            Assert.Equal(text, observed.Classification.Text);

            var sidecarRevision = Guid.NewGuid();
            var sidecarIdentity = Guid.NewGuid();
            var physical = Assert.IsType<SourceFileObservation>(observed.Observation);
            var sidecarRecord = Guid.NewGuid();
            arrange.SourceRevisions.Add(new()
            {
                Id = sidecarRevision, SourceRootId = root.Id, StableSourceIdentity = physical.Identity,
                Revision = 1, CanonicalPath = expectedPath, Classification = "AcceptedUtf8Text", Extension = ".txt",
                ByteLength = observed.ByteLength, ContentSha256 = observed.ContentSha256, DiscoveredAtUtc = now,
                CurrentDiscoveryEvidenceJson = JsonSerializer.Serialize(new
                {
                    kind = "verified-physical-capture", sourceRevisionId = sidecarRevision, configurationRevision = root.ConfigurationRevision,
                    rootIdentityFingerprint = rootFingerprint, stableSourceIdentity = physical.Identity, contentSha256 = observed.ContentSha256,
                    byteLength = observed.ByteLength, lastWriteAtUtc = observed.LastWriteAtUtc, observedAt = now
                })
            });
            arrange.SourceIdentities.Add(new() { Id = sidecarIdentity, StableKey = expectedPath, SourceKind = "retained local source", CreatedAtUtc = now });
            arrange.PipelineRecords.Add(new()
            {
                Id = sidecarRecord, RootLineageRecordId = sidecarRecord, SourceIdentityId = sidecarIdentity, SourceRevisionId = sidecarRevision,
                Revision = 1, ContentHash = observed.ContentSha256, CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true, RegisteredAtUtc = now
            });
            arrange.Jobs.Add(new()
            {
                Id = Guid.NewGuid(), PipelineRecordId = sidecarRecord, SourceRevision = 1, Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractUtf8, PublicState = (int)PublicJobState.Completed, DueAtUtc = now
            });
            var canonical = Guid.NewGuid();
            foreach (var (id, stage, body) in new[] { (Guid.NewGuid(), PipelineStage.Extract, text),
                (canonical, PipelineStage.CanonicalIndex, text), (Guid.NewGuid(), PipelineStage.Publish, "") })
            {
                arrange.Artifacts.Add(new()
                {
                    Id = id, PipelineRecordId = sidecarRecord, SourceRevision = 1, Stage = (int)stage,
                    ContentHash = observed.ContentSha256, ContentType = "text/plain", SearchText = body, CreatedAtUtc = now
                });
            }
            var chunks = TextChunker.Chunk(text).Select(chunk => new TextChunkEntity
            {
                ArtifactId = canonical, SourceRevision = 1, Ordinal = chunk.Ordinal, StartOffset = chunk.StartOffset,
                Length = chunk.Length, Content = chunk.Content, ContentHash = chunk.ContentHash
            }).ToArray();
            arrange.TextChunks.AddRange(chunks[..^1]);
            await arrange.SaveChangesAsync();

            Assert.False(await migration.ConvertPublishedSidecarAsync(revision.Value, default));
            Assert.False(await arrange.PipelineRecords.AsNoTracking().Where(row => row.Id == pipelineRecord.Id).Select(row => row.IsDeleted).SingleAsync());
            arrange.TextChunks.Add(chunks[^1]);
            await arrange.SaveChangesAsync();
            Assert.True(await migration.ConvertPublishedSidecarAsync(revision.Value, default));
            Assert.False(await migration.ConvertPublishedSidecarAsync(revision.Value, default));
            Assert.True(await arrange.PipelineRecords.AsNoTracking().Where(row => row.Id == pipelineRecord.Id).Select(row => row.IsDeleted).SingleAsync());
            Assert.Null(await arrange.SourceRevisions.AsNoTracking().Where(row => row.Id == revision.Value).Select(row => row.SuppressedAtUtc).SingleAsync());
            Assert.Empty(await arrange.Artifacts.AsNoTracking().Where(row => row.Id == oldExtract.Id).Select(row => row.SearchText).SingleAsync());
            var retired = await arrange.MediaTranscriptionGpuRequests.AsNoTracking().SingleAsync();
            Assert.Equal(legacyDigest, retired.ResultDigest);
            Assert.DoesNotContain(text[..100], retired.ResultJson!);
            using var result = JsonDocument.Parse(retired.ResultJson!);
            Assert.Equal(transcription.MetadataJson, result.RootElement.GetProperty("originalMetadataJson").GetString());
            Assert.False(await SqlSourceDeletionOwnership.HasUnconfirmedMediaWorkAsync(arrange, root.Id, default));
        }
    }

    private sealed class ConfirmedCleanupOwnerStillWriting : IDocumentOcrProcessRecovery
    {
        public DocumentOcrExecutionLease? Lease { get; private set; }
        public bool CleanupConfirmed { get; private set; }

        public ValueTask<DocumentOcrRecoveryObservation> InspectAsync(DocumentOcrExecutionLease lease,
            DocumentOcrProcessIdentity? process, bool hasStoredResult, bool cleanupConfirmed, CancellationToken ct)
        {
            Lease = lease;
            CleanupConfirmed = cleanupConfirmed;
            Assert.False(hasStoredResult);
            return ValueTask.FromResult(new DocumentOcrRecoveryObservation(null, "synthetic-owner-writing", OwnerRunning: true));
        }
    }

    private sealed class SyntheticTranscriptionOwnerProbe(GpuInteractiveOwnerIdentity owner) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => owner;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity identity) => GpuInteractiveOwnerObservation.Alive;
    }
}
