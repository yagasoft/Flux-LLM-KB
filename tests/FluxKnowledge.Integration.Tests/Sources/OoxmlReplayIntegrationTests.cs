using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Disposable SQL proof that OOXML replay is retained-artifact-only and source-neutral.</summary>
public sealed class OoxmlReplayIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private const string ForceRetryReasonCode = "activation-force-retryable";
    private const string ForceOverrideReasonCode = "activation-force-policy-override";
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearOoxmlOperatorActionDataAsync(_fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Explicit_activation_replays_retained_docx_xlsx_and_pptx_from_every_source_family_without_the_missing_source_original()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-retained-{Guid.NewGuid():N}");
        var outlookPrivateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-outlook-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        Directory.CreateDirectory(outlookPrivateRoot);
        try
        {
            var seeds = new List<(Guid ActivityId, Guid RevisionId, string Text)>();
            foreach (var (extension, sourceKind, text) in new[]
            {
                (".docx", "watched-file", "word retained sentinel"),
                (".xlsx", "gmail", "sheet retained sentinel"),
                (".pptx", "imap", "slide retained sentinel"),
                (".docx", "outlook", "outlook retained sentinel")
            })
            {
                var bytes = CreateOfficePackage(extension, text);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
                var artifactRoot = sourceKind == "outlook" ? outlookPrivateRoot : privateRoot;
                Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
                await File.WriteAllBytesAsync(Path.Combine(artifactRoot, relativePath), bytes);
                var seeded = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, extension, sourceKind,
                    sourceKind == "outlook" ? outlookPrivateRoot : null);
                seeds.Add((seeded.ActivityId, seeded.RevisionId, text));
            }

            var first = await CreateActivation(privateRoot, ooxmlEnabled: true, outlookSpoolRoot: outlookPrivateRoot).RunOnceAsync(CancellationToken.None);
            var repeat = await CreateActivation(privateRoot, ooxmlEnabled: true, outlookSpoolRoot: outlookPrivateRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal("document-ooxml-structural-extract", first.Capability);
            Assert.Equal(4, first.CompletedBranches);
            Assert.Equal(0, repeat.CompletedBranches);
            await using var verification = CreateContext();
            foreach (var seed in seeds)
            {
                var predecessor = await verification.SourceActivities.SingleAsync(value => value.Id == seed.ActivityId);
                Assert.Equal((int)SourceActivityState.CancelledSuperseded, predecessor.State);
                var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seed.RevisionId);
                Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
                var child = await verification.SourceRevisions.SingleAsync(value => value.ParentSourceRevisionId == seed.RevisionId);
                Assert.Equal(2, child.OriginKind);
                Assert.StartsWith("retained-office-structural-segment:", child.CanonicalPath, StringComparison.Ordinal);
                Assert.Single(await verification.SourceActivities.Where(value => value.SourceRevisionId == child.Id).ToListAsync());
                if (seed.Text == "outlook retained sentinel")
                {
                    var childArtifact = await verification.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.Id);
                    Assert.True(File.Exists(Path.Combine(outlookPrivateRoot, childArtifact.StoreRelativePath)));
                    Assert.False(File.Exists(Path.Combine(privateRoot, childArtifact.StoreRelativePath)));
                }
            }
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
            if (Directory.Exists(outlookPrivateRoot)) Directory.Delete(outlookPrivateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Activation_claims_and_completes_a_requested_ooxml_force_retry_without_an_ordinary_claim()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-force-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateOfficePackage(".docx", "forced retained sentinel");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
            var seeded = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, ".docx", "force-activation");
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
                OoxmlStructuralTextProcessor.Capability,
                CancellationToken.None));
            await RegisterOoxmlCapabilityAsync();
            var blockedAttempt = Assert.Single(await store.ClaimAsync("force-preparation", 1,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
            Assert.True(await store.FailAsync(blockedAttempt, new RetainedProcessorFailure(ForceRetryReasonCode, []), CancellationToken.None));
            await SeedForcePolicyAsync(ForceRetryReasonCode, "retry");

            var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None));
            var request = await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None);

            var result = await CreateActivation(privateRoot, ooxmlEnabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.ClaimedBranches);
            Assert.Equal(1, result.CompletedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            var durableRequest = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            Assert.Equal((byte)OoxmlForceRequestState.Completed, durableRequest.State);
            Assert.Equal(2, branch.AttemptCount);
            Assert.Equal(2, await verification.SourceProcessorAttempts.CountAsync(value => value.BranchId == branch.Id));
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Activation_claims_and_completes_an_override_only_ooxml_request_with_terminal_fences()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-override-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateOfficePackage(".docx", "override retained sentinel");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
            var seeded = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, ".docx", "override-activation");
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
                OoxmlStructuralTextProcessor.Capability,
                CancellationToken.None));
            await RegisterOoxmlCapabilityAsync();
            var blockedAttempt = Assert.Single(await store.ClaimAsync("override-preparation", 1,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
            Assert.True(await store.FailAsync(blockedAttempt, new RetainedProcessorFailure(ForceOverrideReasonCode, []), CancellationToken.None));
            await SeedForcePolicyAsync(ForceOverrideReasonCode, "policy-override");

            await using var before = CreateContext();
            var blockedBranch = await before.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            var actionId = OoxmlForceRequestIdentity.CreateActionId(blockedBranch.Id, OoxmlStructuralTextProcessor.Capability.Id,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, blockedBranch.RowVersion);
            var blockedRowVersionToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(blockedBranch.RowVersion);
            var requestFingerprint = OoxmlForceRequestIdentity.CreateRequestFingerprint(actionId, blockedRowVersionToken);
            Assert.Empty(await before.OperatorActionCapabilityPolicies.Where(value =>
                value.ActionKind == "retry" && value.ReasonCode == ForceOverrideReasonCode).ToListAsync());

            var request = await store.RequestPolicyOverrideAsync(
                new OoxmlForceRequestCommand(actionId, Guid.NewGuid(), requestFingerprint, blockedRowVersionToken),
                CancellationToken.None);
            var result = await CreateActivation(privateRoot, ooxmlEnabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.ClaimedBranches);
            Assert.Equal(1, result.CompletedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
            var durableRequest = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
            var durableAction = await verification.OperatorActionActionLedger.SingleAsync(value => value.ActionId == actionId);
            var terminalAttempt = await verification.SourceProcessorAttempts.SingleAsync(value =>
                value.BranchId == branch.Id && value.LeaseGeneration == durableRequest.ForceAttemptLeaseGeneration);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            Assert.Equal("policy-override", durableRequest.ActionKind);
            Assert.Equal("policy-override", durableAction.ActionKind);
            Assert.Equal((byte)OoxmlForceRequestState.Completed, durableRequest.State);
            Assert.Equal("completed", durableRequest.TerminalReasonCode);
            Assert.NotNull(durableRequest.TerminalAtUtc);
            Assert.Equal(branch.Id, durableRequest.ForceAttemptBranchId);
            Assert.Equal(branch.LeaseGeneration, durableRequest.ForceAttemptLeaseGeneration);
            Assert.Equal(branch.CompletionReceiptFingerprint, durableRequest.TerminalReceiptFingerprint);
            Assert.Null(branch.LeaseOwner);
            Assert.Null(branch.LeaseExpiresAtUtc);
            Assert.NotNull(terminalAttempt.FinishedAtUtc);
            Assert.Equal("completed", terminalAttempt.OutcomeCode);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Activation_finalises_a_forced_malformed_ooxml_request_as_blocked()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-force-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateMalformedOoxml();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
            var seeded = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, ".docx", "force-blocked-activation");
            var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
                OoxmlStructuralTextProcessor.Capability,
                CancellationToken.None));
            await RegisterOoxmlCapabilityAsync();
            var blockedAttempt = Assert.Single(await store.ClaimAsync("force-blocked-preparation", 1,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
            Assert.True(await store.FailAsync(blockedAttempt, new RetainedProcessorFailure(ForceRetryReasonCode, []), CancellationToken.None));
            await SeedForcePolicyAsync(ForceRetryReasonCode, "retry");
            var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None));
            var request = await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None);

            var result = await CreateActivation(privateRoot, ooxmlEnabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.ClaimedBranches);
            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            Assert.Equal((int)RetainedProcessorBranchState.Blocked,
                (await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId)).State);
            var durableRequest = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
            Assert.Equal((byte)OoxmlForceRequestState.Blocked, durableRequest.State);
            Assert.Equal("office-document-xml-invalid", durableRequest.TerminalReasonCode);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Activation_finalises_a_forced_retained_read_failure_as_transient()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-force-transient-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateOfficePackage(".docx", "forced transient retained sentinel");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
            var seeded = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, ".docx", "force-transient-activation");
            var factory = new ContextFactory(_fixture.ConnectionString);
            var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
            Assert.True(await store.PromoteAsync(
                new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
                OoxmlStructuralTextProcessor.Capability,
                CancellationToken.None));
            await RegisterOoxmlCapabilityAsync();
            var blockedAttempt = Assert.Single(await store.ClaimAsync("force-transient-preparation", 1,
                OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
            Assert.True(await store.FailAsync(blockedAttempt, new RetainedProcessorFailure(ForceRetryReasonCode, []), CancellationToken.None));
            await SeedForcePolicyAsync(ForceRetryReasonCode, "retry");
            var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(16, CancellationToken.None));
            var request = await store.RequestForceAsync(
                new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(), action.RequestFingerprint, action.BlockedRowVersionToken),
                CancellationToken.None);

            var retainedReader = new ReadBytesFaultingReader(new SqlRetainedSourceReader(factory, privateRoot));
            var result = await CreateActivation(privateRoot, ooxmlEnabled: true, retainedReader: retainedReader).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.ClaimedBranches);
            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            Assert.Equal((int)RetainedProcessorBranchState.Pending,
                (await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId)).State);
            var durableRequest = await verification.SourceProcessorForceRequests.SingleAsync(value => value.Id == request.RequestId);
            Assert.Equal((byte)OoxmlForceRequestState.Transient, durableRequest.State);
            Assert.Equal("force-request-transient", durableRequest.TerminalReasonCode);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Disabled_ooxml_with_zip_enabled_does_not_register_or_promote_an_office_package_as_an_archive()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateOfficePackage(".docx", "disabled office sentinel");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
            var seed = await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, ".docx", "watched-file");
            await using var before = CreateContext();
            var registrationsBefore = await before.SourceCapabilities.CountAsync(value => value.Id == OoxmlStructuralTextProcessor.Capability.Id);

            var result = await CreateActivation(privateRoot, ooxmlEnabled: false, zipEnabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal("archive-zip-expand", result.Capability);
            await using var verification = CreateContext();
            Assert.Equal(registrationsBefore, await verification.SourceCapabilities.CountAsync(value => value.Id == OoxmlStructuralTextProcessor.Capability.Id));
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seed.RevisionId).ToListAsync());
            var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seed.ActivityId);
            Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
            Assert.Equal("deferred", activity.Reason);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Legacy_office_binaries_are_redesignated_unregistered_and_unclaimed_by_the_ooxml_processor()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var seeds = new List<(Guid ActivityId, Guid RevisionId)>();
            foreach (var (extension, sourceKind, suffix) in new[]
            {
                (".doc", "watched-file", (byte)1), (".xls", "gmail", (byte)2),
                (".ppt", "imap", (byte)3), (".doc", "outlook", (byte)4)
            })
            {
                var bytes = new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, suffix };
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
                Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
                await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), bytes);
                seeds.Add(await SeedDeferredOoxmlAsync(hash, bytes.Length, relativePath, extension, sourceKind));
            }

            await CreateActivation(privateRoot, ooxmlEnabled: false).RunOnceAsync(CancellationToken.None);

            await using var verification = CreateContext();
            Assert.Equal(0, await verification.SourceCapabilities.CountAsync(value => value.ProcessorKind == "document-office-legacy-structural-extract"));
            foreach (var seed in seeds)
            {
                var predecessor = await verification.SourceActivities.SingleAsync(value => value.Id == seed.ActivityId);
                Assert.Equal((int)SourceActivityState.CancelledSuperseded, predecessor.State);
                var relation = await verification.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == predecessor.Id);
                Assert.Equal("superseded-by-legacy-office-designation", relation.RelationshipKind);
                var activity = await verification.SourceActivities.SingleAsync(value => value.Id == relation.SuccessorActivityId);
                Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
                Assert.Equal("document-office-legacy-structural-extract", activity.RequiredCapability);
                Assert.Equal("legacy-office-binary-parser-unavailable", activity.Reason);
                Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seed.RevisionId).ToListAsync());
            }
            Assert.Empty(await new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System)
                .ClaimAsync("ooxml-owner", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Legacy_office_designation_completes_with_the_retrying_sql_execution_strategy()
    {
        const string hash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        var seeded = await SeedDeferredOoxmlAsync(hash, 9, "ignored.bin", ".doc", "watched-file");
        var store = new SqlRetainedProcessorBranchStore(
            new ContextFactory(_fixture.ConnectionString, useRetryingExecutionStrategy: true), TimeProvider.System);

        var designated = await store.DesignateLegacyOfficeAsync(
            new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".doc"),
            CancellationToken.None);

        Assert.True(designated);
        await using var verification = CreateContext();
        var predecessor = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, predecessor.State);
        var successor = await verification.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId && value.Id != seeded.ActivityId);
        Assert.Equal("legacy-office-binary-parser-unavailable", successor.Reason);
    }

    [NativeSqlServerFact]
    public async Task Ooxml_promotion_completes_with_the_retrying_sql_execution_strategy()
    {
        const string hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var seeded = await SeedDeferredOoxmlAsync(hash, 10, "ignored.bin", ".docx", "watched-file");
        var store = new SqlRetainedProcessorBranchStore(
            new ContextFactory(_fixture.ConnectionString, useRetryingExecutionStrategy: true), TimeProvider.System);

        var promoted = await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
            OoxmlStructuralTextProcessor.Capability,
            CancellationToken.None);

        Assert.True(promoted);
        await using var verification = CreateContext();
        Assert.Equal((int)SourceActivityState.CancelledSuperseded,
            (await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
        Assert.Single(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Missing_corrupt_and_malformed_retained_ooxml_are_blocked_without_children()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-ooxml-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            const string missingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var missing = await SeedDeferredOoxmlAsync(missingHash, 5, Path.Combine("sha256", "bb", $"{missingHash}.bin"), ".docx", "watched-file");
            var valid = CreateOfficePackage(".docx", "corrupt input sentinel");
            var corruptHash = Convert.ToHexStringLower(SHA256.HashData(valid));
            var corruptPath = Path.Combine("sha256", corruptHash[..2], $"{corruptHash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", corruptHash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, corruptPath), [1, 2, 3]);
            var corrupt = await SeedDeferredOoxmlAsync(corruptHash, valid.Length, corruptPath, ".xlsx", "gmail");
            var malformed = CreateMalformedOoxml();
            var malformedHash = Convert.ToHexStringLower(SHA256.HashData(malformed));
            var malformedPath = Path.Combine("sha256", malformedHash[..2], $"{malformedHash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", malformedHash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, malformedPath), malformed);
            var malformedSeed = await SeedDeferredOoxmlAsync(malformedHash, malformed.Length, malformedPath, ".docx", "imap");

            var result = await CreateActivation(privateRoot, ooxmlEnabled: true).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            Assert.Equal("retained-artifact-missing", (await verification.SourceActivities.SingleAsync(value => value.Id == missing.ActivityId)).Reason);
            Assert.Equal("retained-artifact-checksum-invalid", (await verification.SourceActivities.SingleAsync(value => value.Id == corrupt.ActivityId)).Reason);
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == malformedSeed.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
            Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == malformedSeed.RevisionId).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Ooxml_branches_preserve_concurrent_stale_retry_and_restart_lease_fences()
    {
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        var concurrent = await PromoteOoxmlBranchAsync(store, 'a');
        var claims = await Task.WhenAll(
            store.ClaimAsync("ooxml-first", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None).AsTask(),
            store.ClaimAsync("ooxml-second", 16, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None).AsTask());
        var owner = Assert.Single(claims.SelectMany(value => value));
        await using (var verifyConcurrent = CreateContext())
        {
            Assert.Equal(1, await verifyConcurrent.SourceProcessorAttempts.CountAsync(value => value.BranchId == owner.BranchId));
        }

        var stale = await PromoteOoxmlBranchAsync(store, 'b');
        var staleClaim = Assert.Single(await store.ClaimAsync("stale-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        await using (var replacement = CreateContext())
        {
            var branch = await replacement.SourceProcessorBranches.SingleAsync(value => value.Id == staleClaim.BranchId);
            branch.LeaseOwner = "replacement-owner";
            branch.LeaseGeneration++;
            branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            await replacement.SaveChangesAsync();
        }
        Assert.False(await store.CommitAsync(staleClaim, new RetainedProcessorCompletion([], new string('c', 64)), CancellationToken.None));
        Assert.False(await store.FailAsync(staleClaim, new RetainedProcessorFailure("stale", []), CancellationToken.None));
        await using (var verifyStale = CreateContext())
        {
            Assert.Empty(await verifyStale.SourceProcessorBranchMembers.Where(value => value.BranchId == staleClaim.BranchId).ToListAsync());
            Assert.Empty(await verifyStale.SourceRevisions.Where(value => value.ParentSourceRevisionId == stale.RevisionId).ToListAsync());
        }

        var retry = await PromoteOoxmlBranchAsync(store, 'd');
        var first = Assert.Single(await store.ClaimAsync("retry-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.True(await store.RetryAsync(first, "retained-artifact-transient", CancellationToken.None));
        var second = Assert.Single(await store.ClaimAsync("retry-restart-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.Equal(first.BranchId, second.BranchId);
        Assert.Equal(first.LeaseGeneration + 1, second.LeaseGeneration);

        await using (var cleanup = CreateContext())
        {
            (await cleanup.SourceProcessorBranches.SingleAsync(value => value.Id == owner.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
            (await cleanup.SourceProcessorBranches.SingleAsync(value => value.Id == staleClaim.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
            (await cleanup.SourceProcessorBranches.SingleAsync(value => value.Id == second.BranchId)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await cleanup.SaveChangesAsync();
        }
        var reclaimed = Assert.Single(await store.ClaimAsync("restart-owner", 1, OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.Equal(second.BranchId, reclaimed.BranchId);
        Assert.Equal(second.LeaseGeneration + 1, reclaimed.LeaseGeneration);
        await using var verifyRestart = CreateContext();
        var abandoned = await verifyRestart.SourceProcessorAttempts.SingleAsync(value => value.BranchId == second.BranchId && value.LeaseGeneration == second.LeaseGeneration);
        Assert.Equal("lease-expired-reconciled", abandoned.OutcomeCode);
        (await verifyRestart.SourceProcessorBranches.SingleAsync(value => value.Id == reclaimed.BranchId)).State = (int)RetainedProcessorBranchState.Blocked;
        await verifyRestart.SaveChangesAsync();
    }

    private RetainedProcessorActivationService CreateActivation(
        string privateRoot,
        bool ooxmlEnabled,
        bool zipEnabled = false,
        IRetainedSourceReader? retainedReader = null,
        string? outlookSpoolRoot = null)
    {
        var factory = new ContextFactory(_fixture.ConnectionString);
        var policy = outlookSpoolRoot is null
            ? null
            : PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(outlookSpoolRoot);
        var writer = new SqlRetainedArtifactWriter(factory, privateRoot, outlookSpoolPolicy: policy);
        var zip = new ZipArchiveRetainedProcessor(writer);
        var ooxml = new OoxmlStructuralTextProcessor(writer);
        return new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new OoxmlStructuralTextCapabilityHandler()])),
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), retainedReader ?? new SqlRetainedSourceReader(factory, privateRoot, policy), zip,
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = ooxmlEnabled, ArchiveZipExpandEnabled = zipEnabled }, TimeProvider.System,
            ooxmlProcessor: ooxml);
    }

    private async Task SeedForcePolicyAsync(string reasonCode, string actionKind)
    {
        await using var context = CreateContext();
        if (await context.OperatorActionCapabilityPolicies.AnyAsync(value =>
                value.DescriptorId == OoxmlStructuralTextProcessor.Capability.Id &&
                value.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                value.DescriptorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                value.SafetyContractId == "retained-binding" &&
                value.HandlerId == "retained-processor-branch-store" &&
                value.ActionKind == actionKind &&
                value.ReasonCode == reasonCode))
        {
            return;
        }

        context.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
        {
            PolicyId = Guid.NewGuid(), PolicyRevision = 1,
            DescriptorId = OoxmlStructuralTextProcessor.Capability.Id,
            DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            DescriptorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            SafetyContractId = "retained-binding", HandlerId = "retained-processor-branch-store",
            ActionKind = actionKind, ReasonCode = reasonCode
        });
        await context.SaveChangesAsync();
    }

    private async Task RegisterOoxmlCapabilityAsync()
    {
        await using var context = CreateContext();
        if (await context.SourceCapabilities.AnyAsync(value => value.Id == OoxmlStructuralTextProcessor.Capability.Id))
        {
            return;
        }

        context.SourceCapabilities.Add(new SourceCapabilityEntity
        {
            Id = OoxmlStructuralTextProcessor.Capability.Id,
            ProcessorKind = OoxmlStructuralTextProcessor.Capability.ProcessorKind,
            ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
            ExecutionClass = (int)ExecutionClass.InProcess,
            AcceptedClassificationsJson = "[\"OoxmlDocumentContainer\"]",
            OutputContract = OoxmlStructuralTextProcessor.Capability.OutputContract,
            ProcessorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
            IsRunnable = true,
            RegisteredBy = "test",
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task<(Guid ActivityId, Guid RevisionId)> PromoteOoxmlBranchAsync(SqlRetainedProcessorBranchStore store, char hashCharacter)
    {
        var hash = new string(hashCharacter, 64);
        var seeded = await SeedDeferredOoxmlAsync(hash, 1, Path.Combine("sha256", hash[..2], $"{Guid.NewGuid():N}.bin"), ".docx", "lease-test");
        Assert.True(await store.PromoteAsync(
            new RetainedProcessorPromotionCandidate(seeded.ActivityId, new SourceRevisionId(seeded.RevisionId), hash, ".docx"),
            OoxmlStructuralTextProcessor.Capability, CancellationToken.None));
        return seeded;
    }

    private async Task<(Guid ActivityId, Guid RevisionId)> SeedDeferredOoxmlAsync(string hash, int byteLength, string relativePath, string extension, string sourceKind,
        string? outlookSpoolRoot = null)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\phase5-public-{sourceKind}-{rootId:N}", DisplayName = sourceKind, State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 128L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"{sourceKind}:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = $"C:\\missing-source-original-sentinel{extension}", Classification = "OoxmlDocumentContainer", Extension = extension, ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        if (outlookSpoolRoot is not null)
        {
            context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity { Id = Guid.NewGuid(), SourceRootId = rootId, DisplayName = "OOXML test profile",
                SpoolRoot = outlookSpoolRoot, IncrementalBasis = 0, State = 0, IsEnabled = true, ConfigurationRevision = 1,
                CadenceTicks = TimeSpan.FromMinutes(5).Ticks, MaximumOverlapTicks = TimeSpan.FromMinutes(1).Ticks, CreatedAtUtc = now, UpdatedAtUtc = now });
        }
        await context.SaveChangesAsync();
        return (activityId, revisionId);
    }

    private static byte[] CreateOfficePackage(string extension, string text)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            if (extension == ".docx")
            {
                WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
                WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
                WriteEntry(archive, "word/document.xml", $"<w:document xmlns:w='w'><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
            }
            else if (extension == ".xlsx")
            {
                WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/sharedStrings.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>");
                WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
                WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r='r'><sheets><sheet name='Second' r:id='rId2'/></sheets></workbook>");
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/><Relationship Id='rIdShared' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings' Target='sharedStrings.xml'/></Relationships>");
                WriteEntry(archive, "xl/sharedStrings.xml", $"<sst><si><t>{text}</t></si></sst>");
                WriteEntry(archive, "xl/worksheets/sheet2.xml", "<worksheet><sheetData><row><c t='s'><v>0</v></c><c><f>PRIVATE_FORMULA_SENTINEL</f><v>42</v></c></row></sheetData></worksheet>");
            }
            else
            {
                WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/ppt/presentation.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml'/><Override PartName='/ppt/slides/slide2.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.slide+xml'/></Types>");
                WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='ppt/presentation.xml'/></Relationships>");
                WriteEntry(archive, "ppt/presentation.xml", "<p:presentation xmlns:p='p' xmlns:r='r'><p:sldIdLst><p:sldId r:id='rId2'/></p:sldIdLst></p:presentation>");
                WriteEntry(archive, "ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide2.xml'/></Relationships>");
                WriteEntry(archive, "ppt/slides/slide2.xml", $"<p:sld xmlns:p='p'><p:txBody><p:p><p:r><p:t>{text}</p:t></p:r></p:p></p:txBody></p:sld>");
            }
        }
        return buffer.ToArray();
    }

    private static byte[] CreateMalformedOoxml()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>unterminated");
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(value
            .Replace("<Types>", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>", StringComparison.Ordinal)
            .Replace("<Relationships>", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>", StringComparison.Ordinal)
            .Replace("xmlns:w='w'", "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'", StringComparison.Ordinal)
            .Replace("xmlns:p='p'", "xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main'", StringComparison.Ordinal)
            .Replace("xmlns:r='r'", "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'", StringComparison.Ordinal)
            .Replace("<workbook xmlns:r=", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r=", StringComparison.Ordinal)
            .Replace("<worksheet>", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'>", StringComparison.Ordinal)
            .Replace("<sst>", "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'>", StringComparison.Ordinal));
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ReadBytesFaultingReader(IRetainedSourceReader inner) : IRetainedSourceReader
    {
        public ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            inner.InspectAsync(sourceRevisionId, cancellationToken);

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            ValueTask.FromException<RetainedSourceBytes>(new IOException("test-retained-read-transient"));

        public ValueTask<FluxKnowledge.Application.Pipeline.Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) => inner.ReadUtf8Async(sourceRevisionId, cancellationToken);
    }

    private sealed class ContextFactory(string connectionString, bool useRetryingExecutionStrategy = false) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                if (useRetryingExecutionStrategy)
                {
                    sqlServer.EnableRetryOnFailure();
                }
            })
            .Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
