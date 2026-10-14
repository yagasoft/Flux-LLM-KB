using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class SearchStatusBrowserTests
{
    [BrowserFact]
    public async Task Waiting_prevents_duplicate_submits_and_fallback_or_failure_keeps_the_search_page_usable()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingress = BrowserTestRoots.Create($"FluxSearchStatusIngress_{Guid.NewGuid():N}");
        var index = BrowserTestRoots.Create($"FluxSearchStatusIndex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingress);
        Directory.CreateDirectory(index);
        var search = new ControlledSearch();
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString,
                ingress, index, services => services.AddSingleton<ISearchService>(search));
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, message) => errors.Add(message);
            await page.GotoAsync(new Uri(host.BaseAddress, "search").AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true });
            var status = page.GetByRole(AriaRole.Status);
            await page.Locator("#search-query").FillAsync("current workspace");
            await button.ClickAsync();
            var first = await search.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assertions.Expect(button).ToBeDisabledAsync();
            await Assertions.Expect(status).ToContainTextAsync("Searching");
            await page.Locator("#search-query").PressAsync("Enter");
            first.SetResult(Response(null));
            await Assertions.Expect(status).ToContainTextAsync("Found 1");
            Assert.Equal(1, search.Calls);

            foreach (var (reason, message) in new[]
            {
                ("index-updating", "index is updating"), ("busy", "busy"),
                ("timeout", "time limit"), ("unavailable", "unavailable")
            })
            {
                await button.ClickAsync();
                var request = await search.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                request.SetResult(Response(reason));
                await Assertions.Expect(status).ToContainTextAsync(message);
                await Assertions.Expect(status).ToContainTextAsync("available results");
                await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Search results" })).ToContainTextAsync("Current cited passage");
                await Assertions.Expect(button).ToBeEnabledAsync();
            }
            await button.ClickAsync();
            var failed = await search.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            failed.SetException(new PassageRetrievalRefusalException("index-updating"));
            await Assertions.Expect(status).ToContainTextAsync("index is updating");
            await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Search results" })).ToHaveCountAsync(0);
            await Assertions.Expect(button).ToBeEnabledAsync();
            await button.ClickAsync();
            var retry = await search.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            retry.SetResult(Response(null));
            await Assertions.Expect(status).ToContainTextAsync("Found 1");
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(ingress, true);
            Directory.Delete(index, true);
        }
    }

    private static SearchResponse Response(string? reason) => new(
        [new SearchHit(new PipelineRecordId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "C:/workspace/guide.txt", 1, "guide.txt", "Current cited passage", 1, ["lexical:full-text"])],
        1, "generation", "local_first") { DegradedStatus = reason };

    private sealed class ControlledSearch : ISearchService
    {
        public Channel<TaskCompletionSource<SearchResponse>> Requests { get; } = Channel.CreateUnbounded<TaskCompletionSource<SearchResponse>>();
        public int Calls { get; private set; }
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            Calls++;
            var completion = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Writer.TryWrite(completion);
            return new(completion.Task.WaitAsync(ct));
        }
    }
}
