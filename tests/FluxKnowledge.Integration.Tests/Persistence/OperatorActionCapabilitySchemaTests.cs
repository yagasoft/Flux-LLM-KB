using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Domain.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OperatorActionCapabilitySchemaTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearOoxmlOperatorActionDataAsync(_fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Capability_policy_and_identity_ledgers_use_the_exact_restrictive_contract()
    {
        using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=OperatorActionCapabilitySchemaModel;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var policy = model.FindEntityType(typeof(OperatorActionCapabilityPolicyEntity))!;
        Assert.Equal(
            [
                nameof(OperatorActionCapabilityPolicyEntity.PolicyId),
                nameof(OperatorActionCapabilityPolicyEntity.PolicyRevision),
                nameof(OperatorActionCapabilityPolicyEntity.DescriptorId),
                nameof(OperatorActionCapabilityPolicyEntity.DescriptorFingerprint),
                nameof(OperatorActionCapabilityPolicyEntity.DescriptorVersion),
                nameof(OperatorActionCapabilityPolicyEntity.SafetyContractId),
                nameof(OperatorActionCapabilityPolicyEntity.HandlerId),
                nameof(OperatorActionCapabilityPolicyEntity.ActionKind),
                nameof(OperatorActionCapabilityPolicyEntity.ReasonCode)
            ],
            policy.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.All(policy.GetProperties(), property =>
        {
            if (!property.IsPrimaryKey()) return;
            Assert.Equal(PropertySaveBehavior.Throw, property.GetAfterSaveBehavior());
        });

        var action = model.FindEntityType(typeof(OperatorActionActionLedgerEntity))!;
        Assert.Contains(
            action.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType == policy &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                [
                    nameof(OperatorActionActionLedgerEntity.PolicyId),
                    nameof(OperatorActionActionLedgerEntity.PolicyRevision),
                    nameof(OperatorActionActionLedgerEntity.DescriptorId),
                    nameof(OperatorActionActionLedgerEntity.DescriptorFingerprint),
                    nameof(OperatorActionActionLedgerEntity.DescriptorVersion),
                    nameof(OperatorActionActionLedgerEntity.SafetyContractId),
                    nameof(OperatorActionActionLedgerEntity.HandlerId),
                    nameof(OperatorActionActionLedgerEntity.ActionKind),
                    nameof(OperatorActionActionLedgerEntity.ReasonCode)
                ]));

        var operation = model.FindEntityType(typeof(OperatorActionOperationLedgerEntity))!;
        Assert.Contains(operation.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(OperatorActionOperationLedgerEntity.OperationId)]));
        Assert.Contains(operation.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType == action && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Equal(PropertySaveBehavior.Throw,
            operation.FindProperty(nameof(OperatorActionOperationLedgerEntity.IgnoreSequence))!.GetAfterSaveBehavior());
        Assert.Equal(PropertySaveBehavior.Throw,
            operation.FindProperty(nameof(OperatorActionOperationLedgerEntity.IgnoreState))!.GetAfterSaveBehavior());

        var request = model.FindEntityType(typeof(SourceProcessorForceRequestEntity))!;
        Assert.Contains(
            request.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType == policy &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                [
                    nameof(SourceProcessorForceRequestEntity.PolicyId),
                    nameof(SourceProcessorForceRequestEntity.PolicyRevision),
                    nameof(SourceProcessorForceRequestEntity.DescriptorId),
                    nameof(SourceProcessorForceRequestEntity.DescriptorFingerprint),
                    nameof(SourceProcessorForceRequestEntity.DescriptorVersion),
                    nameof(SourceProcessorForceRequestEntity.SafetyContractId),
                    nameof(SourceProcessorForceRequestEntity.HandlerId),
                    nameof(SourceProcessorForceRequestEntity.ActionKind),
                    nameof(SourceProcessorForceRequestEntity.PolicyReasonCode)
                ]));

        var ignoreHead = model.FindEntityType(typeof(SourceProcessorActionIgnoreHeadEntity))!;
        Assert.Contains(ignoreHead.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(SourceProcessorActionIgnoreHeadEntity.ActionId)]));
    }

    [NativeSqlServerFact]
    public async Task Hard_denial_trigger_rejects_every_closed_reason_from_capability_policy_membership()
    {
        await using var context = CreateContext();

        foreach (var reasonCode in OperatorActionHardDenialReasons.All)
        {
            context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
            {
                PolicyId = Guid.NewGuid(),
                PolicyRevision = 1,
                DescriptorId = Guid.NewGuid(),
                DescriptorFingerprint = "phase-5-schema-test",
                DescriptorVersion = "phase-5-schema-test",
                SafetyContractId = "retained-binding",
                HandlerId = "schema-test",
                ActionKind = "retry",
                ReasonCode = reasonCode
            });

            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.IsType<SqlException>(failure.InnerException);
            context.ChangeTracker.Clear();
        }
    }

    [NativeSqlServerFact]
    public async Task Direct_sql_cannot_delete_or_mutate_hard_denials_or_capability_policies()
    {
        var policyId = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            seed.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
            {
                PolicyId = policyId, PolicyRevision = 1, DescriptorId = Guid.NewGuid(),
                DescriptorFingerprint = "operator-action-policy-immutable", DescriptorVersion = "v1",
                SafetyContractId = "retained-binding", HandlerId = "schema-test", ActionKind = "ignore",
                ReasonCode = "operator-action-policy-immutable"
            });
            await seed.SaveChangesAsync();
        }

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE [OperatorActionHardDenials] SET [ReasonCode] = [ReasonCode] WHERE [ReasonCode] = N'processor-fence-invalid';",
                     "DELETE FROM [OperatorActionHardDenials] WHERE [ReasonCode] = N'processor-fence-invalid';",
                     $"UPDATE [OperatorActionCapabilityPolicies] SET [HandlerId] = [HandlerId] WHERE [PolicyId] = '{policyId}';",
                     $"DELETE FROM [OperatorActionCapabilityPolicies] WHERE [PolicyId] = '{policyId}';"
                 })
        {
            await using var command = new SqlCommand(sql, connection);
            await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [NativeSqlServerFact]
    public async Task Upgrade_backfills_a_legacy_force_receipt_into_exact_action_and_operation_ledgers()
    {
        await using var database = await _fixture.CreateOperatorActionCapabilityPreviousMigrationDatabaseAsync();
        var legacy = await SeedLegacyForceReceiptAsync(database);

        await using (var upgrade = database.CreateContext())
        {
            await upgrade.GetService<IMigrator>().MigrateAsync();
        }

        await using var verification = database.CreateContext();
        var action = await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == legacy.ActionId);
        var operation = await verification.OperatorActionOperationLedger.SingleAsync(value => value.OperationId == legacy.OperationId);
        Assert.Equal(legacy.BranchId, action.SourceProcessorBranchId);
        Assert.Equal(legacy.ForceRequestId, action.SourceProcessorForceRequestId);
        Assert.Equal(legacy.ActionId, operation.ActionId);
        Assert.Equal(legacy.RequestFingerprint, operation.RequestFingerprint);
    }

    [NativeSqlServerFact]
    public async Task First_ignore_operation_creates_one_head_and_replays_its_original_receipt()
    {
        var actionId = new string('a', 64);
        var operationId = Guid.NewGuid();
        var blockedRowVersion = new byte[8];
        var requestFingerprint = CanonicalRequestFingerprint(actionId, blockedRowVersion);
        await SeedActionAsync(actionId);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        var created = await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, blockedRowVersion, IsIgnored: true),
            CancellationToken.None);
        var replay = await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, blockedRowVersion, IsIgnored: true),
            CancellationToken.None);

        Assert.Equal(1, created.Sequence);
        Assert.True(created.IsIgnored);
        Assert.False(created.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.Equal(created.Sequence, replay.Sequence);
        Assert.Equal(created.IsIgnored, replay.IsIgnored);
        Assert.Equal(created.CommittedAtUtc, replay.CommittedAtUtc);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceProcessorActionIgnoreHeads.Where(value => value.ActionId == actionId).ToListAsync());
        Assert.Single(await verification.OperatorActionOperationLedger.Where(value => value.OperationId == operationId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Ignore_rejects_a_stale_blocked_row_version_and_a_replayed_operation_with_a_different_state()
    {
        var actionId = new string('d', 64);
        var operationId = Guid.NewGuid();
        var blockedRowVersion = new byte[8];
        var staleRowVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };
        var requestFingerprint = CanonicalRequestFingerprint(actionId, blockedRowVersion);
        await SeedActionAsync(actionId);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        var stale = await Assert.ThrowsAsync<OperatorActionRejectedException>(async () =>
            await store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(actionId, Guid.NewGuid(), CanonicalRequestFingerprint(actionId, staleRowVersion), staleRowVersion, IsIgnored: true),
                CancellationToken.None));
        await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, blockedRowVersion, IsIgnored: true),
            CancellationToken.None);
        var collision = await Assert.ThrowsAsync<OperatorActionRejectedException>(async () =>
            await store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, blockedRowVersion, IsIgnored: false),
                CancellationToken.None));

        Assert.Equal("operator-action-stale", stale.ReasonCode);
        Assert.Equal("operator-operation-conflict", collision.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Ignore_replay_rejects_a_changed_expected_blocked_row_version()
    {
        var actionId = new string('6', 64);
        var operationId = Guid.NewGuid();
        var blockedRowVersion = new byte[8];
        var requestFingerprint = CanonicalRequestFingerprint(actionId, blockedRowVersion);
        await SeedActionAsync(actionId);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, blockedRowVersion, IsIgnored: true),
            CancellationToken.None);
        var replay = await Assert.ThrowsAsync<OperatorActionRejectedException>(async () =>
            await store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(actionId, operationId, requestFingerprint, new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, IsIgnored: true),
                CancellationToken.None));

        Assert.Equal("operator-operation-conflict", replay.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_divergent_operation_insert_race_maps_an_incompatible_winner_to_domain_conflict()
    {
        var operationId = Guid.NewGuid();
        var firstActionId = new string('4', 64);
        var secondActionId = new string('5', 64);
        var blockedRowVersion = new byte[8];
        await SeedActionAsync(firstActionId);
        await SeedActionAsync(secondActionId);
        var race = new OperationInsertRaceInterceptor();
        var saveFailures = new SaveFailureObserver();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString, race, saveFailures), TimeProvider.System);
        await using (var before = CreateContext())
        {
            Assert.Empty(await before.OperatorActionOperationLedger
                .Where(value => value.OperationId == operationId)
                .ToListAsync());
        }

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(firstActionId, operationId, CanonicalRequestFingerprint(firstActionId, blockedRowVersion), blockedRowVersion, IsIgnored: true),
                CancellationToken.None).AsTask()),
            CaptureAsync(() => store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(secondActionId, operationId, CanonicalRequestFingerprint(secondActionId, blockedRowVersion), blockedRowVersion, IsIgnored: false),
                CancellationToken.None).AsTask()));

        Assert.Equal(2, race.InitialOperationLedgerReads);
        Assert.Equal(1, saveFailures.Count);
        Assert.Single(outcomes, outcome => outcome.Receipt is not null);
        var failure = Assert.Single(outcomes, outcome => outcome.Exception is not null).Exception;
        var rejection = Assert.IsType<OperatorActionRejectedException>(failure);
        Assert.Equal("operator-operation-conflict", rejection.ReasonCode);
        Assert.IsNotType<DbUpdateException>(failure);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionOperationLedger
            .Where(value => value.OperationId == operationId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_first_ignore_operations_allocate_one_head_with_distinct_sequences()
    {
        var actionId = new string('f', 64);
        var blockedRowVersion = new byte[8];
        var requestFingerprint = CanonicalRequestFingerprint(actionId, blockedRowVersion);
        await SeedActionAsync(actionId);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        var receipts = await Task.WhenAll(
            store.SetOperatorActionIgnoreAsync(new OperatorActionIgnoreCommand(actionId, Guid.NewGuid(), requestFingerprint, blockedRowVersion, IsIgnored: true), CancellationToken.None).AsTask(),
            store.SetOperatorActionIgnoreAsync(new OperatorActionIgnoreCommand(actionId, Guid.NewGuid(), requestFingerprint, blockedRowVersion, IsIgnored: false), CancellationToken.None).AsTask());

        Assert.Equal([1L, 2L], receipts.Select(receipt => receipt.Sequence).Order());
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceProcessorActionIgnoreHeads.Where(value => value.ActionId == actionId).ToListAsync());
        Assert.Equal(2, await verification.OperatorActionOperationLedger.CountAsync(value => value.ActionId == actionId));
    }

    private static string CanonicalRequestFingerprint(string actionId, ReadOnlySpan<byte> blockedRowVersion) =>
        OoxmlForceRequestIdentity.CreateRequestFingerprint(
            actionId,
            OoxmlForceRequestIdentity.EncodeBlockedRowVersion(blockedRowVersion));

    private async Task SeedActionAsync(string actionId)
    {
        var now = DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = $"C:\\operator-action-schema\\{rootId:N}", DisplayName = "Operator action schema", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"operator-action-schema:{revisionId:N}", Revision = 1,
            ContentSha256 = new string('c', 64), CanonicalPath = "C:\\operator-action-schema\\opaque.txt", Classification = "AcceptedUtf8Text",
            Extension = ".txt", ByteLength = 1, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId, SourceRevisionId = revisionId, ActivityKind = 0, ExecutionClass = 0, ProcessorVersion = "operator-action-schema",
            InputFingerprint = new string('c', 64), State = 0, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId, InputSha256 = new string('c', 64),
            ProcessorVersion = "operator-action-schema", ProcessorFingerprint = "operator-action-schema", State = 3,
            LeaseGeneration = 1, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
        {
            PolicyId = policyId, PolicyRevision = 1, DescriptorId = Guid.NewGuid(), DescriptorFingerprint = "operator-action-schema",
            DescriptorVersion = "operator-action-schema", SafetyContractId = "retained-binding", HandlerId = "operator-action-schema",
            ActionKind = "ignore", ReasonCode = "operator-action-schema"
        });
        context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
        {
            ActionId = actionId, PolicyId = policyId, PolicyRevision = 1, DescriptorId = context.OperatorActionCapabilityPolicies.Local.Single().DescriptorId,
            DescriptorFingerprint = "operator-action-schema", DescriptorVersion = "operator-action-schema", SafetyContractId = "retained-binding",
            HandlerId = "operator-action-schema", ActionKind = "ignore", ReasonCode = "operator-action-schema", SourceProcessorBranchId = branchId,
            BlockedRowVersion = new byte[8], CreatedAtUtc = now
        });
        await context.SaveChangesAsync();
    }

    private static async Task<(Guid BranchId, Guid ForceRequestId, Guid OperationId, string ActionId, string RequestFingerprint)> SeedLegacyForceReceiptAsync(
        NativeSqlServerFixture.PreviousMigrationDatabase database)
    {
        var now = DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var actionId = new string('9', 64);
        var requestFingerprint = new string('8', 64);
        var forceRequestId = Guid.NewGuid();
        var descriptorId = Guid.NewGuid();
        const string hash = "7777777777777777777777777777777777777777777777777777777777777777";
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO [SourceRootConfigurations] ([Id], [AllowedClassificationsJson], [CanonicalPath], [ConfigurationRevision], [CrawlMode], [CreatedAtUtc], [DisplayName], [ExcludePatternsJson], [FollowLinks], [IncludePatternsJson], [MaximumFileBytes], [ReconciliationCadenceSeconds], [Recursive], [State], [UpdatedAtUtc])
            VALUES (@rootId, N'[]', @rootPath, 1, 0, @now, N'Operator action upgrade', N'[]', 0, N'[]', 1024, 900, 1, 0, @now);

            INSERT INTO [SourceRevisions] ([Id], [ByteLength], [CanonicalPath], [Classification], [ContentSha256], [DiscoveredAtUtc], [DiscoveryEvidenceJson], [Extension], [Revision], [SourceRootId], [StableSourceIdentity])
            VALUES (@revisionId, 1, N'C:\\operator-action-upgrade\\opaque.docx', N'OoxmlDocumentContainer', @hash, @now, N'{}', N'.docx', 1, @rootId, @stableSourceIdentity);

            INSERT INTO [SourceActivities] ([Id], [ActivityKind], [AttemptCount], [AttemptEvidenceJson], [CreatedAtUtc], [ExecutionClass], [InputFingerprint], [ProcessorVersion], [SourceRevisionId], [State], [UpdatedAtUtc])
            VALUES (@activityId, 0, 0, N'{}', @now, 0, @hash, N'operator-action-upgrade', @revisionId, 0, @now);

            INSERT INTO [SourceProcessorBranches] ([Id], [AttemptCount], [CompletedMemberCount], [CreatedAtUtc], [InputSha256], [LeaseGeneration], [ProcessorFingerprint], [ProcessorVersion], [SourceActivityId], [SourceRevisionId], [State], [UpdatedAtUtc])
            VALUES (@branchId, 1, 0, @now, @hash, 1, N'operator-action-upgrade', N'operator-action-upgrade', @activityId, @revisionId, 3, @now);

            INSERT INTO [SourceProcessorForceRequests] ([Id], [ActionId], [ClaimExpiresAtUtc], [DescriptorFingerprint], [DescriptorId], [ExpectedInputSha256], [OperationId], [OriginalBlockedLeaseGeneration], [OriginalBlockedRowVersion], [OriginalOutcomeCode], [RequestFingerprint], [RequestedAtUtc], [SourceActivityId], [SourceProcessorBranchId], [SourceRevisionId], [State])
            VALUES (@forceRequestId, @actionId, @claimExpiresAtUtc, N'operator-action-upgrade', @descriptorId, @hash, @operationId, 1, @rowVersion, N'office-document-encrypted', @requestFingerprint, @now, @activityId, @branchId, @revisionId, 0);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@rootId", rootId);
        command.Parameters.AddWithValue("@rootPath", $"C:\\operator-action-upgrade\\{rootId:N}");
        command.Parameters.AddWithValue("@revisionId", revisionId);
        command.Parameters.AddWithValue("@stableSourceIdentity", $"operator-action-upgrade:{revisionId:N}");
        command.Parameters.AddWithValue("@activityId", activityId);
        command.Parameters.AddWithValue("@branchId", branchId);
        command.Parameters.AddWithValue("@forceRequestId", forceRequestId);
        command.Parameters.AddWithValue("@descriptorId", descriptorId);
        command.Parameters.AddWithValue("@operationId", operationId);
        command.Parameters.AddWithValue("@actionId", actionId);
        command.Parameters.AddWithValue("@requestFingerprint", requestFingerprint);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@rowVersion", new byte[8]);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@claimExpiresAtUtc", now.AddMinutes(5));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return (branchId, forceRequestId, operationId, actionId, requestFingerprint);
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    private static async Task<OperationOutcome> CaptureAsync(Func<Task<OperatorActionIgnoreReceipt>> action)
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

    private sealed record OperationOutcome(OperatorActionIgnoreReceipt? Receipt, Exception? Exception);

    private sealed class ContextFactory(string connectionString, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = CreateOptions(connectionString, interceptors);

        private static DbContextOptions<FluxKnowledgeDbContext> CreateOptions(string connectionString, IReadOnlyCollection<IInterceptor> interceptors)
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString);
            if (interceptors.Count > 0)
            {
                builder.AddInterceptors(interceptors);
            }

            return builder.Options;
        }

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class OperationInsertRaceInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operationLedgerReads;

        public int InitialOperationLedgerReads => Volatile.Read(ref _operationLedgerReads);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[OperatorActionOperationLedger] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("WITH (UPDLOCK, HOLDLOCK)", "WITH (READUNCOMMITTED)", StringComparison.Ordinal);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[OperatorActionOperationLedger] WITH (READUNCOMMITTED)", StringComparison.Ordinal))
            {
                var arrival = Interlocked.Increment(ref _operationLedgerReads);
                if (arrival == 2)
                {
                    _release.TrySetResult();
                }

                if (arrival <= 2)
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed class SaveFailureObserver : SaveChangesInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is DbUpdateException)
            {
                Interlocked.Increment(ref _count);
            }

            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
    }
}
