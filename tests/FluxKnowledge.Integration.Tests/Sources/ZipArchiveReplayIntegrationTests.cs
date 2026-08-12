using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class ZipArchiveReplayIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Retained_binary_reader_accepts_a_zip_sized_artifact_without_relaxing_the_utf8_text_ceiling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-reader-limits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[64 * 1024 * 1024];
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), bytes);
            var seeded = await SeedDeferredZipAsync(hash, bytes.Length, relativePath);
            using var reader = new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), root);

            var binary = await reader.ReadBytesAsync(new SourceRevisionId(seeded.SourceRevisionId), CancellationToken.None);

            Assert.Equal(bytes.Length, binary.ByteLength);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await reader.ReadUtf8Async(new SourceRevisionId(seeded.SourceRevisionId), CancellationToken.None));
            await using var cleanup = CreateContext();
            (await cleanup.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId)).State = (int)SourceActivityState.DeferredPolicy;
            await cleanup.SaveChangesAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Concurrent_activations_return_one_fenced_branch_owner()
    {
        const string hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "bb", $"{hash}.bin"));
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
            ZipArchiveRetainedProcessor.Capability,
            CancellationToken.None));

        var claims = await Task.WhenAll(
            store.ClaimAsync("first-owner", 16, CancellationToken.None).AsTask(),
            store.ClaimAsync("second-owner", 16, CancellationToken.None).AsTask());

        var owner = Assert.Single(claims.SelectMany(value => value));
        Assert.Equal("first-owner", owner.LeaseOwner);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceProcessorAttempts.CountAsync(value => value.BranchId == owner.BranchId));
        (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == owner.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
        await verification.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task Generic_zip_claim_store_preserves_the_shared_sixteen_claim_limit()
    {
        var branchIds = new List<Guid>();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        for (var ordinal = 0; ordinal < 16; ordinal++)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"shared-claim-{ordinal}")));
            var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", hash[..2], $"{hash}.bin"));
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
                ZipArchiveRetainedProcessor.Capability,
                CancellationToken.None));
        }

        var claims = await store.ClaimAsync(
            "shared-sixteen-owner",
            16,
            ZipArchiveRetainedProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None);

        Assert.Equal(16, claims.Count);
        branchIds.AddRange(claims.Select(claim => claim.BranchId));
        await using var cleanup = CreateContext();
        var branches = await cleanup.SourceProcessorBranches.Where(value => branchIds.Contains(value.Id)).ToArrayAsync();
        Assert.Equal(16, branches.Length);
        foreach (var branch in branches) branch.State = (int)RetainedProcessorBranchState.Blocked;
        await cleanup.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task A_stale_branch_claim_cannot_commit_children_or_failure_evidence()
    {
        const string hash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "cc", $"{hash}.bin"));
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
            ZipArchiveRetainedProcessor.Capability,
            CancellationToken.None));
        var claim = Assert.Single(await store.ClaimAsync("original-owner", 1, CancellationToken.None));
        await using (var replacement = CreateContext())
        {
            var branch = await replacement.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId);
            branch.LeaseOwner = "replacement-owner";
            branch.LeaseGeneration++;
            branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            await replacement.SaveChangesAsync();
        }

        var completion = new RetainedProcessorCompletion([], "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        Assert.False(await store.CommitAsync(claim, completion, CancellationToken.None));
        Assert.False(await store.FailAsync(claim, new RetainedProcessorFailure("stale", []), CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == claim.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
        var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == claim.BranchId);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null(attempt.OutcomeCode);
        (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
        await verification.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task Retryable_failure_finishes_the_attempt_and_releases_the_branch_for_a_new_generation()
    {
        const string hash = "abababababababababababababababababababababababababababababababab";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "ab", $"{hash}.bin"));
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
            ZipArchiveRetainedProcessor.Capability,
            CancellationToken.None));
        var first = Assert.Single(await store.ClaimAsync("first-owner", 1, CancellationToken.None));

        Assert.True(await store.RetryAsync(first, "retained-artifact-transient", CancellationToken.None));

        await using (var verification = CreateContext())
        {
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == first.BranchId);
            Assert.Equal((int)RetainedProcessorBranchState.Pending, branch.State);
            Assert.Null(branch.LeaseOwner);
            Assert.Null(branch.LeaseExpiresAtUtc);
            var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == first.BranchId && value.LeaseGeneration == first.LeaseGeneration);
            Assert.NotNull(attempt.FinishedAtUtc);
            Assert.Equal("retained-artifact-transient", attempt.OutcomeCode);
        }

        var second = Assert.Single(await store.ClaimAsync("second-owner", 1, CancellationToken.None));
        Assert.Equal(first.BranchId, second.BranchId);
        Assert.Equal(first.LeaseGeneration + 1, second.LeaseGeneration);
        await using var cleanup = CreateContext();
        (await cleanup.SourceProcessorBranches.SingleAsync(value => value.Id == first.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
        await cleanup.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task Restart_reclaim_closes_the_expired_attempt_before_issuing_a_new_generation()
    {
        const string hash = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "cd", $"{hash}.bin"));
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
            ZipArchiveRetainedProcessor.Capability,
            CancellationToken.None));
        var first = Assert.Single(await store.ClaimAsync("crashed-owner", 1, CancellationToken.None));
        await using (var expire = CreateContext())
        {
            (await expire.SourceProcessorBranches.SingleAsync(value => value.Id == first.BranchId)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await expire.SaveChangesAsync();
        }

        var resumed = Assert.Single(await store.ClaimAsync("restart-owner", 1, CancellationToken.None));

        Assert.Equal(first.BranchId, resumed.BranchId);
        Assert.Equal(first.LeaseGeneration + 1, resumed.LeaseGeneration);
        await using var verification = CreateContext();
        var abandoned = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == first.BranchId && value.LeaseGeneration == first.LeaseGeneration);
        Assert.NotNull(abandoned.FinishedAtUtc);
        Assert.Equal("lease-expired-reconciled", abandoned.OutcomeCode);
        (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == first.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
        await verification.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task An_expired_lease_during_child_persistence_cannot_commit_children_or_attempt_evidence()
    {
        const string hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string memberHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "ee", $"{hash}.bin"));
        var claimStore = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await claimStore.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.LegacyActivityId, new SourceRevisionId(seeded.SourceRevisionId), hash),
            ZipArchiveRetainedProcessor.Capability,
            CancellationToken.None));
        var claim = Assert.Single(await claimStore.ClaimAsync("expiry-owner", 1, CancellationToken.None));
        var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        await using (var setup = CreateContext())
        {
            (await setup.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId)).LeaseExpiresAtUtc = expiresAtUtc;
            await setup.SaveChangesAsync();
        }

        var barrier = new ChildPersistenceBarrierInterceptor();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString, barrier), TimeProvider.System);
        var member = new RetainedProcessorMember(
            ArchiveMemberIdentity.Create("expiry-parent", "member.txt"),
            memberHash,
            Path.Combine("sha256", "ff", $"{memberHash}.bin"),
            1,
            "AcceptedUtf8Text");
        var completion = new RetainedProcessorCompletion([member], memberHash);

        var commit = store.CommitAsync(claim, completion, CancellationToken.None).AsTask();
        await barrier.ChildPersistenceReached.WaitAsync(TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow <= expiresAtUtc)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        barrier.Release();

        Assert.False(await commit);
        Assert.False(await store.FailAsync(claim, new RetainedProcessorFailure("expired", []), CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == claim.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
        Assert.Empty(await (
            from artifact in verification.SourceArtifacts
            join revision in verification.SourceRevisions on artifact.SourceRevisionId equals revision.Id
            where revision.ParentSourceRevisionId == seeded.SourceRevisionId
            select artifact).ToListAsync());
        Assert.Empty(await (
            from activity in verification.SourceActivities
            join revision in verification.SourceRevisions on activity.SourceRevisionId equals revision.Id
            where revision.ParentSourceRevisionId == seeded.SourceRevisionId
            select activity).ToListAsync());
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId);
        Assert.Null(branch.CompletionReceiptFingerprint);
        Assert.Equal(0, branch.CompletedMemberCount);
        var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == claim.BranchId);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null(attempt.OutcomeCode);
    }

    [NativeSqlServerFact]
    public async Task Outlook_parent_members_are_written_to_the_parent_private_artifact_root()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-zip-private-{Guid.NewGuid():N}");
        var globalRoot = Path.Combine(Path.GetTempPath(), $"flux-zip-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        Directory.CreateDirectory(globalRoot);
        try
        {
            var zipBytes = CreateZip("docs/readme.txt", "private child");
            var parentHash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var parentRelativePath = Path.Combine("sha256", parentHash[..2], $"{parentHash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", parentHash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, parentRelativePath), zipBytes);
            var seeded = await SeedDeferredZipAsync(parentHash, zipBytes.Length, parentRelativePath, privateRoot);
            var childHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("private child")));
            var childRelativePath = Path.Combine("sha256", childHash[..2], $"{childHash}.bin");

            var result = await CreateActivation(globalRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CompletedBranches);
            Assert.True(File.Exists(Path.Combine(privateRoot, childRelativePath)));
            Assert.False(File.Exists(Path.Combine(globalRoot, childRelativePath)));
            await using var verification = CreateContext();
            var child = await verification.SourceRevisions.SingleAsync(value => value.ParentSourceRevisionId == seeded.SourceRevisionId);
            Assert.Equal(childHash, (await verification.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.Id)).ContentSha256);
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.completed");
            var publicJson = JsonSerializer.Serialize(audit);
            Assert.DoesNotContain(privateRoot, publicJson, StringComparison.Ordinal);
            Assert.DoesNotContain("readme.txt", publicJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
            if (Directory.Exists(globalRoot)) Directory.Delete(globalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Archive_member_provenance_survives_an_ordinary_root_reconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zipBytes = CreateZip("docs/readme.txt", "generated child");
            var hash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), zipBytes);
            var seeded = await SeedDeferredZipAsync(hash, zipBytes.Length, relativePath);
            Assert.Equal(1, (await CreateActivation(root).RunOnceAsync(CancellationToken.None)).CompletedBranches);

            await new SqlSourceScanStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System)
                .SuppressUnseenAsync(new SourceRootId(seeded.RootId), new HashSet<SourceRevisionId>(), CancellationToken.None);

            await using var verification = CreateContext();
            var child = await verification.SourceRevisions.SingleAsync(value => value.ParentSourceRevisionId == seeded.SourceRevisionId);
            Assert.Equal(1, child.OriginKind);
            Assert.Null(child.SuppressedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Branch_member_child_columns_have_database_foreign_keys()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_zip_fk_model;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
        var entity = context.Model.FindEntityType(typeof(SourceProcessorBranchMemberEntity))!;

        Assert.Contains(entity.GetForeignKeys(), value => value.Properties.Single().Name == nameof(SourceProcessorBranchMemberEntity.ChildSourceRevisionId) &&
            value.PrincipalEntityType.ClrType == typeof(SourceRevisionEntity));
        Assert.Contains(entity.GetForeignKeys(), value => value.Properties.Single().Name == nameof(SourceProcessorBranchMemberEntity.ChildSourceActivityId) &&
            value.PrincipalEntityType.ClrType == typeof(SourceActivityEntity));
    }

    [NativeSqlServerFact]
    public async Task Promotion_candidate_read_matches_retained_hashes_across_configured_collations()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "aa", $"{hash}.bin"));

        var candidates = await new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System)
            .ReadPromotionCandidatesAsync(16, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(seeded.LegacyActivityId, candidate.LegacyActivityId);
        await using var cleanup = CreateContext();
        var activity = await cleanup.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
        activity.State = (int)SourceActivityState.DeferredPolicy;
        await cleanup.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task Activation_replays_retained_zip_without_reading_missing_source_original()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zipBytes = CreateZip("docs/readme.txt", "retained child");
            var hash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), zipBytes);
            var seeded = await SeedDeferredZipAsync(hash, zipBytes.Length, relativePath);
            Assert.True(Directory.Exists(Path.Combine(root, "sha256")));
            Assert.True(File.Exists(Path.Combine(root, relativePath)));

            var result = await CreateActivation(root).RunOnceAsync(CancellationToken.None);

            Assert.Equal("archive-zip-expand", result.Capability);
            Assert.Equal(1, result.CompletedBranches);
            await using var verification = CreateContext();
            var predecessor = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
            Assert.Equal((int)SourceActivityState.CancelledSuperseded, predecessor.State);
            var relation = await verification.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.LegacyActivityId);
            var successor = await verification.SourceActivities.SingleAsync(value => value.Id == relation.SuccessorActivityId);
            Assert.Equal((int)SourceActivityKind.ArchiveExpansion, successor.ActivityKind);
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceActivityId == successor.Id);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            Assert.Equal(1, branch.CompletedMemberCount);
            var member = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            var child = await verification.SourceRevisions.SingleAsync(value => value.Id == member.ChildSourceRevisionId);
            Assert.Equal(seeded.SourceRevisionId, child.ParentSourceRevisionId);
            Assert.Equal(1, child.OriginKind);
            Assert.Single(await verification.SourceArtifacts.Where(value => value.SourceRevisionId == child.Id).ToListAsync());
            Assert.Single(await verification.SourceActivities.Where(value => value.SourceRevisionId == child.Id).ToListAsync());
            Assert.NotNull(branch.CompletionReceiptFingerprint);

            var repeat = await CreateActivation(root).RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, repeat.CompletedBranches);
            Assert.Equal(1, await verification.SourceProcessorBranches
                .CountAsync(value => value.SourceRevisionId == seeded.SourceRevisionId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Missing_retained_artifact_blocks_the_generic_deferred_activity_without_creating_a_branch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-missing-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var hash = new string('a', 64);
            var seeded = await SeedDeferredZipAsync(hash, 4, Path.Combine("sha256", "aa", $"{hash}.bin"));

            var result = await CreateActivation(root).RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, result.PromotedBranches);
            await using var verification = CreateContext();
            var legacy = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, legacy.State);
            Assert.Equal("retained-artifact-missing", legacy.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Corrupt_retained_artifact_blocks_the_generic_deferred_activity_without_creating_a_branch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-corrupt-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zip = CreateZip("valid.txt", "expected retained bytes");
            var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), [1, 2, 3, 4]);
            var seeded = await SeedDeferredZipAsync(hash, zip.Length, relativePath);

            var result = await CreateActivation(root).RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, result.PromotedBranches);
            await using var verification = CreateContext();
            var legacy = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, legacy.State);
            Assert.Equal("retained-artifact-checksum-invalid", legacy.Reason);
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.SourceRevisionId).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Unsafe_retained_zip_persists_one_sanitised_blocked_member_outcome_per_unsafe_entry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-outcomes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zip = CreateZip([("../private-one.txt", "one"), ("/private-two.txt", "two")]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), zip);
            var seeded = await SeedDeferredZipAsync(hash, zip.Length, relativePath);

            var result = await CreateActivation(root).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
            var outcomes = await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == branch.Id).ToArrayAsync();
            Assert.Equal(2, outcomes.Length);
            Assert.All(outcomes, outcome => { Assert.Equal("blocked", outcome.Disposition); Assert.Equal("archive-entry-path-invalid", outcome.ReasonCode); Assert.Null(outcome.ChildSourceRevisionId); });
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.blocked");
            var publicJson = JsonSerializer.Serialize(audit);
            Assert.DoesNotContain("private-one", publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-two", publicJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Duplicate_unsafe_raw_names_persist_two_parent_bound_member_dispositions_with_the_branch_attempt_and_sanitised_event()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-duplicate-unsafe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zip = CreateZip([("../same-private.txt", "one"), ("../same-private.txt", "two")]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), zip);
            var seeded = await SeedDeferredZipAsync(hash, zip.Length, relativePath);

            var result = await CreateActivation(root).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
            var outcomes = await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == branch.Id).OrderBy(value => value.MemberFingerprint).ToArrayAsync();
            Assert.Equal(2, outcomes.Length);
            Assert.NotEqual(outcomes[0].MemberFingerprint, outcomes[1].MemberFingerprint);
            Assert.All(outcomes, outcome => { Assert.Equal("blocked", outcome.Disposition); Assert.Equal("archive-entry-path-invalid", outcome.ReasonCode); });
            var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch.Id);
            Assert.Equal("archive-entry-path-invalid", attempt.OutcomeCode);
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.blocked");
            var serialised = JsonSerializer.Serialize(new { branch, outcomes, attempt, audit });
            Assert.DoesNotContain("same-private", serialised, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, serialised, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Restart_reconciles_a_written_private_member_after_transient_failure_without_reading_the_source_original()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-zip-restart-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var zip = CreateZip("member.txt", "private recoverable member");
            var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relativePath), zip);
            var seeded = await SeedDeferredZipAsync(hash, zip.Length, relativePath);
            var factory = new ContextFactory(_fixture.ConnectionString);
            var processor = new ZipArchiveRetainedProcessor(new WriteThenFailWriter(new SqlRetainedArtifactWriter(factory, root)));
            var first = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([processor])),
                new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), new SqlRetainedSourceReader(factory, root), processor,
                new RetainedProcessorOptions { ArchiveZipExpandEnabled = true }, TimeProvider.System);

            var transient = await first.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, transient.FailedBranches);
            await using (var interrupted = CreateContext())
            {
                var branch = await interrupted.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
                Assert.Equal((int)RetainedProcessorBranchState.Pending, branch.State);
                Assert.Empty(await interrupted.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            }

            var resumed = await CreateActivation(root).RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, resumed.CompletedBranches);
            await using var verification = CreateContext();
            Assert.Single(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            var attempts = await (from attempt in verification.SourceProcessorAttempts
                                  join branch in verification.SourceProcessorBranches on attempt.BranchId equals branch.Id
                                  where branch.SourceRevisionId == seeded.SourceRevisionId
                                  orderby attempt.LeaseGeneration
                                  select attempt).ToArrayAsync();
            Assert.Equal(new string?[] { "retained-artifact-transient", "completed" }, attempts.Select(value => value.OutcomeCode).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private RetainedProcessorActivationService CreateActivation(string root)
    {
        var factory = new ContextFactory(_fixture.ConnectionString);
        var artifactWriter = new SqlRetainedArtifactWriter(factory, root);
        var processor = new ZipArchiveRetainedProcessor(artifactWriter);
        var registry = new LocalSourceCapabilityHandlerRegistry([processor]);
        return new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), registry),
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System),
            new SqlRetainedSourceReader(factory, root),
            processor,
            new RetainedProcessorOptions { ArchiveZipExpandEnabled = true },
            TimeProvider.System);
    }

    private async Task<(Guid LegacyActivityId, Guid SourceRevisionId, Guid RootId)> SeedDeferredZipAsync(
        string hash,
        int byteLength,
        string relativePath,
        string? outlookSpoolRoot = null)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\zip-retained-tests\\{rootId:N}", DisplayName = "Test", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"zip-parent:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = "C:\\missing-source-original-sentinel.zip", Classification = "DeferredCapability", Extension = ".zip", ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        context.DeferredCapabilities.Add(new DeferredCapabilityEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ArtifactFingerprint = hash, RequiredCapability = "local-source-capability", Provenance = "test", CreatedAtUtc = now });
        if (outlookSpoolRoot is not null)
        {
            context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity { Id = Guid.NewGuid(), SourceRootId = rootId, DisplayName = "ZIP test profile",
                SpoolRoot = outlookSpoolRoot, IncrementalBasis = 0, State = 0, IsEnabled = true, ConfigurationRevision = 1,
                CadenceTicks = TimeSpan.FromMinutes(5).Ticks, MaximumOverlapTicks = TimeSpan.FromMinutes(1).Ticks, CreatedAtUtc = now, UpdatedAtUtc = now });
        }
        await context.SaveChangesAsync(); return (activityId, revisionId, rootId);
    }

    private static byte[] CreateZip(string entryName, string text)
    {
        using var buffer = new MemoryStream(); using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private static byte[] CreateZip(IEnumerable<(string Name, string Text)> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        }
        return buffer.ToArray();
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class ContextFactory(string connectionString, IInterceptor? interceptor = null) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = CreateOptions(connectionString, interceptor);

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());

        private static DbContextOptions<FluxKnowledgeDbContext> CreateOptions(string connectionString, IInterceptor? interceptor)
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }
            return builder.Options;
        }
    }

    private sealed class ChildPersistenceBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _childPersistenceReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _triggered;

        public Task ChildPersistenceReached => _childPersistenceReached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForChildPersistenceAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await WaitForChildPersistenceAsync(command, cancellationToken);
            return result;
        }

        private async Task WaitForChildPersistenceAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.Contains("INSERT INTO [SourceRevisions]", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                _childPersistenceReached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class WriteThenFailWriter(IRetainedArtifactWriter inner) : IRetainedArtifactWriter
    {
        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId parentSourceRevisionId, Stream content, long maximumByteLength, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(parentSourceRevisionId, content, maximumByteLength, cancellationToken);
            throw new IOException("transient failure after private artifact write");
        }
    }
}
