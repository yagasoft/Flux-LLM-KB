using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed class SqlOutlookCaptureStoreTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-11T12:00:00+00:00");
    private static readonly OutlookHostIdentity Host = new("S-1-5-21-100", 4, "host-a");
    private readonly NativeSqlServerFixture _fixture = fixture;
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ManifestHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string CursorFingerprint = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [NativeSqlServerFact]
    public async Task Save_profile_replays_matching_operation_and_rejects_divergence()
    {
        var store = CreateStore();
        var operation = Guid.NewGuid();
        var request = Save(operation, "Inbox");
        var first = await store.SaveProfileAsync(request, CancellationToken.None);
        var replay = await store.SaveProfileAsync(request, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.True(replay.IsReplay);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveProfileAsync(Save(operation, "Different inbox", OtherFingerprint), CancellationToken.None).AsTask());
    }

    [NativeSqlServerFact]
    public async Task Save_profile_commits_with_the_production_retrying_execution_strategy()
    {
        var operationId = Guid.NewGuid();

        var receipt = await CreateStore(useRetryingExecutionStrategy: true)
            .SaveProfileAsync(Save(operationId, "Retrying strategy profile"), CancellationToken.None);

        Assert.True(receipt.Accepted);
        Assert.False(receipt.IsReplay);
        await using var context = await CreateContextAsync();
        Assert.Single(await context.OutlookCaptureOperations
            .Where(row => row.OperationId == operationId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_matching_operations_replay_one_durable_result()
    {
        var barrier = new QueryBarrierInterceptor("OutlookCaptureOperations", 2);
        var store = CreateStore(barrier);
        var operationId = Guid.NewGuid();
        var request = Save(operationId, $"Concurrent-{operationId:N}");

        var receipts = await Task.WhenAll(
            store.SaveProfileAsync(request, CancellationToken.None).AsTask(),
            store.SaveProfileAsync(request, CancellationToken.None).AsTask());

        Assert.All(receipts, receipt => Assert.True(receipt.Accepted));
        Assert.Single(receipts, receipt => receipt.IsReplay);
        await using var context = await CreateContextAsync();
        var operation = await context.OutlookCaptureOperations.SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(1, await context.OutlookCaptureProfiles.CountAsync(row => row.Id == operation.ResourceId));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_matching_existing_profile_updates_replay_one_update()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var updateGate = await SeedCompletedBrowseAsync(seed.ProfileId);
        var operationId = Guid.NewGuid();
        var request = Save(operationId, $"Updated-{operationId:N}") with
        {
            ProfileId = new(seed.ProfileId),
            Enable = true,
            ExpectedConfigurationRevision = updateGate.ConfigurationRevision,
            BrowseCorrelationId = updateGate.BrowseCorrelationId
        };
        var barrier = new QueryBarrierInterceptor("OutlookCaptureProfiles", 2, "WHERE [o].[Id]");
        var store = CreateStore(barrier);

        var receipts = await Task.WhenAll(
            store.SaveProfileAsync(request, CancellationToken.None).AsTask(),
            store.SaveProfileAsync(request, CancellationToken.None).AsTask());

        Assert.All(receipts, receipt => Assert.True(receipt.Accepted));
        Assert.Single(receipts, receipt => receipt.IsReplay);
        await using var context = await CreateContextAsync();
        var profile = await context.OutlookCaptureProfiles.SingleAsync(row => row.Id == seed.ProfileId);
        Assert.Equal(2, profile.ConfigurationRevision);
        Assert.Equal(1, await context.OutlookCaptureOperations.CountAsync(row => row.OperationId == operationId));
    }

    [NativeSqlServerFact]
    public async Task Existing_profile_update_retries_forced_rowversion_conflict()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var updateGate = await SeedCompletedBrowseAsync(seed.ProfileId);
        var operationId = Guid.NewGuid();
        var request = Save(operationId, $"Forced-conflict-{operationId:N}") with
        {
            ProfileId = new(seed.ProfileId),
            Enable = true,
            ExpectedConfigurationRevision = updateGate.ConfigurationRevision,
            BrowseCorrelationId = updateGate.BrowseCorrelationId
        };
        var store = CreateStore(new RowVersionConflictInterceptor("OutlookCaptureProfiles", seed.ProfileId));

        var receipt = await store.SaveProfileAsync(request, CancellationToken.None);

        Assert.True(receipt.Accepted);
        await using var context = await CreateContextAsync();
        var profile = await context.OutlookCaptureProfiles.SingleAsync(row => row.Id == seed.ProfileId);
        Assert.Equal(2, profile.ConfigurationRevision);
        Assert.Equal(1, await context.OutlookCaptureOperations.CountAsync(row => row.OperationId == operationId));
    }

    [NativeSqlServerFact]
    public async Task Existing_profile_rejects_spool_root_rebinding_to_preserve_retained_artifact_identity()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var request = Save(Guid.NewGuid(), "Changed private root") with
        {
            ProfileId = new(seed.ProfileId),
            ExpectedConfigurationRevision = 1,
            SpoolValidation = new OutlookSpoolValidation(
                Fingerprint,
                true,
                true,
                true,
                true,
                "D:\\different-private\\outlook")
        };

        var receipt = await CreateStore().SaveProfileAsync(request, CancellationToken.None);

        Assert.False(receipt.Accepted);
        await using var context = await CreateContextAsync();
        var profile = await context.OutlookCaptureProfiles.SingleAsync(row => row.Id == seed.ProfileId);
        Assert.Equal("C:\\private\\outlook", profile.SpoolRoot);
        Assert.Equal(1, profile.ConfigurationRevision);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_divergent_operation_fails_closed_without_DbUpdateException()
    {
        var barrier = new QueryBarrierInterceptor("OutlookCaptureOperations", 2);
        var store = CreateStore(barrier);
        var operationId = Guid.NewGuid();

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => store.SaveProfileAsync(Save(operationId, "First", Fingerprint), CancellationToken.None).AsTask()),
            CaptureAsync(() => store.SaveProfileAsync(Save(operationId, "Second", OtherFingerprint), CancellationToken.None).AsTask()));

        Assert.Single(outcomes, outcome => outcome.Receipt is { Accepted: true });
        var failure = Assert.Single(outcomes, outcome => outcome.Exception is not null).Exception;
        Assert.IsType<InvalidOperationException>(failure);
        Assert.IsNotType<DbUpdateException>(failure);
        await using var context = await CreateContextAsync();
        var operation = await context.OutlookCaptureOperations.SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(1, await context.OutlookCaptureProfiles.CountAsync(row => row.Id == operation.ResourceId));
    }

    [NativeSqlServerFact]
    public async Task Disabled_profile_rejects_catch_up_request()
    {
        var store = CreateStore();
        var operationId = Guid.NewGuid();
        await store.SaveProfileAsync(Save(operationId, $"Disabled-{operationId:N}"), CancellationToken.None);
        await using var context = await CreateContextAsync();
        var profileId = await context.OutlookCaptureOperations
            .Where(row => row.OperationId == operationId)
            .Select(row => row.ResourceId!.Value)
            .SingleAsync();

        var result = await store.RequestCatchUpAsync(new OutlookCatchUpRequest(Guid.NewGuid(), Fingerprint,
            new OutlookCaptureProfileId(profileId), "manual", OutlookCatchUpProvenance.Manual), CancellationToken.None);

        Assert.False(result.Accepted);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_catch_up_requests_coalesce_to_one_active_resource()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var barrier = new QueryBarrierInterceptor("OutlookCatchUps", 2);
        var store = CreateStore(barrier);
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        var first = new OutlookCatchUpRequest(firstOperation, Fingerprint, new(seed.ProfileId), "same-key", OutlookCatchUpProvenance.Manual);
        var second = new OutlookCatchUpRequest(secondOperation, OtherFingerprint, new(seed.ProfileId), "same-key", OutlookCatchUpProvenance.Hint);

        var receipts = await Task.WhenAll(
            store.RequestCatchUpAsync(first, CancellationToken.None).AsTask(),
            store.RequestCatchUpAsync(second, CancellationToken.None).AsTask());

        Assert.All(receipts, receipt => Assert.True(receipt.Accepted));
        await using var context = await CreateContextAsync();
        var catchUp = await context.OutlookCatchUps.SingleAsync(row =>
            row.ProfileId == seed.ProfileId && row.CoalescingKey == "same-key" && (row.State == 0 || row.State == 1));
        var resources = await context.OutlookCaptureOperations
            .Where(row => row.OperationId == firstOperation || row.OperationId == secondOperation)
            .Select(row => row.ResourceId)
            .ToListAsync();
        Assert.Equal([catchUp.Id, catchUp.Id], resources.Order().ToArray());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_claims_of_one_pending_catch_up_resolve_rowversion_conflict()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var catchUpId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookCatchUps.Add(new OutlookCatchUpEntity
            {
                Id = catchUpId,
                ProfileId = seed.ProfileId,
                CoalescingKey = "pending-claim",
                Provenance = (int)OutlookCatchUpProvenance.Manual,
                State = 0
            });
            await context.SaveChangesAsync();
        }
        var barrier = new QueryBarrierInterceptor("OutlookCatchUps", 2, "WHERE [o].[State]");
        var store = CreateStore(barrier);
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();

        var receipts = await Task.WhenAll(
            store.ClaimCatchUpAsync(
                new OutlookCatchUpClaimRequest(firstOperation, Fingerprint, Host, TimeSpan.FromMinutes(5)),
                CancellationToken.None).AsTask(),
            store.ClaimCatchUpAsync(
                new OutlookCatchUpClaimRequest(secondOperation, OtherFingerprint, Host, TimeSpan.FromMinutes(5)),
                CancellationToken.None).AsTask());

        Assert.Single(receipts, receipt => receipt.Accepted && receipt.Claim is not null);
        Assert.Single(receipts, receipt => !receipt.Accepted && receipt.Claim is null);
        await using var assertionContext = await CreateContextAsync();
        var catchUp = await assertionContext.OutlookCatchUps.SingleAsync(row => row.Id == catchUpId);
        Assert.Equal(1, catchUp.State);
        Assert.Equal(2, await assertionContext.OutlookCaptureOperations.CountAsync(row =>
            row.OperationId == firstOperation || row.OperationId == secondOperation));
    }

    [NativeSqlServerFact]
    public async Task Retryable_host_requeue_defers_then_reclaims_without_advancing_the_folder_cursor()
    {
        var seed = await SeedCaptureAsync();
        var cursorUtc = Now.AddHours(-1);
        await using (var setup = await CreateContextAsync())
        {
            var folder = await setup.OutlookCaptureFolders.SingleAsync(row => row.Id == seed.FolderId);
            folder.CursorUtc = cursorUtc;
            folder.CursorFingerprint = CursorFingerprint;
            await setup.SaveChangesAsync();
        }

        var claim = new OutlookCatchUpClaim(
            seed.CatchUpId,
            new OutlookCaptureProfileId(seed.ProfileId),
            $"catch-up-{seed.ProfileId:N}",
            OutlookCatchUpProvenance.Manual,
            0,
            null,
            Host,
            Now.AddMinutes(10),
            Now,
            seed.FencingToken);
        var retryAtUtc = Now.AddMinutes(1);
        var requeue = await CreateStore().RequeueCatchUpAsync(
            new OutlookCatchUpRequeueRequest(
                Guid.NewGuid(),
                Fingerprint,
                claim,
                OutlookCatchUpFailureReason.RetryableHostFailure,
                retryAtUtc),
            CancellationToken.None);

        Assert.True(requeue.Accepted);
        Assert.False((await CreateStore().ClaimCatchUpAsync(
            new OutlookCatchUpClaimRequest(Guid.NewGuid(), OtherFingerprint, Host, TimeSpan.FromMinutes(5)),
            CancellationToken.None)).Accepted);

        var reclaimed = await CreateStore(now: retryAtUtc).ClaimCatchUpAsync(
            new OutlookCatchUpClaimRequest(Guid.NewGuid(), OtherFingerprint, Host, TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.NotNull(reclaimed.Claim);
        await using var verification = await CreateContextAsync();
        var catchUp = await verification.OutlookCatchUps.SingleAsync(row => row.Id == seed.CatchUpId);
        var folderAfter = await verification.OutlookCaptureFolders.SingleAsync(row => row.Id == seed.FolderId);
        Assert.Equal(1, catchUp.State);
        Assert.Equal(1, catchUp.RetryCount);
        Assert.Equal(OutlookCatchUpFailureReason.RetryableHostFailure.ToString(), catchUp.Reason);
        Assert.Equal(retryAtUtc, catchUp.NotBeforeUtc);
        Assert.Equal(cursorUtc, folderAfter.CursorUtc);
        Assert.Equal(CursorFingerprint, folderAfter.CursorFingerprint);
    }

    [NativeSqlServerFact]
    public async Task Folder_digest_length_prefix_distinguishes_delimiter_collisions()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var firstBrowseId = Guid.NewGuid();
        var secondBrowseId = Guid.NewGuid();
        const long fencingToken = 9;
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.AddRange(
                ClaimedBrowse(firstBrowseId, seed.ProfileId, fencingToken, "Mailbox/First"),
                ClaimedBrowse(secondBrowseId, seed.ProfileId, fencingToken, "Mailbox/Second"));
            await context.SaveChangesAsync();
        }

        var firstId = OutlookCaptureFolderId.New();
        var secondId = OutlookCaptureFolderId.New();
        var store = CreateStore();
        var firstReceipt = await store.CompleteBrowseAsync(
            new OutlookBrowseCompletionRequest(Guid.NewGuid(), Fingerprint, firstBrowseId, Host, fencingToken,
                [new OutlookBrowseFolderProjection(firstId, "First")], 1,
                [new OutlookBrowseFolderResult(firstId, "alpha|beta", "gamma", "First")]),
            CancellationToken.None);
        var secondReceipt = await store.CompleteBrowseAsync(
            new OutlookBrowseCompletionRequest(Guid.NewGuid(), OtherFingerprint, secondBrowseId, Host, fencingToken,
                [new OutlookBrowseFolderProjection(secondId, "Second")], 1,
                [new OutlookBrowseFolderResult(secondId, "alpha", "beta|gamma", "Second")]),
            CancellationToken.None);

        Assert.True(firstReceipt.Accepted);
        Assert.True(secondReceipt.Accepted);
        await using var assertionContext = await CreateContextAsync();
        var folders = await assertionContext.OutlookCaptureFolders
            .Where(row => row.ProfileId == seed.ProfileId)
            .OrderBy(row => row.DisplayName)
            .ToListAsync();
        Assert.Equal(2, folders.Count);
        Assert.NotEqual(folders[0].CanonicalIdentityFingerprint, folders[1].CanonicalIdentityFingerprint);
    }

    [NativeSqlServerFact]
    public async Task Targeted_browse_claim_persists_and_returns_only_the_private_exact_path()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        var request = new OutlookBrowseRequest(
            Guid.NewGuid(),
            Fingerprint,
            browseId,
            Guid.NewGuid(),
            1,
            Now.AddMinutes(5),
            new OutlookCaptureProfileId(seed.ProfileId),
            "Mailbox/Action");
        var store = CreateStore();

        await store.RequestBrowseAsync(request, CancellationToken.None);
        var receipt = await store.ClaimBrowseAsync(
            new OutlookBrowseClaimRequest(Guid.NewGuid(), OtherFingerprint, browseId, Host, Now.AddMinutes(3)),
            CancellationToken.None);

        Assert.True(receipt.Accepted);
        Assert.Equal("Mailbox/Action", receipt.Claim?.TargetPath);
        await using var context = await CreateContextAsync();
        Assert.Equal("Mailbox/Action", await context.OutlookBrowseRequests.Where(row => row.Id == browseId).Select(row => row.TargetPath).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Browse_operation_fingerprint_binds_the_exact_target_without_audit_disclosure()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var operationId = Guid.NewGuid();
        var first = new OutlookBrowseRequest(operationId, Fingerprint, Guid.NewGuid(), Guid.NewGuid(), 1, Now.AddMinutes(5), new OutlookCaptureProfileId(seed.ProfileId), "Mailbox/Action");
        var changedTarget = first with { BrowseRequestId = Guid.NewGuid(), TargetPath = "Mailbox/Private" };
        var distinctOperation = first with { OperationId = Guid.NewGuid(), BrowseRequestId = Guid.NewGuid(), TargetPath = "Mailbox/Private" };
        var store = CreateStore();

        await store.RequestBrowseAsync(first, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequestBrowseAsync(changedTarget, CancellationToken.None).AsTask());
        await store.RequestBrowseAsync(distinctOperation, CancellationToken.None);

        await using var context = await CreateContextAsync();
        var receipts = await context.OutlookCaptureOperations
            .Where(item => item.OperationId == first.OperationId || item.OperationId == distinctOperation.OperationId)
            .OrderBy(item => item.OperationId)
            .ToListAsync();
        Assert.Equal(2, receipts.Count);
        Assert.NotEqual(receipts[0].RequestFingerprint, receipts[1].RequestFingerprint);
        Assert.NotEqual(receipts[0].ResourceId, receipts[1].ResourceId);
        var audits = await context.AuditEvents
            .Where(item => item.CorrelationId == $"outlook-operation:{first.OperationId:N}" ||
                item.CorrelationId == $"outlook-operation:{distinctOperation.OperationId:N}")
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.DoesNotContain("Mailbox/Action", audit.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Mailbox/Private", audit.DetailsJson, StringComparison.Ordinal);
        });
    }

    [NativeSqlServerFact]
    public async Task Legacy_untargeted_browse_row_is_terminal_and_unclaimable_without_inference()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
            {
                Id = browseId,
                ProfileId = seed.ProfileId,
                CorrelationId = Guid.NewGuid(),
                ConfigurationRevision = 1,
                State = 0,
                ExpiresAtUtc = Now.AddMinutes(5),
                TargetPath = null
            });
            await context.SaveChangesAsync();
        }

        var receipt = await CreateStore().ClaimBrowseAsync(
            new OutlookBrowseClaimRequest(Guid.NewGuid(), Fingerprint, browseId, Host, Now.AddMinutes(3)),
            CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.Null(receipt.Claim);
        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Equal(3, row.State);
        Assert.Equal((int)OutlookBrowseFailureCode.Failed, row.FailureCode);
        Assert.Null(row.TargetPath);
        Assert.Null(row.LeaseOwner);
    }

    [NativeSqlServerFact]
    public async Task Targeted_browse_migration_terminalises_previous_pending_leased_and_completed_broad_rows()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var rows = Enumerable.Range(0, 3).Select(index => new OutlookBrowseRequestEntity
        {
            Id = Guid.NewGuid(),
            ProfileId = seed.ProfileId,
            CorrelationId = Guid.NewGuid(),
            ConfigurationRevision = 1,
            State = index,
            ExpiresAtUtc = Now.AddMinutes(5),
            LeaseOwner = index == 1 ? Owner(Host) : null,
            LeaseExpiresAtUtc = index == 1 ? Now.AddMinutes(3) : null,
            TargetPath = null,
            TargetPathFingerprint = null
        }).ToArray();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.AddRange(rows);
            await context.SaveChangesAsync();
        }

        var migration = new AddOutlookBrowseTargetPath();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        typeof(AddOutlookBrowseTargetPath).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, [builder]);
        var terminalisation = Assert.Single(builder.Operations.OfType<SqlOperation>());
        await using (var context = await CreateContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync(terminalisation.Sql);
        }

        await using var verification = await CreateContextAsync();
        var terminal = await verification.OutlookBrowseRequests.Where(item => rows.Select(row => row.Id).Contains(item.Id)).ToListAsync();
        Assert.All(terminal, row =>
        {
            Assert.Equal(3, row.State);
            Assert.Equal((int)OutlookBrowseFailureCode.Failed, row.FailureCode);
            Assert.Null(row.LeaseOwner);
            Assert.Null(row.LeaseExpiresAtUtc);
            Assert.Null(row.TargetPath);
            Assert.Null(row.TargetPathFingerprint);
        });
    }

    [Fact]
    public void Targeted_browse_migration_down_is_explicitly_non_reversible()
    {
        var migration = new AddOutlookBrowseTargetPath();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        var exception = Assert.Throws<TargetInvocationException>(() =>
            typeof(AddOutlookBrowseTargetPath).GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, [builder]));

        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    [NativeSqlServerFact]
    public async Task Expired_targeted_browse_is_terminalised_and_discards_its_private_target()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
            {
                Id = browseId,
                ProfileId = seed.ProfileId,
                CorrelationId = Guid.NewGuid(),
                ConfigurationRevision = 1,
                State = 0,
                ExpiresAtUtc = Now.AddMinutes(-1),
                TargetPath = "Mailbox/Action",
                TargetPathFingerprint = "will-be-discarded"
            });
            await context.SaveChangesAsync();
        }

        var receipt = await CreateStore().ClaimBrowseAsync(
            new OutlookBrowseClaimRequest(Guid.NewGuid(), Fingerprint, browseId, Host, Now.AddMinutes(3)),
            CancellationToken.None);

        Assert.False(receipt.Accepted);
        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Equal(3, row.State);
        Assert.Equal((int)OutlookBrowseFailureCode.Expired, row.FailureCode);
        Assert.Null(row.TargetPath);
        Assert.Null(row.TargetPathFingerprint);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAtUtc);
    }


    [NativeSqlServerFact]
    public async Task Stale_legacy_browse_claim_is_terminalised_not_requeued()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
            {
                Id = browseId,
                ProfileId = seed.ProfileId,
                CorrelationId = Guid.NewGuid(),
                ConfigurationRevision = 1,
                State = 1,
                ExpiresAtUtc = Now.AddMinutes(5),
                LeaseOwner = Owner(Host),
                LeaseExpiresAtUtc = Now.AddMinutes(-1),
                FencingToken = 3,
                TargetPath = null
            });
            await context.SaveChangesAsync();
        }

        await CreateStore().ReleaseStaleBrowseClaimsAsync(Guid.NewGuid(), Fingerprint, Now, CancellationToken.None);

        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Equal(3, row.State);
        Assert.Equal((int)OutlookBrowseFailureCode.Failed, row.FailureCode);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAtUtc);
        Assert.Null(row.TargetPath);
    }

    [NativeSqlServerFact]
    public async Task Legacy_completed_browse_cannot_enable_a_profile_even_when_it_has_a_result()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var gate = await SeedCompletedBrowseAsync(seed.ProfileId, targeted: false);
        var request = Save(Guid.NewGuid(), "Must remain disabled") with
        {
            ProfileId = new OutlookCaptureProfileId(seed.ProfileId),
            Enable = true,
            ExpectedConfigurationRevision = gate.ConfigurationRevision,
            BrowseCorrelationId = gate.BrowseCorrelationId
        };

        var receipt = await CreateStore().SaveProfileAsync(request, CancellationToken.None);

        Assert.False(receipt.Accepted);
    }

    [NativeSqlServerFact]
    public async Task Targeted_completion_discards_the_raw_path_but_retains_private_targeted_provenance()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(ClaimedBrowse(browseId, seed.ProfileId, 7, "Mailbox/Action"));
            await context.SaveChangesAsync();
        }

        var receipt = await CreateStore().CompleteBrowseAsync(BrowseCompletion(browseId, OutlookCaptureFolderId.New(), 7), CancellationToken.None);

        Assert.True(receipt.Accepted);
        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Null(row.TargetPath);
        Assert.NotNull(row.TargetPathFingerprint);
    }

    [NativeSqlServerFact]
    public async Task Broad_completion_from_a_pre_targeting_host_is_terminalised_without_creating_folders()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(ClaimedBrowse(browseId, seed.ProfileId, 11, "Mailbox/Action"));
            await context.SaveChangesAsync();
        }
        var firstFolder = OutlookCaptureFolderId.New();
        var secondFolder = OutlookCaptureFolderId.New();
        var request = new OutlookBrowseCompletionRequest(
            Guid.NewGuid(), Fingerprint, browseId, Host, 11,
            [new OutlookBrowseFolderProjection(firstFolder, "Action"), new OutlookBrowseFolderProjection(secondFolder, "Inbox")],
            1,
            [new OutlookBrowseFolderResult(firstFolder, "store", "action", "Action"), new OutlookBrowseFolderResult(secondFolder, "store", "inbox", "Inbox")]);

        var receipt = await CreateStore().CompleteBrowseAsync(request, CancellationToken.None);

        Assert.False(receipt.Accepted);
        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Equal(3, row.State);
        Assert.Equal((int)OutlookBrowseFailureCode.Failed, row.FailureCode);
        Assert.Empty(await verification.OutlookCaptureFolders.Where(folder => folder.ProfileId == seed.ProfileId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Legacy_leased_untargeted_browse_cannot_complete_or_create_a_folder()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        const long token = 19;
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
            {
                Id = browseId,
                ProfileId = seed.ProfileId,
                CorrelationId = Guid.NewGuid(),
                ConfigurationRevision = 1,
                State = 1,
                ExpiresAtUtc = Now.AddMinutes(5),
                LeaseOwner = Owner(Host),
                LeaseExpiresAtUtc = Now.AddMinutes(3),
                FencingToken = token,
                TargetPath = null
            });
            await context.SaveChangesAsync();
        }

        var folderId = OutlookCaptureFolderId.New();
        var receipt = await CreateStore().CompleteBrowseAsync(
            new OutlookBrowseCompletionRequest(
                Guid.NewGuid(),
                Fingerprint,
                browseId,
                Host,
                token,
                [new OutlookBrowseFolderProjection(folderId, "Action")],
                1,
                [new OutlookBrowseFolderResult(folderId, "store", "folder", "Action")]),
            CancellationToken.None);

        Assert.False(receipt.Accepted);
        await using var verification = await CreateContextAsync();
        var row = await verification.OutlookBrowseRequests.SingleAsync(item => item.Id == browseId);
        Assert.Equal(3, row.State);
        Assert.Equal((int)OutlookBrowseFailureCode.Failed, row.FailureCode);
        Assert.Empty(await verification.OutlookCaptureFolders.Where(folder => folder.ProfileId == seed.ProfileId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_browse_completions_reuse_one_canonical_folder()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var firstBrowseId = Guid.NewGuid();
        var secondBrowseId = Guid.NewGuid();
        const long fencingToken = 11;
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.AddRange(
                ClaimedBrowse(firstBrowseId, seed.ProfileId, fencingToken),
                ClaimedBrowse(secondBrowseId, seed.ProfileId, fencingToken));
            await context.SaveChangesAsync();
        }

        var firstFolderId = OutlookCaptureFolderId.New();
        var secondFolderId = OutlookCaptureFolderId.New();
        var firstRequest = BrowseCompletion(firstBrowseId, firstFolderId, fencingToken);
        var secondRequest = BrowseCompletion(secondBrowseId, secondFolderId, fencingToken);
        var barrier = new QueryBarrierInterceptor("OutlookCaptureFolders", 2, "WHERE [o].[ProfileId]");
        var store = CreateStore(barrier);

        var receipts = await Task.WhenAll(
            store.CompleteBrowseAsync(firstRequest, CancellationToken.None).AsTask(),
            store.CompleteBrowseAsync(secondRequest, CancellationToken.None).AsTask());

        Assert.All(receipts, receipt => Assert.True(receipt.Accepted));
        await using var assertionContext = await CreateContextAsync();
        var folder = await assertionContext.OutlookCaptureFolders.SingleAsync(row =>
            row.ProfileId == seed.ProfileId && row.StoreId == "shared-store" && row.FolderEntryId == "shared-folder");
        var resultFolderIds = await assertionContext.OutlookBrowseResults
            .Where(row => row.BrowseRequestId == firstBrowseId || row.BrowseRequestId == secondBrowseId)
            .Select(row => row.FolderId)
            .ToListAsync();
        Assert.Equal([folder.Id, folder.Id], resultFolderIds.Order().ToArray());
    }

    private SqlOutlookCaptureStore CreateStore(
        IInterceptor? interceptor = null,
        bool useRetryingExecutionStrategy = false,
        DateTimeOffset? now = null) =>
        new(new TestDbContextFactory(_fixture.ConnectionString, interceptor, useRetryingExecutionStrategy), new ManualTimeProvider(now ?? Now));

    private async Task<FluxKnowledgeDbContext> CreateContextAsync() =>
        await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();

    private async Task<CaptureSeed> SeedCaptureAsync(bool includeFolder = true, bool includeActiveCatchUp = true)
    {
        var profileId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var catchUpId = includeActiveCatchUp ? Guid.NewGuid() : Guid.Empty;
        var sourceRootId = Guid.NewGuid();
        const long fencingToken = 17;
        await using var context = await CreateContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = sourceRootId,
            CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
            DisplayName = "Private Outlook capture",
            State = (int)SourceRootState.Paused,
            Recursive = false,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]",
            ReconciliationCadenceSeconds = 86400,
            ConfigurationRevision = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
        {
            Id = profileId,
            SourceRootId = sourceRootId,
            DisplayName = $"Profile-{profileId:N}",
            SpoolRoot = "C:\\private\\outlook",
            IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
            State = (int)OutlookCaptureState.CatchingUp,
            IsEnabled = true,
            ConfigurationRevision = 1,
            CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
            MaximumOverlapTicks = TimeSpan.FromMinutes(5).Ticks,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        if (includeFolder)
        {
            context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
            {
                Id = folderId,
                ProfileId = profileId,
                StoreId = $"store-{profileId:N}",
                FolderEntryId = $"folder-{folderId:N}",
                DisplayName = "Inbox",
                Basis = (int)OutlookIncrementalBasis.LastModificationTime,
                State = (int)OutlookCaptureState.CatchingUp
            });
        }
        if (includeActiveCatchUp)
        {
            context.OutlookCatchUps.Add(new OutlookCatchUpEntity
            {
                Id = catchUpId,
                ProfileId = profileId,
                CoalescingKey = $"catch-up-{profileId:N}",
                Provenance = (int)OutlookCatchUpProvenance.Manual,
                State = 1,
                LeaseOwner = Owner(Host),
                LeaseExpiresAtUtc = Now.AddMinutes(10),
                LastHeartbeatAtUtc = Now,
                FencingToken = fencingToken
            });
        }
        await context.SaveChangesAsync();
        return new CaptureSeed(profileId, folderId, catchUpId, fencingToken);
    }

    private async Task<(long ConfigurationRevision, Guid BrowseCorrelationId)> SeedCompletedBrowseAsync(Guid profileId, bool targeted = true)
    {
        var browseRequestId = Guid.NewGuid();
        var browseCorrelationId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        await using var context = await CreateContextAsync();
        var configurationRevision = await context.OutlookCaptureProfiles
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.ConfigurationRevision)
            .SingleAsync();
        context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
        {
            Id = folderId,
            ProfileId = profileId,
            StoreId = $"update-store-{profileId:N}",
            FolderEntryId = $"update-folder-{folderId:N}",
            DisplayName = "Inbox",
            Basis = (int)OutlookIncrementalBasis.LastModificationTime,
            State = (int)OutlookCaptureState.Ready
        });
        context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
        {
            Id = browseRequestId,
            ProfileId = profileId,
            CorrelationId = browseCorrelationId,
            ConfigurationRevision = configurationRevision,
            State = 2,
            ExpiresAtUtc = Now.AddHours(1),
            TargetPathFingerprint = targeted ? new string('a', 64) : null
        });
        context.OutlookBrowseResults.Add(new OutlookBrowseResultEntity
        {
            Id = Guid.NewGuid(),
            BrowseRequestId = browseRequestId,
            FolderId = folderId,
            DisplayName = "Inbox"
        });
        await context.SaveChangesAsync();
        return (configurationRevision, browseCorrelationId);
    }

    private static OutlookProfileSaveRequest Save(Guid operation, string name, string? fingerprint = null) => new(
        operation,
        fingerprint ?? Fingerprint,
        null,
        name,
        OutlookIncrementalBasis.LastModificationTime,
        new OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)),
        new OutlookSpoolValidation(Fingerprint, true, true, true, true, "C:\\private\\outlook"));

    private static OutlookBrowseRequestEntity ClaimedBrowse(Guid browseId, Guid profileId, long fencingToken, string targetPath = "Mailbox/Shared") => new()
    {
        Id = browseId,
        ProfileId = profileId,
        CorrelationId = Guid.NewGuid(),
        ConfigurationRevision = 1,
        State = 1,
        ExpiresAtUtc = Now.AddHours(1),
        LeaseOwner = Owner(Host),
        LeaseExpiresAtUtc = Now.AddMinutes(10),
        FencingToken = fencingToken,
        TargetPath = targetPath,
        TargetPathFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(targetPath)))
    };

    private static OutlookBrowseCompletionRequest BrowseCompletion(
        Guid browseId,
        OutlookCaptureFolderId folderId,
        long fencingToken) => new(
        Guid.NewGuid(),
        Fingerprint,
        browseId,
        Host,
        fencingToken,
        [new OutlookBrowseFolderProjection(folderId, "Shared")],
        1,
        [new OutlookBrowseFolderResult(folderId, "shared-store", "shared-folder", "Shared")]);

    private static async Task<OperationOutcome> CaptureAsync(Func<Task<OutlookOperationReceipt>> action)
    {
        try
        {
            return new OperationOutcome(await action(), null);
        }
        catch (Exception exception)
        {
            return new OperationOutcome(null, exception);
        }
    }

    private static string Owner(OutlookHostIdentity host) =>
        $"{host.WindowsUserSid}|{host.SessionId}|{host.HostInstanceId}";

    private sealed record CaptureSeed(Guid ProfileId, Guid FolderId, Guid CatchUpId, long FencingToken);
    private sealed record OperationOutcome(OutlookOperationReceipt? Receipt, Exception? Exception);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options;

        public TestDbContextFactory(
            string connectionString,
            IInterceptor? interceptor,
            bool useRetryingExecutionStrategy)
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>();
            builder.UseSqlServer(
                connectionString,
                sqlServer =>
                {
                    if (useRetryingExecutionStrategy)
                    {
                        sqlServer.EnableRetryOnFailure();
                    }
                });
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }
            _options = builder.Options;
        }

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class QueryBarrierInterceptor(
        string tableName,
        int participants,
        string? requiredCommandFragment = null) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(tableName, StringComparison.Ordinal) &&
                (requiredCommandFragment is null || command.CommandText.Contains(requiredCommandFragment, StringComparison.Ordinal)))
            {
                var arrival = Interlocked.Increment(ref _arrivals);
                if (arrival == participants)
                {
                    _release.TrySetResult();
                }
                if (arrival <= participants)
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed class RowVersionConflictInterceptor(string tableName, Guid rowId) : SaveChangesInterceptor
    {
        private int _triggered;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                var connectionString = eventData.Context?.Database.GetConnectionString()
                    ?? throw new InvalidOperationException("A SQL connection is required to force a rowversion conflict.");
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE [{tableName}] SET [Id] = [Id] WHERE [Id] = @rowId";
                command.Parameters.AddWithValue("@rowId", rowId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
