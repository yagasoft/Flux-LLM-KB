using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Integrations.Files;
using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class Phase3BCorpusAndEventsBrowserTests
{
    [BrowserFact]
    public async Task Synthetic_SQL_records_are_browsable_in_Corpus_and_durable_Events()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgePhase3BIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgePhase3BIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot); Directory.CreateDirectory(indexRoot);
        try
        {
            var physicalIdentity = PhysicalFileIdentity.GetDirectory(ingressRoot);
            var root = Guid.NewGuid(); var identity = Guid.NewGuid(); var revision = Guid.NewGuid(); var record = Guid.NewGuid(); var directIdentity = Guid.NewGuid(); var directRecord = Guid.NewGuid(); var deferredIdentity = Guid.NewGuid(); var deferredRevision = Guid.NewGuid(); var deferredRecord = Guid.NewGuid();
            const string correlation = "phase3b-browser-correlation";
            var now = DateTimeOffset.UtcNow;
            await using (var setup = Context(sql.ConnectionString))
            {
                setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = root, CanonicalPath = ingressRoot, DisplayName = "Browser root", State = (int)SourceRootState.Paused, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", MaximumFileBytes = 1024 * 1024, AllowedClassificationsJson = "[]", ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, HealthEvidenceJson = JsonSerializer.Serialize(new { physicalIdentity = new { physicalIdentity.IdentityFingerprint } }), CreatedAtUtc = now, UpdatedAtUtc = now });
                setup.SourceIdentities.AddRange(new SourceIdentityEntity { Id = identity, SourceKind = "local file", StableKey = "browser.txt", CreatedAtUtc = now }, new SourceIdentityEntity { Id = directIdentity, SourceKind = "direct", StableKey = "direct-browser", CreatedAtUtc = now }, new SourceIdentityEntity { Id = deferredIdentity, SourceKind = "local file", StableKey = "deferred.pdf", CreatedAtUtc = now });
                setup.SourceRevisions.Add(new SourceRevisionEntity { Id = revision, SourceRootId = root, StableSourceIdentity = "browser.txt", Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = Path.Combine(ingressRoot, "folder", "browser.txt"), Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now });
                setup.PipelineRecords.Add(new PipelineRecordEntity { Id = record, SourceIdentityId = identity, SourceRevisionId = revision, Revision = 1, ContentHash = new string('b', 64), RootLineageRecordId = record, RegisteredAtUtc = now });
                setup.PipelineRecords.AddRange(new PipelineRecordEntity { Id = directRecord, SourceIdentityId = directIdentity, Revision = 1, ContentHash = new string('d', 64), RootLineageRecordId = directRecord, RegisteredAtUtc = now }, new PipelineRecordEntity { Id = deferredRecord, SourceIdentityId = deferredIdentity, SourceRevisionId = deferredRevision, Revision = 1, ContentHash = new string('e', 64), RootLineageRecordId = deferredRecord, RegisteredAtUtc = now });
                setup.SourceRevisions.Add(new SourceRevisionEntity { Id = deferredRevision, SourceRootId = root, StableSourceIdentity = "deferred.pdf", Revision = 1, ContentSha256 = new string('f', 64), CanonicalPath = Path.Combine(ingressRoot, "folder", "deferred.pdf"), Classification = "DeferredCapability", Extension = ".pdf", DiscoveredAtUtc = now });
                setup.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revision, ActivityKind = 1, ExecutionClass = 0, ProcessorVersion = "v1", InputFingerprint = new string('a', 64), State = (int)SourceActivityState.Completed, ResultingPipelineRecordId = record, CreatedAtUtc = now, UpdatedAtUtc = now });
                setup.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = deferredRevision, ActivityKind = 1, ExecutionClass = 1, ProcessorVersion = "v1", InputFingerprint = new string('f', 64), State = (int)SourceActivityState.DeferredUnsupported, CreatedAtUtc = now, UpdatedAtUtc = now });
                setup.Artifacts.Add(new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record, SourceRevision = 1, Stage = 0, ContentHash = new string('c', 64), ContentType = "text/plain", SearchText = "Synthetic indexed browser snippet", CreatedAtUtc = now });
                setup.AuditEvents.Add(new AuditEventEntity { PipelineRecordId = record, SourceRootId = root, SourceRevisionId = revision, CorrelationId = correlation, EventFamily = "source", Severity = "information", EventType = "source.updated", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now });
                await setup.SaveChangesAsync();
            }
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
            await page.GotoAsync(new Uri(host.BaseAddress, "corpus").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Corpus" }).WaitForAsync();
            await page.GetByText("direct-browser", new PageGetByTextOptions { Exact = false }).WaitForAsync();
            await page.Locator(".filter-drawer summary").ClickAsync();
            await page.GetByLabel("Source root").SelectOptionAsync(root.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "folder" }).ClickAsync();
            await page.GetByText("folder\\browser.txt", new PageGetByTextOptions { Exact = false }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "folder\\browser.txt" }).WaitForAsync();
            await page.GetByText("Synthetic indexed browser snippet", new PageGetByTextOptions { Exact = false }).WaitForAsync();
            await page.GotoAsync(new Uri(host.BaseAddress, "corpus").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("folder\\deferred.pdf", new PageGetByTextOptions { Exact = false }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "folder\\deferred.pdf" }).WaitForAsync();
            Assert.False(await page.GetByRole(AriaRole.Heading, new() { Name = "Indexed text preview" }).IsVisibleAsync());
            await page.GotoAsync(new Uri(host.BaseAddress, "events").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Correlation ID").FillAsync(correlation); await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();
            await page.GetByRole(AriaRole.Cell, new() { Name = "source.updated", Exact = true }).First.WaitForAsync();
            await using (var refreshed = Context(sql.ConnectionString)) { refreshed.AuditEvents.Add(new AuditEventEntity { PipelineRecordId = record, SourceRootId = root, SourceRevisionId = revision, CorrelationId = correlation, EventFamily = "source", Severity = "information", EventType = "source.added", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now.AddSeconds(1) }); await refreshed.SaveChangesAsync(); }
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Correlation ID").FillAsync(correlation); await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();
            await page.GetByRole(AriaRole.Cell, new() { Name = "source.added", Exact = true }).First.WaitForAsync();
        }
        finally { if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, true); if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, true); }
    }
    private static FluxKnowledgeDbContext Context(string connectionString) => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options);
}
