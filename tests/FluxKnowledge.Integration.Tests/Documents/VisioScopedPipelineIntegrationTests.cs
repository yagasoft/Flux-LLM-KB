using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

[Collection("interactive-visio")]
public sealed class VisioScopedPipelineIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private const string Fingerprint = "phase-6-vsdx-retained-visio-v2";
    private static readonly Guid CapabilityId = new("93a2ce7d-a168-4d3e-a40e-5c51c991d786");
    private static readonly Guid LegacyCapabilityId = new("d1e7dab3-b486-462f-bf59-64122f674e0d");
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.SourceCapabilities.Where(row => row.Id == CapabilityId).ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Actual_interactive_command_extracts_one_retained_document_and_replay_is_idempotent()
    {
        if (Environment.GetEnvironmentVariable("FLUX_KB_RUN_ACTUAL_VISIO") != "1" || !OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledge", "visio-cli-proof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inputPath = Path.Combine(root, "public-generated.vsdx");
            await VisioActualComExtractorTests.CreatePublicFixtureAsync(inputPath);
            var bytes = await File.ReadAllBytesAsync(inputPath);
            var retainedRoot = Path.Combine(root, "retained");
            Directory.CreateDirectory(retainedRoot);
            using var store = new ContentAddressedSourceArtifactStore(retainedRoot);
            using var input = new MemoryStream(bytes, writable: false);
            var receipt = await store.PutStreamAsync(input, 16 * 1024 * 1024, default);
            var (request, predecessor) = await SeedAsync(RetainedProcessorBranchState.Blocked);
            var factory = SqlTestData.CreateFactory(fixture);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.SourceRevisions.ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.ContentSha256, receipt.ContentSha256).SetProperty(row => row.ByteLength, bytes.Length));
                await context.SourceArtifacts.ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.ContentSha256, receipt.ContentSha256).SetProperty(row => row.ByteLength, bytes.Length)
                    .SetProperty(row => row.StoreRelativePath, receipt.StoreRelativePath));
                await context.SourceActivities.ExecuteUpdateAsync(set => set.SetProperty(row => row.InputFingerprint, receipt.ContentSha256));
                await context.SourceProcessorBranches.ExecuteUpdateAsync(set => set.SetProperty(row => row.InputSha256, receipt.ContentSha256));
            }
            request = request with { ExpectedInputSha256 = receipt.ContentSha256 };
            using var reader = new SqlRetainedSourceReader(factory, retainedRoot);
            using var lease = VisioExecutionLease.AcquireForTest(new VisioDocumentExtractor(), Path.Combine(root, "execution.lock"));
            using var output = new StringWriter();
            Assert.Equal(0, await VisioDocumentCommand.ExecuteAsync(request, factory, reader, lease, output, default, retainedRoot));
            await using var check = await factory.CreateDbContextAsync();
            var artifact = Assert.Single(await check.Artifacts.ToListAsync());
            Assert.Equal((int)PipelineStage.Extract, artifact.Stage);
            Assert.Contains("Public data: 42", artifact.SearchText, StringComparison.Ordinal);
            Assert.Contains("visio", artifact.DocumentMetadataJson, StringComparison.Ordinal);
            Assert.Equal(2, await check.SourceRevisions.CountAsync());
            Assert.Single(await check.PipelineRecords.ToListAsync());
            Assert.Equal(2, await check.Jobs.CountAsync()); // Completed Extract and queued Normalise.
            Assert.Equal((int)RetainedProcessorBranchState.Blocked,
                (await check.SourceProcessorBranches.SingleAsync(row => row.Id == predecessor)).State);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await VisioDocumentCommand.ExecuteAsync(request, factory, reader, lease, output, default, retainedRoot));
            Assert.Contains("visio-already-extracted", output.ToString(), StringComparison.Ordinal);
            Assert.Single(await check.Artifacts.ToListAsync());
            Assert.Equal(2, await check.Jobs.CountAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeSqlServerTheory]
    [InlineData(RetainedProcessorBranchState.Blocked)]
    [InlineData(RetainedProcessorBranchState.Completed)]
    public async Task Concurrent_exact_requests_create_one_successor_without_mutating_terminal_predecessor(
        RetainedProcessorBranchState terminalState)
    {
        var (request, predecessor) = await SeedAsync(terminalState);
        var store = new SqlRetainedProcessorBranchStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => store.RequestDocumentReprocessAsync(request, default).AsTask()));
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Single(results.Select(result => result.SuccessorBranchId).Distinct());
        Assert.Single(results, result => result.Created);
        await using var check = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Equal((int)terminalState, (await check.SourceProcessorBranches.SingleAsync(b => b.Id == predecessor)).State);
        Assert.Equal(2, await check.SourceProcessorBranches.CountAsync());
        Assert.Single(await check.SourceActivityRelations.ToListAsync());
        Assert.Null((await check.SourceRevisions.SingleAsync()).SuppressedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Blocked_structural_predecessor_with_legacy_pending_activity_and_finished_attempt_is_eligible()
    {
        var (request, predecessor) = await SeedAsync(RetainedProcessorBranchState.Blocked);
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var branch = await setup.SourceProcessorBranches.SingleAsync(row => row.Id == predecessor);
            var activity = await setup.SourceActivities.SingleAsync(row => row.Id == branch.SourceActivityId);
            activity.State = (int)SourceActivityState.Pending;
            branch.LeaseGeneration = 1;
            setup.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = Guid.NewGuid(),
                BranchId = predecessor,
                LeaseGeneration = 1,
                StartedAtUtc = activity.CreatedAtUtc,
                FinishedAtUtc = activity.UpdatedAtUtc,
                OutcomeCode = "office-document-container-invalid"
            });
            await setup.SaveChangesAsync();
        }

        var result = await new SqlRetainedProcessorBranchStore(factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(request, default);

        Assert.True(result.Accepted);
        Assert.True(result.Created);
        Assert.NotNull(result.SuccessorBranchId);
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal((int)RetainedProcessorBranchState.Blocked,
            (await check.SourceProcessorBranches.SingleAsync(row => row.Id == predecessor)).State);
        Assert.Equal((int)SourceActivityState.Pending,
            (await check.SourceActivities.SingleAsync(row => row.Id ==
                check.SourceProcessorBranches.Single(branch => branch.Id == predecessor).SourceActivityId)).State);
    }

    [NativeSqlServerTheory]
    [InlineData("missing")]
    [InlineData("stale-generation")]
    [InlineData("unfinished")]
    [InlineData("wrong-outcome")]
    [InlineData("non-blocked")]
    public async Task Legacy_pending_activity_without_exact_terminal_attempt_is_ineligible(string scenario)
    {
        var predecessorState = scenario == "non-blocked"
            ? RetainedProcessorBranchState.Completed
            : RetainedProcessorBranchState.Blocked;
        var (request, predecessor) = await SeedAsync(predecessorState);
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var branch = await setup.SourceProcessorBranches.SingleAsync(row => row.Id == predecessor);
            var activity = await setup.SourceActivities.SingleAsync(row => row.Id == branch.SourceActivityId);
            activity.State = (int)SourceActivityState.Pending;
            branch.LeaseGeneration = 2;
            if (scenario != "missing")
            {
                setup.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    BranchId = predecessor,
                    LeaseGeneration = scenario == "stale-generation" ? 1 : 2,
                    StartedAtUtc = activity.CreatedAtUtc,
                    FinishedAtUtc = scenario == "unfinished" ? null : activity.UpdatedAtUtc,
                    OutcomeCode = scenario == "wrong-outcome" ? "archive-member-not-utf8" : "office-document-container-invalid"
                });
            }
            await setup.SaveChangesAsync();
        }

        var result = await new SqlRetainedProcessorBranchStore(factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(request, default);

        Assert.False(result.Accepted);
        Assert.False(result.Created);
        Assert.Null(result.SuccessorBranchId);
        await using var check = await factory.CreateDbContextAsync();
        Assert.Single(await check.SourceProcessorBranches.ToListAsync());
        Assert.Empty(await check.SourceActivityRelations.ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("visio-document-connection-invalid", true)]
    [InlineData("visio-document-extraction-failed", false)]
    public async Task Corrected_processor_accepts_only_the_exact_terminal_failed_Visio_pipeline(
        string failureReason,
        bool expectedAccepted)
    {
        const string correctedFingerprint = "phase-6-vsdx-retained-visio-v2";
        var (request, structuralBranchId) = await SeedAsync(RetainedProcessorBranchState.Blocked);
        var factory = SqlTestData.CreateFactory(fixture);
        var now = DateTimeOffset.UtcNow;
        var priorActivityId = Guid.NewGuid();
        var priorBranchId = Guid.NewGuid();
        var childRevisionId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var original = await setup.SourceRevisions.SingleAsync();
            var structural = await setup.SourceProcessorBranches.SingleAsync(row => row.Id == structuralBranchId);
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = priorActivityId,
                SourceRevisionId = original.Id,
                ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = "phase-6-vsdx-visio-v1",
                InputFingerprint = request.ExpectedInputSha256,
                State = (int)SourceActivityState.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = priorBranchId,
                SourceActivityId = priorActivityId,
                SourceRevisionId = original.Id,
                InputSha256 = request.ExpectedInputSha256,
                ProcessorVersion = "phase-6-vsdx-visio-v1",
                ProcessorFingerprint = "phase-6-vsdx-retained-visio-v1",
                State = (int)RetainedProcessorBranchState.Completed,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceActivityRelations.Add(new SourceActivityRelationEntity
            {
                Id = Guid.NewGuid(),
                PredecessorActivityId = structural.SourceActivityId,
                SuccessorActivityId = priorActivityId,
                RelationshipKind = "superseded-by-retained-processor",
                ReasonCode = "superseded-by-exact-document-reprocess-v1",
                CreatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = childRevisionId,
                SourceRootId = original.SourceRootId,
                StableSourceIdentity = "visio-v1-input",
                Revision = 1,
                ContentSha256 = request.ExpectedInputSha256,
                CanonicalPath = string.Empty,
                ParentSourceRevisionId = original.Id,
                Classification = DocumentProcessingInput.VisioClassification,
                Extension = ".vsdx",
                OriginKind = 2,
                ByteLength = original.ByteLength,
                DiscoveredAtUtc = now
            });
            setup.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
            {
                Id = Guid.NewGuid(),
                BranchId = priorBranchId,
                MemberFingerprint = new string('c', 64),
                ChildSourceRevisionId = childRevisionId,
                Disposition = "completed",
                ByteLength = original.ByteLength,
                CreatedAtUtc = now
            });
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identityId,
                SourceKind = "retained local source",
                StableKey = "visio-v1-input",
                CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = identityId,
                SourceRevisionId = childRevisionId,
                Revision = 1,
                ContentHash = request.ExpectedInputSha256,
                RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Extract,
                RegisteredAtUtc = now
            });
            setup.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(),
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractVisio,
                PublicState = (int)FluxKnowledge.Domain.Jobs.PublicJobState.Failed,
                DueAtUtc = now,
                Reason = failureReason
            });
            if (!await setup.SourceCapabilities.AnyAsync(row => row.Id == LegacyCapabilityId))
            {
                setup.SourceCapabilities.Add(new SourceCapabilityEntity
                {
                    Id = LegacyCapabilityId,
                    ProcessorKind = "document-vsdx-visio-extract",
                    ProcessorVersion = "phase-6-vsdx-visio-v1",
                    ProcessorFingerprint = "phase-6-vsdx-retained-visio-v1",
                    ExecutionClass = (int)ExecutionClass.InProcess,
                    OutputContract = "retained:document-vsdx-visio-extract",
                    IsRunnable = true,
                    RegisteredBy = "visio-test",
                    RegisteredAtUtc = now
                });
            }
            await setup.SaveChangesAsync();
        }

        var result = await new SqlRetainedProcessorBranchStore(factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(request with { ExpectedProcessorFingerprint = correctedFingerprint }, default);

        Assert.Equal(expectedAccepted, result.Accepted);
        Assert.Equal(expectedAccepted, result.Created);
        Assert.Equal(expectedAccepted, result.SuccessorBranchId is not null);
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal(expectedAccepted ? 3 : 2, await check.SourceProcessorBranches.CountAsync());
        Assert.Equal(expectedAccepted ? 2 : 1, await check.SourceActivityRelations.CountAsync());
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.Failed,
            (await check.Jobs.SingleAsync(row => row.PipelineRecordId == recordId)).PublicState);
        var legacy = await check.SourceCapabilities.SingleAsync(row => row.Id == LegacyCapabilityId);
        Assert.Equal("phase-6-vsdx-visio-v1", legacy.ProcessorVersion);
        Assert.Equal("phase-6-vsdx-retained-visio-v1", legacy.ProcessorFingerprint);
    }

    [NativeSqlServerFact]
    public async Task Expired_paused_execution_recovers_only_after_previous_whole_command_lease_releases()
    {
        if (Environment.GetEnvironmentVariable("FLUX_KB_RUN_ACTUAL_VISIO") != "1" || !OperatingSystem.IsWindows()) return;
        var (request, _) = await SeedAsync(RetainedProcessorBranchState.Blocked);
        var factory = SqlTestData.CreateFactory(fixture);
        var branches = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var branchId = (await branches.RequestDocumentReprocessAsync(request, default)).SuccessorBranchId!.Value;
        var claim = (await branches.ClaimDocumentBranchAsync(branchId, request.SourceRevisionId,
            request.ExpectedInputSha256, request.ExpectedProcessorFingerprint, "desktop", default))!;
        var child = DocumentProcessingInput.CreateVisioChild(claim,
            new RetainedSourceBytes(request.SourceRevisionId, [1, 2, 3, 4], request.ExpectedInputSha256, 4));
        Assert.True(await branches.CommitAsync(claim, new RetainedProcessorCompletion([child], new string('c', 64)), default));
        await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System).RegisterVisioBranchAsync(request, branchId, default);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var dispatch = (await new SqlOutboxStore(factory).ClaimVisioDocumentAsync(request, branchId, "desktop", now, TimeSpan.FromMinutes(12), default))!;
        var job = (await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "desktop", now, TimeSpan.FromMinutes(12), default))!;
        await using var check = await factory.CreateDbContextAsync();
        await check.SourceRootConfigurations.ExecuteUpdateAsync(set => set.SetProperty(row => row.State, (int)SourceRootState.Paused));
        await check.Jobs.ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await check.OutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledge", "visio-recovery-proof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "execution.lock");
            using (var previousCommand = VisioExecutionLease.AcquireForTest(new VisioDocumentExtractor(), path))
            {
                Assert.Throws<FluxKnowledge.Application.Sources.RetainedProcessorException>(() =>
                    VisioExecutionLease.AcquireForTest(new VisioDocumentExtractor(), path));
                Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.WorkerProcessing,
                    (await check.Jobs.SingleAsync(row => row.Id == job.JobId.Value)).PublicState);
            }
            using var recoveredCommand = VisioExecutionLease.AcquireForTest(new VisioDocumentExtractor(), path);
            using var reader = new SqlRetainedSourceReader(factory, root);
            using var output = new StringWriter();
            Assert.Equal(0, await VisioDocumentCommand.ExecuteAsync(request, factory, reader, recoveredCommand, output, default, root));
            Assert.Contains("visio-expired-execution-cleaned", output.ToString(), StringComparison.Ordinal);
            check.ChangeTracker.Clear();
            Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.Failed,
                (await check.Jobs.SingleAsync(row => row.Id == job.JobId.Value)).PublicState);
            Assert.Empty(await check.Artifacts.ToListAsync());
            Assert.Single(await check.Jobs.ToListAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeSqlServerFact]
    public async Task Wrong_hash_and_paused_owner_are_ineligible_and_create_nothing()
    {
        var (request, _) = await SeedAsync(RetainedProcessorBranchState.Blocked);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        Assert.False((await store.RequestDocumentReprocessAsync(request with { ExpectedInputSha256 = new string('b', 64) }, default)).Accepted);
        await using var check = await factory.CreateDbContextAsync();
        await check.SourceRootConfigurations.ExecuteUpdateAsync(set => set.SetProperty(r => r.State, (int)SourceRootState.Paused));
        Assert.False((await store.RequestDocumentReprocessAsync(request, default)).Accepted);
        Assert.Single(await check.SourceProcessorBranches.ToListAsync());
        Assert.Empty(await check.SourceActivityRelations.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Exact_branch_links_one_document_and_never_claims_a_different_owner_or_branch()
    {
        var (request, _) = await SeedAsync(RetainedProcessorBranchState.Blocked);
        var factory = SqlTestData.CreateFactory(fixture);
        var branches = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var branchId = (await branches.RequestDocumentReprocessAsync(request, default)).SuccessorBranchId!.Value;
        var claim = (await branches.ClaimDocumentBranchAsync(branchId, request.SourceRevisionId,
            request.ExpectedInputSha256, request.ExpectedProcessorFingerprint, "desktop", default))!;
        var input = DocumentProcessingInput.CreateVisioChild(claim,
            new RetainedSourceBytes(request.SourceRevisionId, [1, 2, 3, 4], request.ExpectedInputSha256, 4));
        Assert.True(await branches.CommitAsync(claim, new RetainedProcessorCompletion([input], new string('c', 64)), default));
        var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System);
        var recordId = await registration.RegisterVisioBranchAsync(request, branchId, default);
        Assert.NotNull(recordId);
        Assert.Equal(recordId, await registration.RegisterVisioBranchAsync(request, branchId, default));
        var outbox = new SqlOutboxStore(factory);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Null(await outbox.ClaimVisioDocumentAsync(request with { SourceRevisionId = SourceRevisionId.New() },
            branchId, "desktop", now, TimeSpan.FromMinutes(12), default));
        Assert.Null(await outbox.ClaimVisioDocumentAsync(request, Guid.NewGuid(), "desktop", now, TimeSpan.FromMinutes(12), default));
        var dispatch = await outbox.ClaimVisioDocumentAsync(request, branchId, "desktop", now, TimeSpan.FromMinutes(12), default);
        Assert.NotNull(dispatch);
        Assert.Equal(recordId, dispatch.PipelineRecordId.Value);
        Assert.Equal(PipelineOperations.ExtractVisio, dispatch.Operation);
        Assert.Null(await outbox.ClaimVisioDocumentAsync(request, branchId, "other-desktop", now, TimeSpan.FromMinutes(12), default));
        var job = (await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "desktop", now, TimeSpan.FromMinutes(12), default))!;
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal(2, await check.SourceRevisions.CountAsync());
        Assert.Single(await check.PipelineRecords.ToListAsync());
        Assert.Single(await check.Jobs.ToListAsync());
        await check.SourceRootConfigurations.ExecuteUpdateAsync(set => set.SetProperty(row => row.State, (int)SourceRootState.Paused));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlStageTransitionStore(factory).TransitionAsync(new StageTransitionRequest(dispatch, job,
                new StageArtifact(Guid.NewGuid(), PipelineStage.Extract, request.ExpectedInputSha256,
                    "text/plain; charset=utf-8", "must not publish after pause", now),
                PipelineStage.Normalise, PipelineOperations.NormaliseText, "visio-test"), default));
        Assert.Equal("visio-source-unavailable", exception.Message);
        Assert.Empty(await check.Artifacts.ToListAsync());
        Assert.Single(await check.Jobs.ToListAsync());
    }

    private async Task<(DocumentReprocessRequest Request, Guid BranchId)> SeedAsync(RetainedProcessorBranchState state)
    {
        var now = DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var hash = new string('a', 64);
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = "C:\\visio-scope", DisplayName = "Visio scope", State = (int)SourceRootState.Enabled,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
            MaximumFileBytes = 1024 * 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId, SourceRootId = rootId, StableSourceIdentity = "visio-public-test", Revision = 1,
            ContentSha256 = hash, CanonicalPath = "C:\\visio-scope\\public.vsdx", Classification = "VsdxDocumentContainer",
            Extension = ".vsdx", ByteLength = 4, DiscoveredAtUtc = now
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash,
            StoreRelativePath = $"sha256\\aa\\{hash}.bin", ByteLength = 4
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.TextExtraction,
            ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-6-vsdx-structural-v1",
            InputFingerprint = hash, State = (int)(state == RetainedProcessorBranchState.Completed ? SourceActivityState.Completed : SourceActivityState.FailedTerminal),
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId, InputSha256 = hash,
            ProcessorVersion = "phase-6-vsdx-structural-v1", ProcessorFingerprint = "phase-6-vsdx-retained-structural-v1",
            State = (int)state, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceCapabilities.Add(new SourceCapabilityEntity
        {
            Id = CapabilityId, ProcessorKind = "document-vsdx-visio-extract", ProcessorVersion = "phase-6-vsdx-visio-v2",
            ProcessorFingerprint = Fingerprint, ExecutionClass = (int)ExecutionClass.InProcess,
            OutputContract = "retained:document-vsdx-visio-extract", IsRunnable = true,
            RegisteredBy = "visio-test", RegisteredAtUtc = now
        });
        await context.SaveChangesAsync();
        return (new DocumentReprocessRequest(new SourceRevisionId(revisionId), hash, Fingerprint), branchId);
    }
}
