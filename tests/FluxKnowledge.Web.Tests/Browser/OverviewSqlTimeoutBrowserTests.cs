using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
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
            var delay = new DelayNextSourceRead();
            var factory = new Factory(sql.ConnectionString, delay);
            await using var host = await StartAsync(sql.ConnectionString, root, factory, snapshot: false);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".status-freshness time")).ToHaveCountAsync(1);
            var timestamp = await page.Locator(".status-freshness time").GetAttributeAsync("datetime");
            delay.Arm(10);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            var started = await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Status is busy",
                new() { Timeout = 8000 });
            Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 4.5, 8);
            await Assertions.Expect(page.Locator(".status-freshness time")).ToHaveAttributeAsync("datetime", timestamp!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            Assert.NotEqual(timestamp, await page.Locator(".status-freshness time").GetAttributeAsync("datetime"));
            Assert.Empty(errors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [BrowserFact]
    public async Task The_dashboard_publishes_a_read_over_two_seconds_while_a_source_writer_commits()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var root = BrowserTestRoots.Create($"FluxOverviewConcurrent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var delay = new DelayNextSourceRead();
            var factory = new Factory(sql.ConnectionString, delay);
            await using var writer = factory.CreateDbContext();
            Assert.Matches("^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$", sql.DatabaseName);
            await writer.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON");
            var now = DateTimeOffset.UtcNow;
            writer.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = id, DisplayName = "Concurrent source", CanonicalPath = root,
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1, MaximumFileBytes = 10000,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await WriteRevisionAsync(1);

            await using var host = await StartAsync(sql.ConnectionString, root, factory, snapshot: true);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync();
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("File counts and path", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(Count("Known files", 1)).ToBeVisibleAsync();
            await Assertions.Expect(Count("Pending", 1)).ToBeVisibleAsync();
            var timestamp = page.Locator(".status-freshness time");
            var original = await timestamp.GetAttributeAsync("datetime");
            await WriteRevisionAsync(2);

            delay.Arm(3);
            await RefreshAsync();
            var delayed = await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Counts in this read use a snapshot that predates this commit.
            await WriteRevisionAsync(3);
            Assert.True(Stopwatch.GetElapsedTime(delayed) < TimeSpan.FromSeconds(3),
                "The third revision must commit while the SQL read is still delayed.");
            await Assertions.Expect(Count("Known files", 2)).ToBeVisibleAsync();
            await Assertions.Expect(Count("Pending", 2)).ToBeVisibleAsync();
            Assert.NotEqual(original, await timestamp.GetAttributeAsync("datetime"));
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

            await RefreshAsync();
            await Assertions.Expect(Count("Known files", 3)).ToBeVisibleAsync();
            await Assertions.Expect(Count("Pending", 3)).ToBeVisibleAsync();

            ILocator Count(string label, int count) =>
                page.GetByRole(AriaRole.Button, new() { Name = $"{label}: {count}", Exact = true });
            Task RefreshAsync() => page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();

            async Task WriteRevisionAsync(int number)
            {
                var path = Path.Combine(root, $"file-{number}.txt");
                writer.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = Guid.NewGuid(), SourceRootId = id, StableSourceIdentity = path, Revision = 1,
                    CanonicalPath = path, Extension = ".txt", Classification = "AcceptedUtf8Text",
                    ContentSha256 = new string('a', 64), ByteLength = 1, DiscoveryEvidenceJson = "{}",
                    DiscoveredAtUtc = now
                });
                await writer.SaveChangesAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<PhaseOneVerticalSliceBrowserTests.BrowserHost> StartAsync(string connectionString,
        string root, Factory factory, bool snapshot) =>
        PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
            connectionString, root, Path.Combine(root, "indexes"), services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IDbContextFactory<FluxKnowledgeDbContext>>();
                services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory);
                services.RemoveAll<OverviewReadOptions>();
                services.AddSingleton(new OverviewReadOptions(snapshot));
            });

    private sealed class DelayNextSourceRead : DbCommandInterceptor
    {
        private int _seconds;
        public TaskCompletionSource<long> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(int seconds) => Volatile.Write(ref _seconds, seconds);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM [SourceRootConfigurations]", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            var seconds = Interlocked.Exchange(ref _seconds, 0);
            if (seconds > 0)
            {
                command.CommandText = $"WAITFOR DELAY '00:00:{seconds:00}';\n" + command.CommandText;
                Started.TrySetResult(Stopwatch.GetTimestamp());
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
