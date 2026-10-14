using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class SourcePreviewBudgetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("FluxKnowledgePreview_").FullName;

    [Fact]
    public async Task Preview_limits_actual_content_bytes_and_candidate_count()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a-large.txt"), new string('a', 100));
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "1234");
        await File.WriteAllTextAsync(Path.Combine(_root, "c.txt"), "5678");
        await File.WriteAllTextAsync(Path.Combine(_root, "d.txt"), "9012");
        var enumerator = new LocalSourceEnumerator();
        var files = new List<SourceDiscoveredFile>();
        var limits = new SourcePreviewLimits(20, 3, 6, TimeSpan.FromSeconds(10));

        await foreach (var file in enumerator.PreviewAsync(Root(), limits, CancellationToken.None)) files.Add(file);

        Assert.Equal("b.txt", Assert.Single(files).RelativePath);
        Assert.Contains(enumerator.LastEvidence, item => item.Detail == "PreviewByteLimit" && item.RelativePath == "a-large.txt");
        Assert.Contains(enumerator.LastEvidence, item => item.Detail == "PreviewByteLimit" && item.RelativePath == "c.txt");
        Assert.Contains(enumerator.LastEvidence, item => item.Detail == "PreviewFileLimit");
    }

    [Fact]
    public async Task Preview_stops_discovery_at_its_entry_budget_even_if_no_files_match()
    {
        for (var index = 0; index < 20; index++) Directory.CreateDirectory(Path.Combine(_root, $"directory-{index}"));
        var enumerator = new LocalSourceEnumerator();
        await foreach (var _ in enumerator.PreviewAsync(Root(), new(4, 2, 1024, TimeSpan.FromSeconds(10)), CancellationToken.None))
            Assert.Fail("No content should be discovered.");
        Assert.Contains(enumerator.LastEvidence, item => item.Detail == "PreviewEntryLimit");
    }

    private SourceRootConfiguration Root() => SourceRootConfiguration.Create(_root, "Preview", true, false, 1024);
    public void Dispose() => Directory.Delete(_root, true);
}
