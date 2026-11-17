using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DefaultRetainedPolicyIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Exact_retained_admission_recovers_unexecuted_legacy_pending_text_with_one_successor_and_job()
    {
        var seeded = await SeedAsync(extension: ".dart");
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var pending = await setup.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            pending.ActivityKind = (int)SourceActivityKind.TextExtraction;
            pending.ExecutionClass = (int)ExecutionClass.InProcess;
            pending.State = (int)SourceActivityState.Pending;
            pending.Reason = null;
            await setup.SaveChangesAsync();
        }

        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System,
            seeded.ArtifactRoot, sourceAdmission: new FixtureSourceAdmission(factory));
        await using (var before = await factory.CreateDbContextAsync())
            Assert.False(await registration.RegisterAsync(SqlSourceActivityWriter.Restore(
                await before.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)), default));
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(reader);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var receipt = await service.CommitAsync(mutation, preview.ConfirmationId, "legacy-pending-exact", "test", CancellationToken.None);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, "legacy-pending-exact", "test", CancellationToken.None);
        Assert.Equal(receipt.OperationId, replay.OperationId);
        Assert.True(replay.WasReplay);

        Assert.True(await registration.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await registration.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal("Unknown", (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).Classification);
        var predecessor = await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, predecessor.State);
        Assert.Equal(RetainedDefaultPolicy.RelationshipKind, predecessor.Reason);
        Assert.Equal(0, predecessor.AttemptCount);
        var relation = await db.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.ActivityId);
        var successor = await db.SourceActivities.SingleAsync(value => value.Id == relation.SuccessorActivityId);
        Assert.Equal(successor.Id, (await SqlCurrentSourceActivities.Query(db)
            .SingleAsync(value => value.SourceRevisionId == seeded.RevisionId)).Id);
        Assert.NotNull(successor.ResultingPipelineRecordId);
        Assert.Single(await db.Jobs.Where(value => value.PipelineRecordId == successor.ResultingPipelineRecordId).ToArrayAsync());
        Assert.False(Directory.Exists(seeded.OriginalRoot));
    }

    [NativeSqlServerTheory]
    [InlineData(true, "pending-first")]
    [InlineData(true, "policy-first")]
    [InlineData(true, "concurrent")]
    [InlineData(false, "pending-first")]
    public async Task Legacy_recovery_uses_one_authoritative_predecessor_and_publishes_in_both_source_projections(bool withPolicyOwner, string order)
    {
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline already published.");
        var (seeded, pendingId) = await SeedStrandedTextAsync(withPolicyOwner, pipeline.ArtifactRoot);
        var factory = pipeline.Factory;
        using var retained = new SqlRetainedSourceReader(factory, pipeline.ArtifactRoot);
        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, pipeline.ArtifactRoot,
            sourceAdmission: new FixtureSourceAdmission(factory));
        var service = Service(retained, new RetainedTextActivityPlanner(registration));
        var suffix = seeded.RevisionId.ToString("N");
        var ownerConfirmation = string.Empty;
        var ownerMutation = Mutation(seeded);
        var pendingMutation = Mutation(seeded with { ActivityId = pendingId });
        async Task CommitOwnerAsync()
        {
            var preview = await service.PreviewAsync(ownerMutation, "test", default);
            ownerConfirmation = preview.ConfirmationId;
            await service.CommitAsync(ownerMutation, preview.ConfirmationId, "legacy-owner-" + suffix, "test", default);
        }
        async Task RefusePendingSiblingAsync()
        {
            var preview = await service.PreviewAsync(pendingMutation, "test", default);
            var error = await Assert.ThrowsAsync<NativeOperationException>(() =>
                service.CommitAsync(pendingMutation, preview.ConfirmationId, "legacy-sibling-" + suffix, "test", default).AsTask());
            Assert.Contains(error.ReasonCode, new[] { "policy-item-not-eligible", "operation-fenced" });
        }

        if (!withPolicyOwner) await CommitOwnerAsync();
        else if (order == "concurrent") await Task.WhenAll(CommitOwnerAsync(), RefusePendingSiblingAsync());
        else if (order == "pending-first") { await RefusePendingSiblingAsync(); await CommitOwnerAsync(); }
        else { await CommitOwnerAsync(); await RefusePendingSiblingAsync(); }

        // Native finalization already linked the exact successor without a scan loop.
        Assert.Equal(0, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        // A proven same-successor operation after registration cannot create a
        // second record. Once execution starts, only receipt replay is authorised.
        var boundPreview = await service.PreviewAsync(ownerMutation, "test", default);
        await service.CommitAsync(ownerMutation, boundPreview.ConfirmationId, "legacy-owner-bound-" + suffix, "test", default);
        await pipeline.PumpAsync();
        await using var db = await factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal((int)PipelineStage.Publish, record.CurrentStage);
        var jobs = await db.Jobs.Where(value => value.PipelineRecordId == record.Id).ToArrayAsync();
        Assert.Equal(5, jobs.Length);
        Assert.All(jobs, job => Assert.Equal((int)PublicJobState.Completed, job.PublicState));
        Assert.Contains("safe retained policy sample", (await db.Artifacts.SingleAsync(value =>
            value.PipelineRecordId == record.Id && value.Stage == (int)PipelineStage.CanonicalIndex)).SearchText);
        var publishedGeneration = Guid.Parse((await db.Artifacts.SingleAsync(value =>
            value.PipelineRecordId == record.Id && value.Stage == (int)PipelineStage.Publish)).SearchText!);
        Assert.Equal(await pipeline.Store.GetActiveGenerationIdAsync(default), publishedGeneration);
        var relation = await db.SourceActivityRelations.SingleAsync(value =>
            value.PredecessorActivityId == seeded.ActivityId || value.PredecessorActivityId == pendingId);
        Assert.Equal(seeded.ActivityId, relation.PredecessorActivityId);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded,
            (await db.SourceActivities.SingleAsync(value => value.Id == pendingId)).State);
        if (withPolicyOwner) Assert.Equal((int)SourceActivityState.DeferredPolicy,
            (await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
        var projections = new SourceRootProjectionReader(factory, new NoOriginalIoPolicy(), new LocalSourceEnumerator(),
            new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await projections.ReadRootAsync(seeded.RootId, default));
        Assert.Equal("Indexed", Assert.Single(detail.Files).Status);
        Assert.Equal(record.Id, Assert.Single(detail.Files).CorpusPipelineRecordId);
        Assert.Equal(0, detail.BlockedCount);
        Assert.Equal(0, detail.ErrorCount);
        var overview = Assert.Single(await projections.ReadRootsAsync(default), value => value.Id == seeded.RootId);
        Assert.Equal(1, overview.IndexedCount);
        Assert.Equal(0, overview.BlockedCount);
        Assert.Equal(0, overview.ErrorCount);
        var replay = await service.CommitAsync(ownerMutation, ownerConfirmation, "legacy-owner-" + suffix, "test", default);
        Assert.True(replay.WasReplay);
        var completedPreview = await service.PreviewAsync(ownerMutation, "test", default);
        var completedRefusal = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(ownerMutation,
            completedPreview.ConfirmationId, "legacy-owner-after-publication-" + suffix, "test", default).AsTask());
        Assert.Equal(withPolicyOwner ? "policy-successor-conflict" : "policy-item-not-eligible", completedRefusal.ReasonCode);
        Assert.Equal(0, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        Assert.Equal(5, await db.Jobs.CountAsync(value => value.PipelineRecordId == record.Id));
        Assert.Equal("Unknown", (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).Classification);
        Assert.False(Directory.Exists(seeded.OriginalRoot));
    }

    [NativeSqlServerTheory]
    [InlineData("attempt")]
    [InlineData("attempt-time")]
    [InlineData("record")]
    [InlineData("branch")]
    [InlineData("capability")]
    [InlineData("descriptor")]
    [InlineData("accepted-classification")]
    [InlineData("cancelled-policy-successor")]
    public async Task Legacy_pending_admission_refuses_work_outside_the_unexecuted_no_record_scope(string conflict)
    {
        var (seeded, pendingId) = await SeedStrandedTextAsync(false,
            classification: conflict == "accepted-classification" ? "AcceptedUtf8Text" : "Unknown");
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var pending = await setup.SourceActivities.SingleAsync(value => value.Id == pendingId);
            if (conflict == "attempt") pending.AttemptCount = 1;
            if (conflict == "attempt-time") pending.LastAttemptAtUtc = DateTimeOffset.UtcNow;
            if (conflict == "capability") pending.RequiredCapability = "unconfirmed-handler";
            if (conflict == "descriptor") pending.DescriptorFingerprint = new string('c', 64);
            if (conflict == "record") AddOrphanedRecord(setup, seeded);
            if (conflict == "cancelled-policy-successor") setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = Guid.NewGuid(), SourceRevisionId = seeded.RevisionId, InputFingerprint = seeded.Hash,
                ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability,
                ProcessorVersion = RetainedDefaultPolicy.UnsupportedBinary.ProcessorVersion,
                DescriptorFingerprint = RetainedDefaultPolicy.UnsupportedBinary.ProcessorFingerprint,
                State = (int)SourceActivityState.CancelledSuperseded, Reason = "No installed parser can process this retained binary format.",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            if (conflict == "branch") setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = Guid.NewGuid(), SourceRevisionId = seeded.RevisionId, SourceActivityId = pendingId,
                InputSha256 = seeded.Hash, ProcessorVersion = pending.ProcessorVersion, ProcessorFingerprint = pending.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        using var retained = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(retained);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", default);
        var error = await Assert.ThrowsAsync<NativeOperationException>(() =>
            service.CommitAsync(mutation, preview.ConfirmationId, "legacy-conflict-" + conflict, "test", default).AsTask());
        Assert.Equal("policy-item-not-eligible", error.ReasonCode);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.SourceActivities.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId && value.ProcessorVersion == RetainedDefaultPolicy.Version));
        Assert.False(await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == seeded.ActivityId));
    }

    [NativeSqlServerTheory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("foreign-successor")]
    [InlineData("wrong-policy")]
    [InlineData("missing-relation")]
    [InlineData("admission-hash")]
    [InlineData("admission-type")]
    public async Task Cancelled_legacy_predecessor_requires_its_exact_successor_relation_and_both_proofs(string corruption)
    {
        var (seeded, _) = await SeedStrandedTextAsync(false);
        var factory = SqlTestData.CreateFactory(fixture);
        using var retained = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(retained);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", default);
        await service.CommitAsync(mutation, preview.ConfirmationId, "legacy-before-corruption-" + seeded.RevisionId.ToString("N"), "test", default);
        SourceActivity successor;
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var prior = await setup.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            var relation = await setup.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == prior.Id);
            var next = await setup.SourceActivities.SingleAsync(value => value.Id == relation.SuccessorActivityId);
            if (corruption == "missing") prior.AttemptEvidenceJson = null;
            if (corruption == "malformed") prior.AttemptEvidenceJson = "{";
            if (corruption == "foreign-successor") prior.AttemptEvidenceJson = JsonSerializer.Serialize(new
                { policy = RetainedDefaultPolicy.Version, supersededByActivityId = Guid.NewGuid() });
            if (corruption == "wrong-policy") prior.AttemptEvidenceJson = JsonSerializer.Serialize(new
                { policy = "other-policy", supersededByActivityId = next.Id });
            if (corruption == "missing-relation") setup.SourceActivityRelations.Remove(relation);
            if (corruption == "admission-hash") next.AttemptEvidenceJson = JsonSerializer.Serialize(new
                { policy = RetainedDefaultPolicy.Version, configurationRevision = 1, inputSha256 = new string('b', 64), verifiedByteLength = 1 });
            if (corruption == "admission-type") next.AttemptEvidenceJson = JsonSerializer.Serialize(new
                { policy = RetainedDefaultPolicy.Version, configurationRevision = "1", inputSha256 = seeded.Hash, verifiedByteLength = 1 });
            await setup.SaveChangesAsync();
            successor = SqlSourceActivityWriter.Restore(next);
        }
        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot,
            sourceAdmission: new FixtureSourceAdmission(factory));
        Assert.False(await registration.RegisterAsync(successor, default));
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.PipelineRecords.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId));
        Assert.Null((await db.SourceActivities.SingleAsync(value => value.Id == successor.Id.Value)).ResultingPipelineRecordId);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_admission_rechecks_a_record_or_older_owner_created_after_retained_qualification(bool addRecord)
    {
        var (seeded, _) = await SeedStrandedTextAsync(false);
        var factory = SqlTestData.CreateFactory(fixture);
        using var retained = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var reader = new AfterRetainedRead(retained, async () =>
        {
            await using var changed = await factory.CreateDbContextAsync();
            if (addRecord) AddOrphanedRecord(changed, seeded);
            else
            {
                // The old cancelled retention activity occupies the legacy document key.
                // A distinct descriptor models another current policy owner at this boundary.
                changed.SourceActivities.Add(new SourceActivityEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = seeded.RevisionId, InputFingerprint = seeded.Hash,
                    ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability,
                    ProcessorVersion = "legacy-policy-owner", DescriptorFingerprint = new string('d', 64),
                    State = (int)SourceActivityState.DeferredPolicy, Reason = "File extension is unknown to local source ingestion.",
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            await changed.SaveChangesAsync();
        });
        var service = Service(reader);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", default);
        var error = await Assert.ThrowsAsync<NativeOperationException>(() =>
            service.CommitAsync(mutation, preview.ConfirmationId, "legacy-late-owner-" + seeded.RevisionId.ToString("N"), "test", default).AsTask());
        Assert.Equal("policy-item-not-eligible", error.ReasonCode);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.SourceActivities.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId && value.ProcessorVersion == RetainedDefaultPolicy.Version));
        Assert.Equal((int)SourceActivityState.Pending, (await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
        Assert.False(await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == seeded.ActivityId));
        Assert.False(await db.Jobs.AnyAsync(job => db.PipelineRecords.Any(record =>
            record.Id == job.PipelineRecordId && record.SourceRevisionId == seeded.RevisionId)));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_primary_pending_can_refresh_only_its_never_executed_unlinked_successor(bool restartFirst)
    {
        var (seeded, _) = await SeedStrandedTextAsync(false);
        var factory = SqlTestData.CreateFactory(fixture);
        using var retained = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(retained);
        var suffix = seeded.RevisionId.ToString("N");
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", default);
        await service.CommitAsync(mutation, preview.ConfirmationId, "legacy-initial-" + suffix, "test", default);
        await using (var changed = await factory.CreateDbContextAsync())
        {
            (await changed.SourceRootConfigurations.SingleAsync(value => value.Id == seeded.RootId)).ConfigurationRevision = 2;
            await changed.SaveChangesAsync();
        }
        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot,
            sourceAdmission: new FixtureSourceAdmission(factory));
        if (restartFirst)
        {
            _ = await registration.OfferUnlinkedInProcessActivitiesAsync(default);
            await using var stale = await factory.CreateDbContextAsync();
            Assert.Equal("policy-binding-stale", (await stale.SourceActivities.SingleAsync(value =>
                value.SourceRevisionId == seeded.RevisionId && value.ProcessorVersion == RetainedDefaultPolicy.Version)).Reason);
            Assert.False(await stale.PipelineRecords.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId));
        }
        var refreshed = Mutation(seeded, 2);
        var freshPreview = await service.PreviewAsync(refreshed, "test", default);
        await service.CommitAsync(refreshed, freshPreview.ConfirmationId, "legacy-refresh-" + suffix, "test", default);
        Assert.True(await registration.OfferUnlinkedInProcessActivitiesAsync(default) >= 1);
        Assert.Equal(0, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        await using var db = await factory.CreateDbContextAsync();
        var successor = await db.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId &&
            value.ProcessorVersion == RetainedDefaultPolicy.Version);
        using var proof = JsonDocument.Parse(successor.AttemptEvidenceJson!);
        Assert.Equal(2, proof.RootElement.GetProperty("configurationRevision").GetInt64());
        Assert.NotNull(successor.ResultingPipelineRecordId);
        Assert.Single(await db.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToArrayAsync());
        Assert.Single(await db.PipelineRecords.Where(value => value.SourceRevisionId == seeded.RevisionId).ToArrayAsync());
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, (await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
    }

    private async Task<(Seed Source, Guid PendingId)> SeedStrandedTextAsync(bool withPolicyOwner, string? artifactRoot = null,
        string classification = "Unknown")
    {
        var seeded = await SeedAsync(extension: ".dart", artifactRoot: artifactRoot, classification: classification);
        var factory = SqlTestData.CreateFactory(fixture);
        await using var db = await factory.CreateDbContextAsync();
        var owner = await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
        var pendingId = withPolicyOwner ? Guid.NewGuid() : owner.Id;
        var pending = withPolicyOwner ? new SourceActivityEntity
        {
            Id = pendingId, SourceRevisionId = seeded.RevisionId, InputFingerprint = seeded.Hash,
            ProcessorVersion = "phase-3a-v1", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        } : owner;
        pending.ActivityKind = (int)SourceActivityKind.TextExtraction;
        pending.ExecutionClass = (int)ExecutionClass.InProcess;
        pending.State = (int)SourceActivityState.Pending;
        pending.Reason = null;
        if (withPolicyOwner) db.SourceActivities.Add(pending);
        else db.SourceActivities.Add(new SourceActivityEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = seeded.RevisionId, InputFingerprint = seeded.Hash,
            ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability,
            ProcessorVersion = "phase-3a-v1", State = (int)SourceActivityState.CancelledSuperseded,
            RequiredCapability = "source-artifact-store", Reason = "artifact-io-failed",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (seeded, pendingId);
    }

    private static void AddOrphanedRecord(FluxKnowledgeDbContext db, Seed seeded)
    {
        var identity = Guid.NewGuid();
        var record = Guid.NewGuid();
        db.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identity, SourceKind = "retained local source", StableKey = "legacy-guard:" + identity,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = record, SourceIdentityId = identity, SourceRevisionId = seeded.RevisionId,
            Revision = 1, ContentHash = seeded.Hash, RootLineageRecordId = record,
            CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = DateTimeOffset.UtcNow
        });
    }

    private sealed class AfterRetainedRead(IRetainedSourceReader inner, Func<Task> change) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId revision, CancellationToken token) => inner.ReadBytesAsync(revision, token);
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId revision, CancellationToken token) => inner.ReadUtf8Async(revision, token);
        public async ValueTask<RetainedSourceReadLease> OpenReadAsync(SourceRevisionId revision, long maximumBytes, CancellationToken token)
        {
            var lease = await inner.OpenReadAsync(revision, maximumBytes, token);
            try { await change(); return lease; }
            catch { await lease.DisposeAsync(); throw; }
        }
    }
}
