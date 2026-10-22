using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class OverviewSqlTimeoutBrowserTests
{
    [BrowserFact]
    public async Task A_real_ordinary_SQL_timeout_preserves_the_snapshot_and_the_next_refresh_recovers()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var root = BrowserTestRoots.Create($"FluxOverviewSqlTimeout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var delay = new DelayNextCount();
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString, root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IDbContextFactory<FluxKnowledgeDbContext>>();
                    services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(new Factory(sql.ConnectionString, delay));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".status-freshness time")).ToHaveCountAsync(1);
            var timestamp = await page.Locator(".status-freshness time").GetAttributeAsync("datetime");
            delay.Armed = true;
            var elapsed = Stopwatch.StartNew();
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Status is busy");
            Assert.True(delay.WasDelayed);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 1.5, 5);
            await Assertions.Expect(page.Locator(".status-freshness time")).ToHaveAttributeAsync("datetime", timestamp!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            Assert.NotEqual(timestamp, await page.Locator(".status-freshness time").GetAttributeAsync("datetime"));
            Assert.Empty(errors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DelayNextCount : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool WasDelayed { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM [Jobs]", StringComparison.Ordinal))
            {
                Armed = false;
                WasDelayed = true;
                command.CommandText = "WAITFOR DELAY '00:00:10';\n" + command.CommandText;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(string connection, IInterceptor interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, options => options.EnableRetryOnFailure()).AddInterceptors(interceptor).Options);
    }
}
