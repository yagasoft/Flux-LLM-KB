using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task Folder_digest_length_prefix_distinguishes_delimiter_collisions()
    {
        var seed = await SeedCaptureAsync(includeFolder: false, includeActiveCatchUp: false);
        var browseId = Guid.NewGuid();
        const long fencingToken = 9;
        await using (var context = await CreateContextAsync())
        {
            context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity
            {
                Id = browseId,
                ProfileId = seed.ProfileId,
                CorrelationId = Guid.NewGuid(),
                ConfigurationRevision = 1,
                State = 1,
                ExpiresAtUtc = Now.AddHours(1),
                LeaseOwner = Owner(Host),
                LeaseExpiresAtUtc = Now.AddMinutes(10),
                FencingToken = fencingToken
            });
            await context.SaveChangesAsync();
        }

        var firstId = OutlookCaptureFolderId.New();
        var secondId = OutlookCaptureFolderId.New();
        var projections = new[]
        {
            new OutlookBrowseFolderProjection(firstId, "First"),
            new OutlookBrowseFolderProjection(secondId, "Second")
        };
        var privateFolders = new[]
        {
            new OutlookBrowseFolderResult(firstId, "alpha|beta", "gamma", "First"),
            new OutlookBrowseFolderResult(secondId, "alpha", "beta|gamma", "Second")
        };

        var receipt = await CreateStore().CompleteBrowseAsync(
            new OutlookBrowseCompletionRequest(Guid.NewGuid(), Fingerprint, browseId, Host, fencingToken, projections, 1, privateFolders),
            CancellationToken.None);

        Assert.True(receipt.Accepted);
        await using var assertionContext = await CreateContextAsync();
        var folders = await assertionContext.OutlookCaptureFolders
            .Where(row => row.ProfileId == seed.ProfileId)
            .OrderBy(row => row.DisplayName)
            .ToListAsync();
        Assert.Equal(2, folders.Count);
        Assert.NotEqual(folders[0].CanonicalIdentityFingerprint, folders[1].CanonicalIdentityFingerprint);
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

    private SqlOutlookCaptureStore CreateStore(IInterceptor? interceptor = null) =>
        new(new TestDbContextFactory(_fixture.ConnectionString, interceptor), new ManualTimeProvider(Now));

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

    private async Task<(long ConfigurationRevision, Guid BrowseCorrelationId)> SeedCompletedBrowseAsync(Guid profileId)
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
            ExpiresAtUtc = Now.AddHours(1)
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

    private static OutlookBrowseRequestEntity ClaimedBrowse(Guid browseId, Guid profileId, long fencingToken) => new()
    {
        Id = browseId,
        ProfileId = profileId,
        CorrelationId = Guid.NewGuid(),
        ConfigurationRevision = 1,
        State = 1,
        ExpiresAtUtc = Now.AddHours(1),
        LeaseOwner = Owner(Host),
        LeaseExpiresAtUtc = Now.AddMinutes(10),
        FencingToken = fencingToken
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

        public TestDbContextFactory(string connectionString, IInterceptor? interceptor)
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString);
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
