using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Integrations.Outlook;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Disposable SQL proof that media manifests only use app-owned retained bytes and generic branch fencing.</summary>
public sealed class MediaMetadataReplayIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly FluxKnowledge.Testing.TestResources _resources = new();
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => ClearMediaDataAsync();
    public Task DisposeAsync() => ClearMediaDataAsync();

    [NativeSqlServerTheory]
    [InlineData(".bin", false)]
    [InlineData(".bin", true)]
    [InlineData("", true)]
    public async Task Verified_opaque_gif_is_processed_once_even_if_previously_blocked(string extension, bool previouslyBlocked)
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
            var seeded = await SeedBytesAsync(privateRoot, gif, extension, "opaque");
            await using var verification = CreateContext();
            await verification.SourceRevisions.Where(revision => revision.Id == seeded.RevisionId)
                .ExecuteUpdateAsync(update => update.SetProperty(revision => revision.Classification, "DeferredCapability"));
            await verification.SourceActivities.Where(activity => activity.Id == seeded.ActivityId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(activity => activity.State, (int)(previouslyBlocked
                        ? SourceActivityState.DeferredPolicy : SourceActivityState.DeferredUnsupported))
                    .SetProperty(activity => activity.Reason, previouslyBlocked
                        ? "media-metadata-signature-mismatch" : "media-metadata-extraction-pending"));

            var activation = CreateActivation(privateRoot, enabled: true);
            var result = await activation.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, result.CompletedBranches);
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).ClaimedBranches);
            if (previouslyBlocked)
            {
                var old = await verification.SourceActivities.SingleAsync(activity => activity.Id == seeded.ActivityId);
                Assert.Equal((int)SourceActivityState.CancelledSuperseded, old.State);
                Assert.Equal("media-metadata-signature-mismatch", old.Reason);
            }
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerTheory]
    [InlineData(".bin", true, true)]
    [InlineData(".gif", true, true)]
    [InlineData(".gif", true, false)]
    [InlineData(".gif", false, true)]
    public async Task Outlook_skips_single_pixel_images_without_discarding_larger_images(
        string extension, bool captured, bool singlePixel)
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
            if (!singlePixel) gif[6] = 2;
            var seeded = await SeedBytesAsync(privateRoot, gif, extension, "outlook-decoration");
            await using var verification = CreateContext();
            await AttachWatchedOutlookReceiptAsync(verification, privateRoot, seeded, extension, gif.Length, captured);
            if (extension == ".bin")
            {
                await verification.SourceRevisions.Where(value => value.Id == seeded.RevisionId)
                    .ExecuteUpdateAsync(update => update.SetProperty(value => value.Classification, "DeferredCapability"));
                await verification.SourceActivities.Where(value => value.Id == seeded.ActivityId)
                    .ExecuteUpdateAsync(update => update.SetProperty(value => value.Reason, "media-metadata-extraction-pending"));
            }

            var activation = CreateActivation(privateRoot, enabled: true);
            var activationResult = await activation.RunOnceAsync(CancellationToken.None);
            Assert.True(activationResult.CompletedBranches == 1,
                $"{activationResult}; activity: {await verification.SourceActivities.Where(value => value.Id == seeded.ActivityId).Select(value => value.Reason).SingleAsync()}");
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).ClaimedBranches);
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            var skipped = captured && singlePixel;
            Assert.Equal(skipped ? 0 : 1, branch.CompletedMemberCount);
            var member = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            Assert.Equal(skipped ? "skipped" : "completed", member.Disposition);
            Assert.Equal(skipped ? "outlook-decorative-image" : null, member.ReasonCode);
            Assert.Equal(!skipped, await verification.SourceRevisions.AnyAsync(value => value.ParentSourceRevisionId == seeded.RevisionId));
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Previously_published_outlook_pixel_metadata_requires_exact_single_frame_proof(bool singleFrame)
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var gif = singleFrame
                ? Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")
                : GifWithImageDirectories(2);
            var seeded = await SeedBytesAsync(privateRoot, gif, ".bin", "historical-outlook-decoration");
            await using var db = CreateContext();
            await db.SourceRevisions.Where(value => value.Id == seeded.RevisionId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Classification, "DeferredCapability"));
            await db.SourceActivities.Where(value => value.Id == seeded.ActivityId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.State, (int)SourceActivityState.DeferredPolicy)
                    .SetProperty(value => value.Reason, MediaMetadataRetainedProcessor.SignatureMismatch));

            var activation = CreateActivation(privateRoot, enabled: true);
            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var member = await db.SourceProcessorBranchMembers.SingleAsync(value => value.ChildSourceRevisionId != null);
            var child = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == member.ChildSourceRevisionId);
            var now = DateTimeOffset.UtcNow;
            var identityId = Guid.NewGuid();
            var recordId = Guid.NewGuid();
            db.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identityId, SourceKind = "retained local source", StableKey = $"media-test-finished:{identityId:N}", CreatedAtUtc = now
            });
            db.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId, SourceIdentityId = identityId, SourceRevisionId = child.Id, Revision = 1,
                ContentHash = child.ContentSha256, RootLineageRecordId = recordId, CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true, RegisteredAtUtc = now
            });
            await db.SaveChangesAsync();
            await db.SourceActivities.Where(value => value.Id == member.ChildSourceActivityId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.State, (int)SourceActivityState.Completed)
                    .SetProperty(value => value.ResultingPipelineRecordId, recordId)
                    .SetProperty(value => value.ResultingPipelineRecordRevision, 1L));
            await AttachWatchedOutlookReceiptAsync(db, privateRoot, seeded, ".bin", gif.Length, includeAttachment: true);

            var version = await db.IndexState.Select(value => value.CorpusVersion).SingleAsync();
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).ClaimedBranches);
            var withdrawn = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == child.Id);
            Assert.Equal(singleFrame, withdrawn.SuppressedAtUtc.HasValue);
            Assert.Equal(singleFrame ? OutlookDecorativeImage.RetentionEvidence : null, withdrawn.RetentionEvidenceJson);
            Assert.True(await db.SourceRevisions.AnyAsync(value => value.Id == seeded.RevisionId && value.SuppressedAtUtc == null));
            Assert.True(await db.PipelineRecords.AnyAsync(value => value.Id == recordId && !value.IsDeleted && value.CompletionCriteriaMet));
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).ClaimedBranches);
            Assert.Equal(version + (singleFrame ? 1 : 0), await db.IndexState.Select(value => value.CorpusVersion).SingleAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scoped_outlook_OCR_withdrawal_verifies_the_original_and_preserves_its_ownership(bool actualPixel)
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var png = Png();
            if (!actualPixel) png[19] = 2; // IHDR width.
            var seeded = await SeedBytesAsync(privateRoot, png, ".png", "historical-outlook-ocr");
            await using var db = CreateContext();
            await db.SourceRevisions.Where(value => value.Id == seeded.RevisionId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Classification, "DeferredCapability"));
            Assert.Equal(1, (await CreateActivation(privateRoot, enabled: false, enableImageOcr: true)
                .RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var member = await db.SourceProcessorBranchMembers.SingleAsync(value => value.ChildSourceRevisionId != null);
            var child = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == member.ChildSourceRevisionId);
            var now = DateTimeOffset.UtcNow;
            var identityId = Guid.NewGuid();
            var recordId = Guid.NewGuid();
            db.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identityId, SourceKind = "retained local source", StableKey = $"media-test-finished:{identityId:N}", CreatedAtUtc = now
            });
            db.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId, SourceIdentityId = identityId, SourceRevisionId = child.Id, Revision = 1,
                ContentHash = child.ContentSha256, RootLineageRecordId = recordId, CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true, RegisteredAtUtc = now
            });
            db.DocumentPublications.Add(new DocumentPublicationEntity
            {
                OwnerSourceRevisionId = seeded.RevisionId, DocumentInputSourceRevisionId = child.Id, SourceProcessorBranchId = member.BranchId,
                PipelineRecordId = recordId, PipelineRecordRevision = 1, ProcessorFingerprint = ImageDocumentInputProcessor.Capability.ProcessorFingerprint,
                PublishedAtUtc = now
            });
            db.DocumentOcrRequests.Add(new DocumentOcrRequestEntity
            {
                MiniTaskId = Guid.NewGuid(), ParentJobId = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1,
                RetainedSourceRevisionId = child.Id, ContentSha256 = child.ContentSha256, RequestedPageIndexesJson = "[0]",
                ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", State = (int)DocumentOcrRequestState.Requeued,
                ResultJson = """{"pages":[{"sourceWidth":1,"sourceHeight":1}]}""", ResultDigest = new byte[32],
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            await db.SourceActivities.Where(value => value.Id == member.ChildSourceActivityId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.State, (int)SourceActivityState.Completed)
                    .SetProperty(value => value.ResultingPipelineRecordId, recordId)
                    .SetProperty(value => value.ResultingPipelineRecordRevision, 1L));
            await AttachWatchedOutlookReceiptAsync(db, privateRoot, seeded, ".png", png.Length, includeAttachment: true);

            var factory = new ContextFactory(_fixture.ConnectionString);
            using var reader = new SqlRetainedSourceReader(factory, privateRoot);
            var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System, new FixtureSourceAdmission(factory));
            var approved = new OutlookDecorativePublicationTarget(seeded.RevisionId, child.Id, member.BranchId, recordId, 1);
            Assert.Equal(0, await store.ReconcileOutlookDecorativeImagesAsync(
                16, reader, CancellationToken.None, child.SourceRootId, approvedOcrTargets: [approved with { PipelineRecordRevision = 2 }]));
            var version = await db.IndexState.Select(value => value.CorpusVersion).SingleAsync();
            Assert.Equal(0, await store.ReconcileOutlookDecorativeImagesAsync(16, reader, CancellationToken.None, Guid.NewGuid(), approvedOcrTargets: [approved]));
            Assert.Equal(actualPixel ? 1 : 0, await store.ReconcileOutlookDecorativeImagesAsync(
                16, reader, CancellationToken.None, child.SourceRootId, verifyOnly: true, approvedOcrTargets: [approved]));
            Assert.Equal(version, await db.IndexState.Select(value => value.CorpusVersion).SingleAsync());
            Assert.Equal(actualPixel ? 1 : 0, await store.ReconcileOutlookDecorativeImagesAsync(
                16, reader, CancellationToken.None, child.SourceRootId, approvedOcrTargets: [approved]));
            Assert.Equal(0, await store.ReconcileOutlookDecorativeImagesAsync(16, reader, CancellationToken.None, child.SourceRootId, approvedOcrTargets: [approved]));
            Assert.Equal(actualPixel, (await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == child.Id)).SuppressedAtUtc.HasValue);
            Assert.True(await db.SourceRevisions.AnyAsync(value => value.Id == seeded.RevisionId && value.SuppressedAtUtc == null));
            Assert.True(await db.DocumentOcrRequests.AnyAsync(value => value.PipelineRecordId == recordId && value.ResultDigest != null));
            Assert.True(await db.DocumentPublications.AnyAsync(value => value.OwnerSourceRevisionId == seeded.RevisionId && value.PipelineRecordId == recordId));
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Earlier_unsupported_opaque_mismatches_clear_the_candidate_batch_without_reopening_named_mismatches()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            await using var verification = CreateContext();
            for (var index = 0; index < RetainedProcessorOptions.MaximumAutomaticReplayBatchSize; index++)
                await SeedBlockedAsync("OggS"u8.ToArray(), ".bin", $"unsupported-{index}");
            var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
            var accepted = await SeedBlockedAsync(gif, ".bin", "supported");
            var named = await SeedBlockedAsync(gif, ".png", "wrong-name");

            var activation = CreateActivation(privateRoot, enabled: true);
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            Assert.Equal(0, (await activation.RunOnceAsync(CancellationToken.None)).ClaimedBranches);
            Assert.Equal(16, await verification.SourceActivities.CountAsync(activity => activity.Reason == "media-metadata-format-unsupported"));
            Assert.Equal("media-metadata-signature-mismatch", (await verification.SourceActivities
                .SingleAsync(activity => activity.Id == named.ActivityId)).Reason);
            Assert.Equal(accepted.RevisionId, (await verification.SourceProcessorBranches.SingleAsync()).SourceRevisionId);

            async Task<(Guid ActivityId, Guid RevisionId, string Hash)> SeedBlockedAsync(byte[] bytes, string extension, string kind)
            {
                var seeded = await SeedBytesAsync(privateRoot, bytes, extension, kind);
                await verification.SourceRevisions.Where(revision => revision.Id == seeded.RevisionId)
                    .ExecuteUpdateAsync(update => update.SetProperty(revision => revision.Classification, "DeferredCapability"));
                await verification.SourceActivities.Where(activity => activity.Id == seeded.ActivityId)
                    .ExecuteUpdateAsync(update => update.SetProperty(activity => activity.State, (int)SourceActivityState.DeferredPolicy)
                        .SetProperty(activity => activity.Reason, "media-metadata-signature-mismatch"));
                return seeded;
            }
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Disabled_media_metadata_activation_is_inert()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "disabled");

            var result = await CreateActivation(privateRoot, enabled: false).RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_activation_durably_blocks_only_a_recognised_unsupported_media_format()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var unsupported = await SeedBytesAsync(privateRoot, IsoBmff("avif"), ".avif", "unsupported");
            var unrelated = await SeedBytesAsync(privateRoot, Png(), ".png", "unrelated");

            _ = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            await using var verification = CreateContext();
            var unsupportedActivity = await verification.SourceActivities.SingleAsync(value => value.Id == unsupported.ActivityId);
            var unrelatedActivity = await verification.SourceActivities.SingleAsync(value => value.Id == unrelated.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, unsupportedActivity.State);
            Assert.Equal("media-metadata-format-unsupported", unsupportedActivity.Reason);
            Assert.NotEqual((int)SourceActivityState.DeferredPolicy, unrelatedActivity.State);
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Parser_failure_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "parser-failure");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new FailingParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-parser-failed");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unexpected_preflight_failure_defers_media_with_the_exact_unavailable_outcome()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "unexpected-preflight");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new UnexpectedPreflightParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
            Assert.Equal("media-metadata-parser-unavailable", activity.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unsignalled_preflight_cancellation_defers_media_with_the_exact_unavailable_outcome()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "unsignalled-preflight-cancellation");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new UnsignalledCancellationPreflightParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
            Assert.Equal("media-metadata-parser-unavailable", activity.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unexpected_parser_failure_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "unexpected-parser");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new UnexpectedParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-parser-failed");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unsignalled_parser_cancellation_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "unsignalled-parser-cancellation");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new UnsignalledCancellationParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-parser-failed");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Parser_read_limit_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedBytesAsync(privateRoot, PngWithReadBudgetPadding(), ".png", "read-limit");
            var parser = new ReadLimitParser();
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), parser);

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-read-limit");
            Assert.InRange(parser.ReadCalls, 1, 256);
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Parser_directory_limit_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedBytesAsync(privateRoot, GifWithImageDirectories(MediaMetadataRetainedProcessor.MaximumMetadataDirectories + 1), ".gif", "directory-limit");

            var result = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-parser-failed");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Parser_output_limit_is_durably_blocked_without_a_derived_child()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "output-limit");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new OversizedOutputParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-output-limit");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Disclosure_withholding_prevents_durable_manifest_writes()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "disclosure-withheld");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new WithholdingDisclosure());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-secret-content-withheld");
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_activation_blocks_aac_and_incomplete_id3_with_the_exact_signature_mismatch_outcome()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var aac = await SeedBytesAsync(privateRoot, [0xff, 0xf1, 0x50, 0x80], ".mp3", "aac-signature");
            var truncatedId3 = await SeedBytesAsync(privateRoot, "ID3\x04\0\0"u8.ToArray(), ".mp3", "truncated-id3");
            var bareId3 = await SeedBytesAsync(privateRoot, "ID3\x04\0\0\0\0\0\0"u8.ToArray(), ".mp3", "bare-id3");

            _ = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            await using var verification = CreateContext();
            foreach (var activityId in new[] { aac.ActivityId, truncatedId3.ActivityId, bareId3.ActivityId })
            {
                var activity = await verification.SourceActivities.SingleAsync(value => value.Id == activityId);
                Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
                Assert.Equal("media-metadata-signature-mismatch", activity.Reason);
            }
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_activation_blocks_incomplete_and_non_layer_three_mp3_headers_with_the_exact_signature_mismatch_outcome()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var bare = await SeedBytesAsync(privateRoot, [0xff, 0xfb, 0x90, 0x00], ".mp3", "bare-mp3");
            var id3Truncated = await SeedBytesAsync(privateRoot, "ID3\x03\0\0\0\0\0\0"u8.ToArray().Concat(new byte[] { 0xff, 0xfb, 0x90, 0x00 }).ToArray(), ".mp3", "id3-truncated-mp3");
            var layerOne = await SeedBytesAsync(privateRoot, MpegHeaderWithBody(0xff), ".mp3", "layer-one-mp3");
            var layerTwo = await SeedBytesAsync(privateRoot, MpegHeaderWithBody(0xfd), ".mp3", "layer-two-mp3");

            _ = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            await using var verification = CreateContext();
            foreach (var activityId in new[] { bare.ActivityId, id3Truncated.ActivityId, layerOne.ActivityId, layerTwo.ActivityId })
            {
                var activity = await verification.SourceActivities.SingleAsync(value => value.Id == activityId);
                Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
                Assert.Equal("media-metadata-signature-mismatch", activity.Reason);
            }
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_activation_blocks_signature_confirmed_aac_and_wma_as_unsupported_media()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var aac = await SeedBytesAsync(privateRoot, AacAdts(), ".aac", "aac-unsupported");
            var wma = await SeedBytesAsync(privateRoot, AsfHeader(), ".wma", "wma-unsupported");

            _ = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            await using var verification = CreateContext();
            foreach (var activityId in new[] { aac.ActivityId, wma.ActivityId })
            {
                var activity = await verification.SourceActivities.SingleAsync(value => value.Id == activityId);
                Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
                Assert.Equal("media-metadata-format-unsupported", activity.Reason);
            }
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_activation_replays_only_the_generic_sixteen_claim_ceiling_and_is_idempotent()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeds = new List<(Guid ActivityId, Guid RevisionId, string Hash)>();
            for (var index = 0; index < 17; index++) seeds.Add(await SeedPngAsync(privateRoot, $"ceiling-{index}"));

            var first = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);
            var second = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);
            var third = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal("media-metadata", first.Capability);
            Assert.Equal(16, first.CompletedBranches);
            Assert.Equal(1, second.CompletedBranches);
            Assert.Equal(0, third.CompletedBranches);
            await using var verification = CreateContext();
            foreach (var seed in seeds)
            {
                var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seed.RevisionId);
                Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
                var child = await verification.SourceRevisions.SingleAsync(value => value.ParentSourceRevisionId == seed.RevisionId);
                Assert.Equal(3, child.OriginKind);
                Assert.StartsWith("retained-media-metadata:", child.CanonicalPath, StringComparison.Ordinal);
                Assert.Single(await verification.SourceActivities.Where(value => value.SourceRevisionId == child.Id && value.ActivityKind == (int)SourceActivityKind.TextExtraction).ToListAsync());
            }
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_media_replay_persists_one_canonical_manifest_with_immutable_parent_provenance_and_standard_text_index_link(bool padded)
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedBytesAsync(privateRoot, padded ? PngWithReadBudgetPadding() : Png(), ".png", "manifest");

            Assert.Equal(1, (await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None)).CompletedBranches);
            Assert.Equal(0, (await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None)).CompletedBranches);

            await using var verification = CreateContext();
            var parent = await verification.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId);
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            var member = Assert.Single(await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == branch.Id).ToListAsync());
            var child = await verification.SourceRevisions.SingleAsync(value => value.Id == member.ChildSourceRevisionId);
            var artifact = await verification.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.Id);
            var branchActivity = await verification.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
            var textIndex = await verification.SourceActivities.SingleAsync(value => value.Id == member.ChildSourceActivityId);
            var supersession = await verification.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.ActivityId && value.SuccessorActivityId == branchActivity.Id);
            var manifest = await File.ReadAllTextAsync(Path.Combine(privateRoot, artifact.StoreRelativePath));

            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            Assert.Equal(1, branch.CompletedMemberCount);
            Assert.Equal(parent.Id, child.ParentSourceRevisionId);
            Assert.Equal(parent.SourceRootId, child.SourceRootId);
            Assert.Equal(3, child.OriginKind);
            Assert.StartsWith("retained-media-metadata:", child.CanonicalPath, StringComparison.Ordinal);
            Assert.Equal(".json", child.Extension);
            Assert.Equal("AcceptedUtf8Text", child.Classification);
            Assert.Equal("completed", member.Disposition);
            Assert.Equal(child.Id, member.ChildSourceRevisionId);
            Assert.Equal(textIndex.Id, member.ChildSourceActivityId);
            Assert.Equal((int)SourceActivityState.CancelledSuperseded, (await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
            Assert.Equal("superseded-by-retained-processor", supersession.RelationshipKind);
            Assert.Equal((int)SourceActivityKind.TextExtraction, textIndex.ActivityKind);
            Assert.Equal((int)ExecutionClass.InProcess, textIndex.ExecutionClass);
            Assert.Equal("phase-3a-v1", textIndex.ProcessorVersion);
            Assert.Equal(artifact.ContentSha256, textIndex.InputFingerprint);
            Assert.Equal((int)SourceActivityState.Pending, textIndex.State);
            Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"png\",\"container\":\"png\",\"dimensions\":{\"width\":1,\"height\":1},\"duration_ms\":null,\"audio\":null}", manifest);
            Assert.Equal(1, await verification.SourceRevisions.CountAsync(value => value.ParentSourceRevisionId == seeded.RevisionId));
            Assert.Equal(1, await verification.SourceProcessorBranchMembers.CountAsync(value => value.BranchId == branch.Id));
            Assert.Equal(1, await verification.SourceActivities.CountAsync(value => value.SourceRevisionId == child.Id && value.ActivityKind == (int)SourceActivityKind.TextExtraction));
            Assert.Equal(1, await verification.AuditEvents.CountAsync(value => value.SourceRevisionId == seeded.RevisionId && value.EventType == "retained_processor.completed"));
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unavailable_preflight_keeps_a_valid_media_candidate_deferred_and_a_later_healthy_run_replays_it()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "unavailable");
            var unavailable = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot),
                new AllowingDisclosure(), new UnavailableParser());

            var deferred = await CreateActivation(privateRoot, enabled: true, unavailable).RunOnceAsync(CancellationToken.None);

            Assert.False(deferred.Enabled);
            await using (var verification = CreateContext())
            {
                var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
                Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
                Assert.Equal("media-metadata-parser-unavailable", activity.Reason);
                Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
            }

            var replayed = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, replayed.CompletedBranches);
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Promotion_records_exact_missing_checksum_and_signature_terminal_outcomes_without_source_original_access()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var missingBytes = new byte[Png().Length + 1];
            Png().CopyTo(missingBytes, 0);
            var missingHash = Hash(missingBytes);
            var missing = await SeedDeferredAsync(missingHash, missingBytes.Length, Path.Combine("sha256", missingHash[..2], $"{missingHash}.bin"), ".png", "missing");
            var corrupted = await SeedPngAsync(privateRoot, "corrupted");
            var mismatchBytes = new byte[] { 0xff, 0xd8, 0xff, 0xe0 };
            var mismatch = await SeedBytesAsync(privateRoot, mismatchBytes, ".png", "mismatch");
            await using (var corrupt = CreateContext())
            {
                var artifact = await corrupt.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == corrupted.RevisionId);
                await File.WriteAllBytesAsync(Path.Combine(privateRoot, artifact.StoreRelativePath), new byte[] { 0x00 });
            }

            var result = await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, result.CompletedBranches);
            await using var verification = CreateContext();
            Assert.Equal("retained-artifact-missing", (await verification.SourceActivities.SingleAsync(value => value.Id == missing.ActivityId)).Reason);
            Assert.Equal("retained-artifact-checksum-invalid", (await verification.SourceActivities.SingleAsync(value => value.Id == corrupted.ActivityId)).Reason);
            Assert.Equal("media-metadata-signature-mismatch", (await verification.SourceActivities.SingleAsync(value => value.Id == mismatch.ActivityId)).Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == missing.RevisionId || value.SourceRevisionId == corrupted.RevisionId || value.SourceRevisionId == mismatch.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Healthy_promotion_terminally_blocks_an_oversized_media_artifact_before_any_retained_byte_read()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var hash = new string('a', 64);
            var seeded = await SeedDeferredAsync(hash, checked((int)MediaMetadataRetainedProcessor.MaximumInputBytes + 1),
                Path.Combine("sha256", hash[..2], $"{hash}.bin"), ".png", "oversized-healthy");
            var reader = new OversizedInspectionReader(new SourceRevisionId(seeded.RevisionId), hash);

            var result = await CreateActivation(privateRoot, enabled: true, retainedReader: reader).RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, result.CompletedBranches);
            Assert.Equal(0, reader.ReadCount);
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
            Assert.Equal("media-metadata-input-too-large", activity.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Unavailable_preflight_terminally_blocks_an_oversized_media_artifact_before_any_retained_byte_read()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var hash = new string('b', 64);
            var seeded = await SeedDeferredAsync(hash, checked((int)MediaMetadataRetainedProcessor.MaximumInputBytes + 1),
                Path.Combine("sha256", hash[..2], $"{hash}.bin"), ".png", "oversized-unavailable");
            var reader = new OversizedInspectionReader(new SourceRevisionId(seeded.RevisionId), hash);
            var unavailable = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot),
                new AllowingDisclosure(), new UnavailableParser());

            var result = await CreateActivation(privateRoot, enabled: true, unavailable, reader).RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            Assert.Equal(0, reader.ReadCount);
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
            Assert.Equal("media-metadata-input-too-large", activity.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Claimed_oversized_media_branch_fails_before_any_retained_byte_read()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var hash = new string('c', 64);
            var seeded = await SeedDeferredAsync(hash, checked((int)MediaMetadataRetainedProcessor.MaximumInputBytes + 1),
                Path.Combine("sha256", hash[..2], $"{hash}.bin"), ".png", "oversized-claim");
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System, sourceAdmission: new global::FluxKnowledge.Integration.Tests.Support.FixtureSourceAdmission(new ContextFactory(_fixture.ConnectionString)));
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".png"),
                MediaMetadataRetainedProcessor.Capability, CancellationToken.None));
            var reader = new OversizedInspectionReader(new SourceRevisionId(seeded.RevisionId), hash);

            var result = await CreateActivation(privateRoot, enabled: true, retainedReader: reader).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.ClaimedBranches);
            Assert.Equal(1, result.FailedBranches);
            Assert.Equal(0, reader.ReadCount);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
            Assert.Equal("media-metadata-input-too-large", (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch.Id)).OutcomeCode);
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Media_branch_rejects_a_stale_lease_fence_and_keeps_supersession_provenance_singleton()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "fence");
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System, sourceAdmission: new global::FluxKnowledge.Integration.Tests.Support.FixtureSourceAdmission(new ContextFactory(_fixture.ConnectionString)));
            var candidate = new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), seeded.Hash, ".png");
            Assert.True(await store.PromoteAsync(candidate, MediaMetadataRetainedProcessor.Capability, CancellationToken.None));
            Assert.False(await store.PromoteAsync(candidate, MediaMetadataRetainedProcessor.Capability, CancellationToken.None));
            var claim = Assert.Single(await store.ClaimAsync("media-fence", 1, MediaMetadataRetainedProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
            await using (var replacement = CreateContext())
            {
                var branch = await replacement.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId);
                branch.LeaseOwner = "replacement";
                branch.LeaseGeneration++;
                branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                await replacement.SaveChangesAsync();
            }

            Assert.False(await store.CommitAsync(claim, new RetainedProcessorCompletion([], new string('a', 64)), CancellationToken.None));
            Assert.False(await store.FailAsync(claim, new RetainedProcessorFailure("stale", []), CancellationToken.None));
            await using var verification = CreateContext();
            Assert.Single(await verification.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToListAsync());
            Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    [NativeSqlServerFact]
    public async Task Cancellation_retries_the_media_branch_without_losing_its_supersession_fence()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "cancelled");
            using var cancellation = new CancellationTokenSource();
            var reader = new CancellingReader(_resources.Own(new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), privateRoot)), cancellation);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateActivation(privateRoot, enabled: true, retainedReader: reader).RunOnceAsync(cancellation.Token).AsTask());

            await using (var afterCancellation = CreateContext())
            {
                var branch = await afterCancellation.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
                Assert.Equal((int)RetainedProcessorBranchState.Pending, branch.State);
                Assert.Single(await afterCancellation.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToListAsync());
            }

            Assert.Equal(1, (await CreateActivation(privateRoot, enabled: true).RunOnceAsync(CancellationToken.None)).CompletedBranches);

            await using var completed = CreateContext();
            var completedBranch = await completed.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, completedBranch.State);
            Assert.Single(await completed.SourceProcessorBranchMembers.Where(value => value.BranchId == completedBranch.Id).ToListAsync());
            Assert.Single(await completed.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.RevisionId).ToListAsync());
            Assert.Single(await completed.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToListAsync());
            Assert.Equal(1, await completed.AuditEvents.CountAsync(value => value.SourceRevisionId == seeded.RevisionId && value.EventType == "retained_processor.completed"));
        }
        finally { DeletePrivateRoot(privateRoot); }
    }

    private async Task AttachWatchedOutlookReceiptAsync(FluxKnowledgeDbContext context, string privateRoot,
        (Guid ActivityId, Guid RevisionId, string Hash) seeded, string extension, int bytes, bool includeAttachment)
    {
        var capture = await SeedPngAsync(privateRoot, "capture-binding");
        var root = await context.SourceRootConfigurations.SingleAsync(value => context.SourceRevisions
            .Any(revision => revision.Id == seeded.RevisionId && revision.SourceRootId == value.Id));
        var captureRootId = await context.SourceRevisions.Where(value => value.Id == capture.RevisionId)
            .Select(value => value.SourceRootId).SingleAsync();
        await context.SourceActivities.Where(value => value.Id == capture.ActivityId).ExecuteDeleteAsync();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var exportId = Guid.NewGuid();
        var relativePath = Path.Combine("attachments", "001-image" + extension);
        context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
        {
            Id = profileId, SourceRootId = captureRootId, WatchedSourceRootId = root.Id,
            WatchedRootPath = root.CanonicalPath, WatchedStagingPath = root.CanonicalPath + "-staging",
            WatchedRootIdentity = new string('a', 64), WatchedStagingIdentity = new string('b', 64),
            DisplayName = "Synthetic Outlook profile", SpoolRoot = privateRoot, IsEnabled = true,
            ConfigurationRevision = 1, CadenceTicks = TimeSpan.FromMinutes(5).Ticks,
            MaximumOverlapTicks = TimeSpan.FromMinutes(1).Ticks, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
        {
            Id = folderId, ProfileId = profileId, StoreId = "synthetic-store", FolderEntryId = "synthetic-folder", DisplayName = "Inbox"
        });
        context.OutlookCaptureExports.Add(new OutlookCaptureExportEntity
        {
            Id = exportId, ProfileId = profileId, FolderId = folderId, EntryId = "synthetic-mail",
            SourceFingerprint = new string('f', 64), State = (int)OutlookExportState.Ingested, WatchedWithdrawalAtUtc = now,
            WatchedPreparedBundleJson = JsonSerializer.Serialize(new OutlookWatchedBundleReceipt(exportId, Guid.NewGuid(),
                includeAttachment ? [new(relativePath, seeded.Hash, bytes)] : []))
        });
        await context.SaveChangesAsync();
        await context.SourceRevisions.Where(value => value.Id == seeded.RevisionId)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.CanonicalPath,
                Path.Combine(root.CanonicalPath, exportId.ToString("N"), relativePath)));
    }

    private RetainedProcessorActivationService CreateActivation(string privateRoot, bool enabled, MediaMetadataRetainedProcessor? media = null,
        IRetainedSourceReader? retainedReader = null, bool enableImageOcr = false)
    {
        var factory = new ContextFactory(_fixture.ConnectionString);
        var writer = new SqlRetainedArtifactWriter(factory, privateRoot);
        var zip = new ZipArchiveRetainedProcessor(writer);
        return new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new MediaMetadataCapabilityHandler(), new ImageDocumentInputCapabilityHandler()])),
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System, sourceAdmission: new global::FluxKnowledge.Integration.Tests.Support.FixtureSourceAdmission(factory)), retainedReader ?? _resources.Own(new SqlRetainedSourceReader(factory, privateRoot)), zip,
            new RetainedProcessorOptions { MediaMetadataEnabled = enabled, ImageDocumentOcrEnabled = enableImageOcr }, TimeProvider.System,
            mediaMetadataProcessor: media ?? new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure()),
            imageProcessor: new ImageDocumentInputProcessor());
    }

    private async Task<(Guid ActivityId, Guid RevisionId, string Hash)> SeedPngAsync(string privateRoot, string sourceKind) =>
        await SeedBytesAsync(privateRoot, Png(), ".png", sourceKind);

    private async Task<(Guid ActivityId, Guid RevisionId, string Hash)> SeedBytesAsync(string privateRoot, byte[] bytes, string extension, string sourceKind)
    {
        var hash = Hash(bytes);
        var path = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(Path.Combine(privateRoot, path), bytes);
        var seeded = await SeedDeferredAsync(hash, bytes.Length, path, extension, sourceKind);
        return (seeded.ActivityId, seeded.RevisionId, hash);
    }

    private async Task<(Guid ActivityId, Guid RevisionId)> SeedDeferredAsync(string hash, int byteLength, string relativePath, string extension, string sourceKind)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\phase5-media-{sourceKind}-{rootId:N}", DisplayName = sourceKind,
            State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = MediaMetadataRetainedProcessor.MaximumInputBytes,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"media:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = $"C:\\source-original-must-not-be-read{extension}", Classification = "MediaMetadata", Extension = extension, ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.TextExtraction,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (activityId, revisionId);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private async Task AssertNoDerivedChildAfterFailureAsync(Guid sourceRevisionId, string outcomeCode)
    {
        await using var verification = CreateContext();
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == sourceRevisionId);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
        Assert.Equal(outcomeCode, (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch.Id)).OutcomeCode);
        Assert.Empty(await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == branch.Id).ToListAsync());
        Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == sourceRevisionId).ToListAsync());
    }
    private async Task ClearMediaDataAsync()
    {
        await using var context = CreateContext();
        var rootIds = context.SourceRootConfigurations.Where(value => value.CanonicalPath.StartsWith("C:\\phase5-media-")).Select(value => value.Id);
        var revisionIds = context.SourceRevisions.Where(value => rootIds.Contains(value.SourceRootId));
        var activityIds = context.SourceActivities.Where(value => revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId)).Select(value => value.Id);
        var branchIds = context.SourceProcessorBranches.Where(value => revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId)).Select(value => value.Id);
        await context.DocumentPublications.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync();
        await context.DocumentOcrRequests.Where(value => revisionIds.Select(revision => revision.Id).Contains(value.RetainedSourceRevisionId)).ExecuteDeleteAsync();
        await context.SourceProcessorBranchMembers.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync();
        await context.SourceProcessorAttempts.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync();
        await context.SourceProcessorBranches.Where(value => branchIds.Contains(value.Id)).ExecuteDeleteAsync();
        await context.SourceActivityRelations.Where(value => activityIds.Contains(value.PredecessorActivityId) || activityIds.Contains(value.SuccessorActivityId)).ExecuteDeleteAsync();
        await context.AuditEvents.Where(value =>
            value.SourceActivityId != null && activityIds.Contains(value.SourceActivityId.Value) ||
            value.SourceRevisionId != null && revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId.Value)).ExecuteDeleteAsync();
        await context.SourceActivities.Where(value => activityIds.Contains(value.Id)).ExecuteDeleteAsync();
        var testIdentities = context.SourceIdentities.Where(value => value.StableKey.StartsWith("media-test-finished:"));
        await context.PipelineRecords.Where(value => testIdentities.Select(identity => identity.Id).Contains(value.SourceIdentityId)).ExecuteDeleteAsync();
        await testIdentities.ExecuteDeleteAsync();
        await context.SourceArtifacts.Where(value => revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId)).ExecuteDeleteAsync();
        await context.SourceRevisions.Where(value => rootIds.Contains(value.SourceRootId)).ExecuteDeleteAsync();
        var profileIds = context.OutlookCaptureProfiles.Where(value => rootIds.Contains(value.SourceRootId)).Select(value => value.Id);
        await context.OutlookCaptureExports.Where(value => value.ProfileId.HasValue && profileIds.Contains(value.ProfileId.Value)).ExecuteDeleteAsync();
        await context.OutlookCaptureFolders.Where(value => profileIds.Contains(value.ProfileId)).ExecuteDeleteAsync();
        await context.OutlookCaptureProfiles.Where(value => rootIds.Contains(value.SourceRootId)).ExecuteDeleteAsync();
        await context.SourceRootConfigurations.Where(value => value.CanonicalPath.StartsWith("C:\\phase5-media-")).ExecuteDeleteAsync();
    }
    private static string CreatePrivateRoot() { var root = Path.Combine(Path.GetTempPath(), $"flux-media-retained-{Guid.NewGuid():N}"); Directory.CreateDirectory(root); return root; }
    private void DeletePrivateRoot(string root)
    {
        _resources.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Jj7kAAAAASUVORK5CYII=");
    private static byte[] PngWithReadBudgetPadding()
    {
        // Keep the real 96 MiB read budget, but consume it in 1 MiB reads rather
        // than millions of tiny reads. An unknown ancillary chunk keeps the PNG
        // valid; the normal-parser theory above also verifies its manifest.
        var png = Png();
        var bytes = new byte[1024 * 1024];
        var chunkOffset = png.Length - 12; // Insert before IEND.
        var payloadLength = bytes.Length - png.Length - 12;
        png.AsSpan(0, chunkOffset).CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(chunkOffset, 4), payloadLength);
        "pADd"u8.CopyTo(bytes.AsSpan(chunkOffset + 4));
        var crc = uint.MaxValue;
        foreach (var value in bytes.AsSpan(chunkOffset + 4, payloadLength + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(chunkOffset + 8 + payloadLength, 4), ~crc);
        png.AsSpan(chunkOffset).CopyTo(bytes.AsSpan(bytes.Length - 12));
        return bytes;
    }
    private static byte[] IsoBmff(string brand)
    {
        var bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        return bytes;
    }
    private static byte[] GifWithImageDirectories(int count)
    {
        var bytes = new List<byte>(13 + (count * 15) + 1);
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, 0, 0, 0]);
        for (var index = 0; index < count; index++)
        {
            bytes.AddRange([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0]);
        }
        bytes.Add(0x3b);
        return bytes.ToArray();
    }
    private static byte[] AacAdts() => [0xff, 0xf1, 0x50, 0x80, 0x00, 0xe0, 0xfc];
    private static byte[] AsfHeader() =>
    [
        0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c,
        0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x02
    ];
    private static byte[] MpegHeaderWithBody(byte secondHeaderByte)
    {
        var bytes = new byte[417];
        bytes[0] = 0xff;
        bytes[1] = secondHeaderByte;
        bytes[2] = 0x90;
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class AllowingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }

    private sealed class UnavailableParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(false, "media-metadata-parser-unavailable");
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Unavailable parser must not parse.");
    }

    private sealed class FailingParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new InvalidDataException("synthetic parser failure");
    }

    private sealed class UnexpectedPreflightParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => throw new InvalidOperationException("synthetic unexpected preflight failure");
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Unexpected preflight failure must not parse.");
    }

    private sealed class UnexpectedParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic unexpected parser failure");
    }

    private sealed class UnsignalledCancellationPreflightParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => throw new OperationCanceledException("synthetic unsignalled preflight cancellation");
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Unavailable parser must not parse.");
    }

    private sealed class UnsignalledCancellationParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("synthetic unsignalled parser cancellation");
    }

    private sealed class ReadLimitParser : IMediaMetadataParser
    {
        public int ReadCalls { get; private set; }
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                stream.Position = 0;
                while (true)
                {
                    ReadCalls++;
                    if (stream.Read(buffer) == 0) break;
                }
            }
        }
    }

    private sealed class OversizedOutputParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            new(MediaMetadataFormat.Png, new string('x', MediaMetadataRetainedProcessor.MaximumManifestUtf8Bytes + 1), 1, 1, null, null);
    }

    private sealed class WithholdingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(null, true, "secret-content-withheld");
    }

    private sealed class CancellingReader(IRetainedSourceReader inner, CancellationTokenSource cancellation) : IRetainedSourceReader
    {
        private int _readCount;
        public ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            inner.InspectAsync(sourceRevisionId, cancellationToken);

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                return inner.ReadBytesAsync(sourceRevisionId, cancellationToken);
            }
            cancellation.Cancel();
            return ValueTask.FromException<RetainedSourceBytes>(new OperationCanceledException(cancellation.Token));
        }

        public ValueTask<FluxKnowledge.Application.Pipeline.Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Media activation must not decode retained bytes as text.");
    }

    private sealed class OversizedInspectionReader(SourceRevisionId sourceRevisionId, string hash) : IRetainedSourceReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(sourceRevisionId, requestedRevisionId);
            return ValueTask.FromResult(new RetainedArtifactInspection(sourceRevisionId, hash, MediaMetadataRetainedProcessor.MaximumRetainedInputBytes + 1));
        }

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            ReadCount++;
            throw new Xunit.Sdk.XunitException("Oversized media promotion must not read retained bytes.");
        }

        public ValueTask<FluxKnowledge.Application.Pipeline.Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Media activation must not decode retained bytes as text.");
    }
}
