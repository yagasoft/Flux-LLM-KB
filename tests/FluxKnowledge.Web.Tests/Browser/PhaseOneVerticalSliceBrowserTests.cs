using System.Net.Http.Json;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Components;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set FLUXKNOWLEDGE_BROWSER_TESTS=1 to run browser tests.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SQL_CONNECTION")))
        {
            Skip = "Set FLUXKNOWLEDGE_TEST_SQL_CONNECTION to a disposable SQL test server to run browser tests.";
        }
    }
}

[Trait("Category", "Browser")]
public sealed class PhaseOneVerticalSliceBrowserTests
{
    [BrowserFact]
    public async Task Sql_backed_utf8_registration_is_visible_in_the_interactive_search_slice()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeBrowserIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeBrowserIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            var fileName = "phase-one-browser.txt";
            var filePath = Path.Combine(ingressRoot, fileName);
            const string input = "Native browser search phrase from the SQL hydrated pipeline record.";
            await File.WriteAllTextAsync(filePath, input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await using var host = await BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };
            using var overviewResponse = await client.GetAsync("/");
            var overviewMarkup = await overviewResponse.Content.ReadAsStringAsync();
            Assert.True(
                overviewResponse.IsSuccessStatusCode,
                $"Overview returned {(int)overviewResponse.StatusCode}: {overviewMarkup}");
            Assert.Contains("Pipeline overview", overviewMarkup, StringComparison.Ordinal);
            Assert.Contains("Index status", overviewMarkup, StringComparison.Ordinal);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(host.BaseAddress.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Pipeline overview" }).WaitForAsync();

            using var response = await client.PostAsJsonAsync(
                "/api/pipeline-records/utf8-file",
                new RegisterUtf8FileCommand(filePath, "phase-one-browser-test", fileName));
            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
            Assert.NotNull(await response.Content.ReadFromJsonAsync<RegisterUtf8FileResult>());

            await page.WaitForFunctionAsync(
                """
                () => [...document.querySelectorAll('.status-grid div')]
                    .some(card => card.innerText.includes('Indexed records') && card.innerText.includes('1'))
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            using var recordsResponse = await client.GetAsync("/api/pipeline-records");
            if (!recordsResponse.IsSuccessStatusCode)
            {
                var recordsPayload = await recordsResponse.Content.ReadAsStringAsync();
                Assert.Fail(
                    $"Pipeline record projection returned {(int)recordsResponse.StatusCode}: {recordsPayload}");
            }
            var records = await recordsResponse.Content.ReadFromJsonAsync<IReadOnlyList<PipelineRecordProjection>>();
            Assert.Contains(records ?? [], record => record.SourceIdentity.EndsWith(fileName, StringComparison.Ordinal));

            await page.GetByRole(AriaRole.Link, new() { Name = "Pipeline records" }).ClickAsync();
            await page.GetByText(fileName, new PageGetByTextOptions { Exact = false }).WaitForAsync(
                new LocatorWaitForOptions { Timeout = 30_000 });

            await page.GetByRole(AriaRole.Link, new() { Name = "Search" }).ClickAsync();
            await page.Locator("#search-query").FillAsync("hydrated");
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = fileName, Exact = true }).WaitForAsync(
                new LocatorWaitForOptions { Timeout = 30_000 });
            await page.GetByText(input, new PageGetByTextOptions { Exact = false }).WaitForAsync(
                new LocatorWaitForOptions { Timeout = 30_000 });
        }
        finally
        {
            if (Directory.Exists(ingressRoot))
            {
                Directory.Delete(ingressRoot, recursive: true);
            }

            if (Directory.Exists(indexRoot))
            {
                Directory.Delete(indexRoot, recursive: true);
            }
        }
    }

    internal sealed class BrowserHost(WebApplication application, Uri baseAddress) : IAsyncDisposable
    {
        private const string ValidatedPlaceholderConnection =
            "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
            "Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<BrowserHost> StartAsync(
            string connectionString,
            string ingressRoot,
            string indexRoot,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    Args = [],
                    ApplicationName = typeof(App).Assembly.GetName().Name,
                    // Development enables the real application's static-web-asset manifest
                    // for this Kestrel test host; it does not enable a development exception page.
                    EnvironmentName = "Development"
                });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FluxKnowledge"] = ValidatedPlaceholderConnection,
                    ["LocalIngress:AllowedRoots:0"] = ingressRoot,
                    ["Outlook:AllowedSpoolRoots:0"] = ingressRoot,
                    ["Usearch:RootPath"] = indexRoot
                });
            WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
            // The production options validator deliberately accepts only the FluxKnowledge
            // catalogue. Browser tests retain that contract and replace only the EF factory
            // with their generated disposable catalogue.
            builder.Services.RemoveAll<IDbContextFactory<FluxKnowledgeDbContext>>();
            builder.Services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new DisposableDbContextFactory(connectionString));
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddSingleton<StatusEventFeed>();
            builder.Services.AddSingleton<IStatusEventPublisher>(provider => provider.GetRequiredService<StatusEventFeed>());
            builder.Services.AddScoped<IProjectionReader, SqlProjectionReader>();
            builder.Services.AddScoped<OverviewProjectionState>();
            builder.Services.AddScoped<CircuitHandler, StatusEventCircuitHandler>();
            builder.Services.AddFluxKnowledgeMcp();
            builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<KnowledgeMcpTools>();
            configureServices?.Invoke(builder.Services);

            var application = builder.Build();
            application.UseAntiforgery();
            application.MapStaticAssets();
            application.MapRazorComponents<App>().AddInteractiveServerRenderMode();
            application.MapFluxKnowledgeHealth();
            application.MapFluxKnowledgeIndexHealth();
            application.MapFluxKnowledgeSearch();
            application.MapFluxKnowledgePipelineRecords();
            application.MapMcp("/mcp");
            await application.StartAsync();
            return new BrowserHost(application, new Uri(application.Urls.Single()));
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }

        private sealed class DisposableDbContextFactory(string connectionString)
            : IDbContextFactory<FluxKnowledgeDbContext>
        {
            private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                    .UseSqlServer(connectionString)
                    .Options;

            public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        }
    }
}

public static class BrowserTestRoots
{
    public static string Create(string leafName, IEnumerable<string>? candidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leafName);
        foreach (var candidate in candidates ?? DefaultCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var root = Path.GetFullPath(candidate);
            if (!IsIFileSystemRoot(Path.GetPathRoot(root)))
            {
                return Path.Combine(root, leafName);
            }
        }

        throw new InvalidOperationException(
            "Browser tests require a writable temporary root outside I:.");
    }

    private static IEnumerable<string> DefaultCandidates()
    {
        yield return Path.GetTempPath();
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxKnowledgeTests");
    }

    private static bool IsIFileSystemRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        var normalisedRoot = root.Replace('/', '\\');
        if (normalisedRoot.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            normalisedRoot.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            normalisedRoot = normalisedRoot[4..];
        }

        return string.Equals(normalisedRoot, "I:\\", StringComparison.OrdinalIgnoreCase);
    }
}
