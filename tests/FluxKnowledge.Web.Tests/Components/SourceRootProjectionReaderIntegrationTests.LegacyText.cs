using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class SourceRootProjectionReaderIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("Unknown", false, true)]
    [InlineData("DeferredPolicy", false, true)]
    [InlineData("Unknown", true, true)]
    [InlineData("Unknown", true, false)]
    public async Task Unadmitted_legacy_text_reports_policy_assessment_in_detail_and_overview_without_mutating_history(string classification, bool oversized, bool withPending)
    {
        var root = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var pending = Activity(revision, SourceActivityState.Pending);
        var retained = Activity(revision, SourceActivityState.CancelledSuperseded);
        retained.ActivityKind = (int)SourceActivityKind.DocumentParsing;
        retained.ExecutionClass = (int)ExecutionClass.DeferredCapability;
        retained.RequiredCapability = "source-artifact-store";
        retained.Reason = "artifact-io-failed";
        if (!withPending)
        {
            retained.State = (int)SourceActivityState.DeferredPolicy;
            retained.RequiredCapability = null;
            retained.Reason = "File extension is unknown to local source ingestion.";
        }
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}",
                DisplayName = "Legacy text assessment", State = (int)SourceRootState.Enabled,
                ConfigurationRevision = 1, MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var source = Revision(revision, root, "retained.dart", now, false);
            source.Classification = classification;
            if (oversized) source.ByteLength = SourcePolicyDefaults.MaximumTextBytes + 1;
            setup.SourceRevisions.Add(source);
            setup.SourceActivities.Add(retained);
            if (withPending) setup.SourceActivities.Add(pending);
            await setup.SaveChangesAsync();
        }

        var reason = oversized ? "File exceeds the effective UTF-8 text ingestion limit." : "retained-text-policy-reassessment-required";
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        var file = Assert.Single(detail.Files);
        Assert.Equal("Blocked", file.Status);
        Assert.Equal(reason, file.Reason);
        Assert.Equal(1, detail.BlockedCount);
        await using var db = factory.CreateDbContext();
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(db, [root], default))[root];
        Assert.Equal(1, summary.Blocked);
        Assert.Equal(0, summary.Pending);
        var logical = Assert.Single(summary.Files);
        Assert.Equal("Blocked", logical.Category);
        Assert.Equal(reason, logical.Reason);
        var sample = Assert.Single(PipelineFileSample.Project(summary.Files, [], [], [], new Dictionary<Guid, string>()));
        Assert.Equal("Blocked", sample.File.State);
        Assert.Equal(reason, sample.File.Reason);
        if (withPending) Assert.Equal((int)SourceActivityState.Pending, (await db.SourceActivities.SingleAsync(value => value.Id == pending.Id)).State);
        Assert.Equal(withPending ? (int)SourceActivityState.CancelledSuperseded : (int)SourceActivityState.DeferredPolicy,
            (await db.SourceActivities.SingleAsync(value => value.Id == retained.Id)).State);
        Assert.Equal(withPending ? "artifact-io-failed" : "File extension is unknown to local source ingestion.",
            (await db.SourceActivities.SingleAsync(value => value.Id == retained.Id)).Reason);
    }

    [NativeSqlServerFact]
    public async Task Verified_policy_successor_on_immutable_unknown_keeps_pending_status()
    {
        var root = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var previous = Activity(revision, SourceActivityState.DeferredPolicy);
        previous.ActivityKind = (int)SourceActivityKind.DocumentParsing;
        previous.ExecutionClass = (int)ExecutionClass.DeferredCapability;
        previous.Reason = "File extension is unknown to local source ingestion.";
        var current = Activity(revision, SourceActivityState.Pending);
        current.ProcessorVersion = RetainedDefaultPolicy.Version;
        current.DescriptorFingerprint = RetainedDefaultPolicy.TextFingerprint;
        current.AttemptEvidenceJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            policy = RetainedDefaultPolicy.Version, configurationRevision = 1,
            inputSha256 = current.InputFingerprint, verifiedByteLength = 1
        });
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}",
                DisplayName = "Admitted legacy text", State = (int)SourceRootState.Enabled,
                ConfigurationRevision = 1, MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var source = Revision(revision, root, "admitted.dart", now, false);
            source.Classification = "Unknown";
            setup.SourceRevisions.Add(source);
            setup.SourceActivities.AddRange(previous, current);
            setup.SourceActivityRelations.Add(new SourceActivityRelationEntity
            {
                Id = Guid.NewGuid(), PredecessorActivityId = previous.Id, SuccessorActivityId = current.Id,
                RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version,
                CreatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal("Pending", Assert.Single(detail.Files).Status);
        await using var db = factory.CreateDbContext();
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(db, [root], default))[root];
        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.Blocked);
    }

    [NativeSqlServerFact]
    public async Task Existing_unsupported_policy_successor_keeps_its_specific_deferral_ahead_of_idle_legacy_text()
    {
        var root = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var pending = Activity(revision, SourceActivityState.Pending);
        var old = Activity(revision, SourceActivityState.DeferredPolicy);
        old.ActivityKind = (int)SourceActivityKind.DocumentParsing;
        old.ExecutionClass = (int)ExecutionClass.DeferredCapability;
        old.Reason = "File extension is unknown to local source ingestion.";
        var unsupported = Activity(revision, SourceActivityState.DeferredUnsupported);
        unsupported.ActivityKind = (int)SourceActivityKind.DocumentParsing;
        unsupported.ProcessorVersion = RetainedDefaultPolicy.UnsupportedBinary.ProcessorVersion;
        unsupported.DescriptorFingerprint = RetainedDefaultPolicy.UnsupportedBinary.ProcessorFingerprint;
        unsupported.Reason = "No installed parser can process this retained binary format.";
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}", DisplayName = "Unsupported retained bytes",
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1, MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var source = Revision(revision, root, "unsupported.7z", now, false);
            source.Classification = "Unknown";
            setup.SourceRevisions.Add(source);
            setup.SourceActivities.AddRange(pending, old, unsupported);
            setup.SourceActivityRelations.Add(new SourceActivityRelationEntity
            {
                Id = Guid.NewGuid(), PredecessorActivityId = old.Id, SuccessorActivityId = unsupported.Id,
                RelationshipKind = RetainedDefaultPolicy.RelationshipKind, ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        var file = Assert.Single(detail.Files);
        Assert.Equal("Deferred", file.Status);
        Assert.Equal(unsupported.Reason, file.Reason);
        await using var db = factory.CreateDbContext();
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(db, [root], default))[root];
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(0, summary.Blocked);
        Assert.Equal(unsupported.Reason, Assert.Single(summary.Files).Reason);
    }

    [NativeSqlServerTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Legacy_pre_admission_reason_and_next_action_match_in_detail_and_overview_with_the_effective_root_limit(
        bool withPolicyOwner, bool lowerLimit)
    {
        var root = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var pending = Activity(revision, SourceActivityState.Pending);
        var prior = Activity(revision, withPolicyOwner ? SourceActivityState.DeferredPolicy : SourceActivityState.CancelledSuperseded);
        prior.ActivityKind = (int)SourceActivityKind.DocumentParsing;
        prior.ExecutionClass = (int)ExecutionClass.DeferredCapability;
        prior.Reason = withPolicyOwner ? "File extension is unknown to local source ingestion." : "artifact-io-failed";
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}", DisplayName = "Legacy policy precedence",
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                MaximumFileBytes = lowerLimit ? 1024 * 1024 : SourcePolicyDefaults.MaximumTextBytes,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var source = Revision(revision, root, "unadmitted.dart", now, false);
            source.Classification = "Unknown";
            source.ByteLength = lowerLimit ? 2 * 1024 * 1024 : 1;
            setup.SourceRevisions.Add(source);
            setup.SourceActivities.AddRange(pending, prior);
            await setup.SaveChangesAsync();
        }
        var reason = lowerLimit ? "File exceeds the effective UTF-8 text ingestion limit." : "File extension is unknown to local source ingestion.";
        var nextAction = lowerLimit ? "Review the file size and the source's effective text limit."
            : "Reassess the retained input under the current source policy before queueing it.";
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal("Blocked", Assert.Single(detail.Files).Status);
        Assert.Equal(reason, Assert.Single(detail.Files).Reason);
        await using var db = factory.CreateDbContext();
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(db, [root], default))[root];
        var logical = Assert.Single(summary.Files);
        Assert.Equal("Blocked", logical.Category);
        Assert.Equal(reason, logical.Reason);
        var sample = Assert.Single(PipelineFileSample.Project(summary.Files, [], [], [], new Dictionary<Guid, string>()));
        Assert.Equal(reason, sample.File.Reason);
        Assert.Equal(nextAction, sample.File.NextAction);
        Assert.Equal((int)SourceActivityState.Pending, (await db.SourceActivities.SingleAsync(value => value.Id == pending.Id)).State);
        Assert.Equal(prior.State, (await db.SourceActivities.SingleAsync(value => value.Id == prior.Id)).State);
    }
}
