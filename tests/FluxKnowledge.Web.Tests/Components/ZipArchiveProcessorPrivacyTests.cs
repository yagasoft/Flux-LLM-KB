using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class ZipArchiveProcessorPrivacyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string PrivateRootSentinel = "C:\\private-spool-sentinel";
    private const string MemberNameSentinel = "confidential-member-sentinel.txt";

    [NativeSqlServerFact]
    public async Task Sources_corpus_events_and_pipeline_serialisations_exclude_private_zip_spool_and_member_names()
    {
        var rootId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var sourceIdentityId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = "C:\\phase5-public-root", DisplayName = "Retained archive", State = (int)SourceRootState.Enabled,
                Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 64L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, DisplayName = "Private capture", SpoolRoot = PrivateRootSentinel,
                IncrementalBasis = 0, State = 0, IsEnabled = false, ConfigurationRevision = 1, CadenceTicks = TimeSpan.FromMinutes(5).Ticks,
                MaximumOverlapTicks = TimeSpan.FromMinutes(1).Ticks, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                new SourceRevisionEntity { Id = parentId, SourceRootId = rootId, StableSourceIdentity = "zip-parent", Revision = 1, ContentSha256 = hash,
                    CanonicalPath = "C:\\phase5-public-root\\retained.zip", Classification = "DeferredCapability", Extension = ".zip", ByteLength = 1, DiscoveredAtUtc = now },
                new SourceRevisionEntity { Id = childId, SourceRootId = rootId, ParentSourceRevisionId = parentId,
                    StableSourceIdentity = "retained-archive-member:opaque", Revision = 1, ContentSha256 = new string('b', 64),
                    CanonicalPath = "C:\\phase5-public-root\\retained-archive-members\\opaque", Classification = "AcceptedUtf8Text", Extension = ".txt", OriginKind = 1, ByteLength = 1, DiscoveredAtUtc = now });
            setup.SourceIdentities.Add(new SourceIdentityEntity { Id = sourceIdentityId, SourceKind = "retained archive", StableKey = "retained-archive-member:opaque", CreatedAtUtc = now });
            setup.PipelineRecords.Add(new PipelineRecordEntity { Id = pipelineId, SourceIdentityId = sourceIdentityId, SourceRevisionId = childId, Revision = 1,
                ContentHash = new string('b', 64), RootLineageRecordId = pipelineId, CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true, RegisteredAtUtc = now });
            setup.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = childId, ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-3a-v1", InputFingerprint = new string('b', 64),
                State = (int)SourceActivityState.Completed, ResultingPipelineRecordId = pipelineId, ResultingPipelineRecordRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.AuditEvents.Add(new AuditEventEntity { SourceRootId = rootId, SourceRevisionId = childId, CorrelationId = "phase5-zip", EventFamily = "source",
                Severity = "information", EventType = "retained.archive.completed", Actor = "test",
                DetailsJson = JsonSerializer.Serialize(new { PrivateRootSentinel, MemberNameSentinel }), OccurredAtUtc = now });
            await setup.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(fixture.ConnectionString);
        var sources = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootsAsync(CancellationToken.None);
        var corpus = await new SqlCorpusProjectionReader(factory).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
        var events = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(new OperatorEventQuery(), CancellationToken.None);
        var pipeline = await new SqlProjectionReader(factory, new HealthyRecoveryStatus(), new SqlGpuSchedulerStore(factory))
            .ReadPipelineRecordAsync(pipelineId, CancellationToken.None);
        var serialised = JsonSerializer.Serialize(new { sources, corpus, events, pipeline });

        Assert.DoesNotContain(PrivateRootSentinel, serialised, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberNameSentinel, serialised, StringComparison.Ordinal);
        Assert.Equal("{\"sanitised\":true}", Assert.Single(events.Items).Details);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class HealthyRecoveryStatus : FluxKnowledge.Application.Indexing.IDerivedIndexRecoveryStatus
    {
        public FluxKnowledge.Application.Indexing.DerivedIndexRecoverySnapshot Snapshot { get; } = new(
            FluxKnowledge.Application.Indexing.DerivedIndexRecoveryState.Healthy, null, null, null, null, 0);
    }
}
