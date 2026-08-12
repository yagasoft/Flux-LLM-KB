using System.Globalization;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Reads a sanitised operator projection and delegates all durable transitions to the retained-branch store.</summary>
public sealed class SqlOperatorActionStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    SqlRetainedProcessorBranchStore branchStore) : IOperatorActionStore
{
    private const string CapabilityName = "document-ooxml-structural-extract";

    public async ValueTask<IReadOnlyList<OperatorActionProjection>> ListAsync(
        bool includeIgnored,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var maximum = Math.Clamp(maximumCount, 1, 100);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        var candidateBranchIds = current.Select(value => value.BranchId).ToArray();
        var retryAvailable = await branchStore.ListCurrentForceEligibleActionIdsAsync(
            "retry", candidateBranchIds, cancellationToken).ConfigureAwait(false);
        var overrideAvailable = await branchStore.ListCurrentForceEligibleActionIdsAsync(
            "policy-override", candidateBranchIds, cancellationToken).ConfigureAwait(false);
        current = current.Select(value => value with
        {
            RetryAvailable = retryAvailable.Contains(value.ActionId),
            OverrideAvailable = overrideAvailable.Contains(value.ActionId)
        }).ToList();
        var ledgers = await context.OperatorActionActionLedger.AsNoTracking()
            .OrderByDescending(value => value.CreatedAtUtc)
            .Take(maximum * 2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actionIds = current.Select(value => value.ActionId)
            .Concat(ledgers.Select(value => value.ActionId)).Distinct(StringComparer.Ordinal).ToArray();
        var requests = await context.SourceProcessorForceRequests.AsNoTracking()
            .Where(value => actionIds.Contains(value.ActionId))
            .ToDictionaryAsync(value => value.ActionId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var ignores = await context.SourceProcessorActionIgnoreHeads.AsNoTracking()
            .Where(value => actionIds.Contains(value.ActionId))
            .ToDictionaryAsync(value => value.ActionId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var branchIds = ledgers.Select(value => value.SourceProcessorBranchId).Distinct().ToArray();
        var attempts = await context.SourceProcessorAttempts.AsNoTracking()
            .Where(value => branchIds.Contains(value.BranchId) && value.FinishedAtUtc != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, OperatorActionProjection>(StringComparer.Ordinal);
        foreach (var ledger in ledgers)
        {
            requests.TryGetValue(ledger.ActionId, out var request);
            ignores.TryGetValue(ledger.ActionId, out var ignore);
            var blockedAt = attempts
                .Where(value => value.BranchId == ledger.SourceProcessorBranchId &&
                    (request is null
                        ? value.FinishedAtUtc <= ledger.CreatedAtUtc
                        : value.LeaseGeneration == request.OriginalBlockedLeaseGeneration))
                .OrderByDescending(value => value.FinishedAtUtc)
                .Select(value => value.FinishedAtUtc!.Value)
                .FirstOrDefault();
            if (blockedAt == default) blockedAt = ledger.CreatedAtUtc;
            var rowToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(ledger.BlockedRowVersion);
            var state = request is null
                ? ignore?.IsIgnored == true ? "ignored" : "historical"
                : ((OoxmlForceRequestState)request.State).ToString().ToLowerInvariant();
            result[ledger.ActionId] = new OperatorActionProjection(
                ledger.ActionId,
                request?.RequestFingerprint ?? OoxmlForceRequestIdentity.CreateRequestFingerprint(ledger.ActionId, rowToken),
                request?.Id.ToString("N", CultureInfo.InvariantCulture),
                rowToken,
                CapabilityName,
                state,
                request?.OriginalOutcomeCode ?? ledger.ReasonCode,
                request?.ActionKind ?? ledger.ActionKind,
                blockedAt,
                request?.RequestedAtUtc,
                request?.ClaimedAtUtc,
                request?.TerminalAtUtc,
                OverrideAvailable: false,
                RetryAvailable: false,
                Ignored: ignore?.IsIgnored == true);
        }

        foreach (var candidate in current)
        {
            ignores.TryGetValue(candidate.ActionId, out var ignore);
            if (result.TryGetValue(candidate.ActionId, out var existing))
            {
                result[candidate.ActionId] = existing with
                {
                    ActionState = candidate.DescriptorRunnable ? "blocked" : "descriptor-disabled",
                    ReasonCode = candidate.ReasonCode,
                    BlockedAtUtc = candidate.BlockedAtUtc,
                    OverrideAvailable = candidate.DescriptorRunnable && candidate.OverrideAvailable && requests.GetValueOrDefault(candidate.ActionId) is null,
                    RetryAvailable = candidate.DescriptorRunnable && candidate.RetryAvailable && requests.GetValueOrDefault(candidate.ActionId) is null,
                    Ignored = ignore?.IsIgnored == true
                };
                continue;
            }

            result[candidate.ActionId] = new OperatorActionProjection(
                candidate.ActionId,
                candidate.RequestFingerprint,
                null,
                candidate.RowVersionToken,
                CapabilityName,
                candidate.DescriptorRunnable ? "blocked" : "descriptor-disabled",
                candidate.ReasonCode,
                null,
                candidate.BlockedAtUtc,
                null,
                null,
                null,
                candidate.DescriptorRunnable && candidate.OverrideAvailable,
                candidate.DescriptorRunnable && candidate.RetryAvailable,
                ignore?.IsIgnored == true);
        }

        return result.Values
            .Where(value => includeIgnored || !value.Ignored)
            .OrderByDescending(value => value.BlockedAtUtc)
            .ThenBy(value => value.ActionId, StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
    }

    public async ValueTask<OperatorActionMutationReceipt> ExecuteAsync(
        OperatorActionMutationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OperationId == Guid.Empty) throw new ArgumentException("An operation identity is required.", nameof(command));
        if (command.ActionKind is not ("retry" or "policy-override" or "ignore" or "unignore"))
            throw new ArgumentException("The action kind is not supported.", nameof(command));

        try
        {
            string? requestId = null;
            string state;
            long? sequence = null;
            bool? ignored = null;
            bool wasReplay;
            DateTimeOffset committedAtUtc;
            if (command.ActionKind is "ignore" or "unignore")
            {
                byte[] blockedRowVersion;
                try
                {
                    blockedRowVersion = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(command.ExpectedBlockedRowVersion);
                }
                catch (ArgumentException)
                {
                    blockedRowVersion = [];
                }
                var receipt = await branchStore.SetOperatorActionIgnoreAsync(new OperatorActionIgnoreCommand(
                    command.ActionId, command.OperationId, command.RequestFingerprint,
                    blockedRowVersion,
                    command.ActionKind == "ignore"), cancellationToken).ConfigureAwait(false);
                state = receipt.IsIgnored ? "ignored" : "unignored";
                sequence = receipt.Sequence;
                ignored = receipt.IsIgnored;
                wasReplay = receipt.WasReplay;
                committedAtUtc = receipt.CommittedAtUtc;
            }
            else
            {
                var durableCommand = new OoxmlForceRequestCommand(
                    command.ActionId, command.OperationId, command.RequestFingerprint, command.ExpectedBlockedRowVersion);
                var receipt = command.ActionKind == "retry"
                    ? await branchStore.RequestForceAsync(durableCommand, cancellationToken).ConfigureAwait(false)
                    : await branchStore.RequestPolicyOverrideAsync(durableCommand, cancellationToken).ConfigureAwait(false);
                requestId = receipt.RequestId.ToString("N", CultureInfo.InvariantCulture);
                state = receipt.State.ToString().ToLowerInvariant();
                wasReplay = receipt.WasReplay;
                committedAtUtc = receipt.CommittedAtUtc;
            }

            return new OperatorActionMutationReceipt(
                command.ActionId, command.OperationId, requestId, state, sequence, ignored,
                wasReplay, committedAtUtc);
        }
        catch (OoxmlForceRequestRejectedException exception)
        {
            throw new OperatorActionRequestRejectedException(MapReason(exception.ReasonCode));
        }
        catch (OperatorActionRejectedException exception)
        {
            throw new OperatorActionRequestRejectedException(MapReason(exception.ReasonCode));
        }
    }

    private static string MapReason(string reasonCode) => reasonCode switch
    {
        "operator-action-not-forceable" => "operator-action-not-eligible",
        "operator-action-kind-conflict" => "operator-action-not-eligible",
        _ => reasonCode
    };

    private static async ValueTask<List<CurrentAction>> ReadCurrentAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken)
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;
        var candidates = await (
            from branch in context.SourceProcessorBranches.AsNoTracking()
            join activity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals activity.Id
            join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
            join attempt in context.SourceProcessorAttempts.AsNoTracking() on new { BranchId = branch.Id, branch.LeaseGeneration }
                equals new { attempt.BranchId, attempt.LeaseGeneration }
            join registeredCapability in context.SourceCapabilities.AsNoTracking() on descriptor.Id equals registeredCapability.Id into capabilities
            from capability in capabilities.DefaultIfEmpty()
            where branch.State == (int)RetainedProcessorBranchState.Blocked &&
                  activity.SourceRevisionId == branch.SourceRevisionId &&
                  activity.State == (int)SourceActivityState.Pending &&
                  activity.ActivityKind == (int)SourceActivityKind.TextExtraction &&
                  activity.ExecutionClass == (int)ExecutionClass.InProcess &&
                  activity.ProcessorVersion == descriptor.ProcessorVersion &&
                  branch.ProcessorVersion == descriptor.ProcessorVersion &&
                  branch.ProcessorFingerprint == descriptor.ProcessorFingerprint &&
                  revision.Classification == descriptor.AcceptedClassification &&
                  new[] { ".docx", ".xlsx", ".pptx" }.Contains(revision.Extension.ToLower()) &&
                  attempt.FinishedAtUtc != null &&
                  context.SourceActivityRelations.Any(relation => relation.SuccessorActivityId == activity.Id)
            orderby branch.UpdatedAtUtc descending, branch.Id
            select new CurrentCandidate(
                branch.Id, branch.RowVersion, attempt.OutcomeCode ?? string.Empty,
                attempt.FinishedAtUtc!.Value,
                capability != null &&
                capability.ProcessorKind == descriptor.ProcessorKind &&
                capability.ProcessorVersion == descriptor.ProcessorVersion &&
                capability.ExecutionClass == (int)ExecutionClass.InProcess &&
                capability.ProcessorFingerprint == descriptor.ProcessorFingerprint &&
                capability.OutputContract == descriptor.OutputContract &&
                capability.AcceptedClassificationsJson == "[\"OoxmlDocumentContainer\"]" &&
                capability.IsRunnable))
            .Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);

        return candidates.Select(value =>
        {
            var actionId = OoxmlForceRequestIdentity.CreateActionId(
                value.BranchId, descriptor.Id, descriptor.ProcessorFingerprint, value.RowVersion);
            var token = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(value.RowVersion);
            return new CurrentAction(value.BranchId, actionId,
                OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, token), token,
                value.ReasonCode, value.BlockedAtUtc, value.DescriptorRunnable,
                OverrideAvailable: false, RetryAvailable: false);
        }).ToList();
    }

    private sealed record CurrentCandidate(Guid BranchId, byte[] RowVersion, string ReasonCode,
        DateTimeOffset BlockedAtUtc, bool DescriptorRunnable);
    private sealed record CurrentAction(Guid BranchId, string ActionId, string RequestFingerprint, string RowVersionToken,
        string ReasonCode, DateTimeOffset BlockedAtUtc, bool DescriptorRunnable, bool OverrideAvailable, bool RetryAvailable);
}
