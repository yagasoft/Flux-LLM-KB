using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed partial class DocumentPublicationIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("paused-root")]
    [InlineData("unknown-policy")]
    [InlineData("unknown-descriptor")]
    [InlineData("missing-stage")]
    [InlineData("withdrawn-publication")]
    [InlineData("later-physical-revision")]
    [InlineData("derived-owner")]
    [InlineData("suppressed-child")]
    [InlineData("unreferenced-artifact")]
    [InlineData("leased-branch")]
    [InlineData("cyclic-policy")]
    [InlineData("malformed-correlation")]
    [InlineData("later-failure")]
    [InlineData("one-hop")]
    [InlineData("two-hop")]
    [InlineData("unknown-intermediate")]
    [InlineData("changed-intermediate-hash")]
    [InlineData("later-invalid-edge")]
    [InlineData("unbound-prior-child")]
    [InlineData("branch-only-predecessor")]
    public async Task Selected_office_success_resolves_only_proved_earlier_policy_chain_events(string change)
    {
        var seed = await SeedOfficeEventChainAsync(change == "later-physical-revision",
            change is "one-hop" or "later-invalid-edge" ? 1 : change == "two-hop" ? 2 : 3);
        var reader = new SqlOperatorEventProjectionReader(SqlTestData.CreateFactory(_fixture));
        var query = new OperatorEventQuery(new OperatorEventFilters(Severity: "error"), PageSize: 1);
        var warnings = new OperatorEventQuery(new OperatorEventFilters(Severity: "warning", SourceRootId: seed.RootId), PageSize: 1);
        Assert.Equal(seed.PipelineEventId, Assert.Single((await reader.ReadPageAsync(query, CancellationToken.None)).Items).Id);
        Assert.Equal(seed.BlockedEventId, Assert.Single((await reader.ReadPageAsync(warnings, CancellationToken.None)).Items).Id);
        await PublishAsync(new SqlStageTransitionStore(SqlTestData.CreateFactory(_fixture)), seed.Current, DateTimeOffset.UtcNow);
        await using (var mutate = Context())
        {
            if (change == "paused-root")
                (await mutate.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused;
            if (change == "unknown-policy")
                (await mutate.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seed.PriorOwnerActivityId)).ReasonCode = "unrecognised-policy";
            if (change == "unknown-descriptor")
                (await mutate.SourceActivities.SingleAsync(value => value.Id == seed.PriorOwnerActivityId)).DescriptorFingerprint = "unrecognised-office-fingerprint";
            if (change == "missing-stage")
                mutate.Jobs.Remove(await mutate.Jobs.SingleAsync(value => value.PipelineRecordId == seed.Current.RecordId && value.Stage == (int)PipelineStage.CanonicalIndex));
            if (change == "withdrawn-publication")
                mutate.DocumentPublications.Remove(await mutate.DocumentPublications.SingleAsync());
            if (change == "derived-owner")
                (await mutate.SourceRevisions.SingleAsync(value => value.Id == seed.OwnerId)).OriginKind = 1;
            if (change == "suppressed-child")
                (await mutate.SourceRevisions.SingleAsync(value => value.Id == seed.Current.InputRevisionId)).SuppressedAtUtc = DateTimeOffset.UtcNow;
            if (change == "unreferenced-artifact")
                (await mutate.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == seed.Current.InputRevisionId)).ReferenceCount = 0;
            if (change == "leased-branch")
            {
                var branch = await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seed.Current.BranchId);
                branch.LeaseOwner = "unexpected-owner";
                branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            if (change == "cyclic-policy")
            {
                var currentActivity = (await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seed.Current.BranchId)).SourceActivityId;
                (await mutate.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId != seed.PriorOwnerActivityId &&
                    value.SuccessorActivityId != currentActivity)).SuccessorActivityId = seed.PriorOwnerActivityId;
            }
            if (change is "unknown-intermediate" or "changed-intermediate-hash")
            {
                var intermediateId = (await mutate.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seed.PriorOwnerActivityId)).SuccessorActivityId;
                var intermediate = await mutate.SourceActivities.SingleAsync(value => value.Id == intermediateId);
                if (change == "unknown-intermediate") intermediate.DescriptorFingerprint = "unknown-intermediate";
                else intermediate.InputFingerprint = new string('c', 64);
            }
            if (change == "later-invalid-edge")
            {
                var selectedActivity = (await mutate.SourceProcessorBranches.SingleAsync(value => value.Id == seed.Current.BranchId)).SourceActivityId;
                var next = new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = seed.OwnerId, ActivityKind = (int)SourceActivityKind.TextExtraction,
                    ExecutionClass = 1, ProcessorVersion = "unknown-future", DescriptorFingerprint = "unknown-future", InputFingerprint = new string('b', 64),
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                mutate.SourceActivities.Add(next);
                mutate.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = selectedActivity, SuccessorActivityId = next.Id,
                    RelationshipKind = "unrecognised-policy", ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = DateTimeOffset.UtcNow });
            }
            if (change is "unbound-prior-child" or "branch-only-predecessor")
            {
                var priorMember = await mutate.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == seed.Prior.BranchId);
                if (change == "unbound-prior-child") priorMember.ChildSourceActivityId = null;
                else mutate.SourceProcessorBranchMembers.Remove(priorMember);
            }
            if (change is "malformed-correlation" or "later-failure")
            {
                var later = DateTimeOffset.UtcNow.AddMinutes(1);
                foreach (var value in await mutate.AuditEvents.Where(value => seed.EventIds.Contains(value.Id) && value.Id != seed.IndependentEventId).ToArrayAsync())
                {
                    if (change == "malformed-correlation") value.CorrelationId += ":unrecognised";
                    else value.OccurredAtUtc = later.AddTicks(value.Id == seed.PipelineEventId ? 1 : 0);
                }
            }
            await mutate.SaveChangesAsync();
        }
        var page = await reader.ReadPageAsync(query, CancellationToken.None);
        var resolved = change is "none" or "paused-root" or "later-physical-revision" or "one-hop" or "two-hop" or "later-invalid-edge";
        Assert.Equal(resolved ? seed.IndependentEventId : seed.PipelineEventId, Assert.Single(page.Items).Id);
        if (resolved) Assert.Null(page.NextCursor);
        var warningPage = await reader.ReadPageAsync(warnings, CancellationToken.None);
        if (resolved || change is "unbound-prior-child" or "branch-only-predecessor") Assert.Empty(warningPage.Items);
        else Assert.Equal(seed.BlockedEventId, Assert.Single(warningPage.Items).Id);
        await using var verify = Context();
        Assert.Equal(4, await verify.AuditEvents.CountAsync(value => seed.EventIds.Contains(value.Id)));
        Assert.Equal(seed.PolicyEdgeCount + (change == "later-invalid-edge" ? 1 : 0), await verify.SourceActivityRelations.CountAsync());
        Assert.True(await verify.Jobs.AnyAsync(value => value.PipelineRecordId == seed.Prior.RecordId));
    }

    private async Task<OfficeEventChain> SeedOfficeEventChainAsync(bool physicalHistory = false, int hops = 3)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var rootId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        await using var context = Context();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = "C:\\office-event-proof", DisplayName = "Office event proof", State = 0,
            IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
            MaximumFileBytes = 256 * 1024 * 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = ownerId, SourceRootId = rootId, StableSourceIdentity = "office-event-proof", Revision = 1,
            ContentSha256 = new string('b', 64), CanonicalPath = "C:\\office-event-proof\\original.docx",
            Classification = "OoxmlDocumentContainer", Extension = ".docx", ByteLength = 4, DiscoveredAtUtc = now
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = ownerId, ContentSha256 = new string('b', 64),
            StoreRelativePath = Path.Combine("sha256", "bb", $"{new string('b', 64)}.bin"),
            ByteLength = 4, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
        });
        if (physicalHistory)
            SqlTestData.AddPhysicalRevisionHistory(context, context.SourceRevisions.Local.Single(value => value.Id == ownerId));
        if (!await context.SourceCapabilities.AnyAsync(value => value.Id == OoxmlStructuralTextProcessor.Capability.Id))
            context.SourceCapabilities.Add(new SourceCapabilityEntity
            {
                Id = OoxmlStructuralTextProcessor.Capability.Id, ProcessorKind = OoxmlStructuralTextProcessor.Capability.ProcessorKind,
                ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion,
                ProcessorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint,
                ExecutionClass = (int)ExecutionClass.InProcess, AcceptedClassificationsJson = "[\"OoxmlDocumentContainer\"]",
                OutputContract = OoxmlStructuralTextProcessor.Capability.OutputContract,
                IsRunnable = true, RegisteredBy = "test", RegisteredAtUtc = now
            });
        var prior = SeedCandidate(context, rootId, ownerId, now, "office-event-prior", 0, ooxmlText: true);
        var current = SeedCandidate(context, rootId, ownerId, now, "office-event-current", 1, ooxmlText: true);
        var priorBranch = context.SourceProcessorBranches.Local.Single(value => value.Id == prior.BranchId);
        var currentBranch = context.SourceProcessorBranches.Local.Single(value => value.Id == current.BranchId);
        currentBranch.CompletionReceiptFingerprint = new string('f', 64);
        var priorOwner = context.SourceActivities.Local.Single(value => value.Id == priorBranch.SourceActivityId);
        priorOwner.ProcessorVersion = priorBranch.ProcessorVersion = OoxmlStructuralTextProcessor.LegacyProcessorVersion;
        priorOwner.DescriptorFingerprint = priorBranch.ProcessorFingerprint = OoxmlStructuralTextProcessor.LegacyProcessorFingerprint;
        foreach (var branch in new[] { priorBranch, currentBranch })
        {
            branch.InputSha256 = new string('b', 64);
            // A completed retained branch can leave the owner activity pending.
            var activity = context.SourceActivities.Local.Single(value => value.Id == branch.SourceActivityId);
            activity.InputFingerprint = new string('b', 64);
            activity.State = (int)SourceActivityState.Pending;
        }
        // Derived text differs across processor versions, although the immutable owner does not.
        context.SourceRevisions.Local.Single(value => value.Id == current.InputRevisionId).ContentSha256 = new string('e', 64);
        var currentArtifact = context.SourceArtifacts.Local.Single(value => value.SourceRevisionId == current.InputRevisionId);
        currentArtifact.ContentSha256 = new string('e', 64);
        currentArtifact.StoreRelativePath = Path.Combine("sha256", "ee", $"{new string('e', 64)}.bin");
        context.PipelineRecords.Local.Single(value => value.Id == current.RecordId).ContentHash = new string('e', 64);
        context.SourceActivities.Local.Single(value => value.SourceRevisionId == current.InputRevisionId).InputFingerprint = new string('e', 64);
        var priorChildActivity = context.SourceActivities.Local.Single(value => value.SourceRevisionId == prior.InputRevisionId);
        priorChildActivity.State = (int)SourceActivityState.FailedTerminal;
        context.Jobs.Local.Single(value => value.Id == prior.JobId).PublicState = (int)PublicJobState.Failed;
        var versions = new[]
        {
            (OoxmlStructuralTextProcessor.PreviousProcessorVersion, OoxmlStructuralTextProcessor.PreviousProcessorFingerprint),
            (OoxmlStructuralTextProcessor.Version3ProcessorVersion, OoxmlStructuralTextProcessor.Version3ProcessorFingerprint)
        };
        var previous = priorOwner.Id;
        foreach (var (version, fingerprint) in versions.Take(hops - 1))
        {
            var intermediate = new SourceActivityEntity
            {
                Id = Guid.NewGuid(), SourceRevisionId = ownerId, ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = version, DescriptorFingerprint = fingerprint,
                InputFingerprint = new string('b', 64), State = (int)SourceActivityState.CancelledSuperseded,
                CreatedAtUtc = now, UpdatedAtUtc = now
            };
            context.SourceActivities.Add(intermediate);
            AddPolicyEdge(previous, intermediate.Id);
            previous = intermediate.Id;
        }
        AddPolicyEdge(previous, currentBranch.SourceActivityId);
        foreach (var stage in new[] { PipelineStage.Extract, PipelineStage.Normalise, PipelineStage.CanonicalIndex, PipelineStage.Embed })
            context.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = current.RecordId, SourceRevision = 1, Stage = (int)stage,
                Operation = stage switch
                {
                    PipelineStage.Extract => PipelineOperations.ExtractUtf8,
                    PipelineStage.Normalise => PipelineOperations.NormaliseText,
                    PipelineStage.CanonicalIndex => PipelineOperations.CanonicalIndex,
                    _ => PipelineOperations.Embed
                },
                PublicState = (int)PublicJobState.Completed, DueAtUtc = now
            });
        var independent = new AuditEventEntity
        {
            EventType = "source.access_denied", EventFamily = "source", Severity = "error", Actor = "test",
            SourceRootId = rootId, SourceRevisionId = ownerId, OccurredAtUtc = now.AddMinutes(-1), DetailsJson = "{}"
        };
        var activityFailed = new AuditEventEntity
        {
            EventType = "activity.failed", EventFamily = "activity", Severity = "error", Actor = "test", DetailsJson = "{}",
            SourceRootId = rootId, SourceRevisionId = prior.InputRevisionId, SourceActivityId = priorChildActivity.Id,
            PipelineRecordId = prior.RecordId, CorrelationId = $"source:{prior.InputRevisionId:N}", OccurredAtUtc = now
        };
        var pipelineFailed = new AuditEventEntity
        {
            EventType = "pipeline.failed", EventFamily = "pipeline", Severity = "error", Actor = "test", DetailsJson = "{}",
            PipelineRecordId = prior.RecordId, CorrelationId = $"pipeline:{prior.RecordId:N}:1", OccurredAtUtc = now.AddTicks(1)
        };
        var blocked = new AuditEventEntity
        {
            EventType = "retained_processor.blocked", EventFamily = "retained_processor", Severity = "warning", Actor = "test", DetailsJson = "{}",
            SourceRootId = rootId, SourceRevisionId = ownerId, CorrelationId = $"retained-processor:{prior.BranchId:N}", OccurredAtUtc = now.AddTicks(2)
        };
        context.AuditEvents.AddRange(independent, activityFailed, pipelineFailed, blocked);
        await context.SaveChangesAsync();
        return new(rootId, ownerId, current, prior, priorOwner.Id, blocked.Id, pipelineFailed.Id, independent.Id, hops,
            [independent.Id, activityFailed.Id, pipelineFailed.Id, blocked.Id]);

        void AddPolicyEdge(Guid before, Guid after) => context.SourceActivityRelations.Add(new SourceActivityRelationEntity
        {
            Id = Guid.NewGuid(), PredecessorActivityId = before, SuccessorActivityId = after,
            RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now
        });
    }

    private sealed record OfficeEventChain(Guid RootId, Guid OwnerId, DocumentCandidate Current, DocumentCandidate Prior,
        Guid PriorOwnerActivityId, long BlockedEventId, long PipelineEventId, long IndependentEventId, int PolicyEdgeCount, long[] EventIds);
}
