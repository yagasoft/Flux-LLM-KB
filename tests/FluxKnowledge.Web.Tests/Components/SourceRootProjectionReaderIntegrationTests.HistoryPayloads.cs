using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class SourceRootProjectionReaderIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Source_pages_select_current_and_finished_summaries_without_loading_scan_manifests_or_history_bodies()
    {
        var root = Guid.NewGuid(); var unfinished = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            foreach (var id in new[] { root, unfinished })
                setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = id, CanonicalPath = $"I:\\scan-history-summary\\{id:N}", DisplayName = id.ToString(),
                    State = (int)SourceRootState.Enabled, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
                });
            for (var index = 0; index < 12; index++)
                setup.SourceScanRequests.Add(new SourceScanRequestEntity
                {
                    Id = Guid.NewGuid(), SourceRootId = root, RequestedAtUtc = now.AddMinutes(-20 + index), RequestedBy = "fixture",
                    State = (int)SourceScanRequestState.Completed, ErrorFileCount = 100 + index,
                    IncrementalManifestJson = "{\"history\":\"" + new string('x', 64 * 1024) + "\"}", AuditEvidenceJson = "{}"
                });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(), SourceRootId = root, RequestedAtUtc = now.AddMinutes(-2), RequestedBy = "fixture",
                State = (int)SourceScanRequestState.Failed, ErrorFileCount = 17, DiscoveredFileCount = 31, AuditEvidenceJson = "{}",
                IncrementalManifestJson = new string('x', 64 * 1024)
            });
            foreach (var id in new[] { root, unfinished })
                setup.SourceScanRequests.Add(new SourceScanRequestEntity
                {
                    Id = Guid.NewGuid(), SourceRootId = id, RequestedAtUtc = now.AddMinutes(-1), RequestedBy = "fixture",
                    State = (int)SourceScanRequestState.Running, ErrorFileCount = 800, IncrementalManifestJson = new string('x', 64 * 1024)
                });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(), SourceRootId = root, RequestedAtUtc = now, RequestedBy = "fixture",
                State = (int)SourceScanRequestState.Completed, RequestKind = SqlSourceVerificationStore.RequestKind, ErrorFileCount = 999
            });
            await setup.SaveChangesAsync();
        }
        var trace = new ScanPayloadTrace();
        var reader = new SourceRootProjectionReader(new HistoryProjectionFactory(_fixture.ConnectionString, trace),
            new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var roots = await reader.ReadRootsAsync(default);
        Assert.Equal(17, roots.Single(value => value.Id == root).LastScanIssueCount);
        Assert.Equal(0, roots.Single(value => value.Id == unfinished).LastScanIssueCount);
        trace.AllowLatestAudit = true;
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal("Running", detail.ScanState);
        Assert.Equal(31, detail.DiscoveredCount);
        Assert.Equal(17, detail.LastScanIssueCount);
        var neverFinished = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(unfinished, default));
        Assert.Equal("Running", neverFinished.ScanState);
        Assert.Equal(0, neverFinished.DiscoveredCount);
        Assert.Equal(0, neverFinished.LastScanIssueCount);
    }

    private sealed class ScanPayloadTrace : DbCommandInterceptor
    {
        internal bool AllowLatestAudit { get; set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            var columns = Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray();
            Assert.DoesNotContain("IncrementalManifestJson", columns);
            if (!AllowLatestAudit) Assert.DoesNotContain("AuditEvidenceJson", columns);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HistoryProjectionFactory(string connection, DbCommandInterceptor trace) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(trace).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
