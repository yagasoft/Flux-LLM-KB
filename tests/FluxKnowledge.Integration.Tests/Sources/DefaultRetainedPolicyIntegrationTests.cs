using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Buffers.Binary;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class DefaultRetainedPolicyIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Exact_policy_successor_registers_after_restart_without_changing_immutable_classification_or_history()
    {
        var seeded = await SeedAsync();
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var first = await service.CommitAsync(mutation, preview.ConfirmationId, "policy-first", "test", CancellationToken.None);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, "policy-first", "test", CancellationToken.None);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.True(replay.WasReplay);
        var registration = new SqlRetainedTextRegistrationStore(SqlTestData.CreateFactory(fixture), TimeProvider.System, seeded.ArtifactRoot);
        Assert.True(await registration.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await registration.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Equal("Unknown", (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).Classification);
        var predecessor = await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
        Assert.Equal((int)SourceActivityState.DeferredPolicy, predecessor.State);
        Assert.Equal("File extension is unknown to local source ingestion.", predecessor.Reason);
        var relation = await db.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.ActivityId);
        var successor = await db.SourceActivities.SingleAsync(value => value.Id == relation.SuccessorActivityId);
        Assert.NotNull(successor.ResultingPipelineRecordId);
        Assert.Single(await db.Jobs.Where(value => value.PipelineRecordId == successor.ResultingPipelineRecordId).ToArrayAsync());
        Assert.False(Directory.Exists(seeded.OriginalRoot));
    }

    [NativeSqlServerFact]
    public async Task A_derived_revision_with_physical_looking_history_is_not_a_policy_source_owner()
    {
        var seeded = await SeedAsync(withPhysicalHistory: true, originKind: 2);
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation,
            preview.ConfirmationId, "derived-owner-refused", "test", CancellationToken.None).AsTask());
        Assert.Equal("policy-item-not-eligible", refusal.ReasonCode);
    }

    private NativeCorpusCommandService Service(SqlRetainedSourceReader reader) => new(
        new NativeOperationService(new SqlNativeOperationStore(SqlTestData.CreateFactory(fixture), TimeProvider.System), []),
        new SqlNativeCorpusActionStore(SqlTestData.CreateFactory(fixture), new NoOriginalIoPolicy(), new LocalPrivateContentDisclosure(), retainedSourceReader: reader));

    [NativeSqlServerTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Physical_revision_history_allows_policy_recovery_only_without_an_unsuppressed_competitor(bool suppressed, bool differentIdentity)
    {
        var seeded = await SeedAsync(revision: 2, withPhysicalHistory: true, suppressPrevious: suppressed, differentHistoryIdentity: differentIdentity);
        var factory = SqlTestData.CreateFactory(fixture);
        var previousId = Assert.IsType<Guid>(seeded.PreviousId);
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        if (!suppressed)
        {
            var refusedPreview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
            var refusal = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, refusedPreview.ConfirmationId,
                "physical-history-policy-refused", "test", CancellationToken.None).AsTask());
            Assert.Equal("policy-item-not-eligible", refusal.ReasonCode);
            return;
        }
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await service.CommitAsync(mutation, preview.ConfirmationId, "physical-history-policy", "test", CancellationToken.None);
        Assert.True(await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot)
            .OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None) >= 1);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(previousId, (await verify.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).ParentSourceRevisionId);
        Assert.Single(await verify.PipelineRecords.Where(value => value.SourceRevisionId == seeded.RevisionId).ToArrayAsync());
        Assert.False(Directory.Exists(seeded.OriginalRoot));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_revision_policy_rechecks_history_competitors_at_commit_and_registration(bool afterCommit)
    {
        var seeded = await SeedAsync(revision: 2, withPhysicalHistory: true);
        var factory = SqlTestData.CreateFactory(fixture);
        var previousId = Assert.IsType<Guid>(seeded.PreviousId);
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        if (afterCommit) await service.CommitAsync(mutation, preview.ConfirmationId, "history-before-competitor", "test", CancellationToken.None);
        await using (var change = await factory.CreateDbContextAsync())
        {
            (await change.SourceRevisions.SingleAsync(value => value.Id == previousId)).SuppressedAtUtc = null;
            await change.SaveChangesAsync();
        }
        if (!afterCommit)
        {
            var refused = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, preview.ConfirmationId,
                "history-after-competitor", "test", CancellationToken.None).AsTask());
            Assert.Equal("policy-item-not-eligible", refused.ReasonCode);
        }
        else
        {
            Assert.Equal(0, await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot)
                .OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        }
        await using var verify = await factory.CreateDbContextAsync();
        Assert.False(await verify.PipelineRecords.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId));
    }

    [NativeSqlServerFact]
    public async Task Later_scans_reuse_the_verified_policy_successor_and_supersede_an_unlinked_legacy_pending_activity()
    {
        var seeded = await SeedAsync();
        var store = new SqlSourceActivityStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var draft = new SourceActivityDraft(new SourceRevisionId(seeded.RevisionId), SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess, "phase-3a-v1", seeded.Hash, null, null);
        var orphan = await store.FindOrCreateAsync(draft, CancellationToken.None);
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await service.CommitAsync(mutation, preview.ConfirmationId, "policy-rescan", "test", CancellationToken.None);
        var rescanned = await store.FindOrCreateAsync(draft, CancellationToken.None);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var relation = await db.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.ActivityId);
        Assert.Equal(relation.SuccessorActivityId, rescanned.Id.Value);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, (await db.SourceActivities.SingleAsync(value => value.Id == orphan.Id.Value)).State);
        var cancelled = await db.SourceActivities.SingleAsync(value => value.Id == orphan.Id.Value);
        using var evidence = JsonDocument.Parse(cancelled.AttemptEvidenceJson!);
        Assert.Equal(rescanned.Id.Value, evidence.RootElement.GetProperty("supersededByActivityId").GetGuid());
        Assert.True(await db.AuditEvents.AnyAsync(value => value.SourceActivityId == orphan.Id.Value && value.EventType == "source.unlinked_text_superseded"));
        var registration = new SqlRetainedTextRegistrationStore(SqlTestData.CreateFactory(fixture), TimeProvider.System, seeded.ArtifactRoot);
        Assert.True(await registration.RegisterAsync(rescanned, CancellationToken.None));
        Assert.False(await registration.RegisterAsync(rescanned, CancellationToken.None));
        Assert.Single(await SqlCurrentSourceActivities.Query(db).Where(value => value.SourceRevisionId == seeded.RevisionId).ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_changed_retained_content_is_reported_without_admitting_work(bool changed)
    {
        var seeded = await SeedAsync();
        var path = Path.Combine(seeded.ArtifactRoot, "sha256", seeded.Hash[..2], seeded.Hash + ".bin");
        if (changed) await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("changed retained input"));
        else File.Delete(path);
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var error = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, preview.ConfirmationId, "policy-unavailable", "test", CancellationToken.None).AsTask());
        Assert.Equal(changed ? "retained-artifact-checksum-invalid" : "retained-content-unavailable", error.ReasonCode);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.False(await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == seeded.ActivityId));
        Assert.False(await db.PipelineRecords.AnyAsync(value => value.SourceRevisionId == seeded.RevisionId));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_unexecuted_text_admission_is_visible_and_can_be_reverified(bool configurationChanged)
    {
        var seeded = await SeedAsync(); var factory = SqlTestData.CreateFactory(fixture);
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await service.CommitAsync(mutation, preview.ConfirmationId, "text-before-drift-" + configurationChanged, "test", CancellationToken.None);
        SourceActivity successor;
        await using (var db = await factory.CreateDbContextAsync())
        {
            successor = SqlSourceActivityWriter.Restore(await db.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId && value.ProcessorVersion == RetainedDefaultPolicy.Version));
            if (configurationChanged) (await db.SourceRootConfigurations.SingleAsync(value => value.Id == seeded.RootId)).ConfigurationRevision++;
            else (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).SuppressedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot);
        Assert.False(await registration.RegisterAsync(successor, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal("policy-binding-stale", (await db.SourceActivities.SingleAsync(value => value.Id == successor.Id.Value)).Reason);
        if (!configurationChanged)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).SuppressedAtUtc = null;
            await db.SaveChangesAsync();
        }
        var renewed = Mutation(seeded, configurationChanged ? 2 : 1); var renewal = await service.PreviewAsync(renewed, "test", CancellationToken.None);
        await service.CommitAsync(renewed, renewal.ConfirmationId, "text-after-drift-" + configurationChanged, "test", CancellationToken.None);
        Assert.True(await registration.RegisterAsync(successor, CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData("office")]
    [InlineData("office-v2")]
    [InlineData("office-v3")]
    [InlineData("media")]
    [InlineData("zip")]
    [InlineData("csharp")]
    public async Task Changed_processor_registration_preserves_and_retires_the_legacy_descriptor(string kind)
    {
        var current = kind switch { "office" or "office-v2" or "office-v3" => OoxmlStructuralTextProcessor.Capability, "media" => MediaMetadataRetainedProcessor.Capability,
            "zip" => ZipArchiveRetainedProcessor.Capability, _ => RetainedCsharpCodeProcessor.Capability };
        var old = current with { Id = kind switch { "office-v2" => new Guid("1e73f18a-0151-4b02-aca7-d6ca34a557ea"), "office-v3" => new Guid("c784f889-e910-4d42-95fd-b4d8a91b9a03"), _ => Guid.NewGuid() }, ProcessorVersion = kind switch { "office" => OoxmlStructuralTextProcessor.LegacyProcessorVersion,
            "office-v2" => "phase-5-ooxml-structural-v2",
            "office-v3" => OoxmlStructuralTextProcessor.Version3ProcessorVersion,
            "media" => MediaMetadataRetainedProcessor.LegacyProcessorVersion, "zip" => ZipArchiveRetainedProcessor.LegacyProcessorVersion, _ => current.ProcessorVersion },
            ProcessorFingerprint = kind switch { "office" => OoxmlStructuralTextProcessor.LegacyProcessorFingerprint,
                "office-v2" => "phase-5-ooxml-retained-structural-v2",
                "office-v3" => OoxmlStructuralTextProcessor.Version3ProcessorFingerprint,
                "media" => MediaMetadataRetainedProcessor.LegacyProcessorFingerprint, "zip" => ZipArchiveRetainedProcessor.LegacyProcessorFingerprint,
                _ => RetainedCsharpCodeProcessor.LegacyDescriptorFingerprint } };
        var store = new SqlSourceActivityStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var service = new SourceCapabilityService(store, new LocalSourceCapabilityHandlerRegistry([new DescriptorHandler(old), new DescriptorHandler(current)]));
        Assert.True((await service.RegisterAsync(old, CancellationToken.None)).IsRunnable);
        Assert.True((await service.RegisterAsync(current, CancellationToken.None)).IsRunnable);
        var preserved = await store.FindAsync(old.Id, CancellationToken.None);
        Assert.NotNull(preserved);
        Assert.False(preserved.IsRunnable);
        Assert.Equal(old.ProcessorVersion, preserved.ProcessorVersion);
        Assert.Equal(old.ProcessorFingerprint, preserved.ProcessorFingerprint);
    }

    [NativeSqlServerTheory]
    [InlineData("same-path")]
    [InlineData("case-variant-path")]
    [InlineData("same-identity-older")]
    public async Task Ambiguous_unsuppressed_binding_cannot_be_selected_by_revision_order(string ambiguity)
    {
        var seeded = await SeedAsync(revision: 2);
        await using (var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync())
        {
            var original = await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId);
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = original.SourceRootId,
                CanonicalPath = ambiguity == "same-identity-older" ? Path.Combine(seeded.OriginalRoot, "other.rdl") :
                    ambiguity == "case-variant-path" ? original.CanonicalPath.ToUpperInvariant() : original.CanonicalPath,
                StableSourceIdentity = ambiguity == "same-identity-older" ? original.StableSourceIdentity : "different-identity",
                Revision = 1, ContentSha256 = new string('b', 64), ByteLength = 1, Classification = "Unknown", Extension = ".rdl", DiscoveredAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, preview.ConfirmationId, "ambiguous-policy", "test", CancellationToken.None).AsTask());
        await using var verification = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.False(await verification.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == seeded.ActivityId));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_policy_requests_create_one_successor_and_one_registration()
    {
        var seeded = await SeedAsync();
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var one = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var two = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await Task.WhenAll(service.CommitAsync(mutation, one.ConfirmationId, "policy-concurrent-one", "test", CancellationToken.None).AsTask(),
            service.CommitAsync(mutation, two.ConfirmationId, "policy-concurrent-two", "test", CancellationToken.None).AsTask());
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Single(await db.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToArrayAsync());
        Assert.Equal(2, await db.SourceActivities.CountAsync(value => value.SourceRevisionId == seeded.RevisionId));
        Assert.Single(await SqlCurrentSourceActivities.Query(db).Where(value => value.SourceRevisionId == seeded.RevisionId).ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_or_suppression_drift_blocks_admission_without_creating_a_successor(bool suppress)
    {
        var seeded = await SeedAsync();
        using var reader = new SqlRetainedSourceReader(SqlTestData.CreateFactory(fixture), seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await using (var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync())
        {
            if (suppress) (await db.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId)).SuppressedAtUtc = DateTimeOffset.UtcNow;
            else (await db.SourceRootConfigurations.SingleAsync(value => value.Id == seeded.RootId)).ConfigurationRevision++;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, preview.ConfirmationId, "drift-policy", "test", CancellationToken.None).AsTask());
        await using var verification = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.False(await verification.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == seeded.ActivityId));
    }

    private static NativeCorpusMutation Mutation(Seed seeded, long configurationRevision = 1)
    {
        var payload = JsonSerializer.SerializeToElement(new { rootId = seeded.RootId, sourceRevisionId = seeded.RevisionId,
            predecessorActivityId = seeded.ActivityId, inputSha256 = seeded.Hash, configurationRevision });
        return new NativeCorpusMutation("policy_reprocess", payload);
    }

    [NativeSqlServerTheory]
    [InlineData("office", "none", false)]
    [InlineData("media", "none", false)]
    [InlineData("zip", "none", false)]
    [InlineData("csharp", "none", false)]
    [InlineData("office", "before-claim", false)]
    [InlineData("csharp", "before-claim", false)]
    [InlineData("office", "after-claim", false)]
    [InlineData("csharp", "after-claim", false)]
    [InlineData("office", "expired-claim", false)]
    [InlineData("csharp", "expired-claim", false)]
    [InlineData("office", "none", true)]
    [InlineData("office", "before-claim", true)]
    [InlineData("office", "after-claim", true)]
    [InlineData("office", "expired-claim", true)]
    public async Task Legacy_policy_block_creates_one_current_branch_and_preserves_its_original_attempt(string kind, string drift, bool previousOffice)
        => await VerifyLegacyPolicyRecoveryAsync(kind, drift, previousOffice ? 2 : 1,
            previousOffice ? "office-document-part-unsupported" : "office-document-element-limit");

    [NativeSqlServerTheory]
    [InlineData(2, "office-document-container-invalid", "none")]
    [InlineData(2, "office-document-element-limit", "none")]
    [InlineData(3, "office-document-container-invalid", "none")]
    [InlineData(3, "office-document-element-limit", "none")]
    [InlineData(3, "office-document-container-invalid", "before-claim")]
    [InlineData(3, "office-document-container-invalid", "after-claim")]
    [InlineData(3, "office-document-element-limit", "expired-claim")]
    public async Task V4_compatibility_recovery_requires_explicit_admission_and_preserves_old_versions_and_fences(int officeGeneration, string reason, string drift)
        => await VerifyLegacyPolicyRecoveryAsync("office", drift, officeGeneration, reason);

    private async Task VerifyLegacyPolicyRecoveryAsync(string kind, string drift, int officeGeneration, string officeReason)
    {
        var previousOffice = officeGeneration > 1;
        var descriptor = kind switch { "office" => OoxmlStructuralTextProcessor.Capability, "media" => MediaMetadataRetainedProcessor.Capability,
            "zip" => ZipArchiveRetainedProcessor.Capability, _ => RetainedCsharpCodeProcessor.Capability };
        var extension = kind switch { "office" => ".docx", "media" => ".mp4", "zip" => ".zip", _ => ".cs" };
        var bytes = kind switch { "office" => Word(), "media" => Movie(), "zip" => Zip("nested.zip", Zip("report.rdl", Encoding.UTF8.GetBytes("safe nested text"))),
            _ => Encoding.UTF8.GetBytes("class C { void M(string value = \"" + new string('x', 6000) + "\") { } }") };
        var seeded = await SeedAsync(extension: extension, payload: bytes, classification: kind == "csharp" ? "AcceptedUtf8Text" : "DeferredCapability");
        var reason = kind switch { "office" => officeReason, "media" => "media-metadata-input-too-large",
            "zip" => "nested-archive-depth-limit", _ => "csharp-code-signature-limit" };
        var legacyVersion = kind switch { "office" => $"phase-5-ooxml-structural-v{officeGeneration}", "media" => MediaMetadataRetainedProcessor.LegacyProcessorVersion,
            "zip" => ZipArchiveRetainedProcessor.LegacyProcessorVersion, _ => RetainedCsharpCodeProcessor.ProcessorVersion };
        var legacyFingerprint = kind switch { "office" => $"phase-5-ooxml-retained-structural-v{officeGeneration}", "media" => MediaMetadataRetainedProcessor.LegacyProcessorFingerprint,
            "zip" => ZipArchiveRetainedProcessor.LegacyProcessorFingerprint, _ => RetainedCsharpCodeProcessor.LegacyDescriptorFingerprint };
        var oldBranchId = Guid.NewGuid(); var oldActivityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var historicalActivityId = previousOffice ? Guid.NewGuid() : (Guid?)null;
        var historicalRelationId = previousOffice ? Guid.NewGuid() : (Guid?)null;
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State = (int)SourceActivityState.CancelledSuperseded;
            db.SourceActivities.Add(new SourceActivityEntity { Id = oldActivityId, SourceRevisionId = seeded.RevisionId, ActivityKind = (int)descriptor.AcceptedActivityKind,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = legacyVersion, DescriptorFingerprint = legacyFingerprint, InputFingerprint = seeded.Hash,
                State = (int)SourceActivityState.FailedTerminal, Reason = reason, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = oldBranchId, SourceActivityId = oldActivityId, SourceRevisionId = seeded.RevisionId,
                InputSha256 = seeded.Hash, ProcessorVersion = legacyVersion, ProcessorFingerprint = legacyFingerprint, State = (int)RetainedProcessorBranchState.Blocked,
                LeaseGeneration = 1, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity { Id = Guid.NewGuid(), BranchId = oldBranchId, LeaseGeneration = 1,
                StartedAtUtc = now, FinishedAtUtc = now, OutcomeCode = reason });
            if (historicalActivityId is { } historyId)
            {
                db.SourceActivities.Add(new SourceActivityEntity { Id = historyId, SourceRevisionId = seeded.RevisionId,
                    ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                    ProcessorVersion = OoxmlStructuralTextProcessor.LegacyProcessorVersion,
                    DescriptorFingerprint = OoxmlStructuralTextProcessor.LegacyProcessorFingerprint,
                    InputFingerprint = seeded.Hash, State = (int)SourceActivityState.FailedTerminal,
                    Reason = "office-document-part-unsupported", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = historicalRelationId!.Value,
                    PredecessorActivityId = historyId, SuccessorActivityId = oldActivityId,
                    RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now });
            }
            await db.SaveChangesAsync();
        }
        seeded = seeded with { ActivityId = oldActivityId };
        var capabilities = new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([new DescriptorHandler(descriptor)]));
        Assert.True((await capabilities.RegisterAsync(descriptor, CancellationToken.None)).IsRunnable);
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var service = Service(reader); var mutation = Mutation(seeded);
        var branches = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        if (previousOffice)
        {
            await using var current = await factory.CreateDbContextAsync();
            var currentIds = await SqlCurrentSourceActivities.Query(current).Where(value => value.SourceRevisionId == seeded.RevisionId)
                .Select(value => value.Id).ToArrayAsync();
            Assert.DoesNotContain(historicalActivityId!.Value, currentIds);
            Assert.Contains(oldActivityId, currentIds);
            Assert.Empty(await branches.ReadPromotionCandidatesAsync(16, descriptor, CancellationToken.None));
            Assert.Empty(await branches.ClaimAsync("no-automatic-terminal-replay", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
        }
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await service.CommitAsync(mutation, preview.ConfirmationId, "policy-processor-" + kind + "-" + drift + "-" + officeGeneration + "-" + officeReason, "test", CancellationToken.None);
        if (previousOffice)
            await service.CommitAsync(mutation, preview.ConfirmationId, "policy-processor-" + kind + "-" + drift + "-" + officeGeneration + "-" + officeReason, "test", CancellationToken.None);
        var writer = new SqlRetainedArtifactWriter(factory, seeded.ArtifactRoot);
        async Task ChangeConfigurationAsync()
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.SourceRootConfigurations.SingleAsync(value => value.Id == seeded.RootId)).ConfigurationRevision++;
            await db.SaveChangesAsync();
        }
        async Task ExpireClaimAsync(Guid branchId)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.SourceProcessorBranches.SingleAsync(value => value.Id == branchId)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            if (kind == "csharp") Assert.Empty(await branches.ClaimCsharpCodeAsync("replacement-owner", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
            else Assert.Empty(await branches.ClaimAsync("replacement-owner", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
        }
        if (drift == "before-claim")
        {
            await ChangeConfigurationAsync();
            if (kind == "csharp") Assert.Empty(await branches.ClaimCsharpCodeAsync("stale-test", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
            else Assert.Empty(await branches.ClaimAsync("stale-test", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var current = await db.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId && value.DescriptorFingerprint == descriptor.ProcessorFingerprint);
                Assert.Equal("policy-binding-stale", current.Reason);
                Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await db.SourceProcessorBranches.SingleAsync(value => value.SourceActivityId == current.Id)).State);
            }
            var control = await SeedAsync(extension: extension, payload: bytes, classification: kind == "csharp" ? "AcceptedUtf8Text" : "DeferredCapability");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var activityId = Guid.NewGuid(); var stamp = DateTimeOffset.UtcNow;
                db.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = control.RevisionId, InputFingerprint = control.Hash,
                    ActivityKind = (int)descriptor.AcceptedActivityKind, ExecutionClass = (int)ExecutionClass.InProcess,
                    ProcessorVersion = descriptor.ProcessorVersion, DescriptorFingerprint = descriptor.ProcessorFingerprint, State = (int)SourceActivityState.Pending,
                    CreatedAtUtc = stamp, UpdatedAtUtc = stamp });
                db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = activityId, SourceRevisionId = control.RevisionId,
                    InputSha256 = control.Hash, ProcessorVersion = descriptor.ProcessorVersion, ProcessorFingerprint = descriptor.ProcessorFingerprint,
                    State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = stamp, UpdatedAtUtc = stamp });
                await db.SaveChangesAsync();
            }
            using (var controlReader = new SqlRetainedSourceReader(factory, control.ArtifactRoot))
            {
                if (kind == "csharp")
                {
                    var next = Assert.Single(await branches.ClaimCsharpCodeAsync("control-owner", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
                    Assert.Equal(control.RevisionId, next.SourceRevisionId.Value);
                    var result = await new RetainedCsharpCodeProcessor(controlReader, new LocalPrivateContentDisclosure()).ProcessAsync(next, CancellationToken.None);
                    Assert.True((await branches.CompleteRetainedCsharpCodeAsync(next, result, CancellationToken.None)).IsCommitted);
                }
                else
                {
                    var next = Assert.Single(await branches.ClaimAsync("control-owner", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
                    Assert.Equal(control.RevisionId, next.SourceRevisionId.Value);
                    var result = await new OoxmlStructuralTextProcessor(new SqlRetainedArtifactWriter(factory, control.ArtifactRoot)).ProcessAsync(next,
                        await controlReader.ReadBytesAsync(next.SourceRevisionId, CancellationToken.None), new RetainedProcessorOptions(), CancellationToken.None);
                    Assert.True(await branches.CommitAsync(next, result, CancellationToken.None));
                    foreach (var member in result.Members) if (member.PublicationLease is not null) await member.PublicationLease.DisposeAsync();
                }
            }
            var renewed = Mutation(seeded, 2);
            var renewalPreview = await service.PreviewAsync(renewed, "test", CancellationToken.None);
            await service.CommitAsync(renewed, renewalPreview.ConfirmationId, "policy-renew-" + kind + "-" + officeGeneration + "-" + officeReason, "test", CancellationToken.None);
        }
        if (kind == "csharp")
        {
            var claim = Assert.Single(await branches.ClaimCsharpCodeAsync("policy-test", 16, descriptor.ProcessorFingerprint, CancellationToken.None));
            if (drift is "after-claim" or "expired-claim") await ChangeConfigurationAsync();
            if (drift == "expired-claim") await ExpireClaimAsync(claim.BranchId);
            var completion = await new RetainedCsharpCodeProcessor(reader, new LocalPrivateContentDisclosure()).ProcessAsync(claim, CancellationToken.None);
            Assert.Equal(drift is not ("after-claim" or "expired-claim"), (await branches.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None)).IsCommitted);
        }
        else
        {
            var claim = Assert.Single(await branches.ClaimAsync("policy-test", 16, descriptor.ProcessorFingerprint, CancellationToken.None));
            if (drift is "after-claim" or "expired-claim") await ChangeConfigurationAsync();
            if (drift == "expired-claim") await ExpireClaimAsync(claim.BranchId);
            RetainedProcessorCompletion completion;
            if (kind == "media")
            {
                await using var retained = await reader.OpenReadAsync(claim.SourceRevisionId, MediaMetadataRetainedProcessor.MaximumRetainedInputBytes, CancellationToken.None);
                completion = await new MediaMetadataRetainedProcessor(writer, new LocalPrivateContentDisclosure()).ProcessAsync(claim, retained, CancellationToken.None);
            }
            else
            {
                var retained = await reader.ReadBytesAsync(claim.SourceRevisionId, CancellationToken.None);
                completion = kind == "office" ? await new OoxmlStructuralTextProcessor(writer).ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None) :
                    await new ZipArchiveRetainedProcessor(writer).ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None);
            }
            Assert.Equal(drift is not ("after-claim" or "expired-claim"), await branches.CommitAsync(claim, completion, CancellationToken.None));
            foreach (var member in completion.Members)
                if (member.PublicationLease is not null) await member.PublicationLease.DisposeAsync();
        }
        await using var verification = await factory.CreateDbContextAsync();
        var relation = await verification.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == oldActivityId);
        var successor = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceActivityId == relation.SuccessorActivityId);
        Assert.Equal((int)(drift is "after-claim" or "expired-claim" ? RetainedProcessorBranchState.Blocked : RetainedProcessorBranchState.Completed), successor.State);
        if (drift is "after-claim" or "expired-claim")
        {
            Assert.False(await verification.SourceProcessorBranchMembers.AnyAsync(value => value.BranchId == successor.Id));
            Assert.Null(successor.LeaseOwner);
            Assert.Null(successor.LeaseExpiresAtUtc);
            Assert.Equal("policy-binding-stale", (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == successor.Id)).OutcomeCode);
            var cannotRenew = Mutation(seeded, 2);
            var renewal = await service.PreviewAsync(cannotRenew, "test", CancellationToken.None);
            var error = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(cannotRenew, renewal.ConfirmationId, "executed-renew-" + kind + "-" + drift + "-" + officeGeneration + "-" + officeReason, "test", CancellationToken.None).AsTask());
            Assert.Equal("policy-successor-conflict", error.ReasonCode);
        }
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == oldBranchId)).State);
        Assert.Equal(reason, (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == oldBranchId)).OutcomeCode);
        if (historicalActivityId is { } historyIdToVerify)
        {
            var historicalRelation = await verification.SourceActivityRelations.SingleAsync(value => value.Id == historicalRelationId);
            Assert.Equal(historyIdToVerify, historicalRelation.PredecessorActivityId);
            Assert.Equal(oldActivityId, historicalRelation.SuccessorActivityId);
            Assert.Single(await verification.SourceActivityRelations.Where(value => value.PredecessorActivityId == oldActivityId).ToArrayAsync());
            Assert.Equal((int)SourceActivityState.FailedTerminal, (await verification.SourceActivities.SingleAsync(value => value.Id == historyIdToVerify)).State);
            Assert.Equal((int)SourceActivityState.FailedTerminal, (await verification.SourceActivities.SingleAsync(value => value.Id == oldActivityId)).State);
            var currentIds = await SqlCurrentSourceActivities.Query(verification).Where(value => value.SourceRevisionId == seeded.RevisionId)
                .Select(value => value.Id).ToArrayAsync();
            Assert.DoesNotContain(historyIdToVerify, currentIds);
            Assert.DoesNotContain(oldActivityId, currentIds);
        }
        Assert.DoesNotContain(await SqlCurrentSourceActivities.Query(verification).Where(value => value.SourceRevisionId == seeded.RevisionId).ToArrayAsync(), value => value.Id == oldActivityId);
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Historical_v2_policy_successor_hides_only_its_exact_preserved_v1_failure(bool completed, bool inputChanged)
    {
        var seeded = await SeedAsync(extension: ".docx", payload: Word(), classification: "DeferredCapability");
        var predecessorId = Guid.NewGuid(); var successorId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var original = await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            original.State = (int)SourceActivityState.CancelledSuperseded;
            original.Reason = RetainedDefaultPolicy.RelationshipKind;
            db.SourceActivities.AddRange(
                new SourceActivityEntity { Id = predecessorId, SourceRevisionId = seeded.RevisionId,
                    ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                    ProcessorVersion = "phase-5-ooxml-structural-v1", DescriptorFingerprint = "phase-5-ooxml-retained-structural-v1",
                    InputFingerprint = seeded.Hash, State = (int)SourceActivityState.FailedTerminal,
                    Reason = "office-document-part-unsupported", CreatedAtUtc = now, UpdatedAtUtc = now },
                new SourceActivityEntity { Id = successorId, SourceRevisionId = seeded.RevisionId,
                    ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                    ProcessorVersion = "phase-5-ooxml-structural-v2", DescriptorFingerprint = "phase-5-ooxml-retained-structural-v2",
                    InputFingerprint = inputChanged ? new string('b', 64) : seeded.Hash,
                    State = (int)(completed ? SourceActivityState.Completed : SourceActivityState.FailedTerminal),
                    Reason = completed ? null : "office-document-part-unsupported", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(),
                PredecessorActivityId = predecessorId, SuccessorActivityId = successorId,
                RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now });
            await db.SaveChangesAsync();
        }
        await using var verification = await factory.CreateDbContextAsync();
        var currentIds = await SqlCurrentSourceActivities.Query(verification).Where(value => value.SourceRevisionId == seeded.RevisionId)
            .Select(value => value.Id).ToArrayAsync();
        Assert.Contains(successorId, currentIds);
        Assert.Equal(inputChanged, currentIds.Contains(predecessorId));
        Assert.Equal((int)SourceActivityState.FailedTerminal, (await verification.SourceActivities.SingleAsync(value => value.Id == predecessorId)).State);
    }

    private sealed record DescriptorHandler(SourceCapabilityDescriptor Descriptor) : ILocalSourceCapabilityHandler;

    private static byte[] Zip(string name, byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry(name).Open()) entry.Write(bytes);
        return output.ToArray();
    }
    private static byte[] Word()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
            Add("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            Add("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            Add("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>safe policy text</w:t></w:r></w:p></w:body></w:document>");
        }
        return output.ToArray();
    }
    private static byte[] Movie()
    {
        var bytes = new byte[140];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 24); "ftyp"u8.CopyTo(bytes.AsSpan(4)); "isom"u8.CopyTo(bytes.AsSpan(8)); "isom"u8.CopyTo(bytes.AsSpan(16)); "mp41"u8.CopyTo(bytes.AsSpan(20));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24), 116); "moov"u8.CopyTo(bytes.AsSpan(28));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), 108); "mvhd"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(52), 1000); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(56), 2000);
        return bytes;
    }

    [NativeSqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Activation_gate_rejects_old_runnable_work_and_accepts_terminal_history(int officeGeneration)
    {
        var seeded = await SeedAsync(); var branchId = Guid.NewGuid();
        await using (var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync())
        {
            var activity = await db.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            // Use a distinct activity because its immutable descriptor cannot be edited.
            var pending = new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = seeded.RevisionId,
                ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = $"phase-5-ooxml-structural-v{officeGeneration}",
                DescriptorFingerprint = $"phase-5-ooxml-retained-structural-v{officeGeneration}",
                InputFingerprint = seeded.Hash, State = (int)SourceActivityState.Pending, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
            db.SourceActivities.Add(pending);
            db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = branchId, SourceActivityId = pending.Id, SourceRevisionId = seeded.RevisionId,
                InputSha256 = seeded.Hash, ProcessorVersion = pending.ProcessorVersion, ProcessorFingerprint = pending.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.False(await SqlRetainedPolicyCompatibility.HasNoLegacyRunnableWorkAsync(SqlTestData.CreateFactory(fixture), CancellationToken.None));
        await using (var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync())
        {
            var branch = await db.SourceProcessorBranches.SingleAsync(value => value.Id == branchId);
            branch.State = (int)RetainedProcessorBranchState.Blocked;
            // A historical blocked branch with an old Pending parent is not runnable.
            await db.SaveChangesAsync();
        }
        Assert.True(await SqlRetainedPolicyCompatibility.HasNoLegacyRunnableWorkAsync(SqlTestData.CreateFactory(fixture), CancellationToken.None));
    }

    private async Task<Seed> SeedAsync(long revision = 1, string extension = ".rdl", byte[]? payload = null, string classification = "Unknown",
        bool withPhysicalHistory = false, bool suppressPrevious = true, bool differentHistoryIdentity = false, int originKind = 0)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable)!, "policy-artifacts", Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original-must-not-read");
        var bytes = payload ?? Encoding.UTF8.GetBytes("<Report>safe retained policy sample</Report>");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
        await File.WriteAllBytesAsync(Path.Combine(root, relative), bytes);
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = original, DisplayName = "Policy test", State = 0,
            Recursive = true, MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes, AllowedClassificationsJson = "[\"text/plain\",\"text/x-source-code\"]",
            ConfigurationRevision = 1, ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now });
        var current = new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, CanonicalPath = Path.Combine(original, "report" + extension),
            StableSourceIdentity = $"policy-test:{revisionId:N}", ContentSha256 = hash, Classification = classification, Extension = extension, ByteLength = bytes.Length,
            Revision = revision, DiscoveredAtUtc = now };
        Guid? previousId = withPhysicalHistory ? SqlTestData.AddPhysicalRevisionHistory(db, current, suppressPrevious, differentHistoryIdentity) : null;
        current.OriginKind = originKind;
        db.SourceRevisions.Add(current);
        db.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash,
            StoreRelativePath = relative, ByteLength = bytes.Length, ReferenceCount = 1, ChecksumVerifiedAtUtc = now });
        db.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, State = (int)SourceActivityState.DeferredPolicy, ProcessorVersion = "phase-3a-v1",
            InputFingerprint = hash, Reason = "File extension is unknown to local source ingestion.", CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
        return new Seed(rootId, revisionId, activityId, hash, root, original, previousId);
    }
    private sealed record Seed(Guid RootId, Guid RevisionId, Guid ActivityId, string Hash, string ArtifactRoot, string OriginalRoot, Guid? PreviousId = null);
    private sealed class NoOriginalIoPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new InvalidOperationException("Original source I/O is forbidden in retained replay.");
    }
}
