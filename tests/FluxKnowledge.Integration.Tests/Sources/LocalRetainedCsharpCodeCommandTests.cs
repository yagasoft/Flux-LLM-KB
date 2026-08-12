using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class LocalRetainedCsharpCodeCommandTests
{
    [Fact]
    public async Task Read_only_detail_diagnostics_and_search_commands_return_the_named_local_projection()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reader = new Reader(branchId);

        var detail = await ExecuteAsync(["detail", branchId.ToString("D")], reader);
        var diagnostics = await ExecuteAsync(["diagnostics", branchId.ToString("D")], reader);
        var search = await ExecuteAsync(["search", "Example"], reader);

        Assert.Equal(0, detail.ExitCode);
        Assert.Equal(0, diagnostics.ExitCode);
        Assert.Equal(0, search.ExitCode);
        using var detailDocument = JsonDocument.Parse(detail.Output);
        Assert.Equal("C:\\retained-detail\\sample.cs", detailDocument.RootElement.GetProperty("localPath").GetString());
        Assert.Equal("Example.Type", detailDocument.RootElement.GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        using var diagnosticsDocument = JsonDocument.Parse(diagnostics.Output);
        Assert.Equal("CS0001", diagnosticsDocument.RootElement.GetProperty("diagnostics")[0].GetProperty("diagnosticId").GetString());
        using var searchDocument = JsonDocument.Parse(search.Output);
        Assert.Equal("Example.Type", searchDocument.RootElement.GetProperty("results")[0].GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.DoesNotContain("secret-content-sentinel", string.Concat(detail.Output, diagnostics.Output, search.Output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_Csharp_code_command_is_rejected_without_calling_a_reader_or_creating_a_mutation()
    {
        var reader = new Reader(Guid.NewGuid());

        var result = await ExecuteAsync(["force", Guid.NewGuid().ToString("D")], reader);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, reader.SearchCount);
        Assert.Contains("read-only", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_command_passes_explicit_fact_continuations_to_the_named_local_reader()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reader = new Reader(branchId);

        var result = await ExecuteAsync(["detail", branchId.ToString("D"), "255", "511", "7"], reader);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(reader.PageRequest);
        Assert.Equal(255, reader.PageRequest!.SymbolAfterOrdinal);
        Assert.Equal(511, reader.PageRequest.ReferenceAfterOrdinal);
        Assert.Equal(7, reader.PageRequest.DiagnosticAfterOrdinal);
    }

    [Fact]
    public async Task Search_command_passes_an_actual_fact_row_continuation_to_the_named_local_reader()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reader = new Reader(branchId);

        var result = await ExecuteAsync(["search", "Example", "opaque-bound-cursor"], reader);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(reader.SearchPageRequest);
        Assert.Equal("opaque-bound-cursor", reader.SearchPageRequest!.Cursor!.Token);
    }

    [Fact]
    public async Task Search_command_rejects_a_tampered_cursor_without_echoing_it()
    {
        var reader = new Reader(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var result = await ExecuteAsync(["search", "Example", "tampered"], reader);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Equal("Retained C# search continuation is invalid.", result.Error.Trim());
        Assert.DoesNotContain("tampered", result.Error, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string[] args,
        Reader reader)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await LocalRetainedCsharpCodeCommand.ExecuteAsync(args, reader, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class Reader(Guid branchId) : ILocalRetainedCsharpCodeReader
    {
        public int ReadCount { get; private set; }
        public int SearchCount { get; private set; }
        public LocalRetainedCsharpCodePageRequest? PageRequest { get; private set; }
        public LocalRetainedCsharpCodeSearchPageRequest? SearchPageRequest { get; private set; }

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(requestedBranchId == branchId
                ? Detail(branchId)
                : null);
        }

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            return ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(
                [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
                ]);
        }

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
            Guid requestedBranchId,
            LocalRetainedCsharpCodePageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            PageRequest = pageRequest;
            return ReadAsync(requestedBranchId, cancellationToken);
        }

        public ValueTask<LocalRetainedCsharpCodeSearchPage> SearchPageAsync(
            LocalRetainedCsharpCodeSearchPageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            SearchPageRequest = pageRequest;
            if (string.Equals(pageRequest.Cursor?.Token, "tampered", StringComparison.Ordinal))
            {
                throw new LocalRetainedCsharpCodeSearchCursorException();
            }

            return ValueTask.FromResult(new LocalRetainedCsharpCodeSearchPage(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ],
            null));
        }

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));

        private static LocalRetainedCsharpCodeDetailProjection Detail(Guid branchId) => new(
            branchId,
            new SourceRevisionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            "C:\\retained-detail\\sample.cs",
            new string('a', 64),
            12,
            "success",
            new string('b', 64),
            new string('c', 64),
            1,
            0,
            0,
            [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)],
            [],
            [new LocalRetainedCsharpDiagnosticProjection(0, "CS0001", 2, 0, 1, "bounded parser diagnostic", false, null, false)]);
    }
}
