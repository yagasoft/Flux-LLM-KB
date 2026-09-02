using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceRootProjectionReaderIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Source_root_projections_derive_visible_counts_from_unsuppressed_activity_state_and_terminal_evidence()
    {
        var rootId = Guid.NewGuid();
        var completedRevisionId = Guid.NewGuid();
        var deferredRevisionId = Guid.NewGuid();
        var blockedRevisionId = Guid.NewGuid();
        var failedRevisionId = Guid.NewGuid();
        var suppressedRevisionId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-09T09:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}",
                DisplayName = "Source projection",
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
            var sourceIdentityId = Guid.NewGuid();
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId,
                SourceKind = "projection-test",
                StableKey = $"projection:{recordId:N}",
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
                DiscoveredFileCount = 5,
                ErrorFileCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var list = Assert.Single(await reader.ReadRootsAsync(CancellationToken.None));
        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, list.IndexedCount);
        Assert.Equal(1, list.DeferredCount);
        Assert.Equal(1, list.BlockedCount);
        Assert.Equal(2, list.ErrorCount);
        Assert.Equal(5, detail.DiscoveredCount);
        Assert.Equal(1, detail.IndexedCount);
        Assert.Equal(1, detail.DeferredCount);
        Assert.Equal(1, detail.BlockedCount);
        Assert.Equal(2, detail.ErrorCount);
    }

    [NativeSqlServerFact]
    public async Task Source_root_detail_projects_a_legacy_pdf_generic_reason_without_mutating_the_stored_activity()
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-03T21:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}",
                DisplayName = "Legacy PDF projection",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16 * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId,
                SourceRootId = rootId,
                StableSourceIdentity = $"legacy-pdf:{revisionId:N}",
                Revision = 1,
                ContentSha256 = new string('b', 64),
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}\\legacy.pdf",
                Classification = "DeferredCapability",
                Extension = ".pdf",
                ByteLength = 1,
                DiscoveredAtUtc = now,
                DiscoveryEvidenceJson = "{}"
            });
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activityId,
                SourceRevisionId = revisionId,
                ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.DeferredCapability,
                ProcessorVersion = "phase-3a-v1",
                InputFingerprint = new string('b', 64),
                State = (int)SourceActivityState.DeferredUnsupported,
                Reason = "Binary signature requires a capability that is not registered.",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains(detail.DeferredOrBlockedReasons, value =>
            value.State == nameof(SourceActivityState.DeferredUnsupported) &&
            value.Reason == "pdf-parser-unavailable" && value.Count == 1);
        await using var verification = factory.CreateDbContext();
        Assert.Equal("Binary signature requires a capability that is not registered.",
            (await verification.SourceActivities.SingleAsync(value => value.Id == activityId)).Reason);
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
        StableSourceIdentity = $"identity:{id:N}",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}\\{path}",
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

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoPathPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class NoEnumeration : ISourceFileEnumerator
    {
        public IReadOnlyList<SourceEnumerationEvidence> LastEvidence { get; } = [];

        public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
            SourceRootConfiguration sourceRoot,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
