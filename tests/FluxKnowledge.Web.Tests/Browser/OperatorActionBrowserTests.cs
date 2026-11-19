using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class OperatorActionBrowserTests
{
    [BrowserFact]
    public async Task Selected_archive_shows_whole_branch_preview_before_a_local_browser_can_queue_recovery()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorRecovery_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorRecoveryIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            var databaseOptions = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
            var rootId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var activityId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var hash = new string('a', 64);
            await using (var seed = new FluxKnowledgeDbContext(databaseOptions))
            {
                seed.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = ingressRoot, DisplayName = "Archive source",
                    State = (int)SourceRootState.Enabled, Recursive = true, MaximumFileBytes = 1000, ConfigurationRevision = 1,
                    ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now });
                seed.SourceRevisions.Add(new() { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = "archive-browser-test",
                    Revision = 1, CanonicalPath = Path.Combine(ingressRoot, "encoding.zip"), Extension = ".zip", Classification = "ArchiveZip",
                    ContentSha256 = hash, ByteLength = 16, DiscoveredAtUtc = now });
                seed.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash,
                    StoreRelativePath = Path.Combine("sha256", "aa", hash + ".bin"), ByteLength = 16, ChecksumVerifiedAtUtc = now });
                seed.SourceActivities.Add(new() { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.ArchiveExpansion,
                    ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-5-zip-v1", InputFingerprint = hash,
                    CreatedAtUtc = now, UpdatedAtUtc = now });
                seed.SourceProcessorBranches.Add(new() { Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId,
                    InputSha256 = hash, ProcessorVersion = "phase-5-zip-v1", ProcessorFingerprint = "phase-5-zip-retained-archive-v1",
                    LeaseGeneration = 1, AttemptCount = 1, State = (int)RetainedProcessorBranchState.Blocked, CreatedAtUtc = now, UpdatedAtUtc = now });
                seed.SourceProcessorAttempts.Add(new() { Id = Guid.NewGuid(), BranchId = branchId, LeaseGeneration = 1,
                    OutcomeCode = "archive-member-not-utf8", StartedAtUtc = now, FinishedAtUtc = now });
                await seed.SaveChangesAsync();
            }

            var admission = new SourceWorkAdmission();
            admission.Admit(rootId, 1);
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot, services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ISourceWorkAdmission>(admission));
                services.Replace(ServiceDescriptor.Singleton(new RetainedProcessorOptions { ArchiveZipExpandEnabled = true }));
                services.Replace(ServiceDescriptor.Singleton(provider => new LocalOperatorOriginPolicy(provider.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single())));
            });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
            var submissions = 0;
            await page.RouteAsync("**/api/operator-actions/retained-recovery/commit", async route =>
            {
                switch (++submissions)
                {
                    case 1:
                        await route.FetchAsync();
                        await route.AbortAsync("failed");
                        break;
                    case 2:
                        await route.FulfillAsync(new() { Status = 409, ContentType = "application/json", Body = "{\"reasonCode\":\"deployment-validation-held\"}" });
                        break;
                    default:
                        await route.ContinueAsync();
                        break;
                }
            });
            await page.GotoAsync(new Uri(host.BaseAddress, $"/operator-actions?sourceRevisionId={revisionId:D}&branchId={branchId:D}").ToString(),
                new() { WaitUntil = WaitUntilState.NetworkIdle });
            var preview = page.GetByRole(AriaRole.Button, new() { Name = "Preview zip encoding reconciliation" });
            await preview.PressAsync("Enter");
            await Assertions.Expect(page.GetByText("Reprocess the whole archive branch", new() { Exact = false })).ToBeVisibleAsync();
            await using var verify = new FluxKnowledgeDbContext(databaseOptions);
            Assert.False(await verify.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == activityId));

            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm recovery" }).PressAsync("Enter");
            await Assertions.Expect(page.GetByText("The response was interrupted.", new() { Exact = false })).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Check or retry submission" }).PressAsync("Enter");
            await Assertions.Expect(page.GetByText("Recovery is paused", new() { Exact = false })).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Check or retry submission" }).PressAsync("Enter");
            await Assertions.Expect(page.GetByText("Existing recovery request loaded.", new() { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "View retained branch" })).ToBeVisibleAsync();
            var successor = await verify.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == activityId);
            Assert.Equal("web", await verify.AuditEvents.Where(value => value.SourceActivityId == successor.SuccessorActivityId)
                .Select(value => value.Actor).SingleAsync());
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Direct_loopback_operator_can_open_the_sanitised_page_and_include_ignored_control()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorActions_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorActionIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/operator-actions").ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Operator actions" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Include ignored")).ToBeVisibleAsync();
            await page.GetByText("More tools", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Processor actions" })).ToBeVisibleAsync();
            var markup = await page.ContentAsync();
            Assert.DoesNotContain("branchId", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceRevision", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("canonicalPath", markup, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }
}
