using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>
/// Generated-SQL regressions for the independently reviewed Task 6 lifecycle gaps.
/// Each assertion names a state transition that would otherwise admit a competing
/// route, a generic claim, or internally inconsistent immutable evidence.
/// </summary>
public sealed class RetainedCsharpCodeLifecycleCorrectionIntegrationTests(
    NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Csharp_claim_store_caps_direct_callers_at_the_csharp_specific_limit()
    {
        var seededBranches = new List<CsharpSeed>();
        for (var ordinal = 0; ordinal < 9; ordinal++)
        {
            seededBranches.Add(await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes($"class BatchCap{ordinal} {{ }}")));
        }

        var claims = await Store().ClaimCsharpCodeAsync(
            "csharp-claim-cap-owner",
            16,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None);

        Assert.Equal(8, claims.Count);
        Assert.All(claims, claim => Assert.Contains(seededBranches, seeded => seeded.BranchId == claim.BranchId));

        await using var verification = Context();
        Assert.Equal(
            8,
            await verification.SourceProcessorAttempts.CountAsync(value =>
                seededBranches.Select(seed => seed.BranchId).Contains(value.BranchId)));
    }

    [NativeSqlServerFact]
    public async Task Csharp_claim_runs_its_serialisable_transaction_through_the_retry_execution_strategy()
    {
        var seeded = await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class RetryingClaim { }"));
        var store = new SqlRetainedProcessorBranchStore(
            new RetryingConnectionFactory(fixture.ConnectionString),
            TimeProvider.System);

        var claim = Assert.Single(await store.ClaimCsharpCodeAsync(
            "csharp-retrying-claim-owner",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None));

        Assert.Equal(seeded.BranchId, claim.BranchId);
        await using var verification = Context();
        Assert.Equal(1, await verification.SourceProcessorAttempts.CountAsync(value => value.BranchId == seeded.BranchId));
    }

    [NativeSqlServerFact]
    public async Task Csharp_claim_returns_the_committed_attempt_after_an_ambiguous_commit_retry()
    {
        var seeded = await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class AmbiguousCommitClaim { }"));
        var ambiguousCommit = new AmbiguousCommitInterceptor();
        var store = new SqlRetainedProcessorBranchStore(
            new RetryingConnectionFactory(fixture.ConnectionString, ambiguousCommit),
            TimeProvider.System);

        var claim = Assert.Single(await store.ClaimCsharpCodeAsync(
            "csharp-ambiguous-commit-owner",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None));

        Assert.Equal(1, ambiguousCommit.AmbiguousCommitCount);
        Assert.Equal(seeded.BranchId, claim.BranchId);
        await using var verification = Context();
        var attempt = Assert.Single(await verification.SourceProcessorAttempts
            .Where(value => value.BranchId == seeded.BranchId)
            .ToListAsync());
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal(claim.AttemptId, attempt.Id);
        Assert.Equal(claim.LeaseGeneration, attempt.LeaseGeneration);
        Assert.Equal(claim.LeaseOwner, branch.LeaseOwner);
        Assert.Equal(claim.LeaseGeneration, branch.LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Csharp_claim_retries_a_rolled_back_commit_without_retaining_stale_tracking()
    {
        var seeded = await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class RolledBackCommitClaim { }"));
        var interceptor = new ThrowBeforeFirstCommitInterceptor();
        var store = new SqlRetainedProcessorBranchStore(
            new RetryingConnectionFactory(fixture.ConnectionString, interceptor),
            TimeProvider.System);

        var claim = Assert.Single(await store.ClaimCsharpCodeAsync(
            "csharp-rolled-back-commit-owner",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None));

        await using var verification = Context();
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        var attempt = Assert.Single(await verification.SourceProcessorAttempts
            .Where(value => value.BranchId == seeded.BranchId)
            .ToListAsync());
        Assert.Equal(1, branch.LeaseGeneration);
        Assert.Equal(1, branch.AttemptCount);
        Assert.Equal(1, attempt.LeaseGeneration);
        Assert.Equal(claim.AttemptId, attempt.Id);
        Assert.Equal(claim.BranchId, attempt.BranchId);
    }

    [NativeSqlServerFact]
    public async Task Csharp_claim_cancels_a_retry_delay_using_the_callers_cancellation_token()
    {
        await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class CancellationRetryClaim { }"));
        var interceptor = new ThrowBeforeFirstCommitInterceptor();
        var store = new SqlRetainedProcessorBranchStore(
            new DelayedRetryingConnectionFactory(fixture.ConnectionString, interceptor),
            TimeProvider.System);
        using var cancellation = new CancellationTokenSource();

        var claimTask = store.ClaimCsharpCodeAsync(
            "csharp-cancelled-retry-owner",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            cancellation.Token).AsTask();
        await interceptor.FirstFailure;
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claimTask);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [NativeSqlServerFact]
    public async Task Csharp_replan_accepts_only_the_exact_holding_route_and_fences_an_unclaimed_text_route()
    {
        var seeded = await SeedHoldingRouteAsync(
            holdingReason: "csharp-code-writer-not-ready",
            holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
            holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
            includeLegacyTextRoute: true,
            legacyTextState: SourceActivityState.Pending);
        var unrelated = new[]
        {
            await SeedHoldingRouteAsync(
                holdingReason: "some-other-deferred-reason",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: "unrelated-capability",
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: "unrelated-version",
                includeLegacyTextRoute: false),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false,
                holdingKind: SourceActivityKind.TextExtraction),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false,
                holdingDescriptor: RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false,
                extension: ".txt"),
            await SeedHoldingRouteAsync(
                holdingReason: "csharp-code-writer-not-ready",
                holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
                holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
                includeLegacyTextRoute: false,
                classification: "AcceptedBinary")
        };
        var store = Store();

        var candidates = await store.ReadPromotionCandidatesAsync(
            16,
            RetainedCsharpCodeProcessor.Capability,
            CancellationToken.None);

        var candidate = Assert.Single(candidates, value => value.LegacyActivityId == seeded.HoldingActivityId);
        Assert.All(unrelated, rejected =>
            Assert.DoesNotContain(candidates, value => value.LegacyActivityId == rejected.HoldingActivityId));
        Assert.True(await store.PromoteAsync(candidate, RetainedCsharpCodeProcessor.Capability, CancellationToken.None));

        await using var verification = Context();
        var holding = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.HoldingActivityId);
        var text = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyTextActivityId);
        var successor = await verification.SourceActivities.SingleAsync(value =>
            value.SourceRevisionId == seeded.RevisionId &&
            value.ActivityKind == (int)SourceActivityKind.CodeParsing);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, holding.State);
        Assert.Equal((int)SourceActivityState.DeferredPolicy, text.State);
        Assert.Equal("csharp-code-superseded-text-route", text.Reason);
        Assert.Equal(RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, successor.DescriptorFingerprint);
        Assert.Single(await verification.SourceProcessorBranches.Where(value => value.SourceActivityId == successor.Id).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Csharp_replan_records_a_conflict_and_creates_nothing_when_a_text_route_is_running()
    {
        var seeded = await SeedHoldingRouteAsync(
            holdingReason: "csharp-code-writer-not-ready",
            holdingCapability: RetainedCsharpCodeProcessor.ProcessorKind,
            holdingVersion: RetainedCsharpCodeProcessor.ProcessorVersion,
            includeLegacyTextRoute: true,
            legacyTextState: SourceActivityState.Running);
        var store = Store();
        var candidate = Assert.Single(
            await store.ReadPromotionCandidatesAsync(16, RetainedCsharpCodeProcessor.Capability, CancellationToken.None),
            value => value.LegacyActivityId == seeded.HoldingActivityId);

        Assert.False(await store.PromoteAsync(candidate, RetainedCsharpCodeProcessor.Capability, CancellationToken.None));

        await using var verification = Context();
        var holding = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.HoldingActivityId);
        Assert.Equal((int)SourceActivityState.DeferredUnsupported, holding.State);
        Assert.Equal("csharp-code-legacy-text-conflict", holding.Reason);
        Assert.Empty(await verification.SourceActivities.Where(value =>
            value.SourceRevisionId == seeded.RevisionId &&
            value.ActivityKind == (int)SourceActivityKind.CodeParsing).ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(value =>
            value.SourceActivityId == seeded.HoldingActivityId &&
            value.EventType == "retained_processor.csharp_replan_conflict").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Generic_claim_and_commit_never_consume_a_csharp_branch()
    {
        var bytes = Encoding.UTF8.GetBytes("class GenericClaimMustNotRun { }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();

        var genericClaims = await store.ClaimAsync(
            "generic-claim-owner",
            16,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None);

        Assert.DoesNotContain(genericClaims, value => value.BranchId == seeded.BranchId);
        var csharpClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "csharp-dedicated-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var genericProjection = new RetainedProcessorClaim(
            csharpClaim.BranchId,
            csharpClaim.SourceRevisionId,
            csharpClaim.ParentStableIdentity,
            csharpClaim.InputSha256,
            csharpClaim.LeaseOwner,
            csharpClaim.LeaseGeneration,
            csharpClaim.LeaseExpiresAtUtc);
        Assert.False(await store.CommitAsync(
            genericProjection,
            new RetainedProcessorCompletion([], new string('a', 64)),
            CancellationToken.None));

        var malformed = await SeedCsharpBranchAsync(bytes);
        await using (var mutation = Context())
        {
            var branch = await mutation.SourceProcessorBranches.SingleAsync(value => value.Id == malformed.BranchId);
            var activity = await mutation.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
            activity.ActivityKind = (int)SourceActivityKind.TextExtraction;
            await mutation.SaveChangesAsync();
        }
        Assert.DoesNotContain(
            await store.ClaimAsync(
                "generic-malformed-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == malformed.BranchId);
        var malformedOwner = "generic-malformed-commit-owner";
        var malformedExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        await using (var mutation = Context())
        {
            var branch = await mutation.SourceProcessorBranches.SingleAsync(value => value.Id == malformed.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Running;
            branch.LeaseOwner = malformedOwner;
            branch.LeaseGeneration = 1;
            branch.LeaseExpiresAtUtc = malformedExpiry;
            branch.AttemptCount = 1;
            branch.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mutation.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                LeaseGeneration = branch.LeaseGeneration,
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            await mutation.SaveChangesAsync();
        }
        Assert.False(await store.CommitAsync(
            new RetainedProcessorClaim(
                malformed.BranchId,
                new SourceRevisionId(malformed.RevisionId),
                malformed.StableIdentity,
                malformed.Hash,
                malformedOwner,
                1,
                malformedExpiry),
            new RetainedProcessorCompletion([], new string('b', 64)),
            CancellationToken.None));

        await using var verification = Context();
        Assert.Equal(
            (int)RetainedProcessorBranchState.Running,
            (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
        Assert.Empty(await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Csharp_writer_readiness_is_false_before_the_additive_schema_and_true_after_hardening()
    {
        await using var previous = await fixture.CreateRetainedCsharpPreviousMigrationDatabaseAsync();
        var previousStore = new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(previous.ConnectionString),
            TimeProvider.System);

        Assert.False(await previousStore.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
        Assert.Empty(
            await previousStore.ClaimCsharpCodeAsync(
                "pre-migration-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None));
        var root = Path.Combine(Path.GetTempPath(), $"flux-csharp-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var previousFactory = new ConnectionFactory(previous.ConnectionString);
            using var reader = new SqlRetainedSourceReader(previousFactory, root);
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(
                    new SqlSourceActivityStore(previousFactory, TimeProvider.System),
                    new LocalSourceCapabilityHandlerRegistry([new RetainedCsharpCodeCapabilityHandler()])),
                previousStore,
                reader,
                new ZipArchiveRetainedProcessor(new SqlRetainedArtifactWriter(previousFactory, root)),
                new RetainedProcessorOptions
                {
                    CsharpCodeEnabled = true,
                    ArchiveZipExpandEnabled = false,
                    ArchiveTarExpandEnabled = false,
                    OoxmlDocumentStructuralExtractEnabled = false
                },
                TimeProvider.System,
                csharpProcessor: new RetainedCsharpCodeProcessor(reader, new LocalPrivateContentDisclosure()));

            var result = await activation.RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            await using var previousContext = previous.CreateContext();
            Assert.False(await previousContext.SourceCapabilities.AnyAsync(value =>
                value.Id == RetainedCsharpCodeProcessor.Capability.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Assert.True(await Store().IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Dedicated_claim_and_completion_reject_corrupt_rebound_and_cancelled_bindings()
    {
        var bytes = Encoding.UTF8.GetBytes("class BindingFence { }");
        var corrupt = await SeedCsharpBranchAsync(bytes);
        await RebindArtifactAsync(corrupt.RevisionId, Encoding.UTF8.GetBytes("corrupt-before-claim"));
        var store = Store();

        Assert.DoesNotContain(
            await store.ClaimCsharpCodeAsync(
                "corrupt-claim-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == corrupt.BranchId);

        var rebound = await SeedCsharpBranchAsync(bytes);
        var reboundClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "rebound-completion-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == rebound.BranchId);
        var reboundCompletion = await ProcessAsync(reboundClaim, bytes);
        await RebindArtifactAsync(rebound.RevisionId, Encoding.UTF8.GetBytes("rebound-after-claim"));

        var reboundResult = await store.CompleteRetainedCsharpCodeAsync(
            reboundClaim,
            reboundCompletion,
            CancellationToken.None);
        Assert.False(reboundResult.IsCommitted);
        Assert.Equal("processor-fence-invalid", reboundResult.OutcomeCode);

        var cancelled = await SeedCsharpBranchAsync(bytes);
        var cancelledClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "cancelled-completion-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == cancelled.BranchId);
        var cancelledCompletion = await ProcessAsync(cancelledClaim, bytes);
        await using (var mutation = Context())
        {
            var branch = await mutation.SourceProcessorBranches.SingleAsync(value => value.Id == cancelled.BranchId);
            var activity = await mutation.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
            activity.State = (int)SourceActivityState.CancelledSuperseded;
            activity.Reason = "source-activity-superseded";
            activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await mutation.SaveChangesAsync();
        }

        var cancelledResult = await store.CompleteRetainedCsharpCodeAsync(
            cancelledClaim,
            cancelledCompletion,
            CancellationToken.None);
        Assert.False(cancelledResult.IsCommitted);
        Assert.Equal("processor-fence-invalid", cancelledResult.OutcomeCode);

        await using var verification = Context();
        Assert.Empty(await verification.SourceProcessorCodeCompletionReceipts.Where(value =>
            value.SourceProcessorBranchId == rebound.BranchId ||
            value.SourceProcessorBranchId == cancelled.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_claim_and_expired_restart_reclaim_fence_the_stale_attempt()
    {
        var bytes = Encoding.UTF8.GetBytes("class RestartReclaim { }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var firstStore = Store();
        var secondStore = Store();

        var competingClaims = await Task.WhenAll(
            firstStore.ClaimCsharpCodeAsync(
                "race-owner-a",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None).AsTask(),
            secondStore.ClaimCsharpCodeAsync(
                "race-owner-b",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None).AsTask());
        var firstClaim = Assert.Single(competingClaims.SelectMany(value => value), value => value.BranchId == seeded.BranchId);
        var staleCompletion = await ProcessAsync(firstClaim, bytes);
        await using (var expire = Context())
        {
            var branch = await expire.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            branch.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await expire.SaveChangesAsync();
        }

        var restartedStore = Store();
        var reclaimed = Assert.Single(
            await restartedStore.ClaimCsharpCodeAsync(
                "restart-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);

        Assert.NotEqual(firstClaim.AttemptId, reclaimed.AttemptId);
        Assert.Equal(firstClaim.LeaseGeneration + 1, reclaimed.LeaseGeneration);
        Assert.False((await firstStore.CompleteRetainedCsharpCodeAsync(
            firstClaim,
            staleCompletion,
            CancellationToken.None)).IsCommitted);
        Assert.True((await restartedStore.CompleteRetainedCsharpCodeAsync(
            reclaimed,
            await ProcessAsync(reclaimed, bytes),
            CancellationToken.None)).IsCommitted);

        await using var verification = Context();
        var firstAttempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.Id == firstClaim.AttemptId);
        Assert.NotNull(firstAttempt.FinishedAtUtc);
        Assert.Equal("lease-expired-reconciled", firstAttempt.OutcomeCode);
        Assert.Single(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Exact_replay_rejects_valid_success_and_blocked_field_conflicts()
    {
        var successBytes = Encoding.UTF8.GetBytes("class ReplaySuccess { void M() { } }");
        var successSeed = await SeedCsharpBranchAsync(successBytes);
        var store = Store();
        var successClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "success-conflict-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == successSeed.BranchId);
        var success = await ProcessAsync(successClaim, successBytes);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(successClaim, success, CancellationToken.None)).IsCommitted);
        var changedSymbols = success.Symbols.ToArray();
        var firstSymbol = changedSymbols[0] with { LocalName = success.Symbols[0].LocalName + "Changed" };
        firstSymbol = firstSymbol with
        {
            SymbolFingerprint = RetainedCsharpCodeProcessor.ComputeSymbolFingerprint(
                success.DocumentFingerprint!,
                firstSymbol.Ordinal,
                firstSymbol.DeclarationKindCode,
                firstSymbol.LocalName,
                firstSymbol.QualifiedName,
                firstSymbol.RenderedSignature,
                firstSymbol.Modifiers,
                firstSymbol.LexicalParentOrdinal,
                firstSymbol.SpanStartUtf16,
                firstSymbol.SpanLengthUtf16)
        };
        changedSymbols[0] = firstSymbol;
        var changedSuccess = success with
        {
            Symbols = changedSymbols,
            CompletionFingerprint = RetainedCsharpCodeProcessor.ComputeCompletionFingerprint(
                success.DocumentFingerprint!,
                success.ParserFingerprint,
                changedSymbols.Select(value => value.SymbolFingerprint),
                success.References.Select(value => value.ReferenceFingerprint),
                success.Diagnostics.Select(value => value.DiagnosticFingerprint),
                success.WithheldSymbolCount,
                success.WithheldReferenceCount,
                success.WithheldDiagnosticCount,
                success.ReceiptDiagnosticCodes)
        };
        var successConflict = await store.CompleteRetainedCsharpCodeAsync(successClaim, changedSuccess, CancellationToken.None);
        Assert.False(successConflict.IsCommitted);
        Assert.Equal("csharp-code-completion-conflict", successConflict.OutcomeCode);

        var blockedBytes = Encoding.UTF8.GetBytes("class ReplayBlocked {");
        var blockedSeed = await SeedCsharpBranchAsync(blockedBytes);
        var blockedClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "blocked-conflict-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == blockedSeed.BranchId);
        var blocked = await ProcessAsync(blockedClaim, blockedBytes);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(blockedClaim, blocked, CancellationToken.None)).IsCommitted);
        var changedBlocked = blocked.BlockedDiagnostics.ToArray();
        var firstBlocked = changedBlocked[0] with
        {
            ScannedMessage = (changedBlocked[0].ScannedMessage ?? "syntax") + " changed",
            Withheld = false,
            WithheldReason = null
        };
        firstBlocked = firstBlocked with
        {
            BlockedDiagnosticFingerprint = RetainedCsharpCodeProcessor.ComputeBlockedDiagnosticFingerprint(
                blocked.SourceRevisionId,
                blocked.RetainedArtifactSha256,
                firstBlocked.Ordinal,
                firstBlocked.DiagnosticId,
                firstBlocked.SeverityCode,
                firstBlocked.SpanStartUtf16,
                firstBlocked.SpanLengthUtf16,
                firstBlocked.ScannedMessage,
                firstBlocked.WithheldReason)
        };
        changedBlocked[0] = firstBlocked;
        var changedBlockedCompletion = blocked with
        {
            BlockedDiagnostics = changedBlocked,
            WithheldDiagnosticCount = changedBlocked.Count(value => value.Withheld),
            BlockedCompletionFingerprint = RetainedCsharpCodeProcessor.ComputeBlockedCompletionFingerprint(
                blocked.SourceRevisionId,
                blocked.RetainedArtifactSha256,
                changedBlocked.Select(value => value.BlockedDiagnosticFingerprint),
                changedBlocked.Count(value => value.Withheld),
                blocked.ReceiptDiagnosticCodes)
        };
        var blockedConflict = await store.CompleteRetainedCsharpCodeAsync(blockedClaim, changedBlockedCompletion, CancellationToken.None);
        Assert.False(blockedConflict.IsCommitted);
        Assert.Equal("csharp-code-completion-conflict", blockedConflict.OutcomeCode);
    }

    [NativeSqlServerTheory]
    [InlineData("success-completion-fingerprint")]
    [InlineData("success-blocked-completion-fingerprint")]
    [InlineData("success-withheld-symbol-count")]
    [InlineData("success-withheld-reference-count")]
    [InlineData("success-withheld-diagnostic-count")]
    [InlineData("success-ordered-diagnostic-codes")]
    [InlineData("blocked-completion-fingerprint")]
    [InlineData("blocked-withheld-symbol-count")]
    [InlineData("blocked-withheld-reference-count")]
    [InlineData("blocked-withheld-diagnostic-count")]
    [InlineData("blocked-ordered-diagnostic-codes")]
    [InlineData("blocked-nonempty-success-facts")]
    [InlineData("blocked-diagnostic-branch-id")]
    [InlineData("blocked-diagnostic-attempt-id")]
    public async Task Receipt_first_replay_conflicts_field_by_field(string field)
    {
        var blocked = field.StartsWith("blocked-", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(blocked
            ? "#warning first\n#warning second\nclass ReplayBlockedMatrix {"
            : "#warning first\n#warning second\nclass ReplaySuccessMatrix { }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                $"{field}-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await ProcessAsync(claim, bytes);
        Assert.Equal(blocked ? "csharp-code-syntax-invalid" : "success", completion.OutcomeCode);
        Assert.True(completion.ReceiptDiagnosticCodes.Count >= 2);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion,
            CancellationToken.None)).IsCommitted);

        var conflicting = field switch
        {
            "success-completion-fingerprint" => completion with { CompletionFingerprint = new string('f', 64) },
            "success-blocked-completion-fingerprint" => completion with { BlockedCompletionFingerprint = new string('f', 64) },
            "success-withheld-symbol-count" => completion with { WithheldSymbolCount = completion.WithheldSymbolCount + 1 },
            "success-withheld-reference-count" => completion with { WithheldReferenceCount = completion.WithheldReferenceCount + 1 },
            "success-withheld-diagnostic-count" => completion with { WithheldDiagnosticCount = completion.WithheldDiagnosticCount + 1 },
            "success-ordered-diagnostic-codes" => completion with { ReceiptDiagnosticCodes = completion.ReceiptDiagnosticCodes.Select((value, index) => index == 0 ? "CS9999" : value).ToArray() },
            "blocked-completion-fingerprint" => completion with { BlockedCompletionFingerprint = new string('e', 64) },
            "blocked-withheld-symbol-count" => completion with { WithheldSymbolCount = 1 },
            "blocked-withheld-reference-count" => completion with { WithheldReferenceCount = 1 },
            "blocked-withheld-diagnostic-count" => completion with { WithheldDiagnosticCount = completion.WithheldDiagnosticCount + 1 },
            "blocked-ordered-diagnostic-codes" => completion with { ReceiptDiagnosticCodes = completion.ReceiptDiagnosticCodes.Select((value, index) => index == 0 ? "CS9999" : value).ToArray() },
            "blocked-nonempty-success-facts" => completion with
            {
                Symbols = [new RetainedCsharpCodeSymbol(0, 1, "namespace", "C", "global::C", "class C", string.Empty, -1, 0, 1, new string('a', 64))]
            },
            "blocked-diagnostic-branch-id" => completion with
            {
                BlockedDiagnostics = completion.BlockedDiagnostics.Select((value, index) => index == 0 ? value with { BranchId = Guid.NewGuid() } : value).ToArray()
            },
            "blocked-diagnostic-attempt-id" => completion with
            {
                BlockedDiagnostics = completion.BlockedDiagnostics.Select((value, index) => index == 0 ? value with { AttemptId = Guid.NewGuid() } : value).ToArray()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown replay conflict field.")
        };

        var result = await store.CompleteRetainedCsharpCodeAsync(
            claim,
            conflicting,
            CancellationToken.None);

        Assert.False(result.IsCommitted);
        Assert.False(result.IsReplay);
        Assert.Equal("csharp-code-completion-conflict", result.OutcomeCode);
    }

    [NativeSqlServerFact]
    public async Task Completion_rejects_inconsistent_codes_ordinals_counts_and_fingerprints_before_any_write()
    {
        var bytes = Encoding.UTF8.GetBytes("#warning bounded diagnostic\nclass ReceiptContract { }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "receipt-validation-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await ProcessAsync(claim, bytes);
        var diagnostic = Assert.Single(completion.Diagnostics);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion with { ReceiptDiagnosticCodes = [] },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion with { Diagnostics = [diagnostic with { Ordinal = 1 }] },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion with { WithheldDiagnosticCount = completion.WithheldDiagnosticCount + 1 },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion with { CompletionFingerprint = new string('f', 64) },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion with { DecodedCharacterCount = -1 },
            CancellationToken.None).AsTask());

        await using var verification = Context();
        Assert.Empty(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Success_persists_exact_decoded_character_and_line_counts()
    {
        const string source = "namespace Counts;\npublic sealed class C\n{\n}\n";
        var bytes = Encoding.UTF8.GetBytes(source);
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "document-count-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await ProcessAsync(claim, bytes);

        Assert.True((await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None)).IsCommitted);

        await using var verification = Context();
        var document = await verification.SourceProcessorCodeDocuments.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal(source.Length, document.DecodedCharacterCount);
        Assert.Equal(5, document.LineCount);
    }

    [NativeSqlServerFact]
    public async Task Later_attempt_exact_blocked_replay_preserves_the_original_attempt_owned_diagnostics()
    {
        var bytes = Encoding.UTF8.GetBytes("class Invalid {");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var firstClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "blocked-first-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var firstCompletion = await ProcessAsync(firstClaim, bytes);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(firstClaim, firstCompletion, CancellationToken.None)).IsCommitted);

        var secondAttemptId = Guid.NewGuid();
        var secondOwner = "blocked-later-owner";
        var secondExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        await using (var mutate = Context())
        {
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Running;
            branch.LeaseOwner = secondOwner;
            branch.LeaseGeneration++;
            branch.LeaseExpiresAtUtc = secondExpiry;
            branch.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mutate.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = secondAttemptId,
                BranchId = branch.Id,
                LeaseGeneration = branch.LeaseGeneration,
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            await mutate.SaveChangesAsync();
        }
        var laterClaim = new RetainedCsharpCodeClaim(
            seeded.BranchId,
            new SourceRevisionId(seeded.RevisionId),
            seeded.StableIdentity,
            seeded.Hash,
            secondOwner,
            firstClaim.LeaseGeneration + 1,
            secondExpiry,
            secondAttemptId);
        var laterCompletion = await ProcessAsync(laterClaim, bytes);

        var replay = await store.CompleteRetainedCsharpCodeAsync(laterClaim, laterCompletion, CancellationToken.None);

        Assert.True(replay.IsCommitted);
        Assert.True(replay.IsReplay);
        await using var verification = Context();
        var receipt = await verification.SourceProcessorCodeCompletionReceipts.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal(firstClaim.AttemptId, receipt.SourceProcessorAttemptId);
        Assert.All(await verification.SourceProcessorCodeBlockedDiagnostics.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync(),
            value => Assert.Equal(firstClaim.AttemptId, value.SourceProcessorAttemptId));
    }

    [NativeSqlServerFact]
    public async Task Csharp_schema_has_immutable_fact_triggers_and_success_receipt_document_equality_fence()
    {
        await using var context = Context();

        var immutableTriggers = await context.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM sys.triggers
            WHERE [parent_id] IN (
                OBJECT_ID(N'SourceProcessorCodeDocuments'),
                OBJECT_ID(N'SourceProcessorCodeSymbols'),
                OBJECT_ID(N'SourceProcessorCodeReferences'),
                OBJECT_ID(N'SourceProcessorCodeDiagnostics'),
                OBJECT_ID(N'SourceProcessorCodeCompletionReceipts'),
                OBJECT_ID(N'SourceProcessorCodeBlockedDiagnostics'))
            ORDER BY [name]
            """).ToListAsync();
        Assert.Equal(6, immutableTriggers.Count(value => value.EndsWith("_Immutable", StringComparison.Ordinal)));
        Assert.Equal(5, immutableTriggers.Count(value => value.EndsWith("_InsertFence", StringComparison.Ordinal)));
        Assert.Contains("TR_SourceProcessorCodeDocuments_InsertFence", immutableTriggers);
        Assert.Contains("TR_SourceProcessorCodeCompletionReceipts_OutcomeFence", immutableTriggers);
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM sys.foreign_keys
            WHERE [name] = N'FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_SuccessIdentity'
              AND [delete_referential_action] = 0
            """).SingleAsync());
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM sys.check_constraints
            WHERE [name] = N'CK_SourceProcessorCodeCompletionReceipts_DocumentBranchEquality'
            """).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Hardening_migration_downgrades_to_the_original_csharp_schema_and_reapplies()
    {
        await using var database = await fixture.CreateRetainedCsharpPreviousMigrationDatabaseAsync();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();
        Assert.True(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await context.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeDocuments_InsertFence];");
        Assert.False(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await migrator.MigrateAsync("20260820070404_HardenRetainedCsharpLifecycle");
        Assert.False(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await migrator.MigrateAsync();
        Assert.True(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await context.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Closure];");
        Assert.False(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await migrator.MigrateAsync("20260820062157_AddRetainedCsharpCodeFacts");
        Assert.False(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

        await migrator.MigrateAsync();
        Assert.True(await new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System).IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Persisted_private_facts_withhold_secret_content_and_hard_denials_are_exact()
    {
        const string sentinel = "secret-content-sentinel";
        var bytes = Encoding.UTF8.GetBytes(
            $"#warning {sentinel}\nclass C {{ string M(string value = \"{sentinel}\") {{ \"{sentinel}\".ToString(); CleanTarget(); return value; }} }}");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "secret-fact-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await ProcessAsync(claim, bytes);

        Assert.True(completion.WithheldSymbolCount > 0);
        Assert.True(completion.WithheldReferenceCount > 0);
        Assert.True(completion.WithheldDiagnosticCount > 0);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None)).IsCommitted);

        await using var verification = Context();
        Assert.DoesNotContain(await verification.SourceProcessorCodeSymbols.Where(value => value.DocumentId == seeded.BranchId).ToListAsync(), value =>
            value.LocalName.Contains(sentinel, StringComparison.Ordinal) ||
            value.QualifiedName.Contains(sentinel, StringComparison.Ordinal) ||
            value.RenderedSignature.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(await verification.SourceProcessorCodeReferences.Where(value => value.DocumentId == seeded.BranchId).ToListAsync(), value =>
            value.TargetDisplay.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(await verification.SourceProcessorCodeDiagnostics.Where(value => value.DocumentId == seeded.BranchId).ToListAsync(), value =>
            value.ScannedMessage?.Contains(sentinel, StringComparison.Ordinal) == true);
        var hardDenials = await verification.OperatorActionHardDenials
            .Where(value => value.ReasonCode.StartsWith("csharp-code-"))
            .OrderBy(value => value.ReasonCode)
            .Select(value => value.ReasonCode)
            .ToArrayAsync();
        Assert.Equal(
            OperatorActionHardDenialReasons.All
                .Where(value => value.StartsWith("csharp-code-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
            hardDenials);
    }

    [NativeSqlServerFact]
    public async Task Secret_scan_failure_blocks_the_branch_without_partial_code_facts_or_receipt()
    {
        var bytes = Encoding.UTF8.GetBytes("class ScanFailure { void M() { Target(); } }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "secret-scan-failure-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await new RetainedCsharpCodeProcessor(
            new MemoryReader(claim.SourceRevisionId, bytes),
            new FailingDisclosure()).ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-secret-scan-failed", completion.OutcomeCode);
        Assert.Empty(completion.Symbols);
        Assert.Empty(completion.References);
        Assert.Empty(completion.Diagnostics);
        Assert.True(await store.FailAsync(
            new RetainedProcessorClaim(
                claim.BranchId,
                claim.SourceRevisionId,
                claim.ParentStableIdentity,
                claim.InputSha256,
                claim.LeaseOwner,
                claim.LeaseGeneration,
                claim.LeaseExpiresAtUtc),
            new RetainedProcessorFailure(completion.OutcomeCode, []),
            CancellationToken.None));

        await using var verification = Context();
        Assert.Equal(
            (int)RetainedProcessorBranchState.Blocked,
            (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
        Assert.Empty(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeSymbols.Where(value => value.DocumentId == seeded.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeReferences.Where(value => value.DocumentId == seeded.BranchId).ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeDiagnostics.Where(value => value.DocumentId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Database_closure_rejects_fact_mutation_and_append_after_receipt()
    {
        var bytes = Encoding.UTF8.GetBytes("class ImmutableReceipt { void M() { } }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "immutable-receipt-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(
            claim,
            await ProcessAsync(claim, bytes),
            CancellationToken.None)).IsCommitted);

        await using var context = Context();
        await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [SourceProcessorCodeDocuments]
            SET [DecodedCharacterCount] = [DecodedCharacterCount] + 1
            WHERE [SourceProcessorBranchId] = {seeded.BranchId}
            """));
        await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeSymbols]
                ([DocumentId], [Ordinal], [DeclarationKindCode], [LocalName], [QualifiedName], [RenderedSignature], [Modifiers], [LexicalParentOrdinal], [SpanStartUtf16], [SpanLengthUtf16], [SymbolFingerprint])
            VALUES
                ({seeded.BranchId}, {999}, {1}, {"late"}, {"late"}, {"late"}, {string.Empty}, {-1}, {0}, {1}, {new string('e', 64)})
            """));
        Assert.Single(await context.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Database_closure_rejects_document_insert_after_blocked_receipt()
    {
        var bytes = Encoding.UTF8.GetBytes("class BlockedDocumentFence {");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = Store();
        var claim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "blocked-document-fence-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var completion = await ProcessAsync(claim, bytes);
        Assert.Equal("csharp-code-syntax-invalid", completion.OutcomeCode);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(
            claim,
            completion,
            CancellationToken.None)).IsCommitted);

        await using var context = Context();
        await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeDocuments]
                ([SourceProcessorBranchId], [SourceRevisionId], [RetainedArtifactSha256], [DescriptorFingerprint], [ParserFingerprint], [HandlerImplementationId], [LeaseGeneration], [DecodedCharacterCount], [LineCount], [SymbolCount], [ReferenceCount], [DiagnosticsCount], [WithheldSymbolCount], [WithheldReferenceCount], [WithheldDiagnosticCount], [ReceiptDiagnosticCodeCount], [DocumentFingerprint], [CompletionFingerprint])
            VALUES
                ({seeded.BranchId}, {seeded.RevisionId}, {seeded.Hash}, {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}, {RetainedCsharpCodeProcessor.ParserFingerprint}, {RetainedCsharpCodeProcessor.HandlerImplementationId}, {claim.LeaseGeneration}, {1}, {1}, {0}, {0}, {0}, {0}, {0}, {0}, {0}, {new string('d', 64)}, {new string('c', 64)})
            """));
        Assert.Empty(await context.SourceProcessorCodeDocuments
            .Where(value => value.SourceProcessorBranchId == seeded.BranchId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Database_closure_rejects_blocked_diagnostic_before_or_after_success_receipt()
    {
        var preexistingBytes = Encoding.UTF8.GetBytes("class SuccessWithPreexistingBlockedDiagnostic { }");
        var preexistingSeed = await SeedCsharpBranchAsync(preexistingBytes);
        var store = Store();
        var preexistingClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "preexisting-blocked-diagnostic-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == preexistingSeed.BranchId);
        await using (var setup = Context())
        {
            await InsertSyntheticBlockedDiagnosticAsync(setup, preexistingClaim, "preexisting");
        }

        var preexistingCompletion = await ProcessAsync(preexistingClaim, preexistingBytes);
        var preexistingFailure = await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteRetainedCsharpCodeAsync(
            preexistingClaim,
            preexistingCompletion,
            CancellationToken.None).AsTask());
        Assert.True(preexistingFailure is DbUpdateException or SqlException);
        await using (var verification = Context())
        {
            Assert.Empty(await verification.SourceProcessorCodeDocuments
                .Where(value => value.SourceProcessorBranchId == preexistingSeed.BranchId)
                .ToListAsync());
            Assert.Empty(await verification.SourceProcessorCodeCompletionReceipts
                .Where(value => value.SourceProcessorBranchId == preexistingSeed.BranchId)
                .ToListAsync());
            Assert.Single(await verification.SourceProcessorCodeBlockedDiagnostics
                .Where(value => value.SourceProcessorBranchId == preexistingSeed.BranchId)
                .ToListAsync());
        }

        var appendedBytes = Encoding.UTF8.GetBytes("class SuccessWithAppendedBlockedDiagnostic { }");
        var appendedSeed = await SeedCsharpBranchAsync(appendedBytes);
        var appendedClaim = Assert.Single(
            await store.ClaimCsharpCodeAsync(
                "appended-blocked-diagnostic-owner",
                16,
                RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                CancellationToken.None),
            value => value.BranchId == appendedSeed.BranchId);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(
            appendedClaim,
            await ProcessAsync(appendedClaim, appendedBytes),
            CancellationToken.None)).IsCommitted);

        await using var append = Context();
        await Assert.ThrowsAsync<SqlException>(() => InsertSyntheticBlockedDiagnosticAsync(
            append,
            appendedClaim,
            "appended"));
        Assert.Empty(await append.SourceProcessorCodeBlockedDiagnostics
            .Where(value => value.SourceProcessorBranchId == appendedSeed.BranchId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Writer_readiness_requires_each_enabled_unmodified_csharp_safety_trigger()
    {
        await using var database = await fixture.CreateRetainedCsharpPreviousMigrationDatabaseAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        var store = new SqlRetainedProcessorBranchStore(
            new ConnectionFactory(database.ConnectionString),
            TimeProvider.System);
        Assert.True(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

#pragma warning disable EF1002 // The trigger names, parent tables and event clauses are fixed test contracts below.
        foreach (var contract in CsharpSafetyTriggerContracts)
        {
            var definition = await context.Database.SqlQuery<string>($"""
                SELECT [module].[definition] AS [Value]
                FROM [sys].[triggers] AS [trigger]
                INNER JOIN [sys].[sql_modules] AS [module]
                    ON [module].[object_id] = [trigger].[object_id]
                WHERE [trigger].[name] = {contract.Name}
                  AND [trigger].[parent_id] = OBJECT_ID({$"[dbo].[{contract.TableName}]"})
                """).SingleAsync();
            await context.Database.ExecuteSqlRawAsync(
                $"DISABLE TRIGGER [dbo].[{contract.Name}] ON [dbo].[{contract.TableName}];");
            Assert.False(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
            await context.Database.ExecuteSqlRawAsync(
                $"ENABLE TRIGGER [dbo].[{contract.Name}] ON [dbo].[{contract.TableName}];");
            Assert.True(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));

            await context.Database.ExecuteSqlRawAsync(
                $"ALTER TRIGGER [dbo].[{contract.Name}] ON [dbo].[{contract.TableName}] {contract.Events} AS BEGIN SET NOCOUNT ON; END;");
            Assert.False(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
            await context.Database.ExecuteSqlRawAsync(
                $"DROP TRIGGER [dbo].[{contract.Name}];");
            await context.Database.ExecuteSqlRawAsync(definition);
            Assert.True(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
        }
#pragma warning restore EF1002
    }

    [NativeSqlServerFact]
    public async Task Closing_migration_fails_closed_for_a_blocked_receipt_with_a_code_document_in_an_older_database()
    {
        await using var database = await fixture.CreateRetainedCsharpLifecyclePreviousMigrationDatabaseAsync();
        await using var context = database.CreateContext();
        var blockedReceiptWithDocument = await SeedCsharpBranchAsync(context, Encoding.UTF8.GetBytes("class OldBlockedReceipt { }"));
        var blockedAttempt = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.SourceProcessorAttempts.Add(
            new SourceProcessorAttemptEntity { Id = blockedAttempt, BranchId = blockedReceiptWithDocument.BranchId, LeaseGeneration = 1, StartedAtUtc = now });
        await context.SaveChangesAsync();

        await InsertBlockedReceiptAsync(context, blockedReceiptWithDocument, blockedAttempt);
        await InsertSyntheticDocumentAsync(context, blockedReceiptWithDocument, 1);

        await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync());
        Assert.DoesNotContain(
            "20260820101021_CloseRetainedCsharpMixedOutcomes",
            await context.Database.SqlQuery<string>($"SELECT [MigrationId] AS [Value] FROM [__EFMigrationsHistory]").ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Closing_migration_fails_closed_for_blocked_diagnostics_with_a_success_receipt_in_an_older_database()
    {
        await using var database = await fixture.CreateRetainedCsharpLifecyclePreviousMigrationDatabaseAsync();
        await using var context = database.CreateContext();
        var successReceiptWithBlockedDiagnostic = await SeedCsharpBranchAsync(context, Encoding.UTF8.GetBytes("class OldSuccessReceipt { }"));
        var successAttempt = Guid.NewGuid();
        context.SourceProcessorAttempts.Add(
            new SourceProcessorAttemptEntity
            {
                Id = successAttempt,
                BranchId = successReceiptWithBlockedDiagnostic.BranchId,
                LeaseGeneration = 1,
                StartedAtUtc = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync();

        await InsertSyntheticBlockedDiagnosticAsync(context, successReceiptWithBlockedDiagnostic, successAttempt, "old-mixed");
        await InsertSyntheticDocumentAsync(context, successReceiptWithBlockedDiagnostic, 1);
        await InsertSuccessReceiptAsync(context, successReceiptWithBlockedDiagnostic, successAttempt);

        await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync());
        Assert.DoesNotContain(
            "20260820101021_CloseRetainedCsharpMixedOutcomes",
            await context.Database.SqlQuery<string>($"SELECT [MigrationId] AS [Value] FROM [__EFMigrationsHistory]").ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Two_connection_races_cannot_close_a_branch_with_both_blocked_and_success_facts()
    {
        var documentRace = await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class ReceiptDocumentRace { }"));
        var blockedDiagnosticRace = await SeedCsharpBranchAsync(Encoding.UTF8.GetBytes("class DiagnosticReceiptRace { }"));
        var store = Store();
        var claims = await store.ClaimCsharpCodeAsync("csharp-closure-race", 16,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, CancellationToken.None);
        var documentClaim = Assert.Single(claims, value => value.BranchId == documentRace.BranchId);
        var diagnosticClaim = Assert.Single(claims, value => value.BranchId == blockedDiagnosticRace.BranchId);

        var documentResults = await RunBarrierRaceAsync(
            context => InsertBlockedReceiptAsync(context, documentRace, documentClaim.AttemptId),
            context => InsertSyntheticDocumentAsync(context, documentRace, documentClaim.LeaseGeneration));
        var diagnosticResults = await RunBarrierRaceAsync(
            context => InsertSyntheticBlockedDiagnosticAsync(context, blockedDiagnosticRace, diagnosticClaim.AttemptId, "racing-blocked") ,
            async context =>
            {
                await InsertSyntheticDocumentAsync(context, blockedDiagnosticRace, diagnosticClaim.LeaseGeneration);
                await InsertSuccessReceiptAsync(context, blockedDiagnosticRace, diagnosticClaim.AttemptId);
            });

        Assert.True(documentResults.FirstCommitted);
        Assert.False(documentResults.SecondCommitted);
        Assert.True(diagnosticResults.FirstCommitted);
        Assert.False(diagnosticResults.SecondCommitted);
        await using var verification = Context();
        Assert.False(await verification.SourceProcessorCodeCompletionReceipts.AnyAsync(value =>
            value.SourceProcessorBranchId == documentRace.BranchId &&
            value.OutcomeCode == "csharp-code-syntax-invalid" &&
            verification.SourceProcessorCodeDocuments.Any(document => document.SourceProcessorBranchId == documentRace.BranchId)));
        Assert.False(await verification.SourceProcessorCodeCompletionReceipts.AnyAsync(value =>
            value.SourceProcessorBranchId == blockedDiagnosticRace.BranchId &&
            value.OutcomeCode == "success" &&
            verification.SourceProcessorCodeBlockedDiagnostics.Any(diagnostic => diagnostic.SourceProcessorBranchId == blockedDiagnosticRace.BranchId)));
    }

    private async Task<HoldingSeed> SeedHoldingRouteAsync(
        string holdingReason,
        string holdingCapability,
        string holdingVersion,
        bool includeLegacyTextRoute,
        SourceActivityState legacyTextState = SourceActivityState.Pending,
        SourceActivityKind holdingKind = SourceActivityKind.DocumentParsing,
        string? holdingDescriptor = null,
        string extension = ".cs",
        string classification = "AcceptedUtf8Text")
    {
        var bytes = Encoding.UTF8.GetBytes("class Replan { }");
        var hash = Sha256(bytes);
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var holdingId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var context = Context();
        context.SourceRootConfigurations.Add(Root(rootId, now));
        context.SourceRevisions.Add(Revision(
            rootId,
            revisionId,
            hash,
            bytes.Length,
            now,
            extension: extension,
            classification: classification));
        context.SourceArtifacts.Add(Artifact(revisionId, hash, bytes.Length, now));
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = holdingId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)holdingKind,
            ExecutionClass = (int)ExecutionClass.DeferredCapability,
            ProcessorVersion = holdingVersion,
            InputFingerprint = hash,
            DescriptorFingerprint = holdingDescriptor ?? SourceActivityEntity.LegacyDescriptorFingerprint,
            RequiredCapability = holdingCapability,
            State = (int)SourceActivityState.DeferredUnsupported,
            Reason = holdingReason,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        if (includeLegacyTextRoute)
        {
            context.SourceActivities.Add(new SourceActivityEntity
            {
                Id = textId,
                SourceRevisionId = revisionId,
                ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = "phase-3a-v1",
                InputFingerprint = hash,
                DescriptorFingerprint = SourceActivityEntity.LegacyDescriptorFingerprint,
                State = (int)legacyTextState,
                CreatedAtUtc = now.AddTicks(1),
                UpdatedAtUtc = now.AddTicks(1)
            });
        }
        await context.SaveChangesAsync();
        return new HoldingSeed(holdingId, revisionId, includeLegacyTextRoute ? textId : Guid.Empty);
    }

    private async Task<CsharpSeed> SeedCsharpBranchAsync(byte[] bytes)
    {
        await using var context = Context();
        return await SeedCsharpBranchAsync(context, bytes);
    }

    private static async Task<CsharpSeed> SeedCsharpBranchAsync(
        FluxKnowledgeDbContext context,
        byte[] bytes)
    {
        var hash = Sha256(bytes);
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var stableIdentity = $"retained-csharp:{revisionId:N}";
        var now = DateTimeOffset.UtcNow;
        context.SourceRootConfigurations.Add(Root(rootId, now));
        context.SourceRevisions.Add(Revision(rootId, revisionId, hash, bytes.Length, now, stableIdentity));
        context.SourceArtifacts.Add(Artifact(revisionId, hash, bytes.Length, now));
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)SourceActivityKind.CodeParsing,
            ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion,
            InputFingerprint = hash,
            DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            State = (int)SourceActivityState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId,
            SourceActivityId = activityId,
            SourceRevisionId = revisionId,
            InputSha256 = hash,
            ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion,
            ProcessorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return new CsharpSeed(branchId, revisionId, stableIdentity, hash);
    }

    private async Task RebindArtifactAsync(Guid revisionId, byte[] reboundBytes)
    {
        var reboundHash = Sha256(reboundBytes);
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [SourceArtifacts]
            SET [ContentSha256] = {reboundHash}, [ByteLength] = {reboundBytes.LongLength}
            WHERE [SourceRevisionId] = {revisionId}
            """);
    }

    private static Task<int> InsertSyntheticBlockedDiagnosticAsync(
        FluxKnowledgeDbContext context,
        RetainedCsharpCodeClaim claim,
        string marker) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeBlockedDiagnostics]
                ([SourceProcessorBranchId], [SourceProcessorAttemptId], [Ordinal], [DiagnosticId], [Severity], [SpanStartUtf16], [SpanLengthUtf16], [Representation], [ScannedMessage], [WithheldReason], [BlockedDiagnosticFingerprint])
            VALUES
                ({claim.BranchId}, {claim.AttemptId}, {0}, {"CS9999"}, {3}, {0}, {0}, {"scanned"}, {marker}, {null}, {new string('b', 64)})
            """);

    private static Task<int> InsertSyntheticBlockedDiagnosticAsync(
        FluxKnowledgeDbContext context,
        CsharpSeed seed,
        Guid attemptId,
        string marker) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeBlockedDiagnostics]
                ([SourceProcessorBranchId], [SourceProcessorAttemptId], [Ordinal], [DiagnosticId], [Severity], [SpanStartUtf16], [SpanLengthUtf16], [Representation], [ScannedMessage], [WithheldReason], [BlockedDiagnosticFingerprint])
            VALUES
                ({seed.BranchId}, {attemptId}, {0}, {"CS9999"}, {3}, {0}, {0}, {"scanned"}, {marker}, {null}, {new string('b', 64)})
            """);

    private static Task<int> InsertSyntheticDocumentAsync(
        FluxKnowledgeDbContext context,
        CsharpSeed seed,
        long leaseGeneration) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeDocuments]
                ([SourceProcessorBranchId], [SourceRevisionId], [RetainedArtifactSha256], [DescriptorFingerprint], [ParserFingerprint], [HandlerImplementationId], [LeaseGeneration], [DecodedCharacterCount], [LineCount], [SymbolCount], [ReferenceCount], [DiagnosticsCount], [WithheldSymbolCount], [WithheldReferenceCount], [WithheldDiagnosticCount], [ReceiptDiagnosticCodeCount], [DocumentFingerprint], [CompletionFingerprint])
            VALUES
                ({seed.BranchId}, {seed.RevisionId}, {seed.Hash}, {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}, {RetainedCsharpCodeProcessor.ParserFingerprint}, {RetainedCsharpCodeProcessor.HandlerImplementationId}, {leaseGeneration}, {1}, {1}, {0}, {0}, {0}, {0}, {0}, {0}, {0}, {new string('d', 64)}, {new string('c', 64)})
            """);

    private static Task<int> InsertBlockedReceiptAsync(
        FluxKnowledgeDbContext context,
        CsharpSeed seed,
        Guid attemptId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeCompletionReceipts]
                ([SourceProcessorBranchId], [SourceProcessorAttemptId], [SourceRevisionId], [ActivityKind], [ProcessorVersion], [DescriptorFingerprint], [ParserFingerprint], [RetainedArtifactSha256], [HandlerImplementationId], [OutcomeCode], [DocumentId], [DocumentFingerprint], [CompletionFingerprint], [WithheldSymbolCount], [WithheldReferenceCount], [WithheldDiagnosticCount], [BlockedDiagnosticsCount], [ReceiptDiagnosticCodeCount], [ReceiptDiagnosticCodesWire], [CreatedAtUtc])
            VALUES
                ({seed.BranchId}, {attemptId}, {seed.RevisionId}, {(int)SourceActivityKind.CodeParsing}, {RetainedCsharpCodeProcessor.ProcessorVersion}, {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}, {RetainedCsharpCodeProcessor.ParserFingerprint}, {seed.Hash}, {RetainedCsharpCodeProcessor.HandlerImplementationId}, {"csharp-code-syntax-invalid"}, {null}, {null}, {new string('b', 64)}, {0}, {0}, {0}, {0}, {0}, {"0;"}, {DateTimeOffset.UtcNow})
            """);

    private static Task<int> InsertSuccessReceiptAsync(
        FluxKnowledgeDbContext context,
        CsharpSeed seed,
        Guid attemptId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceProcessorCodeCompletionReceipts]
                ([SourceProcessorBranchId], [SourceProcessorAttemptId], [SourceRevisionId], [ActivityKind], [ProcessorVersion], [DescriptorFingerprint], [ParserFingerprint], [RetainedArtifactSha256], [HandlerImplementationId], [OutcomeCode], [DocumentId], [DocumentFingerprint], [CompletionFingerprint], [WithheldSymbolCount], [WithheldReferenceCount], [WithheldDiagnosticCount], [BlockedDiagnosticsCount], [ReceiptDiagnosticCodeCount], [ReceiptDiagnosticCodesWire], [CreatedAtUtc])
            VALUES
                ({seed.BranchId}, {attemptId}, {seed.RevisionId}, {(int)SourceActivityKind.CodeParsing}, {RetainedCsharpCodeProcessor.ProcessorVersion}, {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}, {RetainedCsharpCodeProcessor.ParserFingerprint}, {seed.Hash}, {RetainedCsharpCodeProcessor.HandlerImplementationId}, {"success"}, {seed.BranchId}, {new string('d', 64)}, {new string('c', 64)}, {0}, {0}, {0}, {0}, {0}, {"0;"}, {DateTimeOffset.UtcNow})
            """);

    private async Task<RaceResult> RunBarrierRaceAsync(
        Func<FluxKnowledgeDbContext, Task> firstWrite,
        Func<FluxKnowledgeDbContext, Task> secondWrite)
    {
        var firstInserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ExecuteHeldWriteAsync(firstWrite, firstInserted, releaseFirst.Task);
        await firstInserted.Task;
        var second = ExecuteWriteAsync(secondWrite, secondStarted);
        await secondStarted.Task;
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500)));
        releaseFirst.TrySetResult();
        return new RaceResult(await first, await second);
    }

    private async Task<bool> ExecuteHeldWriteAsync(
        Func<FluxKnowledgeDbContext, Task> write,
        TaskCompletionSource inserted,
        Task release)
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            await write(context);
            inserted.TrySetResult();
            await release;
            await transaction.CommitAsync();
            return true;
        }
        catch (Exception exception) when (exception is SqlException or DbUpdateException)
        {
            inserted.TrySetResult();
            await transaction.RollbackAsync();
            return false;
        }
    }

    private async Task<bool> ExecuteWriteAsync(
        Func<FluxKnowledgeDbContext, Task> write,
        TaskCompletionSource started)
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            started.TrySetResult();
            await write(context);
            await transaction.CommitAsync();
            return true;
        }
        catch (Exception exception) when (exception is SqlException or DbUpdateException)
        {
            started.TrySetResult();
            await transaction.RollbackAsync();
            return false;
        }
    }

    private static SourceRootConfigurationEntity Root(Guid id, DateTimeOffset now) => new()
    {
        Id = id,
        CanonicalPath = $"C:\\retained-csharp-correction\\{id:N}",
        DisplayName = "C# correction",
        State = 0,
        Recursive = true,
        IncludePatternsJson = "[]",
        ExcludePatternsJson = "[]",
        FollowLinks = false,
        MaximumFileBytes = 64L * 1024 * 1024,
        AllowedClassificationsJson = "[\"text/plain\"]",
        CrawlMode = 0,
        ReconciliationCadenceSeconds = 900,
        ConfigurationRevision = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static SourceRevisionEntity Revision(
        Guid rootId,
        Guid revisionId,
        string hash,
        int byteLength,
        DateTimeOffset now,
        string? stableIdentity = null,
        string extension = ".cs",
        string classification = "AcceptedUtf8Text") => new()
    {
        Id = revisionId,
        SourceRootId = rootId,
        StableSourceIdentity = stableIdentity ?? $"retained-csharp-holding:{revisionId:N}",
        Revision = 1,
        ContentSha256 = hash,
        CanonicalPath = "C:\\source-original-must-not-be-read.cs",
        Classification = classification,
        Extension = extension,
        ByteLength = byteLength,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}"
    };

    private static SourceArtifactEntity Artifact(Guid revisionId, string hash, int byteLength, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        SourceRevisionId = revisionId,
        ContentSha256 = hash,
        StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin",
        ByteLength = byteLength,
        ChecksumVerifiedAtUtc = now,
        ReferenceCount = 1
    };

    private static ValueTask<RetainedCsharpCodeCompletion> ProcessAsync(
        RetainedCsharpCodeClaim claim,
        byte[] bytes) =>
        new RetainedCsharpCodeProcessor(
            new MemoryReader(claim.SourceRevisionId, bytes),
            new LocalPrivateContentDisclosure()).ProcessAsync(claim, CancellationToken.None);

    private SqlRetainedProcessorBranchStore Store() => new(
        SqlTestData.CreateFactory(fixture),
        TimeProvider.System);

    private FluxKnowledgeDbContext Context() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString)
            .Options);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class MemoryReader(SourceRevisionId revisionId, byte[] bytes) : IRetainedSourceReader
    {
        private readonly string _hash = Sha256(bytes);

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RetainedSourceBytes(revisionId, bytes, _hash, bytes.LongLength));

        public ValueTask<Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ConnectionFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class RetryingConnectionFactory(
        string connectionString,
        params IInterceptor[] interceptors)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .AddInterceptors(interceptors)
                .UseSqlServer(connectionString, sqlServer => sqlServer.ExecutionStrategy(
                    dependencies => new RetryExecutionStrategy(dependencies)))
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class DelayedRetryingConnectionFactory(
        string connectionString,
        DbTransactionInterceptor interceptor)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .AddInterceptors(interceptor)
                .UseSqlServer(connectionString, sqlServer => sqlServer.ExecutionStrategy(
                    dependencies => new DelayedRetryExecutionStrategy(dependencies)))
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class RetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SyntheticTransientException;

        protected override bool ShouldVerifySuccessOn(Exception exception) => exception is SyntheticTransientException;
    }

    private sealed class DelayedRetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.FromSeconds(3))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SyntheticTransientException;

        protected override TimeSpan? GetNextDelay(Exception lastException) =>
            lastException is SyntheticTransientException ? TimeSpan.FromSeconds(3) : null;
    }

    private sealed class AmbiguousCommitInterceptor : DbTransactionInterceptor
    {
        private int ambiguousCommitCount;

        public int AmbiguousCommitCount => ambiguousCommitCount;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref ambiguousCommitCount) == 1)
            {
                transaction.Commit();
                throw new SyntheticTransientException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowBeforeFirstCommitInterceptor : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commitFailureCount;

        public Task FirstFailure => firstFailure.Task;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commitFailureCount) == 1)
            {
                firstFailure.TrySetResult();
                throw new SyntheticTransientException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class SyntheticTransientException : Exception;

    private sealed class FailingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) =>
            throw new InvalidOperationException("Synthetic secret scan failure.");
    }

    private sealed record HoldingSeed(Guid HoldingActivityId, Guid RevisionId, Guid LegacyTextActivityId);
    private sealed record CsharpSeed(Guid BranchId, Guid RevisionId, string StableIdentity, string Hash);
    private sealed record RaceResult(bool FirstCommitted, bool SecondCommitted);

    private sealed record CsharpSafetyTriggerContract(string Name, string TableName, string Events);

    private static readonly CsharpSafetyTriggerContract[] CsharpSafetyTriggerContracts =
    [
        new("TR_SourceProcessorCodeDocuments_Immutable", "SourceProcessorCodeDocuments", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeDocuments_InsertFence", "SourceProcessorCodeDocuments", "AFTER INSERT"),
        new("TR_SourceProcessorCodeSymbols_Immutable", "SourceProcessorCodeSymbols", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeSymbols_InsertFence", "SourceProcessorCodeSymbols", "AFTER INSERT"),
        new("TR_SourceProcessorCodeReferences_Immutable", "SourceProcessorCodeReferences", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeReferences_InsertFence", "SourceProcessorCodeReferences", "AFTER INSERT"),
        new("TR_SourceProcessorCodeDiagnostics_Immutable", "SourceProcessorCodeDiagnostics", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeDiagnostics_InsertFence", "SourceProcessorCodeDiagnostics", "AFTER INSERT"),
        new("TR_SourceProcessorCodeCompletionReceipts_Immutable", "SourceProcessorCodeCompletionReceipts", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeCompletionReceipts_OutcomeFence", "SourceProcessorCodeCompletionReceipts", "AFTER INSERT"),
        new("TR_SourceProcessorCodeCompletionReceipts_Closure", "SourceProcessorCodeCompletionReceipts", "AFTER INSERT"),
        new("TR_SourceProcessorCodeBlockedDiagnostics_Immutable", "SourceProcessorCodeBlockedDiagnostics", "AFTER UPDATE, DELETE"),
        new("TR_SourceProcessorCodeBlockedDiagnostics_InsertFence", "SourceProcessorCodeBlockedDiagnostics", "AFTER INSERT")
    ];
}
