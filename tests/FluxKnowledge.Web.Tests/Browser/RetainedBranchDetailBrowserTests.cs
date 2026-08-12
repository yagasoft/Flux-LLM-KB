using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class RetainedBranchDetailBrowserTests
{
    [BrowserFact]
    public async Task Trusted_local_Csharp_search_links_to_retained_fact_detail_without_exposing_withheld_content()
    {
        var branchId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                services =>
                {
                    services.RemoveAll<ILocalRetainedCsharpCodeReader>();
                    services.AddSingleton<ILocalRetainedCsharpCodeReader>(new CsharpReader(branchId));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync($"{host.BaseAddress}search/csharp-code", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Search retained C# facts", Exact = true }).WaitForAsync(
                new LocatorWaitForOptions { Timeout = 5_000 });
            await page.Locator("#csharp-code-query").FillAsync("Example");
            await page.GetByRole(AriaRole.Button, new() { Name = "Search retained C# facts", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "View retained C# facts", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Retained C# code details", Exact = true }).WaitForAsync();
            await page.GetByText("C:\\retained-detail\\browser.cs", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("Example.Type", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("public void Run()", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("CS0001", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("secret-content-withheld", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            Assert.DoesNotContain("secret-content-sentinel", await page.ContentAsync(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Trusted_local_retained_page_shows_verified_detail_and_withheld_excerpt()
    {
        var branchId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedDetailIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedDetailIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                services =>
                {
                    services.RemoveAll<ILocalRetainedDetailReader>();
                    services.AddSingleton<ILocalRetainedDetailReader>(new Reader(branchId));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync($"{host.BaseAddress}sources/retained/{branchId:D}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Retained branch detail" }).WaitForAsync();
            await page.GetByText("C:\\retained-detail\\browser.cs", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("secret-content-withheld", new PageGetByTextOptions { Exact = true }).WaitForAsync();
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Trusted_local_Csharp_detail_shows_totals_and_loads_each_bounded_fact_tail()
    {
        var branchId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpPagingIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpPagingIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                services =>
                {
                    services.RemoveAll<ILocalRetainedCsharpCodeReader>();
                    services.AddSingleton<ILocalRetainedCsharpCodeReader>(new PagedCsharpReader(branchId));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync($"{host.BaseAddress}sources/retained/{branchId:D}/csharp-code", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("256 of 300 symbols", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("256 of 300 references", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("256 of 300 diagnostics", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Load more symbols", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Load more references", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Load more diagnostics", Exact = true }).ClickAsync();
            await page.GetByText("TailSymbol", new PageGetByTextOptions { Exact = true }).First.WaitForAsync();
            await page.GetByText("TailReference", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("CSTAIL", new PageGetByTextOptions { Exact = true }).WaitForAsync();
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Trusted_local_Csharp_search_shows_reference_only_results_and_loads_the_matching_fact_tail()
    {
        var branchId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpSearchPagingIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeRetainedCsharpSearchPagingIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                services =>
                {
                    services.RemoveAll<ILocalRetainedCsharpCodeReader>();
                    services.AddSingleton<ILocalRetainedCsharpCodeReader>(new PagedCsharpReader(branchId));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync($"{host.BaseAddress}search/csharp-code", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.Locator("#csharp-code-query").FillAsync("ReferenceOnly");
            await page.GetByRole(AriaRole.Button, new() { Name = "Search retained C# facts", Exact = true }).ClickAsync();
            await page.GetByText("ReferenceOnly000", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Load more matching facts", Exact = true }).ClickAsync();
            await page.GetByText("TailReferenceOnly", new PageGetByTextOptions { Exact = true }).WaitForAsync();
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    private sealed class Reader(Guid branchId) : ILocalRetainedDetailReader
    {
        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedDetailProjection?>(requestedBranchId == branchId
                ? new LocalRetainedDetailProjection(branchId, Guid.NewGuid(), new SourceRevisionId(Guid.NewGuid()),
                    "C:\\retained-detail\\browser.cs", new string('b', 64), new string('b', 64), 12,
                    new LocalRetainedContentHandle(branchId, new SourceRevisionId(Guid.NewGuid())), [], [])
                : null);

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));
    }

    private sealed class PagedCsharpReader(Guid branchId) : ILocalRetainedCsharpCodeReader
    {
        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(requestedBranchId == branchId ? FirstDetail(branchId) : null);

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
            Guid requestedBranchId,
            LocalRetainedCsharpCodePageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            if (requestedBranchId != branchId)
            {
                return ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(null);
            }

            return ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(pageRequest switch
            {
                { SymbolAfterOrdinal: 255 } => SymbolTail(branchId),
                { ReferenceAfterOrdinal: 255 } => ReferenceTail(branchId),
                { DiagnosticAfterOrdinal: 255 } => DiagnosticTail(branchId),
                _ => FirstDetail(branchId)
            });
        }

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(SearchFirst(branchId).Results);

        public ValueTask<LocalRetainedCsharpCodeSearchPage> SearchPageAsync(
            LocalRetainedCsharpCodeSearchPageRequest pageRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(pageRequest.Cursor is null ? SearchFirst(branchId) : SearchTail(branchId));

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));

        private static LocalRetainedCsharpCodeDetailProjection FirstDetail(Guid branchId) =>
            Detail(branchId,
                Enumerable.Range(0, 256).Select(index => Symbol(index, $"Symbol{index:D3}")).ToArray(),
                Enumerable.Range(0, 256).Select(index => Reference(index, $"Reference{index:D3}")).ToArray(),
                Enumerable.Range(0, 256).Select(index => Diagnostic(index, $"CS{index:D4}")).ToArray(),
                255,
                255,
                255);

        private static LocalRetainedCsharpCodeDetailProjection SymbolTail(Guid branchId) =>
            Detail(branchId, [Symbol(256, "TailSymbol")], [], [], null, null, null);

        private static LocalRetainedCsharpCodeDetailProjection ReferenceTail(Guid branchId) =>
            Detail(branchId, [], [Reference(256, "TailReference")], [], null, null, null);

        private static LocalRetainedCsharpCodeDetailProjection DiagnosticTail(Guid branchId) =>
            Detail(branchId, [], [], [Diagnostic(256, "CSTAIL")], null, null, null);

        private static LocalRetainedCsharpCodeDetailProjection Detail(
            Guid branchId,
            IReadOnlyList<LocalRetainedCsharpSymbolProjection> symbols,
            IReadOnlyList<LocalRetainedCsharpReferenceProjection> references,
            IReadOnlyList<LocalRetainedCsharpDiagnosticProjection> diagnostics,
            int? nextSymbol,
            int? nextReference,
            int? nextDiagnostic) =>
            new(
                branchId,
                new SourceRevisionId(Guid.Parse("77777777-7777-7777-7777-777777777777")),
                "C:\\retained-detail\\paged.cs",
                new string('e', 64),
                12,
                "success",
                new string('f', 64),
                new string('a', 64),
                0,
                0,
                0,
                symbols,
                references,
                diagnostics)
            {
                PersistedSymbolCount = 300,
                PersistedReferenceCount = 300,
                PersistedDiagnosticCount = 300,
                NextSymbolOrdinal = nextSymbol,
                NextReferenceOrdinal = nextReference,
                NextDiagnosticOrdinal = nextDiagnostic
            };

        private static LocalRetainedCsharpCodeSearchPage SearchFirst(Guid branchId) =>
            new(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\paged.cs",
                    new string('e', 64),
                    [])
                {
                    References = Enumerable.Range(0, 256).Select(index => Reference(index, $"ReferenceOnly{index:D3}")).ToArray()
                }
            ],
            new LocalRetainedCsharpCodeSearchCursor("opaque-browser-cursor"));

        private static LocalRetainedCsharpCodeSearchPage SearchTail(Guid branchId) =>
            new(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\paged.cs",
                    new string('e', 64),
                    [])
                {
                    References = [Reference(256, "TailReferenceOnly")]
                }
            ],
            null);

        private static LocalRetainedCsharpSymbolProjection Symbol(int ordinal, string name) =>
            new(ordinal, 1, name, name, name, "public", -1, ordinal, 1);

        private static LocalRetainedCsharpReferenceProjection Reference(int ordinal, string target) =>
            new(ordinal, 1, null, target, ordinal, 1);

        private static LocalRetainedCsharpDiagnosticProjection Diagnostic(int ordinal, string id) =>
            new(ordinal, id, 2, ordinal, 1, "synthetic diagnostic", false, null, false);
    }

    private sealed class CsharpReader(Guid branchId) : ILocalRetainedCsharpCodeReader
    {
        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(requestedBranchId == branchId
                ? Detail(branchId)
                : null);

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\browser.cs",
                    new string('b', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ]);

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));

        private static LocalRetainedCsharpCodeDetailProjection Detail(Guid branchId) => new(
            branchId,
            new SourceRevisionId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            "C:\\retained-detail\\browser.cs",
            new string('b', 64),
            12,
            "success",
            new string('c', 64),
            new string('d', 64),
            1,
            0,
            1,
            [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)],
            [],
            [new LocalRetainedCsharpDiagnosticProjection(0, "CS0001", 2, 0, 1, null, true, "secret-content-withheld", false)]);
    }
}
