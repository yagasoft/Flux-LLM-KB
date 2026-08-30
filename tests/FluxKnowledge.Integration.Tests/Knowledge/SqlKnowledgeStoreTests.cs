using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Knowledge;

public sealed class SqlKnowledgeStoreTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Note_create_requires_preview_commit_and_projects_only_safe_active_content()
    {
        await ClearAsync();
        var service = CreateService();
        var mutation = new KnowledgeMutation("note_create", null, "Release note", "Retained-only content", null, null, null, null, null, null);

        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var receipt = await service.CommitAsync(mutation, preview.ConfirmationId, "note-create-1", "test", CancellationToken.None);
        var query = CreateQueryService();
        var rows = await query.SearchAsync("Retained", 10, CancellationToken.None);

        Assert.False(receipt.WasReplay);
        var row = Assert.Single(rows);
        Assert.Equal("knowledge", row.Provenance);
        Assert.Equal("Retained-only content", row.Content);
    }

    [NativeSqlServerFact]
    public async Task Secret_like_note_is_rejected_before_preview_or_durable_write()
    {
        await ClearAsync();
        var service = CreateService();
        var mutation = new KnowledgeMutation("note_create", null, "Unsafe", "password=secret-content-sentinel", null, null, null, null, null, null);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(mutation, "test", CancellationToken.None).AsTask());

        Assert.Equal("secret-content-withheld", exception.ReasonCode);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.KnowledgeItems.ToListAsync());
        Assert.Empty(await context.NativeOperationIntents.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Secret_like_claim_is_rejected_before_preview_or_durable_write()
    {
        await ClearAsync();
        var mutation = new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "owns", "api_key=secret-content-sentinel", null, null, null, 0.5m);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(() => CreateService().PreviewAsync(mutation, "test", CancellationToken.None).AsTask());

        Assert.Equal("secret-content-withheld", exception.ReasonCode);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await context.NativeOperationIntents.ToListAsync());
        Assert.Empty(await context.KnowledgeClaims.ToListAsync());
        Assert.Empty(await context.KnowledgeClaimHistory.ToListAsync());
        Assert.Empty(await context.KnowledgeRelations.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Claim_forget_creates_tombstone_and_removes_it_from_search_and_graph()
    {
        await ClearAsync();
        var service = CreateService();
        var claim = new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "owns", "Corpus", null, null, null, 0.8m);
        var createPreview = await service.PreviewAsync(claim, "test", CancellationToken.None);
        await service.CommitAsync(claim, createPreview.ConfirmationId, "claim-create", "test", CancellationToken.None);
        await using var beforeForget = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var id = (await beforeForget.KnowledgeClaims.SingleAsync()).Id;
        var forget = new KnowledgeMutation("forget", id.ToString("D"), null, null, null, null, null, null, null, null);
        var forgetPreview = await service.PreviewAsync(forget, "test", CancellationToken.None);
        await service.CommitAsync(forget, forgetPreview.ConfirmationId, "claim-forget", "test", CancellationToken.None);
        var query = CreateQueryService();

        Assert.Empty(await query.SearchAsync("atlas", 10, CancellationToken.None));
        Assert.Empty(await query.GraphAsync("atlas", 3, 10, CancellationToken.None));
        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await verify.KnowledgeTombstones.ToListAsync());
        var stored = await verify.KnowledgeClaims.SingleAsync();
        Assert.Equal(string.Empty, stored.Subject);
        Assert.Equal(string.Empty, stored.ObjectText);
    }

    [NativeSqlServerFact]
    public async Task Claim_upsert_concurrent_revisions_produce_one_new_revision_and_preserve_the_identity()
    {
        await ClearAsync();
        var initial = new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "owns", "Corpus", null, null, null, 0.5m);
        await CommitAsync(initial, "claim-initial");
        var left = initial with { Confidence = 0.6m };
        var right = initial with { Subject = " ATLAS ", Predicate = " OWNS ", ObjectText = " CORPUS ", Confidence = 0.7m };
        var leftPreview = await CreateService().PreviewAsync(left, "test", CancellationToken.None);
        var rightPreview = await CreateService().PreviewAsync(right, "test", CancellationToken.None);

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => CreateService().CommitAsync(left, leftPreview.ConfirmationId, "claim-left", "test", CancellationToken.None)),
            CaptureAsync(() => CreateService().CommitAsync(right, rightPreview.ConfirmationId, "claim-right", "test", CancellationToken.None)));

        Assert.Single(outcomes, value => value is null);
        Assert.Single(outcomes, value => value is NativeOperationException { ReasonCode: "operation-fenced" } or NativeOperationException { ReasonCode: "operation-conflict" });
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var claim = await context.KnowledgeClaims.SingleAsync();
        Assert.Equal(2, claim.Revision);
        Assert.Equal("atlas\u001fowns\u001fcorpus", claim.CanonicalIdentity);
        Assert.Equal(2, await context.KnowledgeClaimHistory.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Claim_transition_preserves_immutable_history_and_stale_preview_is_fenced()
    {
        await ClearAsync();
        var claim = new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "owns", "Corpus", null, null, null, 0.5m);
        await CommitAsync(claim, "transition-initial");
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var id = (await context.KnowledgeClaims.SingleAsync()).Id;
        var transition = new KnowledgeMutation("claim_transition", id.ToString("D"), null, null, null, null, null, "superseded", null, null);
        var preview = await CreateService().PreviewAsync(transition, "test", CancellationToken.None);
        var stale = await context.KnowledgeClaims.SingleAsync();
        stale.Confidence = 0.55m;
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<NativeOperationException>(() => CreateService().CommitAsync(transition, preview.ConfirmationId, "transition-stale", "test", CancellationToken.None).AsTask());
        Assert.Equal("operation-fenced", exception.ReasonCode);

        await CommitAsync(transition, "transition-current");
        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var history = await verify.KnowledgeClaimHistory.Where(value => value.ClaimId == id).OrderBy(value => value.Revision).ToListAsync();
        Assert.Equal(["active", "superseded"], history.Select(value => value.LifecycleState));
        Assert.Equal(1, history[0].Revision);
        Assert.Equal(2, history[1].Revision);
    }

    [NativeSqlServerFact]
    public async Task Knowledge_commit_honours_precommit_cancellation_and_uncertain_retry_returns_the_original_receipt()
    {
        await ClearAsync();
        var cancelled = new KnowledgeMutation("note_create", null, "Cancelled", "No durable content", null, null, null, null, null, null);
        var cancellationPreview = await CreateService().PreviewAsync(cancelled, "test", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().CommitAsync(cancelled, cancellationPreview.ConfirmationId, "cancelled-note", "test", cancellation.Token).AsTask());

        var uncertain = new KnowledgeMutation("note_create", null, "Retry", "Durable receipt", null, null, null, null, null, null);
        var failing = CreateService(static () => throw new InvalidOperationException("response lost"));
        var uncertainPreview = await failing.PreviewAsync(uncertain, "test", CancellationToken.None);
        await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => failing.CommitAsync(uncertain, uncertainPreview.ConfirmationId, "uncertain-note", "test", CancellationToken.None).AsTask());
        var replay = await CreateService().CommitAsync(uncertain, uncertainPreview.ConfirmationId, "uncertain-note", "test", CancellationToken.None);

        Assert.True(replay.WasReplay);
        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await verify.KnowledgeItems.ToListAsync());
        Assert.Single(await verify.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Claim_first_create_uncertain_commit_replays_with_its_stable_logical_target_and_key_collisions_fail()
    {
        await ClearAsync();
        var claim = new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "owns", "Corpus", null, null, null, 0.5m);
        var failing = CreateService(static () => throw new InvalidOperationException("response lost"));
        var preview = await failing.PreviewAsync(claim, "test", CancellationToken.None);
        await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => failing.CommitAsync(claim, preview.ConfirmationId, "claim-uncertain", "test", CancellationToken.None).AsTask());

        var replay = await CreateService().CommitAsync(claim, preview.ConfirmationId, "claim-uncertain", "test", CancellationToken.None);
        var collision = claim with { Confidence = 0.9m };
        var collisionPreview = await CreateService().PreviewAsync(collision, "test", CancellationToken.None);
        var confirmationMismatch = await Assert.ThrowsAsync<NativeOperationException>(() => CreateService().CommitAsync(claim, collisionPreview.ConfirmationId, "claim-uncertain", "test", CancellationToken.None).AsTask());
        var collisionException = await Assert.ThrowsAsync<NativeOperationException>(() => CreateService().CommitAsync(collision, preview.ConfirmationId, "claim-uncertain", "test", CancellationToken.None).AsTask());

        Assert.True(replay.WasReplay);
        Assert.Equal("confirmation-mismatch", confirmationMismatch.ReasonCode);
        Assert.Equal("idempotency-key-conflict", collisionException.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Cancellation_inside_the_knowledge_transaction_rolls_back_mutation_receipt_and_intent_consumption()
    {
        await ClearAsync();
        var mutation = new KnowledgeMutation("note_create", null, "Cancelled", "No durable content", null, null, null, null, null, null);
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(beforeCommitInjector: cancellation.Cancel);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(mutation, preview.ConfirmationId, "cancel-inside-transaction", "test", cancellation.Token).AsTask());

        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await verify.KnowledgeItems.ToListAsync());
        Assert.Empty(await verify.NativeOperationReceipts.ToListAsync());
        Assert.Null((await verify.NativeOperationIntents.SingleAsync()).ConsumedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_sql_save_before_commit_rolls_back_every_durable_change()
    {
        await ClearAsync();
        var mutation = new KnowledgeMutation("note_create", null, "Cancelled", "Rollback after SQL", null, null, null, null, null, null);
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(afterSaveBeforeCommitInjector: cancellation.Cancel);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);

        await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => service.CommitAsync(mutation, preview.ConfirmationId, "cancel-after-save", "test", cancellation.Token).AsTask());

        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await verify.KnowledgeItems.ToListAsync());
        Assert.Empty(await verify.NativeOperationReceipts.ToListAsync());
        Assert.Null((await verify.NativeOperationIntents.SingleAsync()).ConsumedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_transaction_commit_is_uncertain_and_replay_recovers_the_receipt()
    {
        await ClearAsync();
        var mutation = new KnowledgeMutation("note_create", null, "Committed", "Acknowledgement lost", null, null, null, null, null, null);
        using var cancellation = new CancellationTokenSource();
        var failing = CreateService(afterCommitFailureInjector: cancellation.Cancel);
        var preview = await failing.PreviewAsync(mutation, "test", CancellationToken.None);

        await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => failing.CommitAsync(mutation, preview.ConfirmationId, "post-commit-cancel", "test", cancellation.Token).AsTask());
        var replay = await CreateService().CommitAsync(mutation, preview.ConfirmationId, "post-commit-cancel", "test", CancellationToken.None);

        Assert.True(replay.WasReplay);
    }

    [NativeSqlServerFact]
    public async Task Graph_traversal_is_deterministically_bounded_by_depth_and_result_count()
    {
        await ClearAsync();
        await CommitAsync(new KnowledgeMutation("claim_upsert", null, null, null, "Atlas", "links", "Corpus", null, null, null, 0.8m), "graph-1");
        await CommitAsync(new KnowledgeMutation("claim_upsert", null, null, null, "Corpus", "links", "Archive", null, null, null, 0.8m), "graph-2");
        await CommitAsync(new KnowledgeMutation("claim_upsert", null, null, null, "Archive", "links", "Ledger", null, null, null, 0.8m), "graph-3");
        var query = CreateQueryService();

        var depthOne = await query.GraphAsync("atlas", 1, 10, CancellationToken.None);
        var countOne = await query.GraphAsync("atlas", 3, 1, CancellationToken.None);
        var depthThree = await query.GraphAsync("atlas", 3, 10, CancellationToken.None);

        Assert.Single(depthOne);
        Assert.Equal(1, depthOne[0].Depth);
        Assert.Single(countOne);
        Assert.Equal(3, depthThree.Count);
        Assert.Equal([1, 2, 3], depthThree.Select(value => value.Depth));
    }

    [NativeSqlServerFact]
    public async Task Graph_traversal_excludes_seen_claims_in_sql_and_materialises_no_more_than_the_result_cap()
    {
        await ClearAsync();
        var firstId = new Guid("00000000-0000-0000-0000-000000000001");
        var secondId = new Guid("00000000-0000-0000-0000-000000000002");
        await using (var seed = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            seed.KnowledgeClaims.AddRange(
                Claim(firstId, "atlas", "links", "corpus", now),
                Claim(secondId, "corpus", "links", "archive", now));
            seed.KnowledgeRelations.AddRange(
                new KnowledgeRelationEntity { ClaimId = firstId, Subject = "atlas", Predicate = "links", ObjectText = "corpus" },
                new KnowledgeRelationEntity { ClaimId = secondId, Subject = "corpus", Predicate = "links", ObjectText = "archive" });
            await seed.SaveChangesAsync();
        }
        var materialised = 0;
        var store = new SqlKnowledgeStore(SqlTestData.CreateFactory(_fixture), rows => materialised += rows);
        var query = new KnowledgeQueryService(store, new EmptySearchService(), new LocalPrivateContentDisclosure());

        var rows = await query.GraphAsync("atlas", 2, 2, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal([1, 2], rows.Select(value => value.Depth));
        Assert.True(materialised <= 2, $"Materialised {materialised} rows for a cap of 2.");
    }

    private IKnowledgeCommandService CreateService(Action? afterCommitFailureInjector = null, Action? beforeCommitInjector = null, Action? afterSaveBeforeCommitInjector = null) => new KnowledgeCommandService(
        new SqlNativeOperationStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System, afterCommitFailureInjector, beforeCommitInjector, afterSaveBeforeCommitInjector),
        new SqlKnowledgeStore(SqlTestData.CreateFactory(_fixture)),
        new LocalPrivateContentDisclosure());

    private IKnowledgeQueryService CreateQueryService() => new KnowledgeQueryService(
        new SqlKnowledgeStore(SqlTestData.CreateFactory(_fixture)), new EmptySearchService(), new LocalPrivateContentDisclosure());

    private sealed class EmptySearchService : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchResponse([], 0, string.Empty, "local_first"));
    }

    private async Task CommitAsync(KnowledgeMutation mutation, string key)
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", CancellationToken.None);
    }

    private static async Task<Exception?> CaptureAsync(Func<ValueTask<NativeActionReceipt>> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static KnowledgeClaimEntity Claim(Guid id, string subject, string predicate, string objectText, DateTimeOffset now) => new()
    {
        Id = id, CanonicalIdentity = $"{subject}\u001f{predicate}\u001f{objectText}", CanonicalIdentityHash = id.ToString("N").PadRight(64, 'a'),
        Subject = subject, Predicate = predicate, ObjectText = objectText, SafeSearchText = $"{subject}\n{predicate}\n{objectText}",
        Confidence = 0.5m, Revision = 1, LifecycleState = "active", CreatedAtUtc = now, UpdatedAtUtc = now
    };

    private async Task ClearAsync()
    {
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        await context.NativeOperationReceipts.ExecuteDeleteAsync(); await context.NativeOperationIntents.ExecuteDeleteAsync();
        await context.KnowledgeTombstones.ExecuteDeleteAsync(); await context.KnowledgeRelations.ExecuteDeleteAsync(); await context.KnowledgeClaimHistory.ExecuteDeleteAsync();
        await context.KnowledgeClaims.ExecuteDeleteAsync(); await context.KnowledgeItems.ExecuteDeleteAsync();
    }
}
