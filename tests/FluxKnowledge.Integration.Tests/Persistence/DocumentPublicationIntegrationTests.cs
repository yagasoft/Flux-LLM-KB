using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class DocumentPublicationIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(_fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Later_created_vsdx_branch_remains_published_when_an_older_branch_finishes_late()
    {
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var ownerRevisionId = Guid.NewGuid();
        DocumentCandidate stale;
        await using (var setup = Context())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = "C:\\document-publication", DisplayName = "Document publication",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 1024 * 1024, ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = ownerRevisionId, SourceRootId = rootId, StableSourceIdentity = "architecture",
                Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\document-publication\\architecture.vsdx",
                Classification = "VsdxDocumentContainer", Extension = ".vsdx", ByteLength = 4, DiscoveredAtUtc = now
            });

            stale = SeedCandidate(setup, rootId, ownerRevisionId, now, "stale", 0);
            var current = SeedCandidate(setup, rootId, ownerRevisionId, now, "current", 1);
            var successor = SeedCandidate(setup, rootId, ownerRevisionId, now, "successor", 2);
            await setup.SaveChangesAsync();

            await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), current, now.AddMinutes(3));
            await AssertSelectedAsync(current, ownerRevisionId);

            await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), successor, now.AddMinutes(4));
            await AssertSelectedAsync(successor, ownerRevisionId);

            await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), stale, now.AddMinutes(5));
            await AssertSelectedAsync(successor, ownerRevisionId);
        }
    }

    [NativeSqlServerFact]
    public async Task Pause_after_publish_claim_refuses_the_new_document_publication_and_preserves_the_current_one()
    {
        var now = DateTimeOffset.Parse("2026-09-20T13:00:00+00:00");
        var rootId = Guid.NewGuid();
        var ownerRevisionId = Guid.NewGuid();
        DocumentCandidate current;
        DocumentCandidate claimedBeforePause;
        await using (var setup = Context())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = "C:\\document-publication-pause", DisplayName = "Document publication pause",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 1024 * 1024, ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = ownerRevisionId, SourceRootId = rootId, StableSourceIdentity = "architecture-pause",
                Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\document-publication-pause\\architecture.vsdx",
                Classification = "VsdxDocumentContainer", Extension = ".vsdx", ByteLength = 4, DiscoveredAtUtc = now
            });
            current = SeedCandidate(setup, rootId, ownerRevisionId, now, "current-before-pause", 0);
            claimedBeforePause = SeedCandidate(setup, rootId, ownerRevisionId, now, "claimed-before-pause", 1);
            await setup.SaveChangesAsync();
        }

        var store = new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture));
        await PublishAsync(store, current, now.AddMinutes(2));
        await AssertSelectedAsync(current, ownerRevisionId);

        await using (var pause = Context())
        {
            var root = await pause.SourceRootConfigurations.SingleAsync(value => value.Id == rootId);
            root.State = (int)SourceRootState.Paused;
            root.ConfigurationRevision++;
            root.UpdatedAtUtc = now.AddMinutes(3);
            await pause.SaveChangesAsync();
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishAsync(store, claimedBeforePause, now.AddMinutes(4)));

        Assert.Equal("document-publication-source-unavailable", failure.Message);
        await AssertSelectedAsync(current, ownerRevisionId);
        await using var verification = Context();
        Assert.DoesNotContain(
            await verification.Artifacts.ToListAsync(),
            artifact => artifact.PipelineRecordId == claimedBeforePause.RecordId && artifact.Stage == (int)PipelineStage.Publish);
        Assert.False(await verification.SourceRevisions
            .Where(value => value.Id == current.InputRevisionId)
            .Select(value => value.SuppressedAtUtc.HasValue)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Visio_successor_replaces_only_its_original_owner_and_retains_last_good_until_publish()
    {
        var now = DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        DocumentCandidate current;
        DocumentCandidate visio;
        await using (var setup = Context())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = "C:\\visio-publication", DisplayName = "Visio publication", State = 0,
                IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                MaximumFileBytes = 1024 * 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = ownerId, SourceRootId = rootId, StableSourceIdentity = "visio-publication", Revision = 1,
                ContentSha256 = new string('a', 64), CanonicalPath = "C:\\visio-publication\\public.vsdx",
                Classification = "VsdxDocumentContainer", Extension = ".vsdx", ByteLength = 4, DiscoveredAtUtc = now
            });
            current = SeedCandidate(setup, rootId, ownerId, now, "current-structural", 0);
            visio = SeedCandidate(setup, rootId, ownerId, now, "visio-successor", 1, DocumentProcessingInput.Visio);
            await setup.SaveChangesAsync();
        }
        var store = new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture));
        await PublishAsync(store, current, now);
        await AssertSelectedAsync(current, ownerId);
        await PublishAsync(store, visio, now.AddMinutes(2));
        await AssertSelectedAsync(visio, ownerId);
        await using var check = Context();
        Assert.Single(await check.DocumentPublications.ToListAsync());
        Assert.Equal((int)RetainedProcessorBranchState.Completed,
            (await check.SourceProcessorBranches.SingleAsync(row => row.Id == current.BranchId)).State);
        Assert.Equal(2, await check.Artifacts.CountAsync(row => row.Stage == (int)PipelineStage.Publish));
    }

    private static DocumentCandidate SeedCandidate(
        FluxKnowledgeDbContext context,
        Guid rootId,
        Guid ownerRevisionId,
        DateTimeOffset now,
        string name,
        int sequence,
        DocumentProcessingContract? contract = null)
    {
        contract ??= DocumentProcessingInput.Vsdx;
        var inputRevisionId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var ownerActivityId = Guid.NewGuid();
        var inputActivityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        var inputFingerprint = new string((char)('b' + sequence), 64);
        var leaseExpiresAtUtc = now.AddMinutes(30);
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identityId, SourceKind = "retained local source", StableKey = $"document-input:{name}", CreatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = inputRevisionId, SourceRootId = rootId, StableSourceIdentity = $"document-input:{name}", Revision = 1,
            ContentSha256 = new string('a', 64), CanonicalPath = $"C:\\retained\\{name}.vsdx",
            ParentSourceRevisionId = ownerRevisionId, Classification = contract.Classification, Extension = ".vsdx",
            OriginKind = 2, ByteLength = 4, DiscoveredAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId, SourceIdentityId = identityId, SourceRevisionId = inputRevisionId, Revision = 1,
            ContentHash = new string('a', 64), RootLineageRecordId = recordId, CurrentStage = (int)PipelineStage.Embed,
            RegisteredAtUtc = now
        });
        context.SourceActivities.AddRange(
            new SourceActivityEntity
            {
                Id = ownerActivityId, SourceRevisionId = ownerRevisionId, ActivityKind = (int)SourceActivityKind.ArchiveExpansion,
                ExecutionClass = 1, ProcessorVersion = $"branch-{name}", InputFingerprint = inputFingerprint,
                DescriptorFingerprint = new string((char)('e' + sequence), 64), State = (int)SourceActivityState.Completed,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new SourceActivityEntity
            {
                Id = inputActivityId, SourceRevisionId = inputRevisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = 1, ProcessorVersion = contract.ProcessorVersion, InputFingerprint = new string('a', 64),
                State = (int)SourceActivityState.Completed, ResultingPipelineRecordId = recordId, ResultingPipelineRecordRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = ownerActivityId, SourceRevisionId = ownerRevisionId, InputSha256 = new string('a', 64),
            ProcessorVersion = contract.ParentProcessorVersion, ProcessorFingerprint = contract.ParentProcessorFingerprint,
            State = (int)RetainedProcessorBranchState.Completed, CompletedMemberCount = 1,
            CreatedAtUtc = now.AddMinutes(sequence), UpdatedAtUtc = now.AddMinutes(sequence)
        });
        context.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
        {
            Id = Guid.NewGuid(), BranchId = branchId, MemberFingerprint = new string((char)('h' + sequence), 64),
            ChildSourceRevisionId = inputRevisionId, ChildSourceActivityId = inputActivityId, Disposition = "completed",
            ByteLength = 4, CreatedAtUtc = now
        });
        context.Jobs.Add(new JobEntity
        {
            Id = jobId, PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
            Operation = PipelineOperations.Publish, PublicState = (int)PublicJobState.WorkerProcessing, DueAtUtc = now,
            LeaseOwner = $"worker-{name}", LeaseExpiresAtUtc = leaseExpiresAtUtc, LeaseGeneration = 1
        });
        context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = dispatchId, PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Publish,
            Operation = PipelineOperations.Publish, DispatchGeneration = 0, IdempotencyKey = $"document-publication:{name}",
            DueAtUtc = now, CreatedAtUtc = now, LeaseOwner = $"dispatch-{name}", LeaseExpiresAtUtc = leaseExpiresAtUtc,
            LeaseGeneration = 1
        });
        return new DocumentCandidate(inputRevisionId, branchId, recordId, jobId, dispatchId, name, leaseExpiresAtUtc);
    }

    private static async Task PublishAsync(SqlStageTransitionStore store, DocumentCandidate candidate, DateTimeOffset atUtc) =>
        await store.TransitionAsync(
            new StageTransitionRequest(
                new ClaimedDispatchMessage(new DispatchMessageId(candidate.DispatchId), new PipelineRecordId(candidate.RecordId), 1,
                    PipelineStage.Publish, PipelineOperations.Publish, 0, $"document-publication:{candidate.Name}", atUtc,
                    $"dispatch-{candidate.Name}", candidate.LeaseExpiresAtUtc, 1),
                new ClaimedJob(new JobId(candidate.JobId), new PipelineRecordId(candidate.RecordId), 1, PipelineStage.Publish,
                    PipelineOperations.Publish, PublicJobState.WorkerProcessing, atUtc, 0, $"worker-{candidate.Name}",
                    candidate.LeaseExpiresAtUtc, 1),
                new StageArtifact(Guid.NewGuid(), PipelineStage.Publish, new string('a', 64), "text/plain; charset=utf-8",
                    $"published {candidate.Name}", atUtc),
                null, null, "document-publication-test"),
            CancellationToken.None);

    private async Task AssertSelectedAsync(DocumentCandidate expected, Guid ownerRevisionId)
    {
        await using var verification = Context();
        var publication = await verification.DocumentPublications.SingleAsync(value => value.OwnerSourceRevisionId == ownerRevisionId);
        Assert.Equal(expected.InputRevisionId, publication.DocumentInputSourceRevisionId);
        Assert.Equal(expected.BranchId, publication.SourceProcessorBranchId);
        Assert.Equal(expected.RecordId, publication.PipelineRecordId);
    }

    private FluxKnowledgeDbContext Context() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed record DocumentCandidate(
        Guid InputRevisionId,
        Guid BranchId,
        Guid RecordId,
        Guid JobId,
        Guid DispatchId,
        string Name,
        DateTimeOffset LeaseExpiresAtUtc);
}
