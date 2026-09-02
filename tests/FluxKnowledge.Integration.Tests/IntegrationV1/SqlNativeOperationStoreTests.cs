using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed class SqlNativeOperationStoreTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00+00:00");
    private readonly NativeSqlServerFixture _fixture = fixture;
    private string _defaultRowVersion = string.Empty;

    [NativeSqlServerFact]
    public async Task CommitAsync_root_create_creates_only_released_durable_scan_work_and_replays_without_a_second_root()
    {
        await ClearAsync();
        const string payload = "{\"displayName\":\"Native test\",\"path\":\"C:\\\\native-v1-test\"}";
        var rootTarget = new NativeTargetVersion(
            $"root-path:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("C:\\native-v1-test")))}",
            "absent");
        var store = CreateStore(new ManualTimeProvider(Now));
        var fingerprint = NativeOperationCanonicalization.CreateRequestFingerprint("root_create", NativeOperationCanonicalization.CanonicalizeJson(payload), [rootTarget]);
        var preview = await store.CreatePreviewAsync(new NativeActionPreviewRequest("root_create", payload, "test") { Targets = [rootTarget], RequestFingerprint = fingerprint, EffectSummary = "Queue source-root creation." }, CancellationToken.None);
        var admission = new SourceRootPathValidation("C:\\native-v1-test", new SourceRootPhysicalIdentity("C:\\native-v1-test", "C:\\", true, new string('a', 64)), new SourceRootPermissionEvidence(true, new string('b', 64), "{}"));
        var commit = new NativeActionCommitRequest("root_create", payload, preview.ConfirmationId, "root-create", "test") { Targets = [rootTarget], RequestFingerprint = fingerprint, CommitOperation = new NativeCorpusMutationCommitOperation("root_create", NativeOperationCanonicalization.CanonicalizeJson(payload), admission) };

        var first = await store.CommitAsync(commit, CancellationToken.None);
        var replay = await store.CommitAsync(commit, CancellationToken.None);

        Assert.True(replay.WasReplay); Assert.Equal(first.OperationId, replay.OperationId);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var root = await context.SourceRootConfigurations.SingleAsync(value => value.DisplayName == "Native test");
        var request = await context.SourceScanRequests.SingleAsync(value => value.SourceRootId == root.Id);
        Assert.True(request.IsReleased);
        Assert.Equal(1, await context.SourceScanJobs.CountAsync(value => value.SourceScanRequestId == request.Id));
        Assert.Equal(1, await context.SourceScanOutbox.CountAsync(value => value.SourceScanRequestId == request.Id));
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_commits_once_and_replays_the_original_receipt_for_the_same_surface_key_and_fingerprint()
    {
        await ClearAsync();
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        var preview = await store.CreatePreviewAsync(Preview("{\"title\":\"safe\"}"), CancellationToken.None);
        var request = Commit("{\"title\":\"safe\"}", preview.ConfirmationId, "first-key");

        var first = await store.CommitAsync(request, CancellationToken.None);
        var replay = await store.CommitAsync(request, CancellationToken.None);

        Assert.False(first.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.OperationId, replay.OperationId);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await context.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task FindReceiptAsync_reads_the_durable_receipt_by_exact_surface_and_idempotency_key()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{\"title\":\"safe\"}"), CancellationToken.None);
        var committed = await store.CommitAsync(Commit("{\"title\":\"safe\"}", preview.ConfirmationId, "durable-stop-key"), CancellationToken.None);

        var replay = await CreateStore(new ManualTimeProvider(Now)).FindReceiptAsync("durable-stop-key", "mcp", CancellationToken.None);
        var wrongSurface = await CreateStore(new ManualTimeProvider(Now)).FindReceiptAsync("durable-stop-key", "other", CancellationToken.None);

        Assert.NotNull(replay);
        Assert.True(replay.WasReplay);
        Assert.Equal(committed.OperationId, replay.OperationId);
        Assert.Null(wrongSurface);
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_replays_a_codex_stop_identity_when_a_changed_summary_arrives_after_restart()
    {
        await ClearAsync();
        const string actor = "codex-hook";
        var key = "codex-stop-" + new string('a', 64);
        var intentId = Guid.NewGuid();
        await using (var seed = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            seed.NativeOperationIntents.Add(new NativeOperationIntentEntity
            {
                Id = intentId, Action = "note_create", ActorSurface = actor,
                RequestFingerprint = new string('b', 64), ConfirmationHash = new string('c', 64),
                TargetMetadataJson = "[]", CreatedAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(5), ConsumedAtUtc = Now
            });
            seed.NativeOperationReceipts.Add(new NativeOperationReceiptEntity
            {
                OperationId = Guid.NewGuid(), IntentId = intentId, Action = "note_create", ActorSurface = actor,
                IdempotencyKey = key, RequestFingerprint = new string('b', 64), Outcome = "completed", CompletedAtUtc = Now
            });
            await seed.SaveChangesAsync();
        }

        var changed = new KnowledgeMutation("note_create", Guid.NewGuid().ToString("D"), "different", "changed summary", null, null, null, null, null, null);
        var request = new NativeActionCommitRequest("note_create", "{}", "new-confirmation", key, actor)
        {
            RequestFingerprint = NativeOperationCanonicalization.CreateRequestFingerprint("note_create", "{}", []),
            CommitOperation = new KnowledgeMutationCommitOperation(changed)
        };

        var replay = await CreateStore(new ManualTimeProvider(Now)).CommitAsync(request, CancellationToken.None);

        Assert.True(replay.WasReplay);
        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await verify.NativeOperationReceipts.ToListAsync());
        Assert.Empty(await verify.KnowledgeItems.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_reports_expiry_mismatch_fencing_and_idempotency_collision_without_a_second_receipt()
    {
        await ClearAsync();
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        var expired = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(6);
        await AssertReasonAsync("confirmation-expired", () => store.CommitAsync(Commit("{}", expired.ConfirmationId, "expired"), CancellationToken.None));

        clock.UtcNow = Now;
        var preview = await store.CreatePreviewAsync(Preview("{\"a\":1}"), CancellationToken.None);
        await AssertReasonAsync("confirmation-mismatch", () => store.CommitAsync(Commit("{\"a\":2}", preview.ConfirmationId, "mismatch"), CancellationToken.None));
        await AssertReasonAsync("operation-fenced", () => store.CommitAsync(Commit("{\"a\":1}", preview.ConfirmationId, "fenced", "BBBBBBBBBBB="), CancellationToken.None));

        var successful = await store.CommitAsync(Commit("{\"a\":1}", preview.ConfirmationId, "shared"), CancellationToken.None);
        await AssertReasonAsync("idempotency-key-conflict", () => store.CommitAsync(Commit("{}", expired.ConfirmationId, "shared"), CancellationToken.None));
        Assert.NotEqual(Guid.Empty, successful.OperationId);
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_concurrent_requests_produce_one_durable_receipt()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        var request = Commit("{}", preview.ConfirmationId, "concurrent-key");

        var receipts = await Task.WhenAll(
            store.CommitAsync(request, CancellationToken.None).AsTask(),
            CreateStore(new ManualTimeProvider(Now)).CommitAsync(request, CancellationToken.None).AsTask());

        Assert.Single(receipts, receipt => !receipt.WasReplay);
        Assert.Single(receipts, receipt => receipt.WasReplay);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await context.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CreatePreviewAsync_honours_cancellation_before_a_durable_write_and_never_persists_the_raw_payload()
    {
        await ClearAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = CreateStore(new ManualTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreatePreviewAsync(Preview("{\"credential\":\"not-for-storage\"}"), cancellation.Token).AsTask());

        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.NativeOperationIntents.ToListAsync());
        Assert.Empty(await context.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_cancellation_before_transaction_entry_rolls_back_without_mutation_or_receipt()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CommitAsync(Commit("{}", preview.ConfirmationId, "cancel-before-entry"), cancellation.Token).AsTask());

        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.NativeOperationReceipts.ToListAsync());
        Assert.Null((await context.NativeOperationIntents.SingleAsync()).ConsumedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_rejects_a_missing_typed_operation_before_creating_a_receipt()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        await AssertReasonAsync("invalid-commit-operation", () => store.CommitAsync(Commit("{}", preview.ConfirmationId, "missing-operation") with { CommitOperation = null }, CancellationToken.None));
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_rejects_a_corpus_typed_operation_when_its_payload_or_root_admission_is_not_bound_to_the_fingerprinted_request()
    {
        await ClearAsync();
        const string payload = "{\"displayName\":\"Bound root\",\"path\":\"C:\\\\bound-root\"}";
        var canonicalPayload = NativeOperationCanonicalization.CanonicalizeJson(payload);
        var fingerprint = NativeOperationCanonicalization.CreateRequestFingerprint("root_create", canonicalPayload, []);
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(new NativeActionPreviewRequest("root_create", payload, "test")
        {
            RequestFingerprint = fingerprint,
            EffectSummary = "Queue source-root creation."
        }, CancellationToken.None);
        var admission = new SourceRootPathValidation("C:\\bound-root", new SourceRootPhysicalIdentity("C:\\bound-root", "C:\\", true, new string('a', 64)), new SourceRootPermissionEvidence(true, new string('b', 64), "{}"));

        var wrongPayload = new NativeActionCommitRequest("root_create", payload, preview.ConfirmationId, "wrong-corpus-payload", "test")
        {
            RequestFingerprint = fingerprint,
            CommitOperation = new NativeCorpusMutationCommitOperation("root_create", NativeOperationCanonicalization.CanonicalizeJson("{\"displayName\":\"Different\",\"path\":\"C:\\\\bound-root\"}"), admission)
        };
        await AssertReasonAsync("invalid-commit-operation", () => store.CommitAsync(wrongPayload, CancellationToken.None));

        var secondPreview = await store.CreatePreviewAsync(new NativeActionPreviewRequest("root_create", payload, "test")
        {
            RequestFingerprint = fingerprint,
            EffectSummary = "Queue source-root creation."
        }, CancellationToken.None);
        var wrongAdmission = new NativeActionCommitRequest("root_create", payload, secondPreview.ConfirmationId, "wrong-root-admission", "test")
        {
            RequestFingerprint = fingerprint,
            CommitOperation = new NativeCorpusMutationCommitOperation("root_create", canonicalPayload, admission with { CanonicalPath = "C:\\another-root" })
        };
        await AssertReasonAsync("invalid-commit-operation", () => store.CommitAsync(wrongAdmission, CancellationToken.None));

        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.SourceRootConfigurations.ToListAsync());
        Assert.Empty(await context.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_uses_the_prepared_canonical_target_identifier_for_case_and_whitespace_variants()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        var receipt = await store.CommitAsync(Commit("{}", preview.ConfirmationId, "canonical-target") with
        {
            CommitOperation = new NativeFenceTargetMutation(" NOTE-42 ", "canonicalised")
        }, CancellationToken.None);
        Assert.False(receipt.WasReplay);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal("canonicalised", (await context.NativeOperationFenceTargets.SingleAsync(target => target.TargetId == "note-42")).Value);
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_response_loss_after_persistence_is_recovered_by_retrying_the_same_key()
    {
        await ClearAsync();
        var clock = new ManualTimeProvider(Now);
        var preview = await CreateStore(clock).CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        var request = Commit("{}", preview.ConfirmationId, "cancel-after-entry");

        await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => CreateStore(clock, afterCommitFailureInjector: static () => throw new InvalidOperationException("response lost"))
            .CommitAsync(request, CancellationToken.None).AsTask());
        var replay = await CreateStore(clock).CommitAsync(request, CancellationToken.None);

        Assert.True(replay.WasReplay);
    }

    [NativeSqlServerFact]
    public async Task CreatePreviewAsync_persists_only_hashes_and_bounded_safe_target_metadata()
    {
        await ClearAsync();
        const string rawPayload = "{\"token\":\"not-for-storage\",\"title\":\"safe\"}";
        var preview = await CreateStore(new ManualTimeProvider(Now))
            .CreatePreviewAsync(Preview(rawPayload), CancellationToken.None);

        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var intent = await context.NativeOperationIntents.SingleAsync();
        Assert.Equal(64, intent.ConfirmationHash.Length);
        Assert.NotEqual(preview.ConfirmationId, intent.ConfirmationHash);
        Assert.DoesNotContain("not-for-storage", intent.TargetMetadataJson, StringComparison.Ordinal);
        Assert.Contains("note-42", intent.TargetMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.NativeOperationIntentEntity)
            .GetProperties().Select(property => property.Name), property => property.Contains("Payload", StringComparison.Ordinal));
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_fences_an_authoritative_target_changed_after_preview_without_mutation_or_receipt()
    {
        await ClearAsync();
        var rowVersion = await SeedFenceTargetAsync("note-race", "before");
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        var previewRequest = PreviewFor("{\"value\":\"committed\"}", "note-race", rowVersion);
        var preview = await store.CreatePreviewAsync(previewRequest, CancellationToken.None);

        await using (var change = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var target = await change.NativeOperationFenceTargets.SingleAsync(value => value.TargetId == "note-race");
            target.Value = "changed-after-preview";
            await change.SaveChangesAsync();
        }

        var commit = CommitFor("{\"value\":\"committed\"}", preview.ConfirmationId, "race-key", "note-race", rowVersion, "committed");
        await AssertReasonAsync("operation-fenced", () => store.CommitAsync(commit, CancellationToken.None));

        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal("changed-after-preview", (await verify.NativeOperationFenceTargets.SingleAsync(value => value.TargetId == "note-race")).Value);
        Assert.Empty(await verify.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task CommitAsync_replay_requires_the_original_confirmation()
    {
        await ClearAsync();
        var store = CreateStore(new ManualTimeProvider(Now));
        var preview = await store.CreatePreviewAsync(Preview("{}"), CancellationToken.None);
        var request = Commit("{}", preview.ConfirmationId, "confirmation-bound-key");
        var first = await store.CommitAsync(request, CancellationToken.None);

        await AssertReasonAsync("confirmation-mismatch", () => store.CommitAsync(request with { ConfirmationId = "other-confirmation" }, CancellationToken.None));
        var replay = await store.CommitAsync(request, CancellationToken.None);

        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.True(replay.WasReplay);
    }

    private SqlNativeOperationStore CreateStore(TimeProvider clock, IInterceptor? interceptor = null, Action? afterCommitFailureInjector = null) =>
        new(interceptor is null ? SqlTestData.CreateFactory(_fixture) : new TestDbContextFactory(_fixture.ConnectionString, interceptor), clock, afterCommitFailureInjector);

    private async Task ClearAsync()
    {
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        await context.NativeOperationReceipts.ExecuteDeleteAsync();
        await context.NativeOperationIntents.ExecuteDeleteAsync();
        await context.NativeOperationFenceTargets.ExecuteDeleteAsync();
        await context.SourceScanOutbox.ExecuteDeleteAsync();
        await context.SourceScanJobs.ExecuteDeleteAsync();
        await context.SourceScanRequests.ExecuteDeleteAsync();
        await context.SourceRootConfigurations.ExecuteDeleteAsync();
        context.NativeOperationFenceTargets.Add(new() { TargetId = "note-42", Value = "initial" });
        await context.SaveChangesAsync();
        _defaultRowVersion = Convert.ToBase64String((await context.NativeOperationFenceTargets.SingleAsync(target => target.TargetId == "note-42")).RowVersion);
    }

    private async Task<string> SeedFenceTargetAsync(string targetId, string value)
    {
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        context.NativeOperationFenceTargets.Add(new() { TargetId = targetId, Value = value });
        await context.SaveChangesAsync();
        return Convert.ToBase64String((await context.NativeOperationFenceTargets.SingleAsync(target => target.TargetId == targetId)).RowVersion);
    }

    private NativeActionPreviewRequest Preview(string payload) => new("knowledge.create", payload, "mcp")
    {
        Targets = [new NativeTargetVersion("note-42", _defaultRowVersion)],
        RequestFingerprint = Fingerprint("knowledge.create", payload, _defaultRowVersion),
        EffectSummary = "Create knowledge"
    };

    private NativeActionCommitRequest Commit(string payload, string confirmationId, string key, string? rowVersion = null) => new(
        "knowledge.create", payload, confirmationId, key, "mcp")
    {
        Targets = [new NativeTargetVersion("note-42", rowVersion ?? _defaultRowVersion)],
        RequestFingerprint = Fingerprint("knowledge.create", payload, rowVersion ?? _defaultRowVersion),
        CommitOperation = new NativeFenceTargetMutation("note-42", "updated")
    };

    private static NativeActionPreviewRequest PreviewFor(string payload, string targetId, string rowVersion) => new("native.fence.update", payload, "mcp")
    {
        Targets = [new NativeTargetVersion(targetId, rowVersion)],
        RequestFingerprint = NativeOperationCanonicalization.CreateRequestFingerprint("native.fence.update", NativeOperationCanonicalization.CanonicalizeJson(payload), [new NativeTargetVersion(targetId, rowVersion)]),
        EffectSummary = "Update fenced target"
    };

    private static NativeActionCommitRequest CommitFor(string payload, string confirmationId, string key, string targetId, string rowVersion, string newValue) => new(
        "native.fence.update", payload, confirmationId, key, "mcp")
    {
        Targets = [new NativeTargetVersion(targetId, rowVersion)],
        RequestFingerprint = NativeOperationCanonicalization.CreateRequestFingerprint("native.fence.update", NativeOperationCanonicalization.CanonicalizeJson(payload), [new NativeTargetVersion(targetId, rowVersion)]),
        CommitOperation = new NativeFenceTargetMutation(targetId, newValue)
    };

    private static string Fingerprint(string action, string payload, string rowVersion) =>
        NativeOperationCanonicalization.CreateRequestFingerprint(
            action,
            NativeOperationCanonicalization.CanonicalizeJson(payload),
            [new NativeTargetVersion("note-42", rowVersion)]);

    private static async Task AssertReasonAsync(string reasonCode, Func<ValueTask<NativeActionReceipt>> action)
    {
        var exception = await Assert.ThrowsAsync<NativeOperationException>(() => action().AsTask());
        Assert.Equal(reasonCode, exception.ReasonCode);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CancelDuringSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestDbContextFactory(string connectionString, IInterceptor interceptor)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}
