using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Disposable SQL proof for the private retained-branch force foundation; it exposes no public transport.</summary>
public sealed class OoxmlForceRequestFoundationIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private const string RetryableReasonCode = "operator-action-retryable-test";
    private const string RetryActionKind = "retry";
    private const string LegacyHistorySafetyContract = "legacy-historical-receipt";
    private const string LegacyHistoryReasonCode = "legacy-force-request-receipt";
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearOoxmlOperatorActionDataAsync(_fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Current_forceable_ooxml_block_creates_one_durable_requested_receipt_and_excludes_the_branch_from_ordinary_claims()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        Assert.Equal(seeded.BranchId, action.BranchId);
        var request = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);

        Assert.Equal(OoxmlForceRequestState.Requested, request.State);
        Assert.Equal(action.ActionId, request.ActionId);
        Assert.Empty(await store.ClaimAsync("ordinary-ooxml", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        await using var verification = CreateContext();
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal((int)RetainedProcessorBranchState.Pending, branch.State);
        var durable = await verification.SourceProcessorForceRequests.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal(seeded.BranchId, durable.SourceProcessorBranchId);
        Assert.Equal(action.ActionId, durable.ActionId);
        Assert.Equal(action.RequestFingerprint, durable.RequestFingerprint);
        Assert.Null(durable.ForceAttemptBranchId);
        Assert.Null(durable.ForceAttemptLeaseGeneration);
        var audit = await verification.AuditEvents.SingleAsync(value => value.EventType == "operator_action.retained_processor");
        Assert.Equal("anonymous-direct-loopback", audit.Actor);
        Assert.Null(audit.SourceRootId);
        Assert.Null(audit.SourceRevisionId);
        Assert.Null(audit.SourceActivityId);
        Assert.Equal($"operator-action:{action.ActionId}", audit.CorrelationId);
        Assert.Contains("force-request-requested", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-source-sentinel", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("force-foundation", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task A_legacy_history_policy_tuple_cannot_claim_a_current_retained_ooxml_force_request()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var requestId = await SeedLegacyHistoryForceRequestAsync(seeded.BranchId);

        var claims = await store.ClaimForceAsync("legacy-history-owner", 1,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None);

        Assert.Empty(claims);
        await using var verification = CreateContext();
        var durable = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == requestId);
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Requested, durable.State);
        Assert.Equal((int)RetainedProcessorBranchState.Pending, branch.State);
    }

    [NativeSqlServerFact]
    public async Task Operator_action_lifecycle_events_expose_only_the_opaque_action_binding()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        var claim = Assert.Single(await store.ClaimForceAsync("privacy-force-owner", 1,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.True(await store.FailAsync(claim, new RetainedProcessorFailure("operator-action-privacy-block", []), CancellationToken.None));

        await using var verification = CreateContext();
        var audit = await verification.AuditEvents
            .Where(value => value.EventType == "operator_action.retained_processor")
            .OrderBy(value => value.Id)
            .ToListAsync();
        Assert.Equal(3, audit.Count);
        Assert.All(audit, value =>
        {
            Assert.Equal("anonymous-direct-loopback", value.Actor);
            Assert.Null(value.SourceRootId);
            Assert.Null(value.SourceRevisionId);
            Assert.Null(value.SourceActivityId);
            Assert.Equal($"operator-action:{action.ActionId}", value.CorrelationId);
            Assert.Contains("document_ooxml", value.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("retry", value.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private-source-sentinel", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("force-foundation", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
        });
    }

    [NativeSqlServerFact]
    public async Task Open_force_request_remains_listed_as_a_non_forceable_receipt_after_the_branch_moves_to_pending()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);

        var request = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var receipt = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);

        Assert.Equal(action.ActionId, receipt.ActionId);
        Assert.False(receipt.CanForce);
        Assert.Equal(request.State, receipt.RequestState);
    }

    [NativeSqlServerTheory]
    [InlineData("missing")]
    [InlineData("checksum-mismatch")]
    public async Task A_supported_ooxml_outcome_is_absent_when_its_retained_artifact_is_missing_or_mismatched(string artifactCondition)
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync(
            artifactContentSha256: artifactCondition == "checksum-mismatch"
                ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                : null);
        await using (var mutate = CreateContext())
        {
            var artifact = await mutate.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            if (artifactCondition == "missing")
            {
                mutate.SourceArtifacts.Remove(artifact);
            }

            await mutate.SaveChangesAsync();
        }

        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.DoesNotContain(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
    }

    [NativeSqlServerTheory]
    [InlineData("missing")]
    [InlineData("checksum-mismatch")]
    public async Task Public_projection_keeps_an_artifact_invalid_current_block_non_forceable_and_independently_ignoreable(
        string artifactCondition)
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync(
            artifactContentSha256: artifactCondition == "checksum-mismatch"
                ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                : null);
        await using (var mutate = CreateContext())
        {
            var artifact = await mutate.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            if (artifactCondition == "missing") mutate.SourceArtifacts.Remove(artifact);
            await mutate.SaveChangesAsync();
        }
        byte[] rowVersion;
        await using (var identityContext = CreateContext())
        {
            rowVersion = await identityContext.SourceProcessorBranches.Where(value => value.Id == seeded.BranchId)
                .Select(value => value.RowVersion).SingleAsync();
        }
        var actionId = OoxmlForceRequestIdentity.CreateActionId(
            seeded.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, rowVersion);
        var factory = new ContextFactory(_fixture.ConnectionString);
        var branchStore = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var store = new SqlOperatorActionStore(factory, branchStore);

        var action = Assert.Single(await store.ListAsync(includeIgnored: false, 100, CancellationToken.None),
            value => value.ActionId == actionId);
        Assert.False(action.RetryAvailable);
        Assert.False(action.OverrideAvailable);
        var rejection = await Assert.ThrowsAsync<OperatorActionRequestRejectedException>(async () =>
            await store.ExecuteAsync(new OperatorActionMutationCommand(
                action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
                action.BlockedRowVersionToken, RetryActionKind), CancellationToken.None));
        var ignored = await store.ExecuteAsync(new OperatorActionMutationCommand(
            action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
            action.BlockedRowVersionToken, "ignore"), CancellationToken.None);

        Assert.Equal("operator-action-stale", rejection.ReasonCode);
        Assert.True(ignored.Ignored);
        Assert.DoesNotContain(await store.ListAsync(includeIgnored: false, 100, CancellationToken.None),
            value => value.ActionId == action.ActionId);
        Assert.Contains(await store.ListAsync(includeIgnored: true, 100, CancellationToken.None),
            value => value.ActionId == action.ActionId && value.Ignored);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorForceRequests.Where(
            value => value.ActionId == action.ActionId).ToListAsync());
    }

    [NativeSqlServerTheory]
    [MemberData(nameof(HardDenialReasonCodes))]
    public async Task Hard_denial_reason_is_never_listed_as_a_runnable_operator_action(string reasonCode)
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: reasonCode);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        Assert.DoesNotContain(
            await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
    }

    [NativeSqlServerFact]
    public async Task Public_projection_keeps_hard_denial_and_unregistered_capability_actions_ignoreable_and_filters_only_by_ignore_head()
    {
        const string noPolicyReasonCode = "operator-action-projection-no-policy-test";
        var hard = await SeedCurrentBlockedOoxmlAsync(outcomeCode: "office-document-encrypted", seedRetryPolicy: false);
        var noPolicy = await SeedCurrentBlockedOoxmlAsync(outcomeCode: noPolicyReasonCode, seedRetryPolicy: false);
        var factory = new ContextFactory(_fixture.ConnectionString);
        var branchStore = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var store = new SqlOperatorActionStore(factory, branchStore);
        await using var identityContext = CreateContext();
        var hardRowVersion = await identityContext.SourceProcessorBranches.Where(value => value.Id == hard.BranchId)
            .Select(value => value.RowVersion).SingleAsync();
        var noPolicyRowVersion = await identityContext.SourceProcessorBranches.Where(value => value.Id == noPolicy.BranchId)
            .Select(value => value.RowVersion).SingleAsync();
        var hardActionId = OoxmlForceRequestIdentity.CreateActionId(hard.BranchId,
            OoxmlStructuralTextProcessor.Capability.Id, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, hardRowVersion);
        var noPolicyActionId = OoxmlForceRequestIdentity.CreateActionId(noPolicy.BranchId,
            OoxmlStructuralTextProcessor.Capability.Id, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, noPolicyRowVersion);

        var listed = await store.ListAsync(includeIgnored: false, 100, CancellationToken.None);
        var hardAction = Assert.Single(listed, value => value.ActionId == hardActionId);
        var noPolicyAction = Assert.Single(listed, value => value.ActionId == noPolicyActionId);
        Assert.False(hardAction.OverrideAvailable);
        Assert.False(hardAction.RetryAvailable);
        Assert.False(noPolicyAction.OverrideAvailable);
        Assert.False(noPolicyAction.RetryAvailable);

        await using (var disable = CreateContext())
        {
            disable.SourceCapabilities.Remove(await disable.SourceCapabilities.SingleAsync(
                value => value.Id == OoxmlStructuralTextProcessor.Capability.Id));
            await disable.SaveChangesAsync();
        }

        var withoutCapability = await store.ListAsync(includeIgnored: false, 100, CancellationToken.None);
        hardAction = Assert.Single(withoutCapability, value => value.ActionId == hardAction.ActionId);
        noPolicyAction = Assert.Single(withoutCapability, value => value.ActionId == noPolicyAction.ActionId);
        Assert.Equal("descriptor-disabled", hardAction.ActionState);
        Assert.Equal("descriptor-disabled", noPolicyAction.ActionState);
        Assert.False(hardAction.OverrideAvailable);
        Assert.False(hardAction.RetryAvailable);

        var ignored = await store.ExecuteAsync(new OperatorActionMutationCommand(
            hardAction.ActionId, Guid.NewGuid(), hardAction.RequestFingerprint,
            hardAction.BlockedRowVersionToken, "ignore"), CancellationToken.None);

        Assert.False(ignored.WasReplay);
        Assert.DoesNotContain(await store.ListAsync(includeIgnored: false, 100, CancellationToken.None),
            value => value.ActionId == hardAction.ActionId);
        var included = Assert.Single(await store.ListAsync(includeIgnored: true, 100, CancellationToken.None),
            value => value.ActionId == hardAction.ActionId);
        Assert.True(included.Ignored);
        Assert.False(included.OverrideAvailable);
        Assert.False(included.RetryAvailable);
        Assert.NotEqual(hard.BranchId, noPolicy.BranchId);
    }

    [NativeSqlServerFact]
    public async Task Public_mutation_classification_comes_from_the_serialised_durable_operation_result()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var factory = new ContextFactory(_fixture.ConnectionString);
        var branchStore = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var store = new SqlOperatorActionStore(factory, branchStore);
        await using var identityContext = CreateContext();
        var rowVersion = await identityContext.SourceProcessorBranches.Where(value => value.Id == seeded.BranchId)
            .Select(value => value.RowVersion).SingleAsync();
        var actionId = OoxmlForceRequestIdentity.CreateActionId(seeded.BranchId,
            OoxmlStructuralTextProcessor.Capability.Id, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, rowVersion);
        var action = Assert.Single(await store.ListAsync(includeIgnored: false, 100, CancellationToken.None),
            value => value.ActionId == actionId && value.ActionState == "blocked");
        var firstCommand = new OperatorActionMutationCommand(
            action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken, RetryActionKind);

        var first = await store.ExecuteAsync(firstCommand, CancellationToken.None);
        var replay = await store.ExecuteAsync(firstCommand, CancellationToken.None);
        var additionalOperationId = Guid.NewGuid();
        var additional = await store.ExecuteAsync(firstCommand with { OperationId = additionalOperationId }, CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<OperatorActionRequestRejectedException>(async () =>
            await store.ExecuteAsync(firstCommand with
            {
                OperationId = additionalOperationId,
                ActionKind = "policy-override"
            }, CancellationToken.None));

        Assert.False(first.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.False(additional.WasReplay);
        Assert.Equal(first.ActionId, replay.ActionId);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(first.State, replay.State);
        Assert.Equal(first.IgnoreSequence, replay.IgnoreSequence);
        Assert.Equal(first.Ignored, replay.Ignored);
        Assert.Equal(first.CommittedAtUtc, replay.CommittedAtUtc);
        Assert.Equal("operator-operation-conflict", conflict.ReasonCode);
        await using var verification = CreateContext();
        Assert.Equal(2, await verification.OperatorActionOperationLedger.CountAsync(
            value => value.ActionId == action.ActionId));
        Assert.Equal(seeded.BranchId,
            (await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == action.ActionId)).SourceProcessorBranchId);
    }

    [NativeSqlServerFact]
    public async Task Public_store_resolves_an_existing_operation_conflict_before_malformed_changed_binding_fields()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var factory = new ContextFactory(_fixture.ConnectionString);
        var branchStore = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var store = new SqlOperatorActionStore(factory, branchStore);
        byte[] rowVersion;
        await using (var identityContext = CreateContext())
        {
            rowVersion = await identityContext.SourceProcessorBranches.Where(value => value.Id == seeded.BranchId)
                .Select(value => value.RowVersion).SingleAsync();
        }
        var actionId = OoxmlForceRequestIdentity.CreateActionId(
            seeded.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, rowVersion);
        var action = Assert.Single(await store.ListAsync(includeIgnored: false, 100, CancellationToken.None),
            value => value.ActionId == actionId);
        var operationId = Guid.NewGuid();
        await store.ExecuteAsync(new OperatorActionMutationCommand(
            action.ActionId, operationId, action.RequestFingerprint,
            action.BlockedRowVersionToken, RetryActionKind), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<OperatorActionRequestRejectedException>(async () =>
            await store.ExecuteAsync(new OperatorActionMutationCommand(
                "bad-action", operationId, "bad-fingerprint", "bad-row-version", "ignore"),
                CancellationToken.None));

        Assert.Equal("operator-operation-conflict", conflict.ReasonCode);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionOperationLedger.Where(
            value => value.OperationId == operationId).ToListAsync());
        Assert.Equal(seeded.BranchId,
            (await verification.OperatorActionActionLedger.SingleAsync(
                value => value.ActionId == action.ActionId)).SourceProcessorBranchId);
    }

    [NativeSqlServerFact]
    public async Task Public_projection_computes_force_availability_for_the_exact_newest_hundred_candidates()
    {
        const string reasonCode = "operator-action-exact-page-availability-test";
        var orderedAtUtc = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        (Guid BranchId, Guid RevisionId) newest = default;
        for (var index = 0; index < 101; index++)
        {
            newest = await SeedCurrentBlockedOoxmlAsync(
                outcomeCode: reasonCode,
                blockedAtUtc: orderedAtUtc.AddMinutes(index));
        }
        await SeedCapabilityPolicyAsync(reasonCode, "policy-override");
        byte[] newestRowVersion;
        await using (var identityContext = CreateContext())
        {
            newestRowVersion = await identityContext.SourceProcessorBranches
                .Where(value => value.Id == newest.BranchId)
                .Select(value => value.RowVersion)
                .SingleAsync();
        }
        var newestActionId = OoxmlForceRequestIdentity.CreateActionId(
            newest.BranchId,
            OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            newestRowVersion);
        var factory = new ContextFactory(_fixture.ConnectionString);
        var store = new SqlOperatorActionStore(
            factory,
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System));

        var projected = await store.ListAsync(includeIgnored: false, 100, CancellationToken.None);

        Assert.Equal(100, projected.Count);
        var newestAction = Assert.Single(projected, value => value.ActionId == newestActionId);
        Assert.True(newestAction.RetryAvailable);
        Assert.True(newestAction.OverrideAvailable);
    }

    [NativeSqlServerFact]
    public async Task Only_an_exact_registered_retry_policy_may_make_a_non_hard_blocked_generation_actionable()
    {
        const string unregisteredReasonCode = "operator-action-retryable-unregistered-test";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: unregisteredReasonCode, seedRetryPolicy: false);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        Assert.DoesNotContain(
            await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);

        await SeedCapabilityPolicyAsync(unregisteredReasonCode, RetryActionKind);

        var action = Assert.Single(
            await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        var receipt = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);

        Assert.Equal(OoxmlForceRequestState.Requested, receipt.State);
    }

    [NativeSqlServerFact]
    public async Task Exact_registered_non_hard_policy_override_creates_a_retained_bound_requested_receipt()
    {
        const string safeReasonCode = "operator-action-policy-override-success-test";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: safeReasonCode, seedRetryPolicy: false);
        await SeedCapabilityPolicyAsync(safeReasonCode, "policy-override");
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        await using var before = CreateContext();
        var branch = await before.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        var actionId = OoxmlForceRequestIdentity.CreateActionId(
            branch.Id,
            OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            branch.RowVersion);
        var rowVersionToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(branch.RowVersion);
        var requestFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, rowVersionToken);

        var receipt = await store.RequestPolicyOverrideAsync(
            new OoxmlForceRequestCommand(actionId, Guid.NewGuid(), requestFingerprint, rowVersionToken),
            CancellationToken.None);

        Assert.Equal(OoxmlForceRequestState.Requested, receipt.State);
        await using var verification = CreateContext();
        var request = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == receipt.RequestId);
        var action = await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == actionId);
        Assert.Equal("policy-override", request.ActionKind);
        Assert.Equal(safeReasonCode, request.PolicyReasonCode);
        Assert.Equal("retained-binding", request.SafetyContractId);
        Assert.Equal("retained-processor-branch-store", request.HandlerId);
        Assert.Equal("policy-override", action.ActionKind);
        Assert.Equal((int)RetainedProcessorBranchState.Pending,
            (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
    }

    [NativeSqlServerTheory]
    [InlineData(RetryActionKind)]
    [InlineData("policy-override")]
    public async Task A_new_retry_or_policy_override_rejects_an_empty_operation_identity_without_durable_rows(string actionKind)
    {
        const string safeReasonCode = "operator-action-empty-operation-test";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: safeReasonCode, seedRetryPolicy: false);
        await SeedCapabilityPolicyAsync(safeReasonCode, actionKind);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        await using var before = CreateContext();
        var branch = await before.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        var actionId = OoxmlForceRequestIdentity.CreateActionId(
            branch.Id,
            OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            branch.RowVersion);
        var rowVersionToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(branch.RowVersion);
        var requestFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, rowVersionToken);
        var command = new OoxmlForceRequestCommand(actionId, Guid.Empty, requestFingerprint, rowVersionToken);
        Func<Task> request = actionKind == RetryActionKind
            ? () => store.RequestForceAsync(command, CancellationToken.None).AsTask()
            : () => store.RequestPolicyOverrideAsync(command, CancellationToken.None).AsTask();

        var rejection = await Assert.ThrowsAsync<ArgumentException>(request);

        Assert.Equal("command", rejection.ParamName);
        await using var verification = CreateContext();
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == Guid.Empty));
        Assert.Null(await verification.OperatorActionActionLedger.SingleOrDefaultAsync(value => value.ActionId == actionId));
        Assert.Null(await verification.SourceProcessorForceRequests.SingleOrDefaultAsync(value => value.ActionId == actionId));
        Assert.Equal((int)RetainedProcessorBranchState.Blocked,
            (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
    }

    [NativeSqlServerFact]
    public async Task Ignore_creates_an_independent_durable_identity_for_a_current_listed_action_without_a_force_request()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);

        await using (var before = CreateContext())
        {
            Assert.Empty(await before.OperatorActionActionLedger.ToListAsync());
            Assert.Empty(await before.SourceProcessorForceRequests.ToListAsync());
        }

        var receipt = await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
                OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true),
            CancellationToken.None);

        Assert.Equal(1, receipt.Sequence);
        Assert.True(receipt.IsIgnored);
        await using var verification = CreateContext();
        var identity = await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == action.ActionId);
        var head = await verification.SourceProcessorActionIgnoreHeads.SingleAsync(value => value.ActionId == action.ActionId);
        var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal("ignore", identity.ActionKind);
        Assert.Equal(seeded.BranchId, identity.SourceProcessorBranchId);
        Assert.Equal(OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), identity.BlockedRowVersion);
        Assert.Null(identity.SourceProcessorForceRequestId);
        Assert.Equal(1, head.Sequence);
        Assert.True(head.IsIgnored);
        Assert.Empty(await verification.SourceProcessorForceRequests.ToListAsync());
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
        Assert.Equal(1, await verification.SourceProcessorAttempts.CountAsync(value => value.BranchId == seeded.BranchId));
    }

    [NativeSqlServerFact]
    public async Task Fresh_ignore_accepts_a_current_override_only_action_without_inventing_retry_authority()
    {
        const string reasonCode = "operator-action-override-only-ignore-test";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: reasonCode, seedRetryPolicy: false);
        await SeedCapabilityPolicyAsync(reasonCode, "policy-override");
        byte[] rowVersion;
        await using (var context = CreateContext())
        {
            rowVersion = await context.SourceProcessorBranches.Where(value => value.Id == seeded.BranchId)
                .Select(value => value.RowVersion).SingleAsync();
        }
        var actionId = OoxmlForceRequestIdentity.CreateActionId(seeded.BranchId,
            OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            rowVersion);
        var token = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(rowVersion);
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);

        var receipt = await store.SetOperatorActionIgnoreAsync(new OperatorActionIgnoreCommand(
            actionId, Guid.NewGuid(), OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, token),
            rowVersion, IsIgnored: true), CancellationToken.None);

        Assert.True(receipt.IsIgnored);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorForceRequests.ToListAsync());
        Assert.DoesNotContain(await verification.OperatorActionCapabilityPolicies.ToListAsync(),
            value => value.ReasonCode == reasonCode && value.ActionKind == "retry");
    }

    [NativeSqlServerFact]
    public async Task An_independent_ignore_identity_does_not_change_the_later_retry_lifecycle()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var command = new OperatorActionIgnoreCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
            OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true);
        await store.SetOperatorActionIgnoreAsync(command, CancellationToken.None);

        var request = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);

        Assert.Equal(OoxmlForceRequestState.Requested, request.State);
        await using var verification = CreateContext();
        var identity = await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == action.ActionId);
        Assert.Equal("ignore", identity.ActionKind);
        Assert.Null(identity.SourceProcessorForceRequestId);
        Assert.Equal("retry", (await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId)).ActionKind);
    }

    [NativeSqlServerFact]
    public async Task Ignore_resolves_an_existing_operation_before_live_eligibility_and_maps_a_malformed_collision_to_conflict()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        var command = new OperatorActionIgnoreCommand(action.ActionId, operationId, action.RequestFingerprint,
            OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true);
        var created = await store.SetOperatorActionIgnoreAsync(command, CancellationToken.None);

        await using (var invalidate = CreateContext())
        {
            var branch = await invalidate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Pending;
            await invalidate.SaveChangesAsync();
        }

        var replay = await store.SetOperatorActionIgnoreAsync(command, CancellationToken.None);
        var collision = await Assert.ThrowsAsync<OperatorActionRejectedException>(async () =>
            await store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand("not-a-sha256-action-id", operationId, "not-a-sha256-fingerprint", [1], IsIgnored: false),
                CancellationToken.None));

        Assert.False(created.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.Equal(created.ActionId, replay.ActionId);
        Assert.Equal(created.Sequence, replay.Sequence);
        Assert.Equal(created.IsIgnored, replay.IsIgnored);
        Assert.Equal(created.CommittedAtUtc, replay.CommittedAtUtc);
        Assert.Equal("operator-operation-conflict", collision.ReasonCode);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionOperationLedger.Where(value => value.OperationId == operationId).ToListAsync());
        Assert.Single(await verification.SourceProcessorActionIgnoreHeads.Where(value => value.ActionId == action.ActionId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task A_new_ignore_operation_rejects_a_noncanonical_valid_fingerprint_without_a_receipt_or_head()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        var operationId = Guid.NewGuid();
        var mismatchedFingerprint = $"{(action.RequestFingerprint[0] == '0' ? '1' : '0')}{action.RequestFingerprint[1..]}";

        var rejection = await Assert.ThrowsAsync<OperatorActionRejectedException>(async () =>
            await store.SetOperatorActionIgnoreAsync(
                new OperatorActionIgnoreCommand(action.ActionId, operationId, mismatchedFingerprint,
                    OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true),
                CancellationToken.None));

        Assert.Equal("operator-operation-conflict", rejection.ReasonCode);
        await using var verification = CreateContext();
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == operationId));
        Assert.Null(await verification.SourceProcessorActionIgnoreHeads.SingleOrDefaultAsync(value => value.ActionId == action.ActionId));
        Assert.Single(await verification.OperatorActionActionLedger.Where(value => value.ActionId == action.ActionId).ToListAsync());
        Assert.Single(await verification.SourceProcessorForceRequests.Where(value => value.ActionId == action.ActionId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Sequential_ignore_then_unignore_records_distinct_receipts_and_leaves_the_current_action_unignored()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);

        var ignored = await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
                OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true),
            CancellationToken.None);
        var unignored = await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
                OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: false),
            CancellationToken.None);

        Assert.Equal(1, ignored.Sequence);
        Assert.True(ignored.IsIgnored);
        Assert.Equal(2, unignored.Sequence);
        Assert.False(unignored.IsIgnored);
        await using var verification = CreateContext();
        var head = await verification.SourceProcessorActionIgnoreHeads.SingleAsync(value => value.ActionId == action.ActionId);
        Assert.Equal(2, head.Sequence);
        Assert.False(head.IsIgnored);
        var operations = await verification.OperatorActionOperationLedger
            .Where(value => value.ActionId == action.ActionId && value.IgnoreSequence != null)
            .OrderBy(value => value.IgnoreSequence)
            .ToListAsync();
        Assert.Equal([1L, 2L], operations.Select(value => value.IgnoreSequence));
        Assert.Equal([true, false], operations.Select(value => value.IgnoreState));
    }

    [NativeSqlServerFact]
    public async Task A_new_blocked_generation_does_not_inherit_the_previous_action_ignore_state()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        await store.SetOperatorActionIgnoreAsync(
            new OperatorActionIgnoreCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint,
                OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken), IsIgnored: true),
            CancellationToken.None);
        var claim = Assert.Single(await store.ClaimForceAsync("new-generation-ignore-owner", 1,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.True(await store.FailAsync(claim, new RetainedProcessorFailure(RetryableReasonCode, []), CancellationToken.None));

        var nextAction = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);

        Assert.NotEqual(action.ActionId, nextAction.ActionId);
        Assert.True(nextAction.CanForce);
        await using var verification = CreateContext();
        Assert.True((await verification.SourceProcessorActionIgnoreHeads.SingleAsync(value => value.ActionId == action.ActionId)).IsIgnored);
        Assert.DoesNotContain(await verification.SourceProcessorActionIgnoreHeads.ToListAsync(), value => value.ActionId == nextAction.ActionId);
    }

    [NativeSqlServerFact]
    public async Task Override_and_retry_are_mutually_exclusive_for_one_immutable_action_version()
    {
        const string safeReasonCode = "operator-action-kind-conflict-test";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: safeReasonCode, seedRetryPolicy: false);
        await SeedCapabilityPolicyAsync(safeReasonCode, RetryActionKind);
        await SeedCapabilityPolicyAsync(safeReasonCode, "policy-override");
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(
            await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);

        await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestPolicyOverrideAsync(
                new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None));

        Assert.Equal("operator-action-kind-conflict", conflict.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Open_force_receipts_remain_visible_when_the_fresh_candidate_page_is_saturated()
    {
        var seeded = new List<(Guid BranchId, Guid RevisionId)>();
        for (var index = 0; index < 18; index++)
        {
            seeded.Add(await SeedCurrentBlockedOoxmlAsync());
        }

        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var claimedAction = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded[0].BranchId);
        var claimed = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(claimedAction.ActionId, Guid.NewGuid(), claimedAction.RequestFingerprint, claimedAction.BlockedRowVersionToken), CancellationToken.None);
        Assert.Contains(await store.ClaimForceAsync("saturated-receipt-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == claimedAction.BranchId);
        var requestedAction = (await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None))
            .First(value => value.CanForce && value.BranchId != claimedAction.BranchId);
        var requested = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(requestedAction.ActionId, Guid.NewGuid(), requestedAction.RequestFingerprint, requestedAction.BlockedRowVersionToken), CancellationToken.None);

        var summaries = await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None);

        Assert.Equal(16, summaries.Count(value => value.CanForce));
        var requestedReceipt = Assert.Single(summaries, value => value.ActionId == requested.ActionId);
        Assert.False(requestedReceipt.CanForce);
        Assert.Equal(OoxmlForceRequestState.Requested, requestedReceipt.RequestState);
        var claimedReceipt = Assert.Single(summaries, value => value.ActionId == claimed.ActionId);
        Assert.False(claimedReceipt.CanForce);
        Assert.Equal(OoxmlForceRequestState.Claimed, claimedReceipt.RequestState);
    }

    [NativeSqlServerFact]
    public async Task A_force_action_requires_the_activity_to_reference_the_exact_branch_revision()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        await using (var mutate = CreateContext())
        {
            var original = await mutate.SourceRevisions.SingleAsync(value => value.Id == seeded.RevisionId);
            var replacementId = Guid.NewGuid();
            mutate.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = replacementId, SourceRootId = original.SourceRootId, StableSourceIdentity = $"force-revision-mismatch:{replacementId:N}",
                Revision = original.Revision + 1, ContentSha256 = original.ContentSha256, CanonicalPath = $"C:\\force-foundation\\{replacementId:N}.docx",
                Classification = original.Classification, Extension = original.Extension, ByteLength = original.ByteLength,
                DiscoveredAtUtc = original.DiscoveredAtUtc, DiscoveryEvidenceJson = "{}"
            });
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            (await mutate.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId)).SourceRevisionId = replacementId;
            await mutate.SaveChangesAsync();
        }

        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.DoesNotContain(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
    }

    [NativeSqlServerTheory]
    [InlineData("kind")]
    [InlineData("execution")]
    [InlineData("version")]
    public async Task A_force_action_requires_the_exact_pending_ooxml_activity_shape(string mismatch)
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        await using (var mutate = CreateContext())
        {
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            var activity = await mutate.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
            switch (mismatch)
            {
                case "kind": activity.ActivityKind = (int)SourceActivityKind.DocumentParsing; break;
                case "execution": activity.ExecutionClass = (int)ExecutionClass.DeferredCapability; break;
                case "version": activity.ProcessorVersion = "unexpected-version"; break;
            }
            await mutate.SaveChangesAsync();
        }

        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.DoesNotContain(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
    }

    [NativeSqlServerFact]
    public async Task Forced_claim_binds_exactly_one_new_attempt_and_a_repeated_block_finalises_the_original_receipt()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var first = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);

        var claim = Assert.Single(await store.ClaimForceAsync("forced-ooxml", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        Assert.Equal(action.OriginalBlockedLeaseGeneration + 1, claim.LeaseGeneration);
        Assert.True(await store.FailAsync(claim, new RetainedProcessorFailure("office-document-encrypted", []), CancellationToken.None));

        var replay = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(OoxmlForceRequestState.Blocked, replay.State);
        Assert.Equal("office-document-encrypted", replay.TerminalReasonCode);
        Assert.DoesNotContain(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await using var verification = CreateContext();
        var durable = await verification.SourceProcessorForceRequests.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Blocked, durable.State);
        Assert.Equal(claim.BranchId, durable.ForceAttemptBranchId);
        Assert.Equal(claim.LeaseGeneration, durable.ForceAttemptLeaseGeneration);
        Assert.Equal("office-document-encrypted", durable.TerminalReasonCode);
        Assert.Equal(2, await verification.SourceProcessorAttempts.CountAsync(value => value.BranchId == seeded.BranchId));
    }

    [NativeSqlServerFact]
    public async Task Database_time_reconciliation_expires_requested_force_work_into_a_new_blocked_action_version()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var first = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
            CancellationToken.None);
        await using (var expiry = CreateContext())
        {
            await expiry.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorForceRequests]
                SET [RequestedAtUtc] = DATEADD(minute, -6, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                    [ClaimExpiresAtUtc] = DATEADD(minute, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                WHERE [Id] = {first.RequestId};
                """);
        }

        Assert.Equal(1, await store.ReconcileForceRequestsAsync(CancellationToken.None));
        var next = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        Assert.NotEqual(action.ActionId, next.ActionId);
        await using var verification = CreateContext();
        var requestAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == first.RequestId);
        Assert.Equal((byte)OoxmlForceRequestState.Expired, requestAfter.State);
        Assert.Equal("force-request-claim-expired", requestAfter.TerminalReasonCode);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
    }

    [NativeSqlServerFact]
    public async Task Operation_and_action_identities_replay_before_current_eligibility_and_operation_collisions_do_not_mutate()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        var created = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);

        var operationReplay = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var mismatchedVersion = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(Convert.FromHexString("0102030405060708"));
        var collision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, operationId,
                OoxmlForceRequestIdentity.CreateRequestFingerprint(action.ActionId, mismatchedVersion), mismatchedVersion), CancellationToken.None));
        var mismatchedVersionOperationId = Guid.NewGuid();
        var actionReplayWithAnotherOperation = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, mismatchedVersionOperationId,
                    OoxmlForceRequestIdentity.CreateRequestFingerprint(action.ActionId, mismatchedVersion), mismatchedVersion), CancellationToken.None));

        Assert.False(created.WasReplay);
        Assert.True(operationReplay.WasReplay);
        Assert.Equal(created.RequestId, operationReplay.RequestId);
        Assert.Equal(created.ActionId, operationReplay.ActionId);
        Assert.Equal(created.OperationId, operationReplay.OperationId);
        Assert.Equal(created.State, operationReplay.State);
        Assert.Equal(created.TerminalReasonCode, operationReplay.TerminalReasonCode);
        Assert.Equal(created.ForceAttemptLeaseGeneration, operationReplay.ForceAttemptLeaseGeneration);
        Assert.Equal(created.CommittedAtUtc, operationReplay.CommittedAtUtc);
        Assert.Equal("operator-operation-conflict", collision.ReasonCode);
        Assert.Equal("operator-operation-conflict", actionReplayWithAnotherOperation.ReasonCode);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceProcessorForceRequests.CountAsync(value => value.SourceProcessorBranchId == seeded.BranchId));
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == mismatchedVersionOperationId));
        Assert.Equal(1, await verification.AuditEvents.CountAsync(value => value.EventType == "operator_action.retained_processor" && value.CorrelationId == $"operator-action:{action.ActionId}"));
    }

    [NativeSqlServerFact]
    public async Task Operation_collision_is_resolved_before_the_changed_action_or_row_version_is_parsed()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);

        var collision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(new OoxmlForceRequestCommand(new string('b', 64), operationId, action.RequestFingerprint,
                OoxmlForceRequestIdentity.EncodeBlockedRowVersion(Convert.FromHexString("0102030405060708"))), CancellationToken.None));

        Assert.Equal("operator-operation-conflict", collision.ReasonCode);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceProcessorForceRequests.CountAsync(value => value.SourceProcessorBranchId == seeded.BranchId));
        Assert.Equal(1, await verification.AuditEvents.CountAsync(value => value.EventType == "operator_action.retained_processor" && value.CorrelationId == $"operator-action:{action.ActionId}"));
    }

    [NativeSqlServerFact]
    public async Task A_new_operation_for_an_existing_action_is_durably_replayed_and_conflicts_globally()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var original = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var replayOperationId = Guid.NewGuid();

        var additionalOperation = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, replayOperationId, action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var exactReplay = await store.RequestForceAsync(
            new OoxmlForceRequestCommand(action.ActionId, replayOperationId, action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var fingerprintCollisionOperationId = Guid.NewGuid();
        var fingerprintCollision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, fingerprintCollisionOperationId, new string('d', 64), action.BlockedRowVersionToken),
                CancellationToken.None));
        var mismatchedVersion = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(Convert.FromHexString("0102030405060708"));
        var versionCollisionOperationId = Guid.NewGuid();
        var versionCollision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, versionCollisionOperationId,
                    OoxmlForceRequestIdentity.CreateRequestFingerprint(action.ActionId, mismatchedVersion), mismatchedVersion),
                CancellationToken.None));
        var oversizedFingerprintOperationId = Guid.NewGuid();
        var oversizedFingerprintCollision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, oversizedFingerprintOperationId, new string('d', 65), action.BlockedRowVersionToken),
                CancellationToken.None));
        var collision = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(new string('c', 64), replayOperationId, action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None));

        Assert.False(original.WasReplay);
        Assert.False(additionalOperation.WasReplay);
        Assert.True(exactReplay.WasReplay);
        Assert.Equal(original.RequestId, additionalOperation.RequestId);
        Assert.Equal(original.RequestId, exactReplay.RequestId);
        Assert.Equal(additionalOperation.CommittedAtUtc, exactReplay.CommittedAtUtc);
        Assert.Equal("operator-operation-conflict", fingerprintCollision.ReasonCode);
        Assert.Equal("operator-operation-conflict", versionCollision.ReasonCode);
        Assert.Equal("operator-operation-conflict", oversizedFingerprintCollision.ReasonCode);
        Assert.Equal("operator-operation-conflict", collision.ReasonCode);
        await using var verification = CreateContext();
        var operation = await verification.OperatorActionOperationLedger.SingleAsync(value => value.OperationId == replayOperationId);
        Assert.Equal(action.ActionId, operation.ActionId);
        Assert.Equal(action.RequestFingerprint, operation.RequestFingerprint);
        Assert.Equal(2, await verification.OperatorActionOperationLedger.CountAsync(value => value.ActionId == action.ActionId));
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == fingerprintCollisionOperationId));
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == versionCollisionOperationId));
        Assert.Null(await verification.OperatorActionOperationLedger.SingleOrDefaultAsync(value => value.OperationId == oversizedFingerprintOperationId));
    }

    [NativeSqlServerFact]
    public async Task Current_integrity_blocks_are_absent_and_a_stale_row_version_cannot_create_a_receipt()
    {
        var integrity = await SeedCurrentBlockedOoxmlAsync();
        var stale = await SeedCurrentBlockedOoxmlAsync();
        var legacy = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var staleAction = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == stale.BranchId);
        await using (var mutate = CreateContext())
        {
            var integrityAttempt = await mutate.SourceProcessorAttempts.SingleAsync(value => value.BranchId == integrity.BranchId);
            integrityAttempt.OutcomeCode = "retained-artifact-checksum-invalid";
            mutate.SourceArtifacts.Remove(await mutate.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == integrity.RevisionId));
            var staleBranch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == stale.BranchId);
            staleBranch.UpdatedAtUtc = staleBranch.UpdatedAtUtc.AddSeconds(1);
            await mutate.SaveChangesAsync();
            await mutate.Database.ExecuteSqlInterpolatedAsync($"UPDATE [SourceRevisions] SET [Extension] = {".doc"} WHERE [Id] = {legacy.RevisionId}");
        }

        var summaries = await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None);
        Assert.DoesNotContain(summaries, value => value.BranchId == integrity.BranchId);
        Assert.DoesNotContain(summaries, value => value.BranchId == legacy.BranchId);
        var rejected = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestForceAsync(new OoxmlForceRequestCommand(staleAction.ActionId, Guid.NewGuid(), staleAction.RequestFingerprint,
                staleAction.BlockedRowVersionToken), CancellationToken.None));

        Assert.Equal("operator-action-stale", rejected.ReasonCode);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorForceRequests.Where(value => value.SourceProcessorBranchId == stale.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Reconciliation_terminalises_a_stale_requested_receipt_without_overwriting_a_newer_branch_generation()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var request = await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        await using (var mutate = CreateContext())
        {
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Completed;
            branch.LeaseGeneration++;
            branch.UpdatedAtUtc = branch.UpdatedAtUtc.AddSeconds(1);
            await mutate.SaveChangesAsync();
        }

        Assert.Equal(1, await store.ReconcileForceRequestsAsync(CancellationToken.None));
        await using var verification = CreateContext();
        var requestAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, requestAfter.State);
        Assert.Equal("force-request-cancelled", requestAfter.TerminalReasonCode);
        Assert.Null(requestAfter.ForceAttemptLeaseGeneration);
        var branchAfter = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal((int)RetainedProcessorBranchState.Completed, branchAfter.State);
        Assert.Equal(action.OriginalBlockedLeaseGeneration + 1, branchAfter.LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Reconciliation_terminalises_a_superseded_claimed_attempt_without_touching_a_newer_generation()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var request = await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var forceClaim = Assert.Single(await store.ClaimForceAsync("superseded-force-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        var newerGeneration = forceClaim.LeaseGeneration + 1;
        await using (var mutate = CreateContext())
        {
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Running;
            branch.LeaseGeneration = newerGeneration;
            branch.LeaseOwner = "newer-owner";
            branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            branch.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mutate.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, LeaseGeneration = newerGeneration, StartedAtUtc = DateTimeOffset.UtcNow
            });
            await mutate.SaveChangesAsync();
        }

        Assert.Equal(1, await store.ReconcileForceRequestsAsync(CancellationToken.None));
        await using var verification = CreateContext();
        var requestAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
        var supersededAttempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId && value.LeaseGeneration == forceClaim.LeaseGeneration);
        var newerAttempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId && value.LeaseGeneration == newerGeneration);
        var branchAfter = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, requestAfter.State);
        Assert.Equal("force-request-cancelled", requestAfter.TerminalReasonCode);
        Assert.Equal("force-request-cancelled", supersededAttempt.OutcomeCode);
        Assert.NotNull(supersededAttempt.FinishedAtUtc);
        Assert.Null(newerAttempt.FinishedAtUtc);
        Assert.Equal(newerGeneration, branchAfter.LeaseGeneration);
        Assert.Equal("newer-owner", branchAfter.LeaseOwner);
        Assert.Equal((int)RetainedProcessorBranchState.Running, branchAfter.State);
    }

    [NativeSqlServerFact]
    public async Task Reconciliation_terminalises_a_claimed_force_request_when_its_activity_is_cancelled()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var request = await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var claim = Assert.Single(await store.ClaimForceAsync("cancelled-force-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        await using (var mutate = CreateContext())
        {
            var activity = await mutate.SourceActivities.SingleAsync(value => value.Id == action.SourceActivityId);
            activity.State = (int)SourceActivityState.CancelledSuperseded;
            activity.UpdatedAtUtc = activity.UpdatedAtUtc.AddSeconds(1);
            await mutate.SaveChangesAsync();
        }

        Assert.Equal(1, await store.ReconcileForceRequestsAsync(CancellationToken.None));
        await using var verification = CreateContext();
        var requestAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
        var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId && value.LeaseGeneration == claim.LeaseGeneration);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, requestAfter.State);
        Assert.Equal("force-request-cancelled", requestAfter.TerminalReasonCode);
        Assert.Equal("force-request-cancelled", attempt.OutcomeCode);
        Assert.NotNull(attempt.FinishedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Disabled_or_cancelled_force_work_is_atomically_closed_without_a_source_read()
    {
        var disabled = await SeedCurrentBlockedOoxmlAsync();
        var cancelled = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var actions = await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None);
        var disabledAction = Assert.Single(actions, value => value.BranchId == disabled.BranchId);
        var cancelledAction = Assert.Single(actions, value => value.BranchId == cancelled.BranchId);
        var disabledRequest = await store.RequestForceAsync(new OoxmlForceRequestCommand(disabledAction.ActionId, Guid.NewGuid(), disabledAction.RequestFingerprint, disabledAction.BlockedRowVersionToken), CancellationToken.None);
        var cancelledRequest = await store.RequestForceAsync(new OoxmlForceRequestCommand(cancelledAction.ActionId, Guid.NewGuid(), cancelledAction.RequestFingerprint, cancelledAction.BlockedRowVersionToken), CancellationToken.None);
        await using (var mutate = CreateContext())
        {
            var activity = await mutate.SourceActivities.SingleAsync(value => value.Id == cancelledAction.SourceActivityId);
            activity.State = (int)SourceActivityState.CancelledSuperseded;
            activity.UpdatedAtUtc = activity.UpdatedAtUtc.AddSeconds(1);
            await mutate.SaveChangesAsync();
        }

        Assert.True(await store.ReconcileForceRequestsAsync(ooxmlDescriptorEnabled: false, CancellationToken.None) >= 2);
        await using var verification = CreateContext();
        var disabledAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == disabledRequest.RequestId);
        var cancelledAfter = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == cancelledRequest.RequestId);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, disabledAfter.State);
        Assert.Equal("force-request-descriptor-disabled", disabledAfter.TerminalReasonCode);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, cancelledAfter.State);
        Assert.Equal("force-request-cancelled", cancelledAfter.TerminalReasonCode);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == disabled.BranchId)).State);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == cancelled.BranchId)).State);
    }

    [NativeSqlServerFact]
    public async Task Expired_forced_lease_is_closed_before_a_later_normal_generation_and_is_never_rebound()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var forceClaim = Assert.Single(await store.ClaimForceAsync("force-owner", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        await using (var expire = CreateContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorBranches]
                SET [LeaseExpiresAtUtc] = DATEADD(minute, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                WHERE [Id] = {seeded.BranchId};
                """);
        }

        Assert.Equal(1, await store.ReconcileForceRequestsAsync(CancellationToken.None));
        var normalClaim = Assert.Single(await store.ClaimAsync("normal-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.Equal(forceClaim.LeaseGeneration + 1, normalClaim.LeaseGeneration);
        await using var verification = CreateContext();
        var request = await verification.SourceProcessorForceRequests.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Expired, request.State);
        Assert.Equal("lease-expired-reconciled", request.TerminalReasonCode);
        Assert.Equal(forceClaim.LeaseGeneration, request.ForceAttemptLeaseGeneration);
        var expiredAttempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId && value.LeaseGeneration == forceClaim.LeaseGeneration);
        Assert.Equal("lease-expired-reconciled", expiredAttempt.OutcomeCode);
        Assert.NotNull(expiredAttempt.FinishedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Ordinary_claim_lease_uses_database_utc_when_the_application_clock_is_stale()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        await using (var mutate = CreateContext())
        {
            var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
            branch.State = (int)RetainedProcessorBranchState.Pending;
            branch.LeaseOwner = null;
            branch.LeaseExpiresAtUtc = null;
            await mutate.SaveChangesAsync();
        }

        var store = new SqlRetainedProcessorBranchStore(
            new ContextFactory(_fixture.ConnectionString),
            new FixedTimeProvider(DateTimeOffset.Parse("2001-01-01T00:00:00+00:00")));

        var claim = Assert.Single(await store.ClaimAsync("database-time-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        await using var verification = CreateContext();
        var databaseNow = await verification.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
        var branchAfter = await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
        Assert.Equal(branchAfter.LeaseExpiresAtUtc, claim.LeaseExpiresAtUtc);
        Assert.True(branchAfter.LeaseExpiresAtUtc > databaseNow.AddMinutes(4));
        Assert.True(branchAfter.LeaseExpiresAtUtc < databaseNow.AddMinutes(6));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_distinct_operations_for_one_current_action_return_one_durable_request_and_one_audit_record()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var firstStore = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var secondStore = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await firstStore.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);

        var receipts = await Task.WhenAll(
            firstStore.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None).AsTask(),
            secondStore.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None).AsTask());

        Assert.Equal(receipts[0].RequestId, receipts[1].RequestId);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceProcessorForceRequests.CountAsync(value => value.SourceProcessorBranchId == seeded.BranchId));
        Assert.Equal(1, await verification.AuditEvents.CountAsync(value => value.EventType == "operator_action.retained_processor" && value.CorrelationId == $"operator-action:{action.ActionId}"));
    }

    [NativeSqlServerFact]
    public async Task Arbitrary_persistence_failure_with_a_matching_action_winner_is_exposed_without_retrying_the_request()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var discovery = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await discovery.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        var failure = new ArbitraryActionSaveFailureInterceptor(
            _fixture.ConnectionString,
            action);
        var factory = new ContextFactory(_fixture.ConnectionString, failure);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None));

        Assert.Same(failure.Exception, thrown);
        Assert.Equal(1, factory.CreatedContextCount);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionActionLedger.Where(value => value.ActionId == action.ActionId).ToListAsync());
        Assert.Empty(await verification.OperatorActionOperationLedger.Where(value => value.OperationId == operationId).ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(2601)]
    [InlineData(2627)]
    public async Task Action_ledger_source_force_request_unique_violation_is_exposed_without_replaying_a_matching_action_winner(int errorNumber)
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var discovery = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await discovery.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        var failure = new ActionLedgerSourceForceRequestUniqueViolationInterceptor(
            _fixture.ConnectionString,
            action,
            errorNumber);
        var factory = new ContextFactory(_fixture.ConnectionString, failure);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None));

        Assert.Same(failure.Exception, thrown);
        Assert.Equal(1, factory.CreatedContextCount);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionActionLedger.Where(value => value.ActionId == action.ActionId).ToListAsync());
        Assert.Empty(await verification.OperatorActionOperationLedger.Where(value => value.OperationId == operationId).ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false, "operator-action-kind-conflict")]
    [InlineData(true, "operator-operation-conflict")]
    public async Task Action_ledger_primary_key_race_preserves_cross_kind_conflict_and_operation_precedence(
        bool winnerUsesRequestedOperation,
        string expectedReasonCode)
    {
        var reasonCode = $"operator-action-primary-key-race-{Guid.NewGuid():N}";
        var seeded = await SeedCurrentBlockedOoxmlAsync(outcomeCode: reasonCode, seedRetryPolicy: false);
        await SeedCapabilityPolicyAsync(reasonCode, RetryActionKind);
        await SeedCapabilityPolicyAsync(reasonCode, "policy-override");
        var discovery = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await discovery.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        var operationId = Guid.NewGuid();
        var factory = new ActionLedgerPrimaryKeyRaceContextFactory(
            _fixture.ConnectionString,
            action,
            RetryActionKind,
            winnerUsesRequestedOperation ? operationId : null);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);

        var rejection = await Assert.ThrowsAsync<OoxmlForceRequestRejectedException>(async () =>
            await store.RequestPolicyOverrideAsync(
                new OoxmlForceRequestCommand(action.ActionId, operationId, action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None));

        Assert.Equal(expectedReasonCode, rejection.ReasonCode);
        Assert.Equal(2, factory.CreatedContextCount);
        await using var verification = CreateContext();
        Assert.Single(await verification.OperatorActionActionLedger.Where(value => value.ActionId == action.ActionId).ToListAsync());
        Assert.Equal(winnerUsesRequestedOperation,
            await verification.OperatorActionOperationLedger.AnyAsync(value => value.OperationId == operationId));
    }

    [NativeSqlServerFact]
    public async Task Forced_transient_retry_finalises_only_its_generation_and_a_later_normal_claim_is_not_force_bound()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var forceClaim = Assert.Single(await store.ClaimForceAsync("transient-force-owner", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);

        Assert.True(await store.RetryAsync(forceClaim, "processor-cancelled", CancellationToken.None));
        var normalClaim = Assert.Single(await store.ClaimAsync("normal-retry-owner", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);
        await using var verification = CreateContext();
        var request = await verification.SourceProcessorForceRequests.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Transient, request.State);
        Assert.Equal("force-request-transient", request.TerminalReasonCode);
        Assert.Equal(forceClaim.LeaseGeneration, request.ForceAttemptLeaseGeneration);
        Assert.Equal(forceClaim.LeaseGeneration + 1, normalClaim.LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Claimed_force_work_is_cancelled_on_descriptor_disable_and_late_terminal_writes_are_fenced_out()
    {
        var seeded = await SeedCurrentBlockedOoxmlAsync();
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None), value => value.BranchId == seeded.BranchId);
        await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None);
        var claim = Assert.Single(await store.ClaimForceAsync("disabled-force-owner", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None),
            value => value.BranchId == seeded.BranchId);

        Assert.True(await store.ReconcileForceRequestsAsync(ooxmlDescriptorEnabled: false, CancellationToken.None) >= 1);
        Assert.False(await store.FailAsync(claim, new RetainedProcessorFailure("office-document-encrypted", []), CancellationToken.None));
        await using var verification = CreateContext();
        var request = await verification.SourceProcessorForceRequests.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
        Assert.Equal((byte)OoxmlForceRequestState.Cancelled, request.State);
        Assert.Equal("force-request-descriptor-disabled", request.TerminalReasonCode);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
        Assert.Equal("force-request-descriptor-disabled", (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId && value.LeaseGeneration == claim.LeaseGeneration)).OutcomeCode);
    }

    [NativeSqlServerFact]
    public async Task Additive_migration_creates_fenced_force_request_schema_without_historical_backfill()
    {
        await using var database = await _fixture.CreateRetainedProcessorForceRequestPreviousMigrationDatabaseAsync();
        await using (var prior = new SqlConnection(database.ConnectionString))
        {
            await prior.OpenAsync();
            await using var missing = new SqlCommand("SELECT OBJECT_ID(N'[SourceProcessorForceRequests]')", prior);
            Assert.True(await missing.ExecuteScalarAsync() is null or DBNull);
        }
        await using (var context = database.CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync();
            Assert.Empty(await context.SourceProcessorForceRequests.ToListAsync());
        }
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[SourceProcessorForceRequests]') AND [name] IN
                    (N'ActionId', N'OperationId', N'RequestFingerprint', N'OriginalBlockedRowVersion', N'ForceAttemptBranchId', N'ForceAttemptLeaseGeneration', N'TerminalReceiptFingerprint', N'RowVersion')),
                (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'[SourceProcessorForceRequests]') AND is_disabled = 0 AND is_not_trusted = 0),
                (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'[SourceProcessorForceRequests]') AND is_unique = 1 AND [name] IN
                    (N'IX_SourceProcessorForceRequests_ActionId', N'IX_SourceProcessorForceRequests_OperationId', N'IX_SourceProcessorForceRequests_SourceProcessorBranchId_DescriptorId_DescriptorFingerprint_OriginalBlockedRowVersion')),
                (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'[SourceProcessorForceRequests]') AND [name] IN
                    (N'CK_SourceProcessorForceRequests_StateShape', N'CK_SourceProcessorForceRequests_AttemptBinding', N'CK_SourceProcessorForceRequests_OriginalOutcome')),
                (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[SourceProcessorForceRequests]')
                    AND [name] IN (N'ActionId', N'RequestFingerprint', N'ExpectedInputSha256') AND collation_name = N'Latin1_General_100_BIN2'),
                (SELECT TOP (1) definition FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'[SourceProcessorForceRequests]')
                    AND [name] = N'CK_SourceProcessorForceRequests_Timestamps');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(8, reader.GetInt32(0));
        Assert.Equal(5, reader.GetInt32(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt32(3));
        Assert.Equal(3, reader.GetInt32(4));
        Assert.Contains("[TerminalAtUtc]>=[ClaimedAtUtc]", reader.GetString(5), StringComparison.Ordinal);
        await reader.DisposeAsync();

        await using var foreignKeys = new SqlCommand(
            """
            SELECT [name], [delete_referential_action]
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'[SourceProcessorForceRequests]')
              AND is_disabled = 0
              AND is_not_trusted = 0
            ORDER BY [name];
            """, connection);
        await using var foreignKeyReader = await foreignKeys.ExecuteReaderAsync();
        var restrictiveForeignKeys = new List<(string Name, int DeleteReferentialAction)>();
        while (await foreignKeyReader.ReadAsync())
        {
            restrictiveForeignKeys.Add((foreignKeyReader.GetString(0), Convert.ToInt32(foreignKeyReader.GetByte(1))));
        }

        Assert.Equal(5, restrictiveForeignKeys.Count);
        Assert.All(restrictiveForeignKeys, foreignKey => Assert.Equal(0, foreignKey.DeleteReferentialAction));
    }

    public static IEnumerable<object[]> HardDenialReasonCodes() =>
        OperatorActionHardDenialReasons.All.Select(reasonCode => new object[] { reasonCode });

    private async Task SeedCapabilityPolicyAsync(string reasonCode, string actionKind)
    {
        await using var context = CreateContext();
        context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
        {
            PolicyId = Guid.NewGuid(),
            PolicyRevision = 1,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
            DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            SafetyContractId = "retained-binding",
            HandlerId = "retained-processor-branch-store",
            ActionKind = actionKind,
            ReasonCode = reasonCode
        });
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedLegacyHistoryForceRequestAsync(Guid branchId)
    {
        var legacyPolicyId = Guid.NewGuid();
        await using var context = CreateContext();
        var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == branchId);
        var attempt = await context.SourceProcessorAttempts.SingleAsync(value =>
            value.BranchId == branch.Id && value.LeaseGeneration == branch.LeaseGeneration);
        var actionId = OoxmlForceRequestIdentity.CreateActionId(
            branch.Id,
            OoxmlStructuralTextProcessor.Capability.Id,
            OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            branch.RowVersion);
        var requestFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(
            actionId,
            OoxmlForceRequestIdentity.EncodeBlockedRowVersion(branch.RowVersion));
        var operationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
        {
            PolicyId = legacyPolicyId, PolicyRevision = 1,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
            DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            SafetyContractId = LegacyHistorySafetyContract,
            HandlerId = "retained-processor-branch-store",
            ActionKind = RetryActionKind,
            ReasonCode = LegacyHistoryReasonCode
        });
        context.SourceProcessorForceRequests.Add(new SourceProcessorForceRequestEntity
        {
            Id = requestId,
            ActionId = actionId,
            OperationId = operationId,
            RequestFingerprint = requestFingerprint,
            PolicyId = legacyPolicyId,
            PolicyRevision = 1,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
            DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            SafetyContractId = LegacyHistorySafetyContract,
            HandlerId = "retained-processor-branch-store",
            ActionKind = RetryActionKind,
            PolicyReasonCode = LegacyHistoryReasonCode,
            SourceActivityId = branch.SourceActivityId,
            SourceProcessorBranchId = branch.Id,
            SourceRevisionId = branch.SourceRevisionId,
            ExpectedInputSha256 = branch.InputSha256,
            OriginalBlockedLeaseGeneration = branch.LeaseGeneration,
            OriginalBlockedRowVersion = branch.RowVersion,
            OriginalOutcomeCode = attempt.OutcomeCode!,
            State = (byte)OoxmlForceRequestState.Requested,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            ClaimExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
        {
            ActionId = actionId,
            PolicyId = legacyPolicyId,
            PolicyRevision = 1,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
            DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            SafetyContractId = LegacyHistorySafetyContract,
            HandlerId = "retained-processor-branch-store",
            ActionKind = RetryActionKind,
            ReasonCode = LegacyHistoryReasonCode,
            SourceProcessorBranchId = branch.Id,
            BlockedRowVersion = branch.RowVersion,
            SourceProcessorForceRequestId = requestId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        context.OperatorActionOperationLedger.Add(new OperatorActionOperationLedgerEntity
        {
            OperationId = operationId,
            RequestFingerprint = requestFingerprint,
            ActionId = actionId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        branch.State = (int)RetainedProcessorBranchState.Pending;
        branch.LeaseOwner = null;
        branch.LeaseExpiresAtUtc = null;
        branch.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
        return requestId;
    }

    private async Task<(Guid BranchId, Guid RevisionId)> SeedCurrentBlockedOoxmlAsync(
        string? artifactContentSha256 = null,
        string outcomeCode = RetryableReasonCode,
        bool seedRetryPolicy = true,
        DateTimeOffset? blockedAtUtc = null)
    {
        var now = blockedAtUtc ?? DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var predecessorId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = $"C:\\force-foundation\\{rootId:N}", DisplayName = "Force foundation", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 128L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"force-foundation:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = "C:\\private-source-sentinel.docx", Classification = "OoxmlDocumentContainer", Extension = ".docx",
            ByteLength = 1, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = artifactContentSha256 ?? hash, StoreRelativePath = "sha256\\aa\\opaque.bin", ByteLength = 1,
            ChecksumVerifiedAtUtc = now, ReferenceCount = 1
        });
        context.SourceActivities.AddRange(
            new SourceActivityEntity
            {
                Id = predecessorId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash,
                RequiredCapability = "local-source-capability", State = (int)SourceActivityState.CancelledSuperseded, Reason = "superseded",
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new SourceActivityEntity
            {
                Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
                InputFingerprint = hash, State = (int)SourceActivityState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now
            });
        context.SourceActivityRelations.Add(new SourceActivityRelationEntity
        {
            Id = Guid.NewGuid(), PredecessorActivityId = predecessorId, SuccessorActivityId = activityId,
            RelationshipKind = "superseded-by-retained-processor", ReasonCode = "superseded", CreatedAtUtc = now
        });
        if (!await context.SourceCapabilities.AnyAsync(value => value.Id == OoxmlStructuralTextProcessor.Capability.Id))
        {
            context.SourceCapabilities.Add(new SourceCapabilityEntity
            {
                Id = OoxmlStructuralTextProcessor.Capability.Id, ProcessorKind = OoxmlStructuralTextProcessor.Capability.ProcessorKind,
                ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion, ExecutionClass = (int)ExecutionClass.InProcess,
                AcceptedClassificationsJson = "[\"OoxmlDocumentContainer\"]", OutputContract = OoxmlStructuralTextProcessor.Capability.OutputContract,
                ProcessorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, IsRunnable = true, RegisteredBy = "test", RegisteredAtUtc = now
            });
        }
        if (seedRetryPolicy && !OperatorActionHardDenialReasons.All.Contains(outcomeCode, StringComparer.Ordinal) &&
            !await context.OperatorActionCapabilityPolicies.AnyAsync(value =>
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.DescriptorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                value.SafetyContractId == "retained-binding" &&
                value.HandlerId == "retained-processor-branch-store" &&
                value.ActionKind == RetryActionKind &&
                value.ReasonCode == outcomeCode))
        {
            context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
            {
                PolicyId = Guid.NewGuid(), PolicyRevision = 1,
                DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
                DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
                DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
                SafetyContractId = "retained-binding", HandlerId = "retained-processor-branch-store",
                ActionKind = RetryActionKind, ReasonCode = outcomeCode
            });
        }
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId, InputSha256 = hash,
            ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            ProcessorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Blocked, LeaseGeneration = 1, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
        {
            Id = Guid.NewGuid(), BranchId = branchId, LeaseGeneration = 1, StartedAtUtc = now.AddMinutes(-1), FinishedAtUtc = now,
            OutcomeCode = outcomeCode
        });
        await context.SaveChangesAsync();
        return (branchId, revisionId);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ContextFactory(string connectionString, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = CreateOptions(connectionString, interceptors);
        private int _createdContextCount;

        public int CreatedContextCount => Volatile.Read(ref _createdContextCount);

        private static DbContextOptions<FluxKnowledgeDbContext> CreateOptions(string connectionString, IReadOnlyCollection<IInterceptor> interceptors)
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString);
            if (interceptors.Count > 0)
            {
                builder.AddInterceptors(interceptors);
            }

            return builder.Options;
        }

        public FluxKnowledgeDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _createdContextCount);
            return new(_options);
        }

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ArbitraryActionSaveFailureInterceptor(
        string connectionString,
        OoxmlForceActionSummary action) : DbCommandInterceptor
    {
        private int _winnerCreated;

        public DbUpdateException Exception { get; } = new("arbitrary-action-persistence-failure");

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[OperatorActionActionLedger] WITH (READUNCOMMITTED)", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _winnerCreated, 1) == 0)
            {
                await CreateMatchingWinnerAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _winnerCreated) == 1 &&
                command.CommandText.Contains("INSERT INTO [SourceProcessorForceRequests]", StringComparison.Ordinal))
            {
                throw Exception;
            }

            if (command.CommandText.Contains("[OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("WITH (UPDLOCK, HOLDLOCK)", "WITH (READUNCOMMITTED)", StringComparison.Ordinal);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task CreateMatchingWinnerAsync(CancellationToken cancellationToken)
        {
            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
            await using var context = new FluxKnowledgeDbContext(options);
            var branch = await context.SourceProcessorBranches.AsNoTracking()
                .SingleAsync(value => value.Id == action.BranchId, cancellationToken);
            var policy = await context.OperatorActionCapabilityPolicies.AsNoTracking().SingleAsync(value =>
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.DescriptorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                value.SafetyContractId == "retained-binding" &&
                value.HandlerId == "retained-processor-branch-store" &&
                value.ActionKind == RetryActionKind &&
                value.ReasonCode == RetryableReasonCode,
                cancellationToken);
            var rowVersion = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken);
            var now = DateTimeOffset.UtcNow;
            var requestId = Guid.NewGuid();
            context.SourceProcessorForceRequests.Add(new SourceProcessorForceRequestEntity
            {
                Id = requestId, ActionId = action.ActionId, OperationId = Guid.NewGuid(), RequestFingerprint = action.RequestFingerprint,
                PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, PolicyReasonCode = policy.ReasonCode,
                SourceActivityId = branch.SourceActivityId, SourceProcessorBranchId = branch.Id, SourceRevisionId = branch.SourceRevisionId,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, ExpectedInputSha256 = branch.InputSha256,
                OriginalBlockedLeaseGeneration = branch.LeaseGeneration, OriginalBlockedRowVersion = rowVersion, OriginalOutcomeCode = RetryableReasonCode,
                State = (byte)OoxmlForceRequestState.Requested, RequestedAtUtc = now, ClaimExpiresAtUtc = now.AddMinutes(5)
            });
            context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
            {
                ActionId = action.ActionId, PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, ReasonCode = policy.ReasonCode,
                SourceProcessorBranchId = branch.Id, BlockedRowVersion = rowVersion, SourceProcessorForceRequestId = requestId, CreatedAtUtc = now
            });
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ActionLedgerSourceForceRequestUniqueViolationInterceptor(
        string connectionString,
        OoxmlForceActionSummary action,
        int errorNumber) : DbCommandInterceptor
    {
        private int _winnerCreated;

        public DbUpdateException Exception { get; } = CreateActionLedgerSourceForceRequestUniqueViolation(errorNumber);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[OperatorActionActionLedger] WITH (READUNCOMMITTED)", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _winnerCreated, 1) == 0)
            {
                await CreateMatchingWinnerAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _winnerCreated) == 1 &&
                command.CommandText.Contains("INSERT INTO [SourceProcessorForceRequests]", StringComparison.Ordinal))
            {
                throw Exception;
            }

            if (command.CommandText.Contains("[OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                command.CommandText = command.CommandText.Replace("WITH (UPDLOCK, HOLDLOCK)", "WITH (READUNCOMMITTED)", StringComparison.Ordinal);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task CreateMatchingWinnerAsync(CancellationToken cancellationToken)
        {
            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
            await using var context = new FluxKnowledgeDbContext(options);
            var branch = await context.SourceProcessorBranches.AsNoTracking()
                .SingleAsync(value => value.Id == action.BranchId, cancellationToken);
            var policy = await context.OperatorActionCapabilityPolicies.AsNoTracking().SingleAsync(value =>
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.DescriptorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                value.SafetyContractId == "retained-binding" &&
                value.HandlerId == "retained-processor-branch-store" &&
                value.ActionKind == RetryActionKind &&
                value.ReasonCode == RetryableReasonCode,
                cancellationToken);
            var rowVersion = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken);
            var now = DateTimeOffset.UtcNow;
            var requestId = Guid.NewGuid();
            context.SourceProcessorForceRequests.Add(new SourceProcessorForceRequestEntity
            {
                Id = requestId, ActionId = action.ActionId, OperationId = Guid.NewGuid(), RequestFingerprint = action.RequestFingerprint,
                PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, PolicyReasonCode = policy.ReasonCode,
                SourceActivityId = branch.SourceActivityId, SourceProcessorBranchId = branch.Id, SourceRevisionId = branch.SourceRevisionId,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, ExpectedInputSha256 = branch.InputSha256,
                OriginalBlockedLeaseGeneration = branch.LeaseGeneration, OriginalBlockedRowVersion = rowVersion, OriginalOutcomeCode = RetryableReasonCode,
                State = (byte)OoxmlForceRequestState.Requested, RequestedAtUtc = now, ClaimExpiresAtUtc = now.AddMinutes(5)
            });
            context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
            {
                ActionId = action.ActionId, PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, ReasonCode = policy.ReasonCode,
                SourceProcessorBranchId = branch.Id, BlockedRowVersion = rowVersion, SourceProcessorForceRequestId = requestId, CreatedAtUtc = now
            });
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ActionLedgerPrimaryKeyRaceContextFactory(
        string connectionString,
        OoxmlForceActionSummary action,
        string winnerActionKind,
        Guid? winnerOperationId) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(new ActionLedgerPrimaryKeySaveFailureInterceptor())
            .Options;
        private int _createdContextCount;

        public int CreatedContextCount => Volatile.Read(ref _createdContextCount);

        public FluxKnowledgeDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _createdContextCount);
            return new FluxKnowledgeDbContext(_options);
        }

        public async Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _createdContextCount) == 2)
            {
                await CreateWinnerAsync(cancellationToken);
            }

            return new FluxKnowledgeDbContext(_options);
        }

        private async Task CreateWinnerAsync(CancellationToken cancellationToken)
        {
            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
            await using var context = new FluxKnowledgeDbContext(options);
            var branch = await context.SourceProcessorBranches.AsNoTracking()
                .SingleAsync(value => value.Id == action.BranchId, cancellationToken);
            var policy = await context.OperatorActionCapabilityPolicies.AsNoTracking().SingleAsync(value =>
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.DescriptorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                value.SafetyContractId == "retained-binding" &&
                value.HandlerId == "retained-processor-branch-store" &&
                value.ActionKind == winnerActionKind &&
                value.ReasonCode == action.OutcomeCode,
                cancellationToken);
            var rowVersion = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(action.BlockedRowVersionToken);
            var now = DateTimeOffset.UtcNow;
            var requestId = Guid.NewGuid();
            context.SourceProcessorForceRequests.Add(new SourceProcessorForceRequestEntity
            {
                Id = requestId, ActionId = action.ActionId, OperationId = Guid.NewGuid(), RequestFingerprint = action.RequestFingerprint,
                PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, PolicyReasonCode = policy.ReasonCode,
                SourceActivityId = branch.SourceActivityId, SourceProcessorBranchId = branch.Id, SourceRevisionId = branch.SourceRevisionId,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, ExpectedInputSha256 = branch.InputSha256,
                OriginalBlockedLeaseGeneration = branch.LeaseGeneration, OriginalBlockedRowVersion = rowVersion, OriginalOutcomeCode = action.OutcomeCode,
                State = (byte)OoxmlForceRequestState.Requested, RequestedAtUtc = now, ClaimExpiresAtUtc = now.AddMinutes(5)
            });
            context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
            {
                ActionId = action.ActionId, PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, ReasonCode = policy.ReasonCode,
                SourceProcessorBranchId = branch.Id, BlockedRowVersion = rowVersion, SourceProcessorForceRequestId = requestId, CreatedAtUtc = now
            });
            if (winnerOperationId is { } operationId)
            {
                context.OperatorActionOperationLedger.Add(new OperatorActionOperationLedgerEntity
                {
                    OperationId = operationId, RequestFingerprint = action.RequestFingerprint, ActionId = action.ActionId, CreatedAtUtc = now
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ActionLedgerPrimaryKeySaveFailureInterceptor : SaveChangesInterceptor
    {
        public DbUpdateException Exception { get; } = CreateActionLedgerPrimaryKeyViolation();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(Exception);
    }

    private static DbUpdateException CreateActionLedgerSourceForceRequestUniqueViolation(int errorNumber)
    {
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [errorNumber, (byte)0, (byte)14, "server",
                "Cannot insert duplicate key row in object 'dbo.OperatorActionActionLedger' with unique index 'IX_OperatorActionActionLedger_SourceProcessorForceRequestId'.",
                string.Empty, 1, 0, null],
            culture: null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: null, culture: null)!;
        typeof(SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(errors, [error]);
        var sqlException = (SqlException)typeof(SqlException).GetMethods(System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "server"])!;
        using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_Phase1Tests_ActionLedgerUniqueViolation;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);
        return new DbUpdateException("simulated action-ledger source force request unique violation", sqlException,
            [context.Entry(new OperatorActionActionLedgerEntity())]);
    }

    private static DbUpdateException CreateActionLedgerPrimaryKeyViolation()
    {
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [2627, (byte)0, (byte)14, "server",
                "Violation of PRIMARY KEY constraint 'PK_OperatorActionActionLedger'. Cannot insert duplicate key in object 'dbo.OperatorActionActionLedger'.",
                string.Empty, 1, 0, null],
            culture: null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: null, culture: null)!;
        typeof(SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(errors, [error]);
        var sqlException = (SqlException)typeof(SqlException).GetMethods(System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "server"])!;
        using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_Phase1Tests_ActionLedgerPrimaryKeyRace;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);
        return new DbUpdateException("simulated action-ledger primary key race", sqlException,
            [context.Entry(new OperatorActionActionLedgerEntity())]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
