using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SqlProjectionReaderIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Single_record_projection_translates_and_returns_the_registered_record()
    {
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var store = new SqlPipelineStore(factory);
        var receipt = await store.RegisterAsync(
            new Utf8FileRegistration(
                $"C:\\ingress\\{Guid.NewGuid():N}.txt",
                new string('a', 64),
                "integration-test",
                null),
            CancellationToken.None);

        var reader = new SqlProjectionReader(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
            DerivedIndexRecoveryState.Healthy,
            null,
            null,
            null,
            null,
            0)), new SqlGpuSchedulerStore(factory));
        var projection = await reader.ReadPipelineRecordAsync(
            receipt.PipelineRecordId.Value,
            CancellationToken.None);

        Assert.NotNull(projection);
        Assert.Equal(receipt.PipelineRecordId.Value, projection.Id);
        Assert.Equal("WorkerQueued", projection.Status);
    }

    [NativeSqlServerFact]
    public async Task Overview_projection_includes_the_safe_derived_index_recovery_summary()
    {
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var reader = new SqlProjectionReader(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
            DerivedIndexRecoveryState.RetryScheduled,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T08:00:05Z"),
            DerivedIndexRecoveryFailureCategory.TransientIo,
            4)), new SqlGpuSchedulerStore(factory));

        var overview = await reader.ReadOverviewAsync(CancellationToken.None);

        Assert.Equal("RetryScheduled", overview.IndexRecovery.State);
        Assert.Equal("aaaaaaaabbbbccccddddeeeeeeeeeeee", overview.IndexRecovery.ActiveGeneration);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T08:00:05Z"), overview.IndexRecovery.NextRetryAtUtc);
        Assert.Equal("TransientIo", overview.IndexRecovery.FailureCategory);
        Assert.Equal(4, overview.IndexRecovery.CleanedCandidateCount);
    }

    [NativeSqlServerFact]
    public async Task Overview_source_indexing_uses_unsuppressed_activity_state_and_receipts_instead_of_stale_scan_totals()
    {
        var now = DateTimeOffset.Parse("2026-08-09T10:30:00+00:00");
        var rootId = Guid.NewGuid();
        var completedRevisionId = Guid.NewGuid();
        var deferredRevisionId = Guid.NewGuid();
        var blockedRevisionId = Guid.NewGuid();
        var failedRevisionId = Guid.NewGuid();
        var suppressedRevisionId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var sourceIdentityId = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"C:\\overview-source-indexing\\{rootId:N}",
                DisplayName = "Overview source indexing",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                Revision(completedRevisionId, rootId, "completed.txt", now, false),
                Revision(deferredRevisionId, rootId, "deferred.pdf", now, false),
                Revision(blockedRevisionId, rootId, "blocked.cs", now, false),
                Revision(failedRevisionId, rootId, "failed.txt", now, false),
                Revision(suppressedRevisionId, rootId, "historic.txt", now, true));
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId,
                SourceKind = "overview-source-indexing",
                StableKey = $"overview:{recordId:N}",
                CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceIdentityId,
                SourceRevisionId = completedRevisionId,
                Revision = 1,
                ContentHash = new string('a', 64),
                RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true,
                RegisteredAtUtc = now
            });
            setup.SourceActivities.AddRange(
                Activity(completedRevisionId, SourceActivityState.Completed, recordId),
                Activity(deferredRevisionId, SourceActivityState.DeferredUnsupported),
                Activity(blockedRevisionId, SourceActivityState.DeferredPolicy),
                Activity(failedRevisionId, SourceActivityState.FailedTerminal),
                Activity(suppressedRevisionId, SourceActivityState.Completed));
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(),
                SourceRootId = rootId,
                RequestKind = 0,
                RequestedBy = "test",
                RequestedAtUtc = now,
                IsReleased = true,
                ReleasedAtUtc = now,
                State = (int)SourceScanRequestState.Completed,
                IndexedFileCount = 99,
                DeferredFileCount = 98,
                BlockedFileCount = 97,
                ErrorFileCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SqlProjectionReader(
            factory,
            new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
                DerivedIndexRecoveryState.Healthy,
                null,
                null,
                null,
                null,
                0)),
            new SqlGpuSchedulerStore(factory));

        var overview = await reader.ReadOverviewAsync(CancellationToken.None);

        Assert.Equal(1, overview.SourceIndexing.RootCount);
        Assert.Equal(1, overview.SourceIndexing.IndexedCount);
        Assert.Equal(1, overview.SourceIndexing.DeferredCount);
        Assert.Equal(1, overview.SourceIndexing.BlockedCount);
        Assert.Equal(2, overview.SourceIndexing.ErrorCount);
    }

    [NativeSqlServerFact]
    public async Task Overview_projection_reads_the_sanitised_GPU_scheduler_SQL_snapshot()
    {
        var now = DateTimeOffset.Parse("2026-07-30T12:00:00+00:00");
        var nextDeferredAtUtc = now.AddMinutes(5);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var arrange = factory.CreateDbContext())
        {
            arrange.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = "projection-slot",
                State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = now
            });
            var scheduler = await arrange.GpuSchedulerStates.SingleAsync(state => state.Id == 1);
            scheduler.NextDeferredAtUtc = nextDeferredAtUtc;
            scheduler.UpdatedAtUtc = now;
            await arrange.SaveChangesAsync();
        }

        var reader = new SqlProjectionReader(
            factory,
            new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
                DerivedIndexRecoveryState.Healthy,
                null,
                null,
                null,
                null,
                0)),
            new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(now)));

        var overview = await reader.ReadOverviewAsync(CancellationToken.None);

        Assert.Equal(1, overview.GpuSchedulerStatus.AvailableSlotCount);
        Assert.False(overview.GpuSchedulerStatus.HasActiveBatch);
        Assert.Null(overview.GpuSchedulerStatus.ActiveBatchLane);
        Assert.Equal(nextDeferredAtUtc, overview.GpuSchedulerStatus.NextDeferredAtUtc);
        Assert.Equal("None", overview.GpuSchedulerStatus.UncertainCapacity.State);
        Assert.Null(overview.GpuSchedulerStatus.UncertainCapacity.AgeMinutes);
    }

    private sealed class FixedRecoveryStatus(DerivedIndexRecoverySnapshot snapshot)
        : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = snapshot;
    }

    private static SourceRevisionEntity Revision(
        Guid id,
        Guid rootId,
        string path,
        DateTimeOffset now,
        bool suppressed) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = $"overview-source:{id:N}",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"C:\\overview-source-indexing\\{rootId:N}\\{path}",
        Classification = "AcceptedUtf8Text",
        Extension = Path.GetExtension(path),
        ByteLength = 1,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}",
        SuppressedAtUtc = suppressed ? now : null
    };

    private static SourceActivityEntity Activity(
        Guid sourceRevisionId,
        SourceActivityState state,
        Guid? resultingPipelineRecordId = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceRevisionId = sourceRevisionId,
        ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)(state == SourceActivityState.DeferredUnsupported
            ? ExecutionClass.DeferredCapability
            : ExecutionClass.InProcess),
        ProcessorVersion = "phase-3a-v1",
        InputFingerprint = new string('a', 64),
        State = (int)state,
        ResultingPipelineRecordId = resultingPipelineRecordId,
        ResultingPipelineRecordRevision = resultingPipelineRecordId is null ? null : 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class TestDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
