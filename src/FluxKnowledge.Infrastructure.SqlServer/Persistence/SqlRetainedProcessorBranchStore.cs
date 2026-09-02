using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Durable branch claims and fenced completion writes for retained processors.</summary>
public sealed class SqlRetainedProcessorBranchStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider)
    : IRetainedProcessorBranchStore
{
    private const string RetainedCsharpSchemaMigration = "20260820101021_CloseRetainedCsharpMixedOutcomes";
    private const string RetainedBindingSafetyContract = "retained-binding";
    private const string RetainedBranchStoreHandler = "retained-processor-branch-store";
    private const string IgnorePolicyReason = "operator-action-ignore";
    private const string OperatorActionEvent = "operator_action.retained_processor";
    private const string OperatorActionActor = "anonymous-direct-loopback";

    public async ValueTask<bool> IsRetainedCsharpCodeWriterReadyAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        try
        {
            if (close)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
                BEGIN
                    SELECT CONVERT(int, 0);
                    RETURN;
                END;

                SELECT CONVERT(int, CASE WHEN
                    EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId)
                    AND COL_LENGTH(N'dbo.SourceActivities', N'DescriptorFingerprint') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeDocuments', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeSymbols', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeReferences', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeDiagnostics', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeCompletionReceipts', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.SourceProcessorCodeBlockedDiagnostics', N'U') IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM (VALUES
                            (N'TR_SourceProcessorCodeDocuments_Immutable', N'[dbo].[SourceProcessorCodeDocuments]', 0xFAAE7073D0D92C89C9A5C18C665CFC6BE8610F54FD9571412B891B07CF332E89),
                            (N'TR_SourceProcessorCodeDocuments_InsertFence', N'[dbo].[SourceProcessorCodeDocuments]', 0x039A2B48703ACE38E66FA3D54AC823E1E8828DFFFFF44A9F583D426BD563B783),
                            (N'TR_SourceProcessorCodeSymbols_Immutable', N'[dbo].[SourceProcessorCodeSymbols]', 0xFA4DD0857588D50FF86BFB042437EDC4C44FDBC4ADE777EC00355D508DEF95DE),
                            (N'TR_SourceProcessorCodeSymbols_InsertFence', N'[dbo].[SourceProcessorCodeSymbols]', 0x6B3A69634DDD508654ECE0509CE3D7329B9F0612B6CD80B53BFAD5BCFBBBFAB5),
                            (N'TR_SourceProcessorCodeReferences_Immutable', N'[dbo].[SourceProcessorCodeReferences]', 0x21299AB2365ADB4C62EF57ED1E1906BED8B2AC1F16D569A2E17DFC7EED254B41),
                            (N'TR_SourceProcessorCodeReferences_InsertFence', N'[dbo].[SourceProcessorCodeReferences]', 0x84822A861517D868D0D4A0684A4963DB79AC4C6B4E7267672AD7A1B6C50B116E),
                            (N'TR_SourceProcessorCodeDiagnostics_Immutable', N'[dbo].[SourceProcessorCodeDiagnostics]', 0xC88660B9635AAB26CE57D25261EC246723DFDB5FF44E7A79588E70EAC077F7AC),
                            (N'TR_SourceProcessorCodeDiagnostics_InsertFence', N'[dbo].[SourceProcessorCodeDiagnostics]', 0x49817453CC1ADE9C2B17ED6397518FFAA85DF31EE19D7660ADAF5F0F960EEDF2),
                            (N'TR_SourceProcessorCodeCompletionReceipts_Immutable', N'[dbo].[SourceProcessorCodeCompletionReceipts]', 0x33706FA83F97B6164227C28E8442092F0BBA3FBD41F3518E904726B181F3DAAF),
                            (N'TR_SourceProcessorCodeCompletionReceipts_OutcomeFence', N'[dbo].[SourceProcessorCodeCompletionReceipts]', 0xF804A3BEE2176044E91A83CFEDDC6FA913069D7E7551339809E8C40EC435E9F1),
                            (N'TR_SourceProcessorCodeCompletionReceipts_Closure', N'[dbo].[SourceProcessorCodeCompletionReceipts]', 0xB5DD4F58C1F8F582D47A1B470F34A1E37405ABAB97D905CA8416994D7DA910F6),
                            (N'TR_SourceProcessorCodeBlockedDiagnostics_Immutable', N'[dbo].[SourceProcessorCodeBlockedDiagnostics]', 0x5F7B4B1750A6F2010932603B2F2B0B73ACF87FDD564E825D3E856AD20F376729),
                            (N'TR_SourceProcessorCodeBlockedDiagnostics_InsertFence', N'[dbo].[SourceProcessorCodeBlockedDiagnostics]', 0x3568CE121C335934062F621988828F00E131985F9D0D6616DA2A73B9B3E553B6)
                        ) AS [expected]([Name], [ParentObjectName], [DefinitionHash])
                        LEFT JOIN [sys].[triggers] AS [trigger]
                            ON [trigger].[name] = [expected].[Name]
                           AND [trigger].[parent_id] = OBJECT_ID([expected].[ParentObjectName])
                        LEFT JOIN [sys].[sql_modules] AS [module]
                            ON [module].[object_id] = [trigger].[object_id]
                        WHERE [trigger].[object_id] IS NULL
                           OR [trigger].[is_disabled] <> 0
                           OR [module].[definition] IS NULL
                           OR HASHBYTES('SHA2_256', [module].[definition]) <> [expected].[DefinitionHash])
                    AND EXISTS (
                        SELECT 1 FROM sys.foreign_keys
                        WHERE [name] = N'FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_SuccessIdentity'
                          AND [delete_referential_action] = 0)
                    AND EXISTS (
                        SELECT 1 FROM sys.check_constraints
                        WHERE [name] = N'CK_SourceProcessorCodeCompletionReceipts_DocumentBranchEquality')
                    AND EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE [object_id] = OBJECT_ID(N'dbo.SourceProcessorCodeCompletionReceipts')
                          AND [name] = N'HandlerImplementationId'
                          AND [collation_name] = N'Latin1_General_100_BIN2')
                    THEN 1 ELSE 0 END);
                """;
            command.Parameters.Add(new SqlParameter("@migrationId", SqlDbType.NVarChar, 150)
            {
                Value = RetainedCsharpSchemaMigration
            });
            var ready = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return ready is int value && value == 1;
        }
        catch (SqlException exception) when (exception.Number is 208 or 207)
        {
            return false;
        }
        finally
        {
            if (close)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<OperatorActionIgnoreReceipt> SetOperatorActionIgnoreAsync(
        OperatorActionIgnoreCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OperationId == Guid.Empty) throw new ArgumentException("An operator action operation identity is required.", nameof(command));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var priorOperation = await context.OperatorActionOperationLedger
            .FromSqlInterpolated($"SELECT * FROM [OperatorActionOperationLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [OperationId] = {command.OperationId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (priorOperation is not null)
        {
            var priorAction = await context.OperatorActionActionLedger
                .FromSqlInterpolated($"SELECT * FROM [OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {priorOperation.ActionId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (IsMatchingIgnoreReplay(priorOperation, priorAction, command, out var sequence, out var isIgnored))
            {
                return new OperatorActionIgnoreReceipt(priorOperation.ActionId, command.OperationId, sequence, isIgnored)
                {
                    WasReplay = true,
                    CommittedAtUtc = priorOperation.CreatedAtUtc
                };
            }

            throw new OperatorActionRejectedException("operator-operation-conflict");
        }

        OoxmlForceRequestIdentity.RequireSha256(command.ActionId, nameof(command.ActionId));
        OoxmlForceRequestIdentity.RequireSha256(command.RequestFingerprint, nameof(command.RequestFingerprint));
        if (command.ExpectedBlockedRowVersion is not { Length: 8 })
            throw new ArgumentException("An eight-byte blocked row version is required.", nameof(command));
        var expectedFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(
            command.ActionId,
            OoxmlForceRequestIdentity.EncodeBlockedRowVersion(command.ExpectedBlockedRowVersion));
        if (!string.Equals(command.RequestFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new OperatorActionRejectedException("operator-operation-conflict");
        }

        var action = await context.OperatorActionActionLedger
            .FromSqlInterpolated($"SELECT * FROM [OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {command.ActionId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            var candidates = await ReadCurrentOoxmlBlockedIgnoreCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
            var candidate = candidates.SingleOrDefault(value =>
                string.Equals(OoxmlForceRequestIdentity.CreateActionId(value.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
                    OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, value.RowVersion), command.ActionId, StringComparison.Ordinal));
            if (candidate is null || !candidate.RowVersion.SequenceEqual(command.ExpectedBlockedRowVersion))
            {
                throw new OperatorActionRejectedException("operator-action-stale");
            }

            var branch = await context.SourceProcessorBranches
                .FromSqlInterpolated($"SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidate.BranchId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (branch is null)
            {
                throw new OperatorActionRejectedException("operator-action-stale");
            }
            var blockedAttempt = await context.SourceProcessorAttempts
                .FromSqlInterpolated($"SELECT * FROM [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK) WHERE [BranchId] = {branch.Id} AND [LeaseGeneration] = {branch.LeaseGeneration} AND [FinishedAtUtc] IS NOT NULL")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (blockedAttempt is null)
            {
                throw new OperatorActionRejectedException("operator-action-stale");
            }

            var lockedCandidates = await ReadCurrentOoxmlBlockedIgnoreCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
            candidate = lockedCandidates.SingleOrDefault(value => value.BranchId == branch.Id);
            if (candidate is null || !candidate.RowVersion.SequenceEqual(command.ExpectedBlockedRowVersion) ||
                !string.Equals(OoxmlForceRequestIdentity.CreateActionId(candidate.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
                    OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, candidate.RowVersion), command.ActionId, StringComparison.Ordinal))
            {
                throw new OperatorActionRejectedException("operator-action-stale");
            }

            var ignorePolicy = new OperatorActionCapabilityPolicyEntity
            {
                PolicyId = Guid.NewGuid(),
                PolicyRevision = 1,
                DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
                DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
                DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
                SafetyContractId = RetainedBindingSafetyContract,
                HandlerId = RetainedBranchStoreHandler,
                ActionKind = "ignore",
                ReasonCode = IgnorePolicyReason
            };
            context.OperatorActionCapabilityPolicies.Add(ignorePolicy);
            action = new OperatorActionActionLedgerEntity
            {
                ActionId = command.ActionId,
                PolicyId = ignorePolicy.PolicyId,
                PolicyRevision = ignorePolicy.PolicyRevision,
                DescriptorId = ignorePolicy.DescriptorId,
                DescriptorFingerprint = ignorePolicy.DescriptorFingerprint,
                DescriptorVersion = ignorePolicy.DescriptorVersion,
                SafetyContractId = ignorePolicy.SafetyContractId,
                HandlerId = ignorePolicy.HandlerId,
                ActionKind = ignorePolicy.ActionKind,
                ReasonCode = ignorePolicy.ReasonCode,
                SourceProcessorBranchId = candidate.BranchId,
                BlockedRowVersion = candidate.RowVersion,
                CreatedAtUtc = default
            };
            context.OperatorActionActionLedger.Add(action);
        }
        else if (!action.BlockedRowVersion.SequenceEqual(command.ExpectedBlockedRowVersion))
        {
            throw new OperatorActionRejectedException("operator-action-stale");
        }

        var head = await context.SourceProcessorActionIgnoreHeads
            .FromSqlInterpolated($"SELECT * FROM [SourceProcessorActionIgnoreHeads] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {command.ActionId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        if (action.CreatedAtUtc == default)
        {
            action.CreatedAtUtc = now;
        }
        var nextSequence = checked((head?.Sequence ?? 0) + 1);
        if (head is null)
        {
            context.SourceProcessorActionIgnoreHeads.Add(new SourceProcessorActionIgnoreHeadEntity
            {
                ActionId = command.ActionId, Sequence = nextSequence, IsIgnored = command.IsIgnored, UpdatedAtUtc = now
            });
        }
        else
        {
            head.Sequence = nextSequence;
            head.IsIgnored = command.IsIgnored;
            head.UpdatedAtUtc = now;
        }

        context.OperatorActionOperationLedger.Add(new OperatorActionOperationLedgerEntity
        {
            OperationId = command.OperationId,
            RequestFingerprint = command.RequestFingerprint,
            ActionId = command.ActionId,
            CreatedAtUtc = now,
            IgnoreSequence = nextSequence,
            IgnoreState = command.IsIgnored
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OperatorActionIgnoreReceipt(command.ActionId, command.OperationId, nextSequence, command.IsIgnored)
            {
                WasReplay = false,
                CommittedAtUtc = now
            };
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await using var replayContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winner = await replayContext.OperatorActionOperationLedger.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OperationId == command.OperationId, cancellationToken).ConfigureAwait(false);
            var winnerAction = winner is null
                ? null
                : await replayContext.OperatorActionActionLedger.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ActionId == winner.ActionId, cancellationToken).ConfigureAwait(false);
            if (winner is not null && IsMatchingIgnoreReplay(winner, winnerAction, command, out var sequence, out var isIgnored))
            {
                return new OperatorActionIgnoreReceipt(command.ActionId, command.OperationId, sequence, isIgnored)
                {
                    WasReplay = true,
                    CommittedAtUtc = winner.CreatedAtUtc
                };
            }

            if (winner is not null)
            {
                throw new OperatorActionRejectedException("operator-operation-conflict");
            }

            throw;
        }
    }

    private static bool IsMatchingIgnoreReplay(
        OperatorActionOperationLedgerEntity operation,
        OperatorActionActionLedgerEntity? action,
        OperatorActionIgnoreCommand command,
        out long sequence,
        out bool isIgnored)
    {
        sequence = default;
        isIgnored = default;
        if (!string.Equals(operation.ActionId, command.ActionId, StringComparison.Ordinal) ||
            !string.Equals(operation.RequestFingerprint, command.RequestFingerprint, StringComparison.Ordinal) ||
            action is null ||
            command.ExpectedBlockedRowVersion is null ||
            !action.BlockedRowVersion.SequenceEqual(command.ExpectedBlockedRowVersion) ||
            operation.IgnoreSequence is not long storedSequence ||
            operation.IgnoreState is not bool storedIgnoreState ||
            storedIgnoreState != command.IsIgnored)
        {
            return false;
        }

        sequence = storedSequence;
        isIgnored = storedIgnoreState;
        return true;
    }

    public async ValueTask<IReadOnlyList<OoxmlForceActionSummary>> ListForceEligibleOoxmlActionsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await ReadCurrentOoxmlBlockedCandidatesAsync(context, Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize), "retry", cancellationToken)
            .ConfigureAwait(false);
        var openRequests = await context.SourceProcessorForceRequests.AsNoTracking()
            .Where(value => value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                !context.OperatorActionHardDenials.Any(denial => denial.ReasonCode == value.OriginalOutcomeCode) &&
                (value.State == (byte)OoxmlForceRequestState.Requested || value.State == (byte)OoxmlForceRequestState.Claimed))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = candidates.Select(candidate =>
        {
            var actionId = OoxmlForceRequestIdentity.CreateActionId(candidate.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, candidate.RowVersion);
            var rowVersionToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(candidate.RowVersion);
            var request = openRequests.SingleOrDefault(value =>
                value.SourceProcessorBranchId == candidate.BranchId &&
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.OriginalBlockedRowVersion.SequenceEqual(candidate.RowVersion));
            return new OoxmlForceActionSummary(candidate.BranchId, candidate.SourceActivityId, new SourceRevisionId(candidate.SourceRevisionId), actionId,
                OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, rowVersionToken), rowVersionToken, candidate.LeaseGeneration,
                candidate.OutcomeCode, candidate.IsForceable && request is null, request is null ? null : (OoxmlForceRequestState)request.State);
        }).ToList();
        foreach (var request in openRequests.Where(value => summaries.All(summary => !string.Equals(summary.ActionId, value.ActionId, StringComparison.Ordinal))))
        {
            summaries.Add(new OoxmlForceActionSummary(request.SourceProcessorBranchId, request.SourceActivityId, new SourceRevisionId(request.SourceRevisionId),
                request.ActionId, request.RequestFingerprint, OoxmlForceRequestIdentity.EncodeBlockedRowVersion(request.OriginalBlockedRowVersion),
                request.OriginalBlockedLeaseGeneration, request.OriginalOutcomeCode, false, (OoxmlForceRequestState)request.State));
        }

        return summaries.ToArray();
    }

    internal async ValueTask<IReadOnlySet<string>> ListCurrentForceEligibleActionIdsAsync(
        string actionKind,
        IReadOnlyCollection<Guid> candidateBranchIds,
        CancellationToken cancellationToken)
    {
        if (actionKind is not ("retry" or "policy-override"))
            throw new ArgumentException("Only force action kinds may be selected.", nameof(actionKind));
        ArgumentNullException.ThrowIfNull(candidateBranchIds);
        if (candidateBranchIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await ReadCurrentOoxmlBlockedCandidatesAsync(
            context, null, actionKind, cancellationToken, candidateBranchIds).ConfigureAwait(false);
        return candidates.Select(candidate => OoxmlForceRequestIdentity.CreateActionId(
                candidate.BranchId,
                OoxmlStructuralTextProcessor.Capability.Id,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
                candidate.RowVersion))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask<OoxmlForceRequestReceipt> RequestForceAsync(
        OoxmlForceRequestCommand command,
        CancellationToken cancellationToken) =>
        await RequestActionAsync(command, "retry", cancellationToken).ConfigureAwait(false);

    /// <summary>Creates the policy-override lifecycle only when an exact durable policy authorises the current blocked version.</summary>
    public async ValueTask<OoxmlForceRequestReceipt> RequestPolicyOverrideAsync(
        OoxmlForceRequestCommand command,
        CancellationToken cancellationToken) =>
        await RequestActionAsync(command, "policy-override", cancellationToken).ConfigureAwait(false);

    private async ValueTask<OoxmlForceRequestReceipt> RequestActionAsync(
        OoxmlForceRequestCommand command,
        string actionKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var priorOperation = await context.OperatorActionOperationLedger
            .FromSqlInterpolated($"SELECT * FROM [OperatorActionOperationLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [OperationId] = {command.OperationId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (priorOperation is not null)
        {
            var priorActionForOperation = await context.OperatorActionActionLedger
                .FromSqlInterpolated($"SELECT * FROM [OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {priorOperation.ActionId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var priorRequest = priorActionForOperation is null
                ? null
                : await context.SourceProcessorForceRequests
                    .FromSqlInterpolated($"SELECT * FROM [SourceProcessorForceRequests] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {priorActionForOperation.ActionId}")
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (IsMatchingForceRequestReplay(priorOperation, priorActionForOperation, priorRequest, command, actionKind))
            {
                return ToReceipt(priorRequest!, wasReplay: true, priorOperation.CreatedAtUtc);
            }

            throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
        }

        if (command.OperationId == Guid.Empty)
            throw new ArgumentException("An operator action operation identity is required.", nameof(command));
        OoxmlForceRequestIdentity.RequireSha256(command.ActionId, nameof(command.ActionId));

        var priorAction = await context.OperatorActionActionLedger
            .FromSqlInterpolated($"SELECT * FROM [OperatorActionActionLedger] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {command.ActionId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (priorAction is not null)
        {
            var priorRequest = await context.SourceProcessorForceRequests
                .FromSqlInterpolated($"SELECT * FROM [SourceProcessorForceRequests] WITH (UPDLOCK, HOLDLOCK) WHERE [ActionId] = {priorAction.ActionId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (priorRequest is not null)
            {
                if (!IsMatchingForceRequestReplay(null, priorAction, priorRequest, command, actionKind))
                {
                    if (!string.Equals(priorAction.ActionKind, actionKind, StringComparison.Ordinal) ||
                        !string.Equals(priorRequest.ActionKind, actionKind, StringComparison.Ordinal))
                    {
                        throw new OoxmlForceRequestRejectedException("operator-action-kind-conflict");
                    }

                    throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
                }

                var operationRecordedAtUtc = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
                context.OperatorActionOperationLedger.Add(new OperatorActionOperationLedgerEntity
                {
                    OperationId = command.OperationId, RequestFingerprint = command.RequestFingerprint,
                    ActionId = command.ActionId, CreatedAtUtc = operationRecordedAtUtc
                });
                try
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return ToReceipt(priorRequest, wasReplay: false, operationRecordedAtUtc);
                }
                catch (DbUpdateException)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await using var replayContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    var winnerOperation = await replayContext.OperatorActionOperationLedger.AsNoTracking()
                        .SingleOrDefaultAsync(value => value.OperationId == command.OperationId, cancellationToken).ConfigureAwait(false);
                    if (winnerOperation is not null)
                    {
                        var winnerAction = await replayContext.OperatorActionActionLedger.AsNoTracking()
                            .SingleOrDefaultAsync(value => value.ActionId == winnerOperation.ActionId, cancellationToken).ConfigureAwait(false);
                        var winnerRequest = winnerAction is null
                            ? null
                            : await replayContext.SourceProcessorForceRequests.AsNoTracking()
                                .SingleOrDefaultAsync(value => value.ActionId == winnerAction.ActionId, cancellationToken).ConfigureAwait(false);
                        if (IsMatchingForceRequestReplay(winnerOperation, winnerAction, winnerRequest, command, actionKind))
                        {
                            return ToReceipt(winnerRequest!, wasReplay: true, winnerOperation.CreatedAtUtc);
                        }

                        throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
                    }

                    throw;
                }
            }

            if (!IsIgnoreActionIdentity(priorAction))
            {
                if (!string.Equals(priorAction.ActionKind, actionKind, StringComparison.Ordinal))
                {
                    throw new OoxmlForceRequestRejectedException("operator-action-kind-conflict");
                }

                throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
            }
        }

        var expectedRowVersion = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(command.ExpectedBlockedRowVersion);
        var expectedFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(command.ActionId, command.ExpectedBlockedRowVersion);
        if (!string.Equals(command.RequestFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new OoxmlForceRequestRejectedException("operator-action-stale");
        }

        var candidates = await ReadCurrentOoxmlBlockedCandidatesAsync(context, null, actionKind, cancellationToken).ConfigureAwait(false);
        var candidate = candidates.SingleOrDefault(value =>
            string.Equals(OoxmlForceRequestIdentity.CreateActionId(value.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, value.RowVersion), command.ActionId, StringComparison.Ordinal));
        if (candidate is null || !candidate.RowVersion.SequenceEqual(expectedRowVersion))
        {
            throw new OoxmlForceRequestRejectedException("operator-action-stale");
        }

        var branch = await context.SourceProcessorBranches
            .FromSqlInterpolated($"SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidate.BranchId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            throw new OoxmlForceRequestRejectedException("operator-action-stale");
        }
        var blockedAttempt = await context.SourceProcessorAttempts
            .FromSqlInterpolated($"SELECT * FROM [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK) WHERE [BranchId] = {branch.Id} AND [LeaseGeneration] = {branch.LeaseGeneration} AND [FinishedAtUtc] IS NOT NULL")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (blockedAttempt is null)
        {
            throw new OoxmlForceRequestRejectedException("operator-action-stale");
        }

        var lockedCandidates = await ReadCurrentOoxmlBlockedCandidatesAsync(context, null, actionKind, cancellationToken).ConfigureAwait(false);
        candidate = lockedCandidates.SingleOrDefault(value => value.BranchId == branch.Id);
        if (candidate is null || !candidate.RowVersion.SequenceEqual(expectedRowVersion) ||
            !string.Equals(OoxmlForceRequestIdentity.CreateActionId(candidate.BranchId, OoxmlStructuralTextProcessor.Capability.Id,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, candidate.RowVersion), command.ActionId, StringComparison.Ordinal))
        {
            throw new OoxmlForceRequestRejectedException("operator-action-stale");
        }
        if (!candidate.IsForceable)
        {
            throw new OoxmlForceRequestRejectedException("operator-action-not-forceable");
        }

        var policy = await context.OperatorActionCapabilityPolicies
            .FromSqlInterpolated($"SELECT * FROM [OperatorActionCapabilityPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [DescriptorId] = {OoxmlStructuralTextProcessor.Capability.Id} AND [DescriptorFingerprint] = {OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint} AND [DescriptorVersion] = {OoxmlStructuralTextProcessor.Capability.ProcessorVersion} AND [SafetyContractId] = {RetainedBindingSafetyContract} AND [HandlerId] = {RetainedBranchStoreHandler} AND [ActionKind] = {actionKind} AND [ReasonCode] = {candidate.OutcomeCode}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (policy is null || await context.OperatorActionHardDenials.AsNoTracking()
                .AnyAsync(denial => denial.ReasonCode == candidate.OutcomeCode, cancellationToken).ConfigureAwait(false))
        {
            throw new OoxmlForceRequestRejectedException("operator-action-not-forceable");
        }

        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var request = new SourceProcessorForceRequestEntity
        {
            Id = Guid.NewGuid(), ActionId = command.ActionId, OperationId = command.OperationId, RequestFingerprint = command.RequestFingerprint,
            PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision, DescriptorVersion = policy.DescriptorVersion,
            SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, PolicyReasonCode = policy.ReasonCode,
            SourceActivityId = candidate.SourceActivityId, SourceProcessorBranchId = candidate.BranchId, SourceRevisionId = candidate.SourceRevisionId,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id, DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            ExpectedInputSha256 = candidate.InputSha256, OriginalBlockedLeaseGeneration = candidate.LeaseGeneration,
            OriginalBlockedRowVersion = candidate.RowVersion, OriginalOutcomeCode = candidate.OutcomeCode,
            State = (byte)OoxmlForceRequestState.Requested, RequestedAtUtc = now, ClaimExpiresAtUtc = now.AddMinutes(5)
        };
        branch.State = (int)RetainedProcessorBranchState.Pending;
        branch.LeaseOwner = null;
        branch.LeaseExpiresAtUtc = null;
        branch.UpdatedAtUtc = now;
        context.SourceProcessorForceRequests.Add(request);
        if (priorAction is null)
        {
            context.OperatorActionActionLedger.Add(new OperatorActionActionLedgerEntity
            {
                ActionId = command.ActionId, PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision,
                DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint, DescriptorVersion = policy.DescriptorVersion,
                SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind,
                ReasonCode = policy.ReasonCode, SourceProcessorBranchId = candidate.BranchId, BlockedRowVersion = candidate.RowVersion,
                SourceProcessorForceRequestId = request.Id, CreatedAtUtc = now
            });
        }
        context.OperatorActionOperationLedger.Add(new OperatorActionOperationLedgerEntity
        {
            OperationId = command.OperationId, RequestFingerprint = command.RequestFingerprint, ActionId = command.ActionId, CreatedAtUtc = now
        });
        AddOperatorActionEvent(context, request, now, "requested", "force-request-requested");
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToReceipt(request, wasReplay: false, now);
        }
        catch (DbUpdateException exception) when (IsActionLedgerDuplicateKeyViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await using var replayContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winnerOperation = await replayContext.OperatorActionOperationLedger.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OperationId == command.OperationId, cancellationToken).ConfigureAwait(false);
            if (winnerOperation is not null)
            {
                var winnerAction = await replayContext.OperatorActionActionLedger.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ActionId == winnerOperation.ActionId, cancellationToken).ConfigureAwait(false);
                var winnerRequest = winnerAction is null
                    ? null
                    : await replayContext.SourceProcessorForceRequests.AsNoTracking()
                        .SingleOrDefaultAsync(value => value.ActionId == winnerAction.ActionId, cancellationToken).ConfigureAwait(false);
                if (IsMatchingForceRequestReplay(winnerOperation, winnerAction, winnerRequest, command, actionKind))
                {
                    return ToReceipt(winnerRequest!, wasReplay: true, winnerOperation.CreatedAtUtc);
                }

                throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
            }

            var winnerActionForRace = await replayContext.OperatorActionActionLedger.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ActionId == command.ActionId, cancellationToken).ConfigureAwait(false);
            if (winnerActionForRace is not null)
            {
                var winnerRequestForRace = await replayContext.SourceProcessorForceRequests.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.ActionId == winnerActionForRace.ActionId, cancellationToken).ConfigureAwait(false);
                if (!IsMatchingForceRequestReplay(null, winnerActionForRace, winnerRequestForRace, command, actionKind))
                {
                    if (winnerRequestForRace is not null &&
                        (!string.Equals(winnerActionForRace.ActionKind, actionKind, StringComparison.Ordinal) ||
                         !string.Equals(winnerRequestForRace.ActionKind, actionKind, StringComparison.Ordinal)))
                    {
                        throw new OoxmlForceRequestRejectedException("operator-action-kind-conflict");
                    }

                    throw new OoxmlForceRequestRejectedException("operator-operation-conflict");
                }

                return await RequestActionAsync(command, actionKind, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static bool IsActionLedgerDuplicateKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException &&
        sqlException.Number is 2601 or 2627 &&
        sqlException.Message.Contains("'PK_OperatorActionActionLedger'", StringComparison.Ordinal) &&
        exception.Entries.Any(static entry => entry.Entity is OperatorActionActionLedgerEntity);

    private static bool IsMatchingForceRequestReplay(
        OperatorActionOperationLedgerEntity? operation,
        OperatorActionActionLedgerEntity? action,
        SourceProcessorForceRequestEntity? request,
        OoxmlForceRequestCommand command,
        string actionKind)
    {
        if (operation is not null &&
            (!string.Equals(operation.ActionId, command.ActionId, StringComparison.Ordinal) ||
             !string.Equals(operation.RequestFingerprint, command.RequestFingerprint, StringComparison.Ordinal) ||
             operation.IgnoreSequence is not null ||
             operation.IgnoreState is not null))
        {
            return false;
        }

        if (action is null || request is null ||
            !string.Equals(action.ActionId, command.ActionId, StringComparison.Ordinal) ||
            (!string.Equals(action.ActionKind, actionKind, StringComparison.Ordinal) && !IsIgnoreActionIdentity(action)) ||
            (!IsIgnoreActionIdentity(action) && action.SourceProcessorForceRequestId != request.Id) ||
            !string.Equals(request.ActionId, action.ActionId, StringComparison.Ordinal) ||
            !string.Equals(request.ActionKind, actionKind, StringComparison.Ordinal) ||
            !request.OriginalBlockedRowVersion.SequenceEqual(action.BlockedRowVersion))
        {
            return false;
        }

        var expectedBlockedRowVersion = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(action.BlockedRowVersion);
        var expectedRequestFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(action.ActionId, expectedBlockedRowVersion);
        return string.Equals(command.ExpectedBlockedRowVersion, expectedBlockedRowVersion, StringComparison.Ordinal) &&
               string.Equals(command.RequestFingerprint, expectedRequestFingerprint, StringComparison.Ordinal) &&
               string.Equals(request.RequestFingerprint, expectedRequestFingerprint, StringComparison.Ordinal);
    }

    private static bool IsIgnoreActionIdentity(OperatorActionActionLedgerEntity action) =>
        string.Equals(action.ActionKind, "ignore", StringComparison.Ordinal) && action.SourceProcessorForceRequestId is null;

    public async ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimForceAsync(
        string leaseOwner,
        int maximumCount,
        string processorFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (!string.Equals(processorFingerprint, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal))
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var requested = await context.SourceProcessorForceRequests
            .FromSqlInterpolated($"""
                SELECT TOP ({Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize)}) *
                FROM [SourceProcessorForceRequests] WITH (UPDLOCK, HOLDLOCK)
                WHERE [State] = {(byte)OoxmlForceRequestState.Requested}
                  AND [DescriptorFingerprint] = {processorFingerprint}
                  AND [ClaimExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                ORDER BY [RequestedAtUtc], [Id]
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (requested.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return [];
        }

        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var expiry = now.AddMinutes(5);
        var claims = new List<RetainedProcessorClaim>(requested.Count);
        foreach (var request in requested)
        {
            var branch = await context.SourceProcessorBranches
                .FromSqlInterpolated($"SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.SourceProcessorBranchId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (branch is null || branch.State != (int)RetainedProcessorBranchState.Pending ||
                branch.LeaseGeneration != request.OriginalBlockedLeaseGeneration ||
                !await IsForceRequestBindingCurrentAsync(context, request, branch, cancellationToken).ConfigureAwait(false) ||
                !await IsForceRequestPolicyCurrentAsync(context, request, cancellationToken).ConfigureAwait(false) ||
                !await IsOoxmlDescriptorRunnableAsync(context, request, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            branch.State = (int)RetainedProcessorBranchState.Running;
            branch.LeaseOwner = leaseOwner;
            branch.LeaseExpiresAtUtc = expiry;
            branch.LeaseGeneration++;
            branch.AttemptCount++;
            branch.UpdatedAtUtc = now;
            request.State = (byte)OoxmlForceRequestState.Claimed;
            request.ClaimedAtUtc = now;
            request.ForceAttemptBranchId = branch.Id;
            request.ForceAttemptLeaseGeneration = branch.LeaseGeneration;
            context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = Guid.NewGuid(), BranchId = branch.Id, LeaseGeneration = branch.LeaseGeneration, StartedAtUtc = now
            });
            AddOperatorActionEvent(context, request, now, "claimed", "force-request-claimed");
            var revision = await context.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == branch.SourceRevisionId, cancellationToken).ConfigureAwait(false);
            claims.Add(new RetainedProcessorClaim(branch.Id, new SourceRevisionId(branch.SourceRevisionId), revision.StableSourceIdentity,
                branch.InputSha256, leaseOwner, branch.LeaseGeneration, expiry));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claims;
    }

    public ValueTask<int> ReconcileForceRequestsAsync(CancellationToken cancellationToken) =>
        ReconcileForceRequestsAsync(ooxmlDescriptorEnabled: true, cancellationToken);

    public async ValueTask<int> ReconcileForceRequestsAsync(bool ooxmlDescriptorEnabled, CancellationToken cancellationToken)
    {
        await using var discovery = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var requestIds = await discovery.SourceProcessorForceRequests.AsNoTracking()
            .Where(value => value.State == (byte)OoxmlForceRequestState.Requested || value.State == (byte)OoxmlForceRequestState.Claimed)
            .OrderBy(value => value.ClaimExpiresAtUtc).ThenBy(value => value.Id)
            .Select(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = 0;
        foreach (var requestId in requestIds)
        {
            if (await ReconcileForceRequestAsync(requestId, ooxmlDescriptorEnabled, cancellationToken).ConfigureAwait(false)) reconciled++;
        }

        return reconciled;
    }

    private async ValueTask<bool> ReconcileForceRequestAsync(
        Guid requestId,
        bool ooxmlDescriptorEnabled,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var request = await context.SourceProcessorForceRequests
            .FromSqlInterpolated($"""
                SELECT *
                FROM [SourceProcessorForceRequests] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {requestId}
                  AND [State] IN ({(byte)OoxmlForceRequestState.Requested}, {(byte)OoxmlForceRequestState.Claimed})
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (request is null) return false;
        var branch = await context.SourceProcessorBranches
            .FromSqlInterpolated($"SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.SourceProcessorBranchId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var descriptorRunnable = ooxmlDescriptorEnabled &&
            await IsForceRequestPolicyCurrentAsync(context, request, cancellationToken).ConfigureAwait(false) &&
            await IsOoxmlDescriptorRunnableAsync(context, request, cancellationToken).ConfigureAwait(false);
        var bindingCurrent = branch is not null && await IsForceRequestBindingCurrentAsync(context, request, branch, cancellationToken).ConfigureAwait(false);

        if (request.State == (byte)OoxmlForceRequestState.Requested)
        {
            if (!bindingCurrent || branch is null || branch.State != (int)RetainedProcessorBranchState.Pending ||
                branch.LeaseGeneration != request.OriginalBlockedLeaseGeneration || branch.LeaseOwner is not null ||
                branch.LeaseExpiresAtUtc is not null)
            {
                return await TerminaliseRequestedForceRequestAsync(context, transaction, request, branch, now, "force-request-cancelled", cancellationToken).ConfigureAwait(false);
            }
            if (!descriptorRunnable)
            {
                return await TerminaliseRequestedForceRequestAsync(context, transaction, request, branch, now, "force-request-descriptor-disabled", cancellationToken).ConfigureAwait(false);
            }
            if (request.ClaimExpiresAtUtc <= now)
            {
                return await TerminaliseRequestedForceRequestAsync(context, transaction, request, branch, now, "force-request-claim-expired", cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        if (!bindingCurrent || branch is null || request.ForceAttemptBranchId != request.SourceProcessorBranchId ||
            request.ForceAttemptLeaseGeneration is null || branch.State != (int)RetainedProcessorBranchState.Running ||
            branch.LeaseGeneration != request.ForceAttemptLeaseGeneration || string.IsNullOrWhiteSpace(branch.LeaseOwner))
        {
            return await TerminaliseClaimedForceRequestAsync(context, transaction, request, branch, now, "force-request-cancelled", cancellationToken).ConfigureAwait(false);
        }
        if (!descriptorRunnable)
        {
            return await TerminaliseClaimedForceRequestAsync(context, transaction, request, branch, now, "force-request-descriptor-disabled", cancellationToken).ConfigureAwait(false);
        }
        if (branch.LeaseExpiresAtUtc is null || branch.LeaseExpiresAtUtc <= now)
        {
            return await TerminaliseClaimedForceRequestAsync(context, transaction, request, branch, now, "lease-expired-reconciled", cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async ValueTask<bool> TerminaliseRequestedForceRequestAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        SourceProcessorForceRequestEntity request,
        SourceProcessorBranchEntity? branch,
        DateTimeOffset now,
        string terminalReasonCode,
        CancellationToken cancellationToken)
    {
        if (request.State != (byte)OoxmlForceRequestState.Requested)
        {
            return false;
        }
        request.State = terminalReasonCode == "force-request-claim-expired" ? (byte)OoxmlForceRequestState.Expired : (byte)OoxmlForceRequestState.Cancelled;
        request.TerminalAtUtc = now;
        request.TerminalReasonCode = terminalReasonCode;
        request.TerminalReceiptFingerprint = OoxmlForceRequestIdentity.CreateTerminalReceiptFingerprint(request.Id, terminalReasonCode);
        if (branch is not null && branch.Id == request.SourceProcessorBranchId &&
            branch.State == (int)RetainedProcessorBranchState.Pending &&
            branch.LeaseGeneration == request.OriginalBlockedLeaseGeneration &&
            branch.LeaseOwner is null && branch.LeaseExpiresAtUtc is null)
        {
            branch.State = (int)RetainedProcessorBranchState.Blocked;
            branch.LeaseOwner = null;
            branch.LeaseExpiresAtUtc = null;
            branch.UpdatedAtUtc = now;
        }
        AddForceTransitionEvent(context, request, now, terminalReasonCode);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> TerminaliseClaimedForceRequestAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        SourceProcessorForceRequestEntity request,
        SourceProcessorBranchEntity? branch,
        DateTimeOffset now,
        string terminalReasonCode,
        CancellationToken cancellationToken)
    {
        if (request.State != (byte)OoxmlForceRequestState.Claimed || request.ForceAttemptBranchId != request.SourceProcessorBranchId ||
            request.ForceAttemptLeaseGeneration is null)
        {
            return false;
        }
        var attempt = await context.SourceProcessorAttempts
            .FromSqlInterpolated($"""
                SELECT *
                FROM [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK)
                WHERE [BranchId] = {request.ForceAttemptBranchId.Value}
                  AND [LeaseGeneration] = {request.ForceAttemptLeaseGeneration.Value}
                  AND [FinishedAtUtc] IS NULL
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (attempt is not null)
        {
            attempt.FinishedAtUtc = now;
            attempt.OutcomeCode = terminalReasonCode;
        }
        request.State = terminalReasonCode == "lease-expired-reconciled" ? (byte)OoxmlForceRequestState.Expired : (byte)OoxmlForceRequestState.Cancelled;
        request.TerminalAtUtc = now;
        request.TerminalReasonCode = terminalReasonCode;
        request.TerminalReceiptFingerprint = OoxmlForceRequestIdentity.CreateTerminalReceiptFingerprint(request.Id, terminalReasonCode);
        if (branch is not null && branch.Id == request.ForceAttemptBranchId.Value &&
            branch.State == (int)RetainedProcessorBranchState.Running &&
            branch.LeaseGeneration == request.ForceAttemptLeaseGeneration.Value &&
            !string.IsNullOrWhiteSpace(branch.LeaseOwner))
        {
            branch.State = terminalReasonCode == "lease-expired-reconciled" ? (int)RetainedProcessorBranchState.Pending : (int)RetainedProcessorBranchState.Blocked;
            branch.LeaseOwner = null;
            branch.LeaseExpiresAtUtc = null;
            branch.UpdatedAtUtc = now;
        }
        AddForceTransitionEvent(context, request, now, terminalReasonCode);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void AddForceTransitionEvent(
        FluxKnowledgeDbContext context,
        SourceProcessorForceRequestEntity request,
        DateTimeOffset now,
        string reasonCode) =>
        AddOperatorActionEvent(
            context,
            request,
            now,
            request.State == (byte)OoxmlForceRequestState.Expired ? "expired" : "cancelled",
            reasonCode);

    private static void AddOperatorActionEvent(
        FluxKnowledgeDbContext context,
        SourceProcessorForceRequestEntity request,
        DateTimeOffset now,
        string state,
        string reasonCode)
    {
        OperatorEventAppender.Add(context, new OperatorEventDraft(
            OperatorActionEvent,
            "operator_action",
            "information",
            OperatorActionActor,
            now,
            CorrelationId: $"operator-action:{request.ActionId}",
            Details: new { descriptor = "document_ooxml", action = request.ActionKind, state, reasonCode }));
    }

    public async ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken)
        => await ReadPromotionCandidatesAsync(maximumCount, ZipArchiveRetainedProcessor.Capability, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
        int maximumCount,
        SourceCapabilityDescriptor capability,
        CancellationToken cancellationToken)
    {
        var extensions = capability.ProcessorKind switch
        {
            "document-ooxml-structural-extract" => new[] { ".docx", ".xlsx", ".pptx" },
            "retained-csharp-code" => new[] { ".cs" },
            "media-metadata" => new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".mp3", ".wav", ".mov", ".mp4", ".m4v" },
            "archive-zip-expand" => Array.Empty<string>(),
            "archive-tar-expand" => Array.Empty<string>(),
            _ => Array.Empty<string>()
        };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = from activity in context.SourceActivities.AsNoTracking()
                      join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                      join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
                      where activity.State == (int)SourceActivityState.DeferredUnsupported &&
                            activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                            EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            !context.SourceProcessorBranches.Any(branch => branch.SourceActivityId == activity.Id)
                      select new { activity, revision, artifact };
        if (capability.ProcessorKind == "document-ooxml-structural-extract")
            candidates = candidates.Where(value => extensions.Contains(value.revision.Extension.ToLower()));
        else if (capability.ProcessorKind == "media-metadata")
            candidates = candidates.Where(value => extensions.Contains(value.revision.Extension.ToLower()));
        else if (capability.ProcessorKind == "retained-csharp-code")
            candidates = candidates.Where(value =>
                value.revision.Extension.ToLower() == ".cs" &&
                value.revision.Classification == "AcceptedUtf8Text" &&
                value.activity.ActivityKind == (int)SourceActivityKind.DocumentParsing &&
                value.activity.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion &&
                value.activity.DescriptorFingerprint == SourceActivityEntity.LegacyDescriptorFingerprint &&
                value.activity.RequiredCapability == RetainedCsharpCodeProcessor.ProcessorKind &&
                value.activity.Reason == "csharp-code-writer-not-ready");
        else if (capability.ProcessorKind == "archive-zip-expand")
            candidates = candidates.Where(value => !new[] { ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt" }.Contains(value.revision.Extension.ToLower()));
        return await candidates.OrderBy(value => value.activity.CreatedAtUtc).ThenBy(value => value.activity.Id)
            .Select(value => new RetainedProcessorPromotionCandidate(value.activity.Id, new SourceRevisionId(value.activity.SourceRevisionId), value.activity.InputFingerprint, value.revision.Extension))
            .Take(Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadRecognisedUnsupportedMediaCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var extensions = new[] { ".avif", ".heic", ".heif", ".avi", ".mkv", ".webm", ".ogg", ".flac", ".aac", ".m4a", ".wma" };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await (from activity in context.SourceActivities.AsNoTracking()
                      join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                      join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
                      where activity.State == (int)SourceActivityState.DeferredUnsupported &&
                            activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                            extensions.Contains(revision.Extension.ToLower()) &&
                            EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            !context.SourceProcessorBranches.Any(branch => branch.SourceActivityId == activity.Id)
                      orderby activity.CreatedAtUtc, activity.Id
                      select new RetainedProcessorPromotionCandidate(activity.Id, new SourceRevisionId(activity.SourceRevisionId), activity.InputFingerprint, revision.Extension))
            .Take(Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadLegacyOfficeDesignationCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await (from activity in context.SourceActivities.AsNoTracking()
                      join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                      join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
                      where activity.State == (int)SourceActivityState.DeferredUnsupported &&
                            activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                            activity.RequiredCapability != "document-office-legacy-structural-extract" &&
                            new[] { ".doc", ".xls", ".ppt" }.Contains(revision.Extension.ToLower()) &&
                            EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                            !context.SourceActivityRelations.Any(relation => relation.PredecessorActivityId == activity.Id)
                      orderby activity.CreatedAtUtc, activity.Id
                      select new RetainedProcessorPromotionCandidate(
                          activity.Id,
                          new SourceRevisionId(activity.SourceRevisionId),
                          activity.InputFingerprint,
                          revision.Extension))
            .Take(Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DesignateLegacyOfficeAsync(
        RetainedProcessorPromotionCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var predecessor = await context.SourceActivities.SingleOrDefaultAsync(value => value.Id == candidate.LegacyActivityId, cancellationToken).ConfigureAwait(false);
            if (predecessor is null || predecessor.State != (int)SourceActivityState.DeferredUnsupported ||
                predecessor.ExecutionClass != (int)ExecutionClass.DeferredCapability ||
                string.Equals(predecessor.RequiredCapability, "document-office-legacy-structural-extract", StringComparison.Ordinal) ||
                await context.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == predecessor.Id, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var now = timeProvider.GetUtcNow();
            var successor = new SourceActivityEntity
            {
                Id = Guid.NewGuid(), SourceRevisionId = predecessor.SourceRevisionId,
                ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.DeferredCapability,
                ProcessorVersion = "phase-5-office-legacy-designation-v1",
                InputFingerprint = predecessor.InputFingerprint,
                RequiredCapability = "document-office-legacy-structural-extract",
                Reason = "legacy-office-binary-parser-unavailable",
                State = (int)SourceActivityState.DeferredUnsupported,
                CreatedAtUtc = now, UpdatedAtUtc = now
            };
            const string supersessionReason = "superseded-by-legacy-office-designation";
            predecessor.State = (int)SourceActivityState.CancelledSuperseded;
            predecessor.Reason = supersessionReason;
            predecessor.UpdatedAtUtc = now;
            context.SourceActivities.Add(successor);
            context.SourceActivityRelations.Add(new SourceActivityRelationEntity
            {
                Id = Guid.NewGuid(), PredecessorActivityId = predecessor.Id, SuccessorActivityId = successor.Id,
                RelationshipKind = "superseded-by-legacy-office-designation", ReasonCode = supersessionReason, CreatedAtUtc = now
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate candidate, SourceCapabilityDescriptor capability, CancellationToken cancellationToken)
    {
        if (capability.Id == RetainedCsharpCodeProcessor.Capability.Id)
        {
            return await PromoteRetainedCsharpAsync(candidate, capability, cancellationToken).ConfigureAwait(false);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var legacy = await context.SourceActivities.SingleOrDefaultAsync(value => value.Id == candidate.LegacyActivityId, cancellationToken).ConfigureAwait(false);
        if (legacy is null || legacy.State != (int)SourceActivityState.DeferredUnsupported ||
            await context.SourceProcessorBranches.AnyAsync(value => value.SourceActivityId == legacy.Id, cancellationToken).ConfigureAwait(false)) return false;
        var successor = new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = legacy.SourceRevisionId, ActivityKind = (int)capability.AcceptedActivityKind,
            ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = capability.ProcessorVersion, InputFingerprint = legacy.InputFingerprint,
            DescriptorFingerprint = capability.ProcessorKind == RetainedCsharpCodeProcessor.ProcessorKind
                ? capability.ProcessorFingerprint
                : SourceActivityEntity.LegacyDescriptorFingerprint,
            State = (int)SourceActivityState.Pending, CreatedAtUtc = timeProvider.GetUtcNow(), UpdatedAtUtc = timeProvider.GetUtcNow() };
        var supersessionReason = $"superseded-by-{capability.ProcessorKind}";
        legacy.State = (int)SourceActivityState.CancelledSuperseded; legacy.Reason = supersessionReason; legacy.UpdatedAtUtc = timeProvider.GetUtcNow();
        context.SourceActivities.Add(successor);
        context.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = legacy.Id, SuccessorActivityId = successor.Id,
            RelationshipKind = "superseded-by-retained-processor", ReasonCode = supersessionReason, CreatedAtUtc = timeProvider.GetUtcNow() });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = successor.Id, SourceRevisionId = successor.SourceRevisionId,
            InputSha256 = legacy.InputFingerprint, ProcessorVersion = capability.ProcessorVersion, ProcessorFingerprint = capability.ProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = timeProvider.GetUtcNow(), UpdatedAtUtc = timeProvider.GetUtcNow() });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return true;
    }

    private async ValueTask<bool> PromoteRetainedCsharpAsync(
        RetainedProcessorPromotionCandidate candidate,
        SourceCapabilityDescriptor capability,
        CancellationToken cancellationToken)
    {
        if (!LocalSourceCapabilityHandlerRegistry.SameDescriptor(capability, RetainedCsharpCodeProcessor.Capability) ||
            !await IsRetainedCsharpCodeWriterReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var holding = await context.SourceActivities.FromSqlInterpolated($"""
            SELECT * FROM [SourceActivities] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = {candidate.LegacyActivityId}
              AND [SourceRevisionId] = {candidate.SourceRevisionId.Value}
              AND [ActivityKind] = {(int)SourceActivityKind.DocumentParsing}
              AND [ExecutionClass] = {(int)ExecutionClass.DeferredCapability}
              AND [ProcessorVersion] = {RetainedCsharpCodeProcessor.ProcessorVersion}
              AND [InputFingerprint] = {candidate.InputSha256}
              AND [DescriptorFingerprint] = {SourceActivityEntity.LegacyDescriptorFingerprint}
              AND [RequiredCapability] = {RetainedCsharpCodeProcessor.ProcessorKind}
              AND [Reason] = {"csharp-code-writer-not-ready"}
              AND [State] = {(int)SourceActivityState.DeferredUnsupported}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (holding is null ||
            await context.SourceProcessorBranches.AnyAsync(value => value.SourceActivityId == holding.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var revision = await context.SourceRevisions.FromSqlInterpolated($"""
            SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = {holding.SourceRevisionId}
              AND [Extension] = {".cs"}
              AND [Classification] = {"AcceptedUtf8Text"}
              AND [ContentSha256] = {holding.InputFingerprint}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var artifact = revision is null
            ? null
            : await context.SourceArtifacts.FromSqlInterpolated($"""
                SELECT * FROM [SourceArtifacts] WITH (UPDLOCK, HOLDLOCK)
                WHERE [SourceRevisionId] = {holding.SourceRevisionId}
                  AND [ContentSha256] = {holding.InputFingerprint}
                  AND [ByteLength] = {revision.ByteLength}
                """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (revision is null || artifact is null)
        {
            return false;
        }

        var legacyTextRoutes = await context.SourceActivities.FromSqlInterpolated($"""
            SELECT * FROM [SourceActivities] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SourceRevisionId] = {holding.SourceRevisionId}
              AND [ActivityKind] = {(int)SourceActivityKind.TextExtraction}
              AND [InputFingerprint] = {holding.InputFingerprint}
              AND [State] IN (
                  {(int)SourceActivityState.Pending},
                  {(int)SourceActivityState.Running},
                  {(int)SourceActivityState.DeferredUnsupported},
                  {(int)SourceActivityState.FailedRetryable})
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasConflict = legacyTextRoutes.Any(value =>
            value.State == (int)SourceActivityState.Running ||
            value.ResultingPipelineRecordId is not null) ||
            await context.SourceProcessorBranches.AnyAsync(
                value => legacyTextRoutes.Select(route => route.Id).Contains(value.SourceActivityId),
                cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        if (hasConflict)
        {
            holding.Reason = "csharp-code-legacy-text-conflict";
            holding.UpdatedAtUtc = now;
            OperatorEventAppender.Add(context, new OperatorEventDraft(
                "retained_processor.csharp_replan_conflict",
                "retained_processor",
                "warning",
                "retained-csharp-replan",
                now,
                SourceRootId: revision.SourceRootId,
                SourceRevisionId: revision.Id,
                SourceActivityId: holding.Id,
                CorrelationId: $"retained-csharp-replan:{holding.Id:N}",
                Details: new { reasonCode = "csharp-code-legacy-text-conflict" }));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var legacyTextRoute in legacyTextRoutes)
        {
            legacyTextRoute.State = (int)SourceActivityState.DeferredPolicy;
            legacyTextRoute.Reason = "csharp-code-superseded-text-route";
            legacyTextRoute.UpdatedAtUtc = now;
        }

        var successor = new SourceActivityEntity
        {
            Id = Guid.NewGuid(),
            SourceRevisionId = holding.SourceRevisionId,
            ActivityKind = (int)SourceActivityKind.CodeParsing,
            ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = capability.ProcessorVersion,
            InputFingerprint = holding.InputFingerprint,
            DescriptorFingerprint = capability.ProcessorFingerprint,
            State = (int)SourceActivityState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        holding.State = (int)SourceActivityState.CancelledSuperseded;
        holding.Reason = "superseded-by-retained-csharp-code";
        holding.UpdatedAtUtc = now;
        context.SourceActivities.Add(successor);
        context.SourceActivityRelations.Add(new SourceActivityRelationEntity
        {
            Id = Guid.NewGuid(),
            PredecessorActivityId = holding.Id,
            SuccessorActivityId = successor.Id,
            RelationshipKind = "superseded-by-retained-processor",
            ReasonCode = "superseded-by-retained-csharp-code",
            CreatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = Guid.NewGuid(),
            SourceActivityId = successor.Id,
            SourceRevisionId = successor.SourceRevisionId,
            InputSha256 = successor.InputFingerprint,
            ProcessorVersion = capability.ProcessorVersion,
            ProcessorFingerprint = capability.ProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcomeCode, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var legacy = await context.SourceActivities.SingleOrDefaultAsync(value => value.Id == candidate.LegacyActivityId, cancellationToken).ConfigureAwait(false);
        if (legacy is null || legacy.State != (int)SourceActivityState.DeferredUnsupported ||
            await context.SourceProcessorBranches.AnyAsync(value => value.SourceActivityId == legacy.Id, cancellationToken).ConfigureAwait(false)) return false;
        legacy.State = (int)SourceActivityState.DeferredPolicy;
        legacy.Reason = outcomeCode;
        legacy.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> DeferPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcomeCode, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var legacy = await context.SourceActivities.SingleOrDefaultAsync(value => value.Id == candidate.LegacyActivityId, cancellationToken).ConfigureAwait(false);
        if (legacy is null || legacy.State != (int)SourceActivityState.DeferredUnsupported ||
            await context.SourceProcessorBranches.AnyAsync(value => value.SourceActivityId == legacy.Id, cancellationToken).ConfigureAwait(false)) return false;
        legacy.Reason = outcomeCode;
        legacy.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<IReadOnlyList<RetainedCsharpCodeClaim>> ClaimCsharpCodeAsync(
        string leaseOwner,
        int maximumCount,
        string processorFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (!string.Equals(processorFingerprint, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) ||
            !await IsRetainedCsharpCodeWriterReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }
        var executionState = new CsharpClaimExecutionState();
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(executionState, async (state, retryCancellationToken) =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(retryCancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, retryCancellationToken).ConfigureAwait(false);
            var now = await DatabaseUtcNowAsync(context, retryCancellationToken).ConfigureAwait(false);
            var expiry = now.AddMinutes(5);
            var branches = await context.SourceProcessorBranches.FromSqlInterpolated($"""
            SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
            WHERE ([State] = {(int)RetainedProcessorBranchState.Pending}
                OR ([State] = {(int)RetainedProcessorBranchState.Running} AND [LeaseExpiresAtUtc] < TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')))
              AND [ProcessorFingerprint] = {processorFingerprint}
              AND [ProcessorVersion] = {RetainedCsharpCodeProcessor.ProcessorVersion}
              AND EXISTS (SELECT 1 FROM [SourceActivities] AS [activity] WITH (UPDLOCK, HOLDLOCK)
                  WHERE [activity].[Id] = [SourceProcessorBranches].[SourceActivityId]
                    AND [activity].[SourceRevisionId] = [SourceProcessorBranches].[SourceRevisionId]
                    AND [activity].[ActivityKind] = {(int)SourceActivityKind.CodeParsing}
                    AND [activity].[ExecutionClass] = {(int)ExecutionClass.InProcess}
                    AND [activity].[ProcessorVersion] = {RetainedCsharpCodeProcessor.ProcessorVersion}
                    AND [activity].[InputFingerprint] COLLATE Latin1_General_100_BIN2 = [SourceProcessorBranches].[InputSha256] COLLATE Latin1_General_100_BIN2
                    AND [activity].[DescriptorFingerprint] = {processorFingerprint}
                    AND [activity].[State] = {(int)SourceActivityState.Pending})
              AND EXISTS (
                  SELECT 1
                  FROM [SourceRevisions] AS [revision] WITH (UPDLOCK, HOLDLOCK)
                  INNER JOIN [SourceArtifacts] AS [artifact] WITH (UPDLOCK, HOLDLOCK)
                      ON [artifact].[SourceRevisionId] = [revision].[Id]
                  WHERE [revision].[Id] = [SourceProcessorBranches].[SourceRevisionId]
                    AND [revision].[Extension] = {".cs"}
                    AND [revision].[Classification] = {"AcceptedUtf8Text"}
                    AND [revision].[ContentSha256] COLLATE Latin1_General_100_BIN2 = [SourceProcessorBranches].[InputSha256] COLLATE Latin1_General_100_BIN2
                    AND [artifact].[ContentSha256] COLLATE Latin1_General_100_BIN2 = [SourceProcessorBranches].[InputSha256] COLLATE Latin1_General_100_BIN2
                    AND [artifact].[ByteLength] = [revision].[ByteLength])
            """).OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.Id).Take(Math.Clamp(
                maximumCount,
                1,
                RetainedCsharpCodeProcessor.MaximumClaimBatchSize)).ToListAsync(retryCancellationToken).ConfigureAwait(false);
            var attempts = new Dictionary<Guid, Guid>();
            foreach (var branch in branches)
            {
                if (branch.State == (int)RetainedProcessorBranchState.Running && branch.LeaseExpiresAtUtc < now)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [SourceProcessorAttempts] SET [FinishedAtUtc] = {now}, [OutcomeCode] = {"lease-expired-reconciled"} WHERE [BranchId] = {branch.Id} AND [LeaseGeneration] = {branch.LeaseGeneration} AND [FinishedAtUtc] IS NULL;", retryCancellationToken).ConfigureAwait(false);
                }
                branch.State = (int)RetainedProcessorBranchState.Running;
                branch.LeaseOwner = leaseOwner;
                branch.LeaseExpiresAtUtc = expiry;
                branch.LeaseGeneration++;
                branch.AttemptCount++;
                branch.UpdatedAtUtc = now;
                var attemptId = Guid.NewGuid();
                attempts.Add(branch.Id, attemptId);
                context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity { Id = attemptId, BranchId = branch.Id, LeaseGeneration = branch.LeaseGeneration, StartedAtUtc = now });
            }
            await context.SaveChangesAsync(retryCancellationToken).ConfigureAwait(false);
            var identities = await context.SourceRevisions.AsNoTracking().Where(value => branches.Select(branch => branch.SourceRevisionId).Contains(value.Id)).ToDictionaryAsync(value => value.Id, retryCancellationToken).ConfigureAwait(false);
            state.AttemptedClaims = branches.Select(branch => new RetainedCsharpCodeClaim(branch.Id, new SourceRevisionId(branch.SourceRevisionId), identities[branch.SourceRevisionId].StableSourceIdentity, branch.InputSha256, leaseOwner, branch.LeaseGeneration, expiry, attempts[branch.Id])).ToArray();
            await transaction.CommitAsync(retryCancellationToken).ConfigureAwait(false);
            return state.AttemptedClaims;
        }, async (state, retryCancellationToken) =>
        {
            if (state.AttemptedClaims.Count == 0)
            {
                return new ExecutionResult<IReadOnlyList<RetainedCsharpCodeClaim>>(true, state.AttemptedClaims);
            }

            await using var verificationContext = await contextFactory.CreateDbContextAsync(retryCancellationToken).ConfigureAwait(false);
            var attemptIds = state.AttemptedClaims.Select(value => value.AttemptId).ToArray();
            var branchIds = state.AttemptedClaims.Select(value => value.BranchId).ToArray();
            var attempts = await verificationContext.SourceProcessorAttempts.AsNoTracking()
                .Where(value => attemptIds.Contains(value.Id))
                .Select(value => new { value.Id, value.BranchId, value.LeaseGeneration })
                .ToListAsync(retryCancellationToken)
                .ConfigureAwait(false);
            var branches = await verificationContext.SourceProcessorBranches.AsNoTracking()
                .Where(value => branchIds.Contains(value.Id))
                .Select(value => new { value.Id, value.LeaseOwner, value.LeaseGeneration, value.State })
                .ToListAsync(retryCancellationToken)
                .ConfigureAwait(false);
            var succeeded = attempts.Count == state.AttemptedClaims.Count &&
                branches.Count == state.AttemptedClaims.Count &&
                state.AttemptedClaims.All(claim => attempts.Any(attempt =>
                    attempt.Id == claim.AttemptId &&
                    attempt.BranchId == claim.BranchId &&
                    attempt.LeaseGeneration == claim.LeaseGeneration)) &&
                state.AttemptedClaims.All(claim => branches.Any(branch =>
                    branch.Id == claim.BranchId &&
                    branch.LeaseOwner == claim.LeaseOwner &&
                    branch.LeaseGeneration == claim.LeaseGeneration &&
                    branch.State == (int)RetainedProcessorBranchState.Running));
            return new ExecutionResult<IReadOnlyList<RetainedCsharpCodeClaim>>(succeeded, state.AttemptedClaims);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RetainedCsharpCodeCompletionWriteResult> CompleteRetainedCsharpCodeAsync(
        RetainedCsharpCodeClaim claim,
        RetainedCsharpCodeCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(completion);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.SourceProcessorCodeCompletionReceipts.FromSqlInterpolated($"SELECT * FROM [SourceProcessorCodeCompletionReceipts] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceProcessorBranchId] = {claim.BranchId}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            var exact = IsCanonicalCsharpCompletion(claim, completion) &&
                await IsExactCsharpReplayAsync(context, receipt, completion, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return exact
                ? new RetainedCsharpCodeCompletionWriteResult(true, true, receipt.OutcomeCode, receipt.CompletionFingerprint)
                : new RetainedCsharpCodeCompletionWriteResult(false, false, "csharp-code-completion-conflict", null);
        }

        ValidateCsharpCompletion(claim, completion);

        var branch = await context.SourceProcessorBranches.FromSqlInterpolated($"""
            SELECT * FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = {claim.BranchId} AND [SourceRevisionId] = {claim.SourceRevisionId.Value}
              AND [InputSha256] = {claim.InputSha256} AND [ProcessorVersion] = {completion.ProcessorVersion}
              AND [ProcessorFingerprint] = {completion.DescriptorFingerprint} AND [LeaseOwner] = {claim.LeaseOwner}
              AND [LeaseGeneration] = {claim.LeaseGeneration} AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
              AND [State] = {(int)RetainedProcessorBranchState.Running}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var attempt = await context.SourceProcessorAttempts.FromSqlInterpolated($"SELECT * FROM [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {claim.AttemptId} AND [BranchId] = {claim.BranchId} AND [LeaseGeneration] = {claim.LeaseGeneration} AND [FinishedAtUtc] IS NULL").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var activity = branch is null ? null : await context.SourceActivities.FromSqlInterpolated($"SELECT * FROM [SourceActivities] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {branch.SourceActivityId} AND [SourceRevisionId] = {claim.SourceRevisionId.Value} AND [ActivityKind] = {(int)SourceActivityKind.CodeParsing} AND [ExecutionClass] = {(int)ExecutionClass.InProcess} AND [ProcessorVersion] = {completion.ProcessorVersion} AND [InputFingerprint] = {claim.InputSha256} AND [DescriptorFingerprint] = {completion.DescriptorFingerprint} AND [State] = {(int)SourceActivityState.Pending}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var revision = branch is null ? null : await context.SourceRevisions.FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {claim.SourceRevisionId.Value} AND [Extension] = {".cs"} AND [Classification] = {"AcceptedUtf8Text"} AND [ContentSha256] = {claim.InputSha256}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var artifact = revision is null ? null : await context.SourceArtifacts.FromSqlInterpolated($"SELECT * FROM [SourceArtifacts] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRevisionId] = {claim.SourceRevisionId.Value} AND [ContentSha256] = {claim.InputSha256} AND [ByteLength] = {revision.ByteLength}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branch is null || attempt is null || activity is null || revision is null || artifact is null)
        {
            return new RetainedCsharpCodeCompletionWriteResult(false, false, "processor-fence-invalid", null);
        }

        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var codesWire = EncodeReceiptDiagnosticCodes(completion.ReceiptDiagnosticCodes);
        Guid? documentId = null;
        if (completion.OutcomeCode == "success")
        {
            documentId = claim.BranchId;
            context.SourceProcessorCodeDocuments.Add(new SourceProcessorCodeDocumentEntity
            {
                SourceProcessorBranchId = documentId.Value, SourceRevisionId = claim.SourceRevisionId.Value, RetainedArtifactSha256 = completion.RetainedArtifactSha256,
                DescriptorFingerprint = completion.DescriptorFingerprint, ParserFingerprint = completion.ParserFingerprint, HandlerImplementationId = RetainedCsharpCodeProcessor.HandlerImplementationId,
                LeaseGeneration = claim.LeaseGeneration, DecodedCharacterCount = completion.DecodedCharacterCount, LineCount = completion.LineCount,
                SymbolCount = completion.Symbols.Count, ReferenceCount = completion.References.Count, DiagnosticsCount = completion.Diagnostics.Count,
                WithheldSymbolCount = completion.WithheldSymbolCount, WithheldReferenceCount = completion.WithheldReferenceCount, WithheldDiagnosticCount = completion.WithheldDiagnosticCount,
                ReceiptDiagnosticCodeCount = completion.ReceiptDiagnosticCodes.Count, DocumentFingerprint = completion.DocumentFingerprint!, CompletionFingerprint = completion.CompletionFingerprint!
            });
            context.SourceProcessorCodeSymbols.AddRange(completion.Symbols.Select(value => new SourceProcessorCodeSymbolEntity { DocumentId = documentId.Value, Ordinal = value.Ordinal, DeclarationKindCode = value.DeclarationKindCode, LocalName = value.LocalName, QualifiedName = value.QualifiedName, RenderedSignature = value.RenderedSignature, Modifiers = value.Modifiers, LexicalParentOrdinal = value.LexicalParentOrdinal, SpanStartUtf16 = value.SpanStartUtf16, SpanLengthUtf16 = value.SpanLengthUtf16, SymbolFingerprint = value.SymbolFingerprint }));
            context.SourceProcessorCodeReferences.AddRange(completion.References.Select(value => new SourceProcessorCodeReferenceEntity { DocumentId = documentId.Value, Ordinal = value.Ordinal, RelationshipKindCode = value.RelationshipKindCode, SourceSymbolOrdinal = value.SourceSymbolOrdinal, TargetDisplay = value.TargetDisplay, SpanStartUtf16 = value.SpanStartUtf16, SpanLengthUtf16 = value.SpanLengthUtf16, ReferenceFingerprint = value.ReferenceFingerprint }));
            context.SourceProcessorCodeDiagnostics.AddRange(completion.Diagnostics.Select(value => new SourceProcessorCodeDiagnosticEntity { DocumentId = documentId.Value, Ordinal = value.Ordinal, DiagnosticId = value.DiagnosticId, Severity = checked((byte)value.SeverityCode), SpanStartUtf16 = value.SpanStartUtf16, SpanLengthUtf16 = value.SpanLengthUtf16, Representation = value.Withheld ? "withheld" : "scanned", ScannedMessage = value.ScannedMessage, WithheldReason = value.WithheldReason, DiagnosticFingerprint = value.DiagnosticFingerprint }));
        }
        else
        {
            context.SourceProcessorCodeBlockedDiagnostics.AddRange(completion.BlockedDiagnostics.Select(value => new SourceProcessorCodeBlockedDiagnosticEntity { SourceProcessorBranchId = claim.BranchId, SourceProcessorAttemptId = claim.AttemptId, Ordinal = value.Ordinal, DiagnosticId = value.DiagnosticId, Severity = checked((byte)value.SeverityCode), SpanStartUtf16 = value.SpanStartUtf16, SpanLengthUtf16 = value.SpanLengthUtf16, Representation = value.Withheld ? "withheld" : "scanned", ScannedMessage = value.ScannedMessage, WithheldReason = value.WithheldReason, BlockedDiagnosticFingerprint = value.BlockedDiagnosticFingerprint }));
        }
        // Facts are flushed before the receipt so the receipt insert can be the
        // database-level closure point for row/count and identity triggers. The
        // surrounding serialisable transaction still makes the operation atomic.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.SourceProcessorCodeCompletionReceipts.Add(new SourceProcessorCodeCompletionReceiptEntity
        {
            SourceProcessorBranchId = claim.BranchId, SourceProcessorAttemptId = claim.AttemptId, SourceRevisionId = claim.SourceRevisionId.Value, ActivityKind = (int)SourceActivityKind.CodeParsing,
            ProcessorVersion = completion.ProcessorVersion, DescriptorFingerprint = completion.DescriptorFingerprint, ParserFingerprint = completion.ParserFingerprint, RetainedArtifactSha256 = completion.RetainedArtifactSha256,
            HandlerImplementationId = RetainedCsharpCodeProcessor.HandlerImplementationId, OutcomeCode = completion.OutcomeCode, DocumentId = documentId, DocumentFingerprint = completion.DocumentFingerprint,
            CompletionFingerprint = completion.OutcomeCode == "success" ? completion.CompletionFingerprint! : completion.BlockedCompletionFingerprint!, WithheldSymbolCount = completion.WithheldSymbolCount,
            WithheldReferenceCount = completion.WithheldReferenceCount, WithheldDiagnosticCount = completion.WithheldDiagnosticCount, BlockedDiagnosticsCount = completion.BlockedDiagnostics.Count,
            ReceiptDiagnosticCodeCount = completion.ReceiptDiagnosticCodes.Count, ReceiptDiagnosticCodesWire = codesWire, CreatedAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var terminalState = completion.OutcomeCode == "success" ? RetainedProcessorBranchState.Completed : RetainedProcessorBranchState.Blocked;
        var terminalFingerprint = completion.OutcomeCode == "success" ? completion.CompletionFingerprint! : completion.BlockedCompletionFingerprint!;
        if (await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [SourceProcessorBranches] SET [State] = {(int)terminalState}, [CompletionReceiptFingerprint] = {terminalFingerprint}, [CompletedMemberCount] = 0, [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL, [UpdatedAtUtc] = {now} WHERE [Id] = {claim.BranchId} AND [LeaseOwner] = {claim.LeaseOwner} AND [LeaseGeneration] = {claim.LeaseGeneration} AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AND [State] = {(int)RetainedProcessorBranchState.Running};", cancellationToken).ConfigureAwait(false) != 1)
        {
            return new RetainedCsharpCodeCompletionWriteResult(false, false, "processor-fence-invalid", null);
        }
        attempt.FinishedAtUtc = now;
        attempt.OutcomeCode = completion.OutcomeCode;
        activity.State = completion.OutcomeCode == "success" ? (int)SourceActivityState.Completed : (int)SourceActivityState.FailedTerminal;
        activity.Reason = completion.OutcomeCode == "success" ? null : completion.OutcomeCode;
        activity.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetainedCsharpCodeCompletionWriteResult(true, false, completion.OutcomeCode, completion.OutcomeCode == "success" ? completion.CompletionFingerprint : completion.BlockedCompletionFingerprint);
    }

    private static void ValidateCsharpCompletion(RetainedCsharpCodeClaim claim, RetainedCsharpCodeCompletion completion)
    {
        const string withheldReason = "secret-content-withheld";
        if (completion.BranchId != claim.BranchId || completion.SourceRevisionId != claim.SourceRevisionId || completion.AttemptId != claim.AttemptId ||
            !string.Equals(completion.RetainedArtifactSha256, claim.InputSha256, StringComparison.Ordinal) || !string.Equals(completion.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal) || completion.LeaseGeneration != claim.LeaseGeneration ||
            !string.Equals(completion.ProcessorVersion, RetainedCsharpCodeProcessor.ProcessorVersion, StringComparison.Ordinal) || !string.Equals(completion.DescriptorFingerprint, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) || !string.Equals(completion.ParserFingerprint, RetainedCsharpCodeProcessor.ParserFingerprint, StringComparison.Ordinal)) throw new ArgumentException("The C# completion does not match its durable claim.", nameof(completion));
        if (completion.Symbols is null || completion.References is null || completion.Diagnostics is null ||
            completion.BlockedDiagnostics is null || completion.ReceiptDiagnosticCodes is null)
        {
            throw new ArgumentException("The C# completion collections are required.", nameof(completion));
        }
        var success = completion.OutcomeCode == "success";
        var blocked = completion.OutcomeCode == "csharp-code-syntax-invalid";
        if (!success && !blocked ||
            completion.ReceiptDiagnosticCodes.Count > RetainedCsharpCodeProcessor.MaximumDiagnostics ||
            completion.WithheldSymbolCount < 0 || completion.WithheldReferenceCount < 0 || completion.WithheldDiagnosticCount < 0 ||
            completion.Symbols.Count + completion.WithheldSymbolCount > RetainedCsharpCodeProcessor.MaximumSymbols ||
            completion.References.Count + completion.WithheldReferenceCount > RetainedCsharpCodeProcessor.MaximumReferences)
        {
            throw new ArgumentException("The C# completion has an invalid terminal contract.", nameof(completion));
        }

        if (success)
        {
            if (!IsCanonicalSha256(completion.DocumentFingerprint) || !IsCanonicalSha256(completion.CompletionFingerprint) ||
                completion.BlockedCompletionFingerprint is not null || completion.BlockedDiagnostics.Count != 0 ||
                completion.DecodedCharacterCount < 0 || completion.DecodedCharacterCount > RetainedCsharpCodeProcessor.MaximumDecodedUtf16CodeUnits ||
                completion.LineCount < 1 || completion.LineCount > completion.DecodedCharacterCount + 1 ||
                completion.Diagnostics.Count > RetainedCsharpCodeProcessor.MaximumDiagnostics ||
                completion.ReceiptDiagnosticCodes.Count != completion.Diagnostics.Count ||
                completion.WithheldDiagnosticCount != completion.Diagnostics.Count(value => value.Withheld) ||
                !completion.ReceiptDiagnosticCodes.SequenceEqual(completion.Diagnostics.Select(value => value.DiagnosticId), StringComparer.Ordinal))
            {
                throw new ArgumentException("A successful C# completion requires a self-consistent document fact set.", nameof(completion));
            }

            var expectedDocumentFingerprint = RetainedCsharpCodeProcessor.ComputeDocumentFingerprint(
                completion.SourceRevisionId,
                completion.RetainedArtifactSha256,
                completion.ParserFingerprint);
            if (!string.Equals(completion.DocumentFingerprint, expectedDocumentFingerprint, StringComparison.Ordinal))
            {
                throw new ArgumentException("The C# document fingerprint does not authenticate its identity.", nameof(completion));
            }

            for (var ordinal = 0; ordinal < completion.Symbols.Count; ordinal++)
            {
                var symbol = completion.Symbols[ordinal];
                var fingerprint = RetainedCsharpCodeProcessor.ComputeSymbolFingerprint(
                    completion.DocumentFingerprint!, symbol.Ordinal, symbol.DeclarationKindCode, symbol.LocalName,
                    symbol.QualifiedName, symbol.RenderedSignature, symbol.Modifiers, symbol.LexicalParentOrdinal,
                    symbol.SpanStartUtf16, symbol.SpanLengthUtf16);
                if (symbol.Ordinal != ordinal || symbol.DeclarationKindCode is < 1 or > 20 ||
                    symbol.LocalName.Length > RetainedCsharpCodeProcessor.MaximumIdentifierUtf16CodeUnits ||
                    symbol.QualifiedName.Length > RetainedCsharpCodeProcessor.MaximumSignatureUtf16CodeUnits ||
                    symbol.RenderedSignature.Length > RetainedCsharpCodeProcessor.MaximumSignatureUtf16CodeUnits ||
                    symbol.LexicalParentOrdinal < -1 || symbol.LexicalParentOrdinal >= symbol.Ordinal ||
                    symbol.SpanStartUtf16 < 0 || symbol.SpanLengthUtf16 < 0 ||
                    !string.Equals(symbol.SymbolFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A C# symbol fact is not canonical.", nameof(completion));
                }
            }

            for (var ordinal = 0; ordinal < completion.References.Count; ordinal++)
            {
                var reference = completion.References[ordinal];
                var fingerprint = RetainedCsharpCodeProcessor.ComputeReferenceFingerprint(
                    completion.DocumentFingerprint!, reference.Ordinal, reference.RelationshipKindCode,
                    reference.SourceSymbolOrdinal, reference.TargetDisplay, reference.SpanStartUtf16,
                    reference.SpanLengthUtf16);
                if (reference.Ordinal != ordinal || reference.RelationshipKindCode is < 1 or > 7 ||
                    reference.SourceSymbolOrdinal is < 0 || reference.SourceSymbolOrdinal >= completion.Symbols.Count ||
                    reference.TargetDisplay.Length > RetainedCsharpCodeProcessor.MaximumSignatureUtf16CodeUnits ||
                    reference.SpanStartUtf16 < 0 || reference.SpanLengthUtf16 < 0 ||
                    !string.Equals(reference.ReferenceFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A C# reference fact is not canonical.", nameof(completion));
                }
            }

            for (var ordinal = 0; ordinal < completion.Diagnostics.Count; ordinal++)
            {
                var diagnostic = completion.Diagnostics[ordinal];
                var fingerprint = RetainedCsharpCodeProcessor.ComputeDiagnosticFingerprint(
                    completion.DocumentFingerprint!, diagnostic.Ordinal, diagnostic.DiagnosticId,
                    diagnostic.SeverityCode, diagnostic.SpanStartUtf16, diagnostic.SpanLengthUtf16,
                    diagnostic.ScannedMessage, diagnostic.WithheldReason);
                if (diagnostic.Ordinal != ordinal || diagnostic.SeverityCode is < 0 or > 3 ||
                    diagnostic.DiagnosticId.Length is 0 or > 64 || diagnostic.SpanStartUtf16 < 0 ||
                    diagnostic.SpanLengthUtf16 < 0 || !HasValidDiagnosticRepresentation(
                        diagnostic.Withheld, diagnostic.ScannedMessage, diagnostic.WithheldReason, withheldReason) ||
                    !string.Equals(diagnostic.DiagnosticFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A C# diagnostic fact is not canonical.", nameof(completion));
                }
            }

            var expectedCompletionFingerprint = RetainedCsharpCodeProcessor.ComputeCompletionFingerprint(
                completion.DocumentFingerprint!, completion.ParserFingerprint,
                completion.Symbols.Select(value => value.SymbolFingerprint),
                completion.References.Select(value => value.ReferenceFingerprint),
                completion.Diagnostics.Select(value => value.DiagnosticFingerprint),
                completion.WithheldSymbolCount, completion.WithheldReferenceCount,
                completion.WithheldDiagnosticCount, completion.ReceiptDiagnosticCodes);
            if (!string.Equals(completion.CompletionFingerprint, expectedCompletionFingerprint, StringComparison.Ordinal))
            {
                throw new ArgumentException("The C# completion fingerprint does not authenticate its facts.", nameof(completion));
            }
        }

        if (blocked)
        {
            if (completion.DocumentFingerprint is not null || completion.CompletionFingerprint is not null ||
                !IsCanonicalSha256(completion.BlockedCompletionFingerprint) || completion.Symbols.Count != 0 ||
                completion.References.Count != 0 || completion.Diagnostics.Count != 0 ||
                completion.WithheldSymbolCount != 0 || completion.WithheldReferenceCount != 0 ||
                completion.DecodedCharacterCount != 0 || completion.LineCount != 0 ||
                completion.BlockedDiagnostics.Count > RetainedCsharpCodeProcessor.MaximumDiagnostics ||
                completion.ReceiptDiagnosticCodes.Count != completion.BlockedDiagnostics.Count ||
                completion.WithheldDiagnosticCount != completion.BlockedDiagnostics.Count(value => value.Withheld) ||
                !completion.ReceiptDiagnosticCodes.SequenceEqual(completion.BlockedDiagnostics.Select(value => value.DiagnosticId), StringComparer.Ordinal))
            {
                throw new ArgumentException("A syntax-invalid completion requires only self-consistent attempt-owned diagnostics.", nameof(completion));
            }

            for (var ordinal = 0; ordinal < completion.BlockedDiagnostics.Count; ordinal++)
            {
                var diagnostic = completion.BlockedDiagnostics[ordinal];
                var fingerprint = RetainedCsharpCodeProcessor.ComputeBlockedDiagnosticFingerprint(
                    completion.SourceRevisionId, completion.RetainedArtifactSha256, diagnostic.Ordinal,
                    diagnostic.DiagnosticId, diagnostic.SeverityCode, diagnostic.SpanStartUtf16,
                    diagnostic.SpanLengthUtf16, diagnostic.ScannedMessage, diagnostic.WithheldReason);
                if (diagnostic.BranchId != claim.BranchId || diagnostic.AttemptId != claim.AttemptId ||
                    diagnostic.Ordinal != ordinal || diagnostic.SeverityCode is < 0 or > 3 ||
                    diagnostic.DiagnosticId.Length is 0 or > 64 || diagnostic.SpanStartUtf16 < 0 ||
                    diagnostic.SpanLengthUtf16 < 0 || !HasValidDiagnosticRepresentation(
                        diagnostic.Withheld, diagnostic.ScannedMessage, diagnostic.WithheldReason, withheldReason) ||
                    !string.Equals(diagnostic.BlockedDiagnosticFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A blocked C# diagnostic fact is not canonical.", nameof(completion));
                }
            }

            var expectedBlockedFingerprint = RetainedCsharpCodeProcessor.ComputeBlockedCompletionFingerprint(
                completion.SourceRevisionId, completion.RetainedArtifactSha256,
                completion.BlockedDiagnostics.Select(value => value.BlockedDiagnosticFingerprint),
                completion.WithheldDiagnosticCount, completion.ReceiptDiagnosticCodes);
            if (!string.Equals(completion.BlockedCompletionFingerprint, expectedBlockedFingerprint, StringComparison.Ordinal))
            {
                throw new ArgumentException("The blocked C# completion fingerprint does not authenticate its diagnostics.", nameof(completion));
            }
        }
    }

    private static bool IsCanonicalCsharpCompletion(
        RetainedCsharpCodeClaim claim,
        RetainedCsharpCodeCompletion completion)
    {
        try
        {
            ValidateCsharpCompletion(claim, completion);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasValidDiagnosticRepresentation(
        bool withheld,
        string? scannedMessage,
        string? persistedWithheldReason,
        string expectedWithheldReason) =>
        withheld
            ? scannedMessage is null && string.Equals(persistedWithheldReason, expectedWithheldReason, StringComparison.Ordinal)
            : scannedMessage is not null && scannedMessage.Length <= RetainedCsharpCodeProcessor.MaximumDiagnosticMessageUtf16CodeUnits && persistedWithheldReason is null;

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string EncodeReceiptDiagnosticCodes(IReadOnlyList<string> codes) => $"{codes.Count};" + string.Concat(codes.Select(value => $"{System.Text.Encoding.UTF8.GetByteCount(value)}:{value}"));

    private static async ValueTask<bool> IsExactCsharpReplayAsync(FluxKnowledgeDbContext context, SourceProcessorCodeCompletionReceiptEntity receipt, RetainedCsharpCodeCompletion completion, CancellationToken cancellationToken)
    {
        if (completion.Symbols is null || completion.References is null || completion.Diagnostics is null ||
            completion.BlockedDiagnostics is null || completion.ReceiptDiagnosticCodes is null ||
            completion.ReceiptDiagnosticCodes.Any(value => value is null))
        {
            return false;
        }

        var expectedFingerprint = completion.OutcomeCode == "success" ? completion.CompletionFingerprint : completion.BlockedCompletionFingerprint;
        if (receipt.SourceRevisionId != completion.SourceRevisionId.Value || receipt.ActivityKind != (int)SourceActivityKind.CodeParsing || !string.Equals(receipt.ProcessorVersion, completion.ProcessorVersion, StringComparison.Ordinal) || !string.Equals(receipt.DescriptorFingerprint, completion.DescriptorFingerprint, StringComparison.Ordinal) || !string.Equals(receipt.ParserFingerprint, completion.ParserFingerprint, StringComparison.Ordinal) || !string.Equals(receipt.RetainedArtifactSha256, completion.RetainedArtifactSha256, StringComparison.Ordinal) || !string.Equals(receipt.HandlerImplementationId, RetainedCsharpCodeProcessor.HandlerImplementationId, StringComparison.Ordinal) || !string.Equals(receipt.OutcomeCode, completion.OutcomeCode, StringComparison.Ordinal) || !string.Equals(receipt.CompletionFingerprint, expectedFingerprint, StringComparison.Ordinal) || receipt.WithheldSymbolCount != completion.WithheldSymbolCount || receipt.WithheldReferenceCount != completion.WithheldReferenceCount || receipt.WithheldDiagnosticCount != completion.WithheldDiagnosticCount || receipt.BlockedDiagnosticsCount != completion.BlockedDiagnostics.Count || receipt.ReceiptDiagnosticCodeCount != completion.ReceiptDiagnosticCodes.Count || !string.Equals(receipt.ReceiptDiagnosticCodesWire, EncodeReceiptDiagnosticCodes(completion.ReceiptDiagnosticCodes), StringComparison.Ordinal)) return false;
        if (completion.OutcomeCode == "success")
        {
            if (receipt.DocumentId is null || !string.Equals(receipt.DocumentFingerprint, completion.DocumentFingerprint, StringComparison.Ordinal)) return false;
            var document = await context.SourceProcessorCodeDocuments.AsNoTracking()
                .SingleOrDefaultAsync(value => value.SourceProcessorBranchId == receipt.DocumentId, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.SourceRevisionId != completion.SourceRevisionId.Value ||
                !string.Equals(document.RetainedArtifactSha256, completion.RetainedArtifactSha256, StringComparison.Ordinal) ||
                !string.Equals(document.DescriptorFingerprint, completion.DescriptorFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.ParserFingerprint, completion.ParserFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.HandlerImplementationId, RetainedCsharpCodeProcessor.HandlerImplementationId, StringComparison.Ordinal) ||
                document.DecodedCharacterCount != completion.DecodedCharacterCount || document.LineCount != completion.LineCount ||
                document.SymbolCount != completion.Symbols.Count || document.ReferenceCount != completion.References.Count ||
                document.DiagnosticsCount != completion.Diagnostics.Count || document.WithheldSymbolCount != completion.WithheldSymbolCount ||
                document.WithheldReferenceCount != completion.WithheldReferenceCount || document.WithheldDiagnosticCount != completion.WithheldDiagnosticCount ||
                document.ReceiptDiagnosticCodeCount != completion.ReceiptDiagnosticCodes.Count ||
                !string.Equals(document.DocumentFingerprint, completion.DocumentFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.CompletionFingerprint, completion.CompletionFingerprint, StringComparison.Ordinal)) return false;
            var symbols = await context.SourceProcessorCodeSymbols.AsNoTracking().Where(value => value.DocumentId == receipt.DocumentId).OrderBy(value => value.Ordinal).Select(value => value.SymbolFingerprint).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var references = await context.SourceProcessorCodeReferences.AsNoTracking().Where(value => value.DocumentId == receipt.DocumentId).OrderBy(value => value.Ordinal).Select(value => value.ReferenceFingerprint).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = await context.SourceProcessorCodeDiagnostics.AsNoTracking().Where(value => value.DocumentId == receipt.DocumentId).OrderBy(value => value.Ordinal).Select(value => value.DiagnosticFingerprint).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return symbols.SequenceEqual(completion.Symbols.OrderBy(value => value.Ordinal).Select(value => value.SymbolFingerprint)) && references.SequenceEqual(completion.References.OrderBy(value => value.Ordinal).Select(value => value.ReferenceFingerprint)) && diagnostics.SequenceEqual(completion.Diagnostics.OrderBy(value => value.Ordinal).Select(value => value.DiagnosticFingerprint));
        }
        if (receipt.DocumentId is not null) return false;
        var blocked = await context.SourceProcessorCodeBlockedDiagnostics.AsNoTracking().Where(value => value.SourceProcessorBranchId == receipt.SourceProcessorBranchId && value.SourceProcessorAttemptId == receipt.SourceProcessorAttemptId).OrderBy(value => value.Ordinal).Select(value => value.BlockedDiagnosticFingerprint).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return blocked.SequenceEqual(completion.BlockedDiagnostics.OrderBy(value => value.Ordinal).Select(value => value.BlockedDiagnosticFingerprint));
    }

    public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) =>
        ClaimAsync(leaseOwner, maximumCount, null, cancellationToken);

    public async ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, string? processorFingerprint, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var expiry = now.AddMinutes(5);
        var branches = await context.SourceProcessorBranches
            .FromSqlInterpolated($"""
                SELECT *
                FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
                WHERE ([State] = {(int)RetainedProcessorBranchState.Pending}
                   OR ([State] = {(int)RetainedProcessorBranchState.Running} AND [LeaseExpiresAtUtc] < TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')))
                  AND ({processorFingerprint} IS NULL OR [ProcessorFingerprint] = {processorFingerprint})
                  AND [ProcessorFingerprint] <> {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM [SourceProcessorForceRequests] AS [force] WITH (UPDLOCK, HOLDLOCK)
                      WHERE [force].[SourceProcessorBranchId] = [SourceProcessorBranches].[Id]
                        AND [force].[State] IN ({(byte)OoxmlForceRequestState.Requested}, {(byte)OoxmlForceRequestState.Claimed}))
                  AND EXISTS (
                      SELECT 1
                      FROM [SourceActivities] AS [activity] WITH (UPDLOCK, HOLDLOCK)
                      WHERE [activity].[Id] = [SourceProcessorBranches].[SourceActivityId]
                        AND [activity].[ActivityKind] <> {(int)SourceActivityKind.CodeParsing}
                        AND [activity].[State] <> {(int)SourceActivityState.CancelledSuperseded})
                """)
            .OrderBy(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id)
            .Take(Math.Clamp(maximumCount, 1, RetainedProcessorOptions.MaximumAutomaticReplayBatchSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var branch in branches)
        {
            if (branch.State == (int)RetainedProcessorBranchState.Running && branch.LeaseExpiresAtUtc < now)
            {
                if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [SourceProcessorAttempts]
                        SET [FinishedAtUtc] = {now}, [OutcomeCode] = {"lease-expired-reconciled"}
                        WHERE [BranchId] = {branch.Id}
                          AND [LeaseGeneration] = {branch.LeaseGeneration}
                          AND [FinishedAtUtc] IS NULL;
                        """, cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The expired retained processor attempt is missing or already finalised.");
                }
            }
            branch.State = (int)RetainedProcessorBranchState.Running; branch.LeaseOwner = leaseOwner; branch.LeaseExpiresAtUtc = expiry; branch.LeaseGeneration++; branch.AttemptCount++; branch.UpdatedAtUtc = now;
            context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity { Id = Guid.NewGuid(), BranchId = branch.Id, LeaseGeneration = branch.LeaseGeneration, StartedAtUtc = now }); }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var revisions = await context.SourceRevisions.AsNoTracking().Where(value => branches.Select(branch => branch.SourceRevisionId).Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        return branches.Select(branch => new RetainedProcessorClaim(branch.Id, new SourceRevisionId(branch.SourceRevisionId), revisions[branch.SourceRevisionId].StableSourceIdentity,
            branch.InputSha256, leaseOwner, branch.LeaseGeneration, expiry)).ToArray();
    }

    public async ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var forceRequest = await LockClaimedForceRequestAsync(context, claim, cancellationToken).ConfigureAwait(false);
        var branch = await context.SourceProcessorBranches
            .FromSqlInterpolated($"""
                SELECT *
                FROM [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {claim.BranchId}
                  AND [LeaseOwner] = {claim.LeaseOwner}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                  AND [State] = {(int)RetainedProcessorBranchState.Running}
                  AND [ProcessorFingerprint] <> {RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint}
                  AND EXISTS (
                      SELECT 1 FROM [SourceActivities] AS [activity] WITH (UPDLOCK, HOLDLOCK)
                      WHERE [activity].[Id] = [SourceProcessorBranches].[SourceActivityId]
                        AND [activity].[ActivityKind] <> {(int)SourceActivityKind.CodeParsing})
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branch is null) return false;
        var parent = await context.SourceRevisions.SingleAsync(value => value.Id == branch.SourceRevisionId, cancellationToken).ConfigureAwait(false);
        foreach (var member in completion.Members)
        {
            var child = await context.SourceRevisions.SingleOrDefaultAsync(value =>
                value.ParentSourceRevisionId == parent.Id && value.StableSourceIdentity == member.StableSourceIdentity, cancellationToken).ConfigureAwait(false);
            if (child is null)
            {
                child = new SourceRevisionEntity
                {
                    Id = Guid.NewGuid(), SourceRootId = parent.SourceRootId, StableSourceIdentity = member.StableSourceIdentity,
                    Revision = 1, ContentSha256 = member.ContentSha256,
                    CanonicalPath = member.SyntheticLocator,
                    ParentSourceRevisionId = parent.Id, Classification = member.Classification, Extension = member.Extension, OriginKind = member.OriginKind,
                    ByteLength = member.ByteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
                };
                context.SourceRevisions.Add(child);
                context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = child.Id, ContentSha256 = member.ContentSha256,
                    StoreRelativePath = member.StoreRelativePath, ByteLength = member.ByteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
            }
            var childActivity = await context.SourceActivities.SingleOrDefaultAsync(value => value.SourceRevisionId == child.Id &&
                value.ActivityKind == (int)SourceActivityKind.TextExtraction && value.ProcessorVersion == "phase-3a-v1" && value.InputFingerprint == member.ContentSha256, cancellationToken).ConfigureAwait(false);
            if (childActivity is null)
            {
                childActivity = new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = child.Id, ActivityKind = (int)SourceActivityKind.TextExtraction,
                    ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-3a-v1", InputFingerprint = member.ContentSha256,
                    State = (int)SourceActivityState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now };
                context.SourceActivities.Add(childActivity);
            }
            var persistedMember = await context.SourceProcessorBranchMembers.SingleOrDefaultAsync(value => value.BranchId == branch.Id && value.MemberFingerprint == member.MemberFingerprint, cancellationToken).ConfigureAwait(false);
            if (persistedMember is null)
            {
                context.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branch.Id, MemberFingerprint = member.MemberFingerprint,
                    ChildSourceRevisionId = child.Id, ChildSourceActivityId = childActivity.Id, Disposition = "completed", ByteLength = member.ByteLength, CreatedAtUtc = now });
            }
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (await context.SourceProcessorBranchMembers.CountAsync(value => value.BranchId == branch.Id &&
                (value.Disposition != "completed" || value.ChildSourceRevisionId == null || value.ChildSourceActivityId == null), cancellationToken).ConfigureAwait(false) != 0 ||
            await context.SourceProcessorBranchMembers.CountAsync(value => value.BranchId == branch.Id, cancellationToken).ConfigureAwait(false) != completion.Members.Count)
        {
            throw new InvalidOperationException("A retained processor completion requires every member to have a child disposition.");
        }
        if (forceRequest is not null)
        {
            AddOperatorActionEvent(context, forceRequest, now, "completed", "completed");
        }
        else
        {
            OperatorEventAppender.Add(context, new OperatorEventDraft("retained_processor.completed", "retained_processor", "information", "retained-processor",
                now, SourceRootId: parent.SourceRootId, SourceRevisionId: parent.Id, SourceActivityId: branch.SourceActivityId,
                CorrelationId: $"retained-processor:{branch.Id:N}", Details: new { kind = EventKind(branch.ProcessorFingerprint), reasonCode = "completed" }));
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorBranches]
                SET [State] = {(int)RetainedProcessorBranchState.Completed},
                    [CompletionReceiptFingerprint] = {completion.ReceiptFingerprint},
                    [CompletedMemberCount] = {completion.Members.Count},
                    [LeaseOwner] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [UpdatedAtUtc] = {now}
                WHERE [Id] = {claim.BranchId}
                  AND [LeaseOwner] = {claim.LeaseOwner}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                  AND [State] = {(int)RetainedProcessorBranchState.Running};
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK)
                SET [FinishedAtUtc] = {now}, [OutcomeCode] = {"completed"}
                WHERE [BranchId] = {claim.BranchId}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [FinishedAtUtc] IS NULL;
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The retained processor completion attempt is missing or already finalised.");
        }
        if (forceRequest is not null)
        {
            forceRequest.State = (byte)OoxmlForceRequestState.Completed;
            forceRequest.TerminalAtUtc = now;
            forceRequest.TerminalReceiptFingerprint = completion.ReceiptFingerprint;
            forceRequest.TerminalReasonCode = "completed";
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var forceRequest = await LockClaimedForceRequestAsync(context, claim, cancellationToken).ConfigureAwait(false);
        var processorFingerprint = await context.SourceProcessorBranches.AsNoTracking().Where(value => value.Id == claim.BranchId)
            .Select(value => value.ProcessorFingerprint).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
                SET [State] = {(int)RetainedProcessorBranchState.Blocked},
                    [LeaseOwner] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [UpdatedAtUtc] = {now}
                WHERE [Id] = {claim.BranchId}
                  AND [LeaseOwner] = {claim.LeaseOwner}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                  AND [State] = {(int)RetainedProcessorBranchState.Running};
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }
        foreach (var outcome in failure.MemberOutcomes)
        {
            var existing = await context.SourceProcessorBranchMembers.SingleOrDefaultAsync(value =>
                value.BranchId == claim.BranchId && value.MemberFingerprint == outcome.MemberFingerprint, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                context.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
                {
                    Id = Guid.NewGuid(), BranchId = claim.BranchId, MemberFingerprint = outcome.MemberFingerprint,
                    Disposition = outcome.Disposition, ReasonCode = outcome.ReasonCode, ByteLength = outcome.ByteLength, CreatedAtUtc = now
                });
            }
            else if (!string.Equals(existing.Disposition, outcome.Disposition, StringComparison.Ordinal) ||
                     !string.Equals(existing.ReasonCode, outcome.ReasonCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A retained processor member outcome conflicts with its durable disposition.");
            }
        }
        if (forceRequest is not null)
        {
            AddOperatorActionEvent(context, forceRequest, now, "blocked", failure.OutcomeCode);
        }
        else
        {
            var rootId = await context.SourceRevisions.AsNoTracking().Where(value => value.Id == claim.SourceRevisionId.Value)
                .Select(value => value.SourceRootId).SingleAsync(cancellationToken).ConfigureAwait(false);
            OperatorEventAppender.Add(context, new OperatorEventDraft("retained_processor.blocked", "retained_processor", "warning", "retained-processor",
                now, SourceRootId: rootId, SourceRevisionId: claim.SourceRevisionId.Value, CorrelationId: $"retained-processor:{claim.BranchId:N}",
                Details: new { kind = EventKind(processorFingerprint), reasonCode = failure.OutcomeCode }));
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK)
                SET [FinishedAtUtc] = {now}, [OutcomeCode] = {failure.OutcomeCode}
                WHERE [BranchId] = {claim.BranchId}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [FinishedAtUtc] IS NULL;
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The retained processor failure attempt is missing or already finalised.");
        }
        if (forceRequest is not null)
        {
            forceRequest.State = (byte)OoxmlForceRequestState.Blocked;
            forceRequest.TerminalAtUtc = now;
            forceRequest.TerminalReceiptFingerprint = OoxmlForceRequestIdentity.CreateTerminalReceiptFingerprint(forceRequest.Id, failure.OutcomeCode);
            forceRequest.TerminalReasonCode = failure.OutcomeCode;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseUtcNowAsync(context, cancellationToken).ConfigureAwait(false);
        var forceRequest = await LockClaimedForceRequestAsync(context, claim, cancellationToken).ConfigureAwait(false);
        var processorFingerprint = await context.SourceProcessorBranches.AsNoTracking().Where(value => value.Id == claim.BranchId)
            .Select(value => value.ProcessorFingerprint).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorBranches] WITH (UPDLOCK, HOLDLOCK)
                SET [State] = {(int)RetainedProcessorBranchState.Pending},
                    [LeaseOwner] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [UpdatedAtUtc] = {now}
                WHERE [Id] = {claim.BranchId}
                  AND [LeaseOwner] = {claim.LeaseOwner}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [LeaseExpiresAtUtc] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                  AND [State] = {(int)RetainedProcessorBranchState.Running};
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }
        if (forceRequest is not null)
        {
            AddOperatorActionEvent(context, forceRequest, now, "transient", "force-request-transient");
        }
        else
        {
            var rootId = await context.SourceRevisions.AsNoTracking().Where(value => value.Id == claim.SourceRevisionId.Value)
                .Select(value => value.SourceRootId).SingleAsync(cancellationToken).ConfigureAwait(false);
            OperatorEventAppender.Add(context, new OperatorEventDraft("retained_processor.retry_scheduled", "retained_processor", "information", "retained-processor",
                now, SourceRootId: rootId, SourceRevisionId: claim.SourceRevisionId.Value, CorrelationId: $"retained-processor:{claim.BranchId:N}",
                Details: new { kind = EventKind(processorFingerprint), reasonCode = outcomeCode }));
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceProcessorAttempts] WITH (UPDLOCK, HOLDLOCK)
                SET [FinishedAtUtc] = {now}, [OutcomeCode] = {outcomeCode}
                WHERE [BranchId] = {claim.BranchId}
                  AND [LeaseGeneration] = {claim.LeaseGeneration}
                  AND [FinishedAtUtc] IS NULL;
                """, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The retained processor retry attempt is missing or already finalised.");
        }
        if (forceRequest is not null)
        {
            forceRequest.State = (byte)OoxmlForceRequestState.Transient;
            forceRequest.TerminalAtUtc = now;
            forceRequest.TerminalReceiptFingerprint = OoxmlForceRequestIdentity.CreateTerminalReceiptFingerprint(forceRequest.Id, "force-request-transient");
            forceRequest.TerminalReasonCode = "force-request-transient";
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string EventKind(string? processorFingerprint) => processorFingerprint switch
    {
        "phase-5-zip-retained-archive-v1" => "archive_zip",
        "phase-5-tar-retained-archive-v1" => "archive_tar",
        "phase-5-ooxml-retained-structural-v1" => "document_ooxml",
        "phase-5-media-metadata-retained-v1" => "media_metadata",
        _ => "retained_processor"
    };

    private static async ValueTask<DateTimeOffset> DatabaseUtcNowAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]")
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    private static async ValueTask<SourceProcessorForceRequestEntity?> LockClaimedForceRequestAsync(
        FluxKnowledgeDbContext context,
        RetainedProcessorClaim claim,
        CancellationToken cancellationToken) =>
        await context.SourceProcessorForceRequests
            .FromSqlInterpolated($"""
                SELECT *
                FROM [SourceProcessorForceRequests] WITH (UPDLOCK, HOLDLOCK)
                WHERE [State] = {(byte)OoxmlForceRequestState.Claimed}
                  AND [ForceAttemptBranchId] = {claim.BranchId}
                  AND [ForceAttemptLeaseGeneration] = {claim.LeaseGeneration}
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static async ValueTask<bool> IsOoxmlDescriptorRunnableAsync(
        FluxKnowledgeDbContext context,
        SourceProcessorForceRequestEntity request,
        CancellationToken cancellationToken)
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;
        return await context.SourceCapabilities.AsNoTracking().AnyAsync(value =>
            value.Id == request.DescriptorId && value.Id == descriptor.Id &&
            value.ProcessorKind == descriptor.ProcessorKind && value.ProcessorVersion == descriptor.ProcessorVersion &&
            value.ProcessorFingerprint == request.DescriptorFingerprint && value.ProcessorFingerprint == descriptor.ProcessorFingerprint &&
            value.ExecutionClass == (int)ExecutionClass.InProcess && value.OutputContract == descriptor.OutputContract &&
             value.AcceptedClassificationsJson == "[\"OoxmlDocumentContainer\"]" && value.IsRunnable, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> IsForceRequestPolicyCurrentAsync(
        FluxKnowledgeDbContext context,
        SourceProcessorForceRequestEntity request,
        CancellationToken cancellationToken)
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;
        if (request.DescriptorId != descriptor.Id ||
            !string.Equals(request.DescriptorFingerprint, descriptor.ProcessorFingerprint, StringComparison.Ordinal) ||
            !string.Equals(request.DescriptorVersion, descriptor.ProcessorVersion, StringComparison.Ordinal) ||
            !string.Equals(request.SafetyContractId, RetainedBindingSafetyContract, StringComparison.Ordinal) ||
            !string.Equals(request.HandlerId, RetainedBranchStoreHandler, StringComparison.Ordinal) ||
            request.ActionKind is not ("retry" or "policy-override") ||
            !string.Equals(request.PolicyReasonCode, request.OriginalOutcomeCode, StringComparison.Ordinal))
        {
            return false;
        }

        var exactCurrentPolicy = await context.OperatorActionCapabilityPolicies.AsNoTracking().AnyAsync(policy =>
                policy.PolicyId == request.PolicyId &&
                policy.PolicyRevision == request.PolicyRevision &&
                policy.DescriptorId == descriptor.Id &&
                policy.DescriptorFingerprint == descriptor.ProcessorFingerprint &&
                policy.DescriptorVersion == descriptor.ProcessorVersion &&
                policy.SafetyContractId == RetainedBindingSafetyContract &&
                policy.HandlerId == RetainedBranchStoreHandler &&
                policy.ActionKind == request.ActionKind &&
                policy.ReasonCode == request.OriginalOutcomeCode &&
                !context.OperatorActionHardDenials.Any(denial => denial.ReasonCode == request.OriginalOutcomeCode), cancellationToken)
            .ConfigureAwait(false);
        if (!exactCurrentPolicy || !await IsOoxmlDescriptorRunnableAsync(context, request, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await context.SourceProcessorAttempts.AsNoTracking().AnyAsync(attempt =>
            attempt.BranchId == request.SourceProcessorBranchId &&
            attempt.LeaseGeneration == request.OriginalBlockedLeaseGeneration &&
            attempt.FinishedAtUtc != null &&
            attempt.OutcomeCode == request.OriginalOutcomeCode, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> IsForceRequestBindingCurrentAsync(
        FluxKnowledgeDbContext context,
        SourceProcessorForceRequestEntity request,
        SourceProcessorBranchEntity branch,
        CancellationToken cancellationToken)
    {
        if (branch.Id != request.SourceProcessorBranchId || branch.SourceActivityId != request.SourceActivityId ||
            branch.SourceRevisionId != request.SourceRevisionId || branch.InputSha256 != request.ExpectedInputSha256 ||
            branch.ProcessorVersion != OoxmlStructuralTextProcessor.Capability.ProcessorVersion ||
            branch.ProcessorFingerprint != request.DescriptorFingerprint)
        {
            return false;
        }

        return await (from activity in context.SourceActivities.AsNoTracking()
                      join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                      join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
                      where activity.Id == request.SourceActivityId &&
                            activity.SourceRevisionId == request.SourceRevisionId &&
                            activity.State == (int)SourceActivityState.Pending &&
                            activity.ActivityKind == (int)SourceActivityKind.TextExtraction &&
                            activity.ExecutionClass == (int)ExecutionClass.InProcess &&
                            activity.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                            EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(request.ExpectedInputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                            EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(request.ExpectedInputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                            EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                                EF.Functions.Collate(request.ExpectedInputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                            context.SourceActivityRelations.Any(relation => relation.SuccessorActivityId == activity.Id)
                      select activity.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OoxmlForceRequestReceipt ToReceipt(
        SourceProcessorForceRequestEntity request,
        bool wasReplay,
        DateTimeOffset committedAtUtc) => new(
            request.Id, request.ActionId, request.OperationId, (OoxmlForceRequestState)request.State,
            request.TerminalReasonCode, request.ForceAttemptLeaseGeneration)
        {
            WasReplay = wasReplay,
            CommittedAtUtc = committedAtUtc
        };

    private static async ValueTask<List<ForceCandidate>> ReadCurrentOoxmlBlockedIgnoreCandidatesAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken)
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;
        return await (
            from branch in context.SourceProcessorBranches.AsNoTracking()
            join activity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals activity.Id
            join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
            join attempt in context.SourceProcessorAttempts.AsNoTracking() on new { BranchId = branch.Id, branch.LeaseGeneration }
                equals new { attempt.BranchId, attempt.LeaseGeneration }
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
            orderby branch.UpdatedAtUtc, branch.Id
            select new ForceCandidate(branch.Id, branch.SourceActivityId, branch.SourceRevisionId,
                branch.InputSha256, branch.LeaseGeneration, branch.RowVersion, attempt.OutcomeCode ?? string.Empty,
                false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<List<ForceCandidate>> ReadCurrentOoxmlBlockedCandidatesAsync(
        FluxKnowledgeDbContext context,
        int? maximumCount,
        string actionKind,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? candidateBranchIds = null)
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;
        var query =
            from branch in context.SourceProcessorBranches.AsNoTracking()
            join activity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals activity.Id
            join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            join attempt in context.SourceProcessorAttempts.AsNoTracking() on new { BranchId = branch.Id, branch.LeaseGeneration }
                equals new { attempt.BranchId, attempt.LeaseGeneration }
            join capability in context.SourceCapabilities.AsNoTracking() on descriptor.Id equals capability.Id
            where branch.State == (int)RetainedProcessorBranchState.Blocked &&
                  (candidateBranchIds == null || candidateBranchIds.Contains(branch.Id)) &&
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
                  capability.ProcessorKind == descriptor.ProcessorKind &&
                    capability.ProcessorVersion == descriptor.ProcessorVersion &&
                    capability.ExecutionClass == (int)ExecutionClass.InProcess &&
                    capability.ProcessorFingerprint == descriptor.ProcessorFingerprint &&
                    capability.OutputContract == descriptor.OutputContract &&
                    capability.AcceptedClassificationsJson == "[\"OoxmlDocumentContainer\"]" &&
                    capability.IsRunnable &&
                    !context.OperatorActionHardDenials.Any(denial => denial.ReasonCode == attempt.OutcomeCode) &&
                    context.OperatorActionCapabilityPolicies.Any(policy =>
                        policy.DescriptorId == descriptor.Id &&
                        EF.Functions.Collate(policy.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                            EF.Functions.Collate(descriptor.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                        policy.DescriptorVersion == descriptor.ProcessorVersion &&
                        policy.SafetyContractId == RetainedBindingSafetyContract &&
                        policy.HandlerId == RetainedBranchStoreHandler &&
                        policy.ActionKind == actionKind &&
                        policy.ReasonCode == attempt.OutcomeCode) &&
                   EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                  EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  context.SourceActivityRelations.Any(relation => relation.SuccessorActivityId == activity.Id)
            orderby branch.UpdatedAtUtc, branch.Id
            select new ForceCandidate(branch.Id, branch.SourceActivityId, branch.SourceRevisionId,
                branch.InputSha256, branch.LeaseGeneration, branch.RowVersion, attempt.OutcomeCode ?? string.Empty,
                true);
        if (maximumCount is { } maximum)
        {
            query = query.Take(maximum);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class CsharpClaimExecutionState
    {
        public IReadOnlyList<RetainedCsharpCodeClaim> AttemptedClaims { get; set; } = [];
    }

    private sealed record ForceCandidate(
        Guid BranchId,
        Guid SourceActivityId,
        Guid SourceRevisionId,
        string InputSha256,
        long LeaseGeneration,
        byte[] RowVersion,
        string OutcomeCode,
        bool IsForceable);
}
