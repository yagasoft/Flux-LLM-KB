using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Final_graph_queries_preserve_every_force_action_and_relation_ownership_arm()
    {
        var graph = await SeedArtifactPageGraphAsync([(40, 0), (40, 0)]);
        var scopes = await SeedPublicationQueryGraphAsync(graph.Source, graph.Control, 0);
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var policy = new OperatorActionCapabilityPolicyEntity
        {
            PolicyId = Guid.NewGuid(), PolicyRevision = 1, DescriptorId = Guid.NewGuid(), DescriptorFingerprint = hash,
            DescriptorVersion = "synthetic-v1", SafetyContractId = "synthetic-safety", HandlerId = "synthetic-handler",
            ActionKind = "force", ReasonCode = "synthetic-policy"
        };
        var ownedRelations = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var foreignRelations = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var foreignRequest = Guid.NewGuid();
        var foreignAction = 5.ToString("x64");
        await using (var setup = CreateContext())
        {
            setup.OperatorActionCapabilityPolicies.Add(policy);
            foreach (var id in ownedRelations) AddActivity(id, graph.Source.RevisionId);
            foreach (var id in foreignRelations) AddActivity(id, graph.Control.RevisionId);
            AddRelation(scopes.OwnedActivity, foreignRelations[0]);
            AddRelation(foreignRelations[1], ownedRelations[0]);
            AddRelation(ownedRelations[1], ownedRelations[2]);
            AddRelation(foreignRelations[2], foreignRelations[3]);
            // Each force-request ownership OR arm is independently sufficient.
            AddAction(1, Guid.NewGuid(), scopes.OwnedBranch, scopes.ForeignActivity, graph.Control.RevisionId, scopes.ForeignBranch);
            AddAction(2, Guid.NewGuid(), scopes.ForeignBranch, scopes.ForeignActivity, graph.Source.RevisionId, scopes.ForeignBranch);
            AddAction(3, Guid.NewGuid(), scopes.ForeignBranch, scopes.OwnedActivity, graph.Control.RevisionId, scopes.ForeignBranch);
            // Direct branch ownership is independent of a force-request link.
            AddAction(4, null, scopes.OwnedBranch, scopes.OwnedActivity, graph.Source.RevisionId, scopes.OwnedBranch);
            AddAction(5, foreignRequest, scopes.ForeignBranch, scopes.ForeignActivity, graph.Control.RevisionId, scopes.ForeignBranch);
            await setup.SaveChangesAsync();

            void AddActivity(Guid id, Guid revision) => setup.SourceActivities.Add(new()
            {
                Id = id, SourceRevisionId = revision, ActivityKind = 0, ExecutionClass = 0,
                ProcessorVersion = id.ToString("N"), InputFingerprint = hash,
                State = (int)SourceActivityState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            void AddRelation(Guid predecessor, Guid successor) => setup.SourceActivityRelations.Add(new()
            {
                Id = Guid.NewGuid(), PredecessorActivityId = predecessor, SuccessorActivityId = successor,
                RelationshipKind = "synthetic", ReasonCode = "synthetic", CreatedAtUtc = now
            });
            void AddAction(int number, Guid? request, Guid requestBranch, Guid activity, Guid revision, Guid actionBranch)
            {
                var action = number.ToString("x64");
                var operation = Guid.NewGuid();
                if (request.HasValue)
                    setup.SourceProcessorForceRequests.Add(new()
                    {
                        Id = request.Value, ActionId = action, OperationId = operation, RequestFingerprint = hash,
                        PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision, DescriptorId = policy.DescriptorId,
                        DescriptorFingerprint = policy.DescriptorFingerprint, DescriptorVersion = policy.DescriptorVersion,
                        SafetyContractId = policy.SafetyContractId, HandlerId = policy.HandlerId, ActionKind = policy.ActionKind,
                        PolicyReasonCode = policy.ReasonCode, SourceProcessorBranchId = requestBranch,
                        SourceActivityId = activity, SourceRevisionId = revision, ExpectedInputSha256 = hash,
                        OriginalBlockedRowVersion = BitConverter.GetBytes((long)number), OriginalOutcomeCode = "synthetic-blocked",
                        RequestedAtUtc = now, ClaimExpiresAtUtc = now.AddMinutes(10), State = 0
                    });
                setup.OperatorActionActionLedger.Add(new()
                {
                    ActionId = action, PolicyId = policy.PolicyId, PolicyRevision = policy.PolicyRevision,
                    DescriptorId = policy.DescriptorId, DescriptorFingerprint = policy.DescriptorFingerprint,
                    DescriptorVersion = policy.DescriptorVersion, SafetyContractId = policy.SafetyContractId,
                    HandlerId = policy.HandlerId, ActionKind = policy.ActionKind, ReasonCode = policy.ReasonCode,
                    SourceProcessorBranchId = actionBranch, SourceProcessorForceRequestId = request,
                    BlockedRowVersion = BitConverter.GetBytes((long)number), CreatedAtUtc = now
                });
                setup.OperatorActionOperationLedger.Add(new()
                { OperationId = operation, RequestFingerprint = hash, ActionId = action, CreatedAtUtc = now });
                setup.SourceProcessorActionIgnoreHeads.Add(new()
                { ActionId = action, Sequence = 1, IsIgnored = true, UpdatedAtUtc = now });
            }
        }

        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records", (await store.PurgeAsync(work, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);
        await using (var check = CreateContext())
        {
            Assert.Equal(foreignRequest, (await check.SourceProcessorForceRequests.SingleAsync()).Id);
            Assert.Equal(foreignAction, (await check.OperatorActionActionLedger.SingleAsync()).ActionId);
            Assert.Equal(foreignAction, (await check.OperatorActionOperationLedger.SingleAsync()).ActionId);
            Assert.Equal(foreignAction, (await check.SourceProcessorActionIgnoreHeads.SingleAsync()).ActionId);
            var relation = await check.SourceActivityRelations.SingleAsync();
            Assert.Equal(foreignRelations[2], relation.PredecessorActivityId);
            Assert.Equal(foreignRelations[3], relation.SuccessorActivityId);
            Assert.False(await check.SourceActivities.AnyAsync(value => value.SourceRevisionId == graph.Source.RevisionId));
            Assert.Equal(scopes.ForeignBranch, (await check.SourceProcessorBranches.SingleAsync()).Id);
        }
        await CompleteArtifactGraphAsync(store, graph.Source, graph.Control, graph.Operation);
    }
}
