using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("normal")]
    [InlineData("mp3-signature")]
    [InlineData("wav-signature")]
    [InlineData("outside-window")]
    [InlineData("catch-up")]
    [InlineData("catch-up-over-text-limit")]
    [InlineData("discovery-over-text-limit")]
    [InlineData("revised-original")]
    [InlineData("unapproved")]
    [InlineData("stale")]
    [InlineData("sidecar")]
    [InlineData("plain-sidecar")]
    [InlineData("unready")]
    [InlineData("existing")]
    [InlineData("foreign-pipeline")]
    [InlineData("wrong-signature")]
    public async Task Native_media_discovery_requires_current_approval_and_queues_an_exact_durable_job(string scenarioName)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var extension = scenarioName switch
        {
            "mp3-signature" => ".mp3",
            "wav-signature" => ".wav",
            _ => ".mp4"
        };
        var path = Path.Combine(scenario.Files, "example" + extension);
        byte[] media = scenarioName switch
        {
            "wrong-signature" => "%PDF-1.7 fake media cutoff"u8.ToArray(),
            "wav-signature" => "RIFF\x04\0\0\0WAVE"u8.ToArray(),
            "mp3-signature" => [0xff, 0xfb, 0x90, 0x00, .. new byte[413]],
            _ => "\0\0\0\u0014ftypisom\0\0\0\0isom"u8.ToArray()
        };
        await File.WriteAllBytesAsync(path, media);
        await scenario.ScanFilesystemAsync();
        await using var db = scenario.Factory.CreateDbContext();
        var original = await db.SourceRevisions.SingleAsync(value => value.CanonicalPath == path);
        if (scenarioName == "revised-original")
        {
            await File.AppendAllTextAsync(path, "Changed payload");
            await scenario.ScanFilesystemAsync();
        }
        var source = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.CanonicalPath == path &&
            value.SuppressedAtUtc == null);
        if (scenarioName == "revised-original") Assert.Equal(original.Id, source.ParentSourceRevisionId);
        if (scenarioName is "catch-up-over-text-limit" or "discovery-over-text-limit")
        {
            var root = await db.SourceRootConfigurations.SingleAsync(value => value.Id == source.SourceRootId);
            root.MaximumFileBytes = source.ByteLength - 1;
            await db.SaveChangesAsync();
        }
        var threshold = scenarioName is "catch-up" or "catch-up-over-text-limit" or "outside-window"
            ? source.DiscoveredAtUtc.AddDays(1) : source.DiscoveredAtUtc.AddSeconds(-1);
        var options = new MediaTranscriptionDiscoveryOptions(
            [scenarioName == "unapproved" ? Guid.NewGuid() : source.SourceRootId], threshold,
            scenarioName is "catch-up" or "catch-up-over-text-limit" or "stale" ? [source.Id] : scenarioName == "revised-original" ? [original.Id] : []);
        if (scenarioName == "stale") await File.AppendAllTextAsync(path, "Changed after capture");
        if (scenarioName == "sidecar") await File.WriteAllTextAsync(Path.ChangeExtension(path, ".vtt"), "WEBVTT\n\n");
        if (scenarioName == "plain-sidecar") await File.WriteAllTextAsync(Path.ChangeExtension(path, ".txt"), "Potential manual transcript");
        if (scenarioName == "foreign-pipeline") await scenario.QueueAsync(source.Id);
        using var retained = new SqlRetainedSourceReader(scenario.Factory, scenario.Retained);
        var store = new SqlMediaTranscriptionRegistrationStore(scenario.Factory, scenario.Admission,
            new WindowsSourceObservationReader(), retained, TimeProvider.System);
        var shouldQueue = scenarioName is "normal" or "catch-up" or "catch-up-over-text-limit" or "discovery-over-text-limit" or "existing" or
            "revised-original" or "mp3-signature" or "wav-signature";

        if (scenarioName == "existing")
        {
            var first = await store.RunOnceAsync(options, runtimeReady: true, default);
            Assert.Equal(1, first.Queued);
        }
        var concurrent = await Task.WhenAll(
            store.RunOnceAsync(options, scenarioName != "unready", default).AsTask(),
            store.RunOnceAsync(options, scenarioName != "unready", default).AsTask());
        Assert.Equal(scenarioName == "existing" || !shouldQueue ? 0 : 1, concurrent.Sum(result => result.Queued));
        var activities = await db.SourceActivities.AsNoTracking().Where(value => value.SourceRevisionId == source.Id &&
            value.ActivityKind == (int)SourceActivityKind.MediaTranscription).ToArrayAsync();
        var records = await db.PipelineRecords.AsNoTracking().Where(value => value.SourceRevisionId == source.Id).ToArrayAsync();

        if (!shouldQueue)
        {
            if (scenarioName == "foreign-pipeline")
            {
                var prior = Assert.Single(records);
                var priorJob = await db.Jobs.SingleAsync(value => value.PipelineRecordId == prior.Id);
                Assert.Equal(PipelineOperations.Embed, priorJob.Operation);
                Assert.Equal("media-transcription-existing-work-conflict", Assert.Single(activities).Reason);
            }
            else
            {
                Assert.Empty(records);
            }
            if (scenarioName is "sidecar" or "plain-sidecar" or "unready")
            {
                var held = Assert.Single(activities);
                Assert.Null(held.ResultingPipelineRecordId);
                Assert.Equal((int)SourceActivityState.DeferredPolicy, held.State);
                Assert.Equal(scenarioName == "unready" ? "media-transcription-runtime-unavailable" : "media-adjacent-transcript-unverified", held.Reason);
            }
            if (scenarioName == "unready")
            {
                var heldId = Assert.Single(activities).Id;
                Assert.Equal(1, (await store.RunOnceAsync(options, runtimeReady: true, default)).Queued);
                var recovered = await db.SourceActivities.AsNoTracking().SingleAsync(value => value.Id == heldId);
                Assert.Equal((int)SourceActivityState.Pending, recovered.State);
                Assert.NotNull(recovered.ResultingPipelineRecordId);
                Assert.Null(recovered.Reason);
            }
            return;
        }

        if (scenarioName == "revised-original")
            Assert.False(await db.PipelineRecords.AnyAsync(value => value.SourceRevisionId == original.Id));
        var record = Assert.Single(records);
        var activity = Assert.Single(activities);
        Assert.Equal(record.Id, activity.ResultingPipelineRecordId);
        Assert.Equal(source.ContentSha256, record.ContentHash);
        Assert.Equal(source.ContentSha256, activity.InputFingerprint);
        Assert.Equal((int)SourceActivityState.Pending, activity.State);
        var job = await db.Jobs.SingleAsync(value => value.PipelineRecordId == record.Id);
        var delivery = await db.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
        Assert.Equal(MediaTranscriptionDiscoveryOptions.Operation, job.Operation);
        Assert.Equal((int)PipelineStage.Extract, job.Stage);
        Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(0, delivery.DispatchGeneration);
        Assert.Equal(delivery.Operation, job.Operation);
        Assert.Equal(record.Revision, delivery.SourceRevision);
        var outbox = new SqlOutboxStore(scenario.Factory, sourceAdmission: scenario.Admission);
        Assert.Null(await outbox.ClaimNextDueAsync("without-media-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8], default));
        var claim = await outbox.ClaimNextDueAsync("media-stage", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            [MediaTranscriptionDiscoveryOptions.Operation], default);
        Assert.Equal(delivery.Id, Assert.IsType<ClaimedDispatchMessage>(claim).DispatchMessageId.Value);
    }
}
