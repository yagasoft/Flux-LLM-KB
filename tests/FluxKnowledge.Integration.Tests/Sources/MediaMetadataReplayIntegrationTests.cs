using System.Security.Cryptography;
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
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => ClearMediaDataAsync();
    public Task DisposeAsync() => ClearMediaDataAsync();

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
            var seeded = await SeedPngAsync(privateRoot, "read-limit");
            var media = new MediaMetadataRetainedProcessor(new SqlRetainedArtifactWriter(new ContextFactory(_fixture.ConnectionString), privateRoot), new AllowingDisclosure(), new ReadLimitParser());

            var result = await CreateActivation(privateRoot, enabled: true, media).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await AssertNoDerivedChildAfterFailureAsync(seeded.RevisionId, "media-metadata-read-limit");
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

    [NativeSqlServerFact]
    public async Task Successful_media_replay_persists_one_canonical_manifest_with_immutable_parent_provenance_and_standard_text_index_link()
    {
        var privateRoot = CreatePrivateRoot();
        try
        {
            var seeded = await SeedPngAsync(privateRoot, "manifest");

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
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
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
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
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
            var reader = new CancellingReader(new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), privateRoot), cancellation);

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

    private RetainedProcessorActivationService CreateActivation(string privateRoot, bool enabled, MediaMetadataRetainedProcessor? media = null,
        IRetainedSourceReader? retainedReader = null)
    {
        var factory = new ContextFactory(_fixture.ConnectionString);
        var writer = new SqlRetainedArtifactWriter(factory, privateRoot);
        var zip = new ZipArchiveRetainedProcessor(writer);
        return new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new MediaMetadataCapabilityHandler()])),
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), retainedReader ?? new SqlRetainedSourceReader(factory, privateRoot), zip,
            new RetainedProcessorOptions { MediaMetadataEnabled = enabled }, TimeProvider.System,
            mediaMetadataProcessor: media ?? new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure()));
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
        await context.SourceProcessorBranchMembers.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync();
        await context.SourceProcessorAttempts.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync();
        await context.SourceProcessorBranches.Where(value => branchIds.Contains(value.Id)).ExecuteDeleteAsync();
        await context.SourceActivityRelations.Where(value => activityIds.Contains(value.PredecessorActivityId) || activityIds.Contains(value.SuccessorActivityId)).ExecuteDeleteAsync();
        await context.AuditEvents.Where(value =>
            value.SourceActivityId != null && activityIds.Contains(value.SourceActivityId.Value) ||
            value.SourceRevisionId != null && revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId.Value)).ExecuteDeleteAsync();
        await context.SourceActivities.Where(value => activityIds.Contains(value.Id)).ExecuteDeleteAsync();
        await context.SourceArtifacts.Where(value => revisionIds.Select(revision => revision.Id).Contains(value.SourceRevisionId)).ExecuteDeleteAsync();
        await context.SourceRevisions.Where(value => rootIds.Contains(value.SourceRootId)).ExecuteDeleteAsync();
        await context.SourceRootConfigurations.Where(value => value.CanonicalPath.StartsWith("C:\\phase5-media-")).ExecuteDeleteAsync();
    }
    private static string CreatePrivateRoot() { var root = Path.Combine(Path.GetTempPath(), $"flux-media-retained-{Guid.NewGuid():N}"); Directory.CreateDirectory(root); return root; }
    private static void DeletePrivateRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Jj7kAAAAASUVORK5CYII=");
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
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                stream.Position = 0;
                while (stream.Read(buffer) > 0) { }
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
            return ValueTask.FromResult(new RetainedArtifactInspection(sourceRevisionId, hash, MediaMetadataRetainedProcessor.MaximumInputBytes + 1));
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
