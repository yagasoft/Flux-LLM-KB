using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class IncrementalSourceScanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_content_failure_and_directory_membership_failure_have_distinct_namespace_evidence(bool directoryFailure)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "NamespaceEvidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            var path = Path.Combine(area, "one.txt");
            await File.WriteAllTextAsync(path, "evidence");
            using var blocked = directoryFailure ? null : new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var enumerator = new LocalSourceEnumerator(beforeDirectoryRecheck: directoryFailure
                ? _ => throw new IOException("Fixture incomplete directory listing") : null);
            var root = SourceRootConfiguration.Create(area, "Namespace evidence fixture", true, false, 1024);
            await foreach (var _ in enumerator.EnumerateAsync(root, default)) { }
            var issue = Assert.Single(enumerator.LastEvidence);
            Assert.Equal(directoryFailure, issue.NamespaceIncomplete);
            Assert.Equal(directoryFailure ? "." : "one.txt", issue.RelativePath);
        }
        finally { Directory.Delete(area, true); }
    }

    [Theory]
    [InlineData(".", 1)]
    [InlineData("folder", 0)]
    [InlineData("folder/nested.txt", 0)]
    public async Task Targeted_hints_cannot_expand_a_nonrecursive_roots_admitted_depth(string target, int expectedFiles)
    {
        var path = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "ScopeDepth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "folder"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "top.txt"), "top");
            await File.WriteAllTextAsync(Path.Combine(path, "folder", "nested.txt"), "outside admitted depth");
            var root = SourceRootConfiguration.Create(path, "Nonrecursive scope fixture", false, false, 1024);
            var bytes = 0;
            var enumerator = new LocalSourceEnumerator { SourceBytesRead = count => bytes += count };
            using var scope = enumerator.UseScope(new([target.Replace('/', Path.DirectorySeparatorChar)], null));
            var found = new List<string>();
            var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "test", 1));
            await foreach (var item in enumerator.EnumerateCapturedAsync(root, ownership, default)) found.Add(item.Snapshot.RelativePath);
            Assert.Equal(expectedFiles, found.Count);
            Assert.All(found, relative => Assert.Equal("top.txt", relative));
            Assert.Equal(expectedFiles * 3, bytes);
            if (target != ".") Assert.Equal(0, enumerator.LastEnumeratedEntries);
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(".", true, "one.txt", true)]
    [InlineData(".", true, "folder/one.txt", true)]
    [InlineData(".", false, "one.txt", false)]
    [InlineData("folder", true, "folder/one.txt", true)]
    [InlineData("folder", false, "folder/one.txt", false)]
    [InlineData("folder", true, "folder2/one.txt", false)]
    [InlineData("one.txt", false, "ONE.txt", true)]
    public void Scope_includes_only_the_exact_path_or_admitted_subtree(string scope, bool subtree, string path, bool expected)
        => Assert.Equal(expected, SourcePathScope.Contains(scope.Replace('/', Path.DirectorySeparatorChar), subtree,
            path.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task One_file_scope_does_not_enumerate_or_read_its_siblings_and_a_reuse_filter_reads_zero()
    {
        var path = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "ScopeNative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "one.txt"), "first");
            await File.WriteAllTextAsync(Path.Combine(path, "other.txt"), "second");
            var root = SourceRootConfiguration.Create(path, "scope fixture", true, false, 1024);
            var bytes = 0;
            var enumerator = new LocalSourceEnumerator { SourceBytesRead = count => bytes += count };
            var lease = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "test", 1));
            using (enumerator.UseScope(new(["one.txt"], null)))
            {
                var files = new List<string>();
                await foreach (var item in enumerator.EnumerateCapturedAsync(root, lease, default)) files.Add(item.Snapshot.RelativePath);
                Assert.Equal(["one.txt"], files);
                Assert.Equal(5, bytes);
                Assert.Equal(0, enumerator.LastEnumeratedEntries);
            }
            bytes = 0;
            using (enumerator.UseScope(new(["one.txt"], (_, _) => ValueTask.FromResult(false))))
            {
                await foreach (var _ in enumerator.EnumerateCapturedAsync(root, lease, default)) Assert.Fail("Reused content must not be captured.");
                Assert.Equal(0, bytes);
            }
            using (enumerator.UseScope(new(["."], null)))
            {
                var found = 0;
                await foreach (var _ in enumerator.EnumerateCapturedAsync(root, lease, default)) found++;
                Assert.Equal(2, found);
            }
        }
        finally { Directory.Delete(path, true); }
    }
}
