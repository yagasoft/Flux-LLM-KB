using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Outlook;

public sealed class OutlookWatchedBundleTests
{
    [Fact]
    public async Task Concurrent_publication_keeps_one_stable_bundle_and_the_original_attachment_bytes()
    {
        using var fixture = new BundleRoot();
        var writer = new OutlookWatchedBundleWriter(fixture.WatchRoot, fixture.StageRoot);
        var exportId = Guid.NewGuid();
        byte[] pdf = "%PDF-1.4"u8.ToArray();
        var bundle = new OutlookWatchedBundle(exportId, "Quarterly [report]", "Alice", null, "Original body",
            [new("..\\../../report.pdf", pdf), new("REPORT.pdf", pdf)]);

        var outcomes = await Task.WhenAll(
            writer.PublishAsync(bundle, CancellationToken.None),
            writer.PublishAsync(bundle, CancellationToken.None));

        Assert.Single(outcomes, created => created);
        var directory = Assert.Single(Directory.GetDirectories(fixture.WatchRoot));
        Assert.Equal(exportId.ToString("N"), Path.GetFileName(directory));
        var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "message.md"));
        Assert.Contains("Original body", markdown, StringComparison.Ordinal);
        Assert.Contains("Quarterly \\[report\\]", markdown, StringComparison.Ordinal);
        Assert.Contains("(attachments/001-report.pdf)", markdown, StringComparison.Ordinal);
        var attachments = Directory.GetFiles(Path.Combine(directory, "attachments"));
        Assert.Equal(2, attachments.Length);
        foreach (var attachment in attachments)
            Assert.Equal(pdf, await File.ReadAllBytesAsync(attachment));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StageRoot));
    }

    [Fact]
    public async Task A_signature_decoration_is_never_read_or_linked_by_the_watched_bundle_writer()
    {
        using var fixture = new BundleRoot();
        var writer = new OutlookWatchedBundleWriter(fixture.WatchRoot, fixture.StageRoot);
        var exportId = Guid.NewGuid();
        var signature = new OutlookWatchedAttachment("logo.png", _ => throw new InvalidOperationException("Decoration was read."))
        {
            IsSignatureDecoration = true
        };
        var bundle = new OutlookWatchedBundle(exportId, null, null, null, "Body",
            [signature, new OutlookWatchedAttachment("screenshot.png", new byte[] { 1, 2, 3 })]);

        Assert.True(await writer.PublishAsync(bundle, CancellationToken.None));
        var directory = Path.Combine(fixture.WatchRoot, exportId.ToString("N"));
        var image = Assert.Single(Directory.GetFiles(Path.Combine(directory, "attachments")));
        Assert.Equal("002-screenshot.png", Path.GetFileName(image));
        var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "message.md"));
        Assert.Contains("002-screenshot.png", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("logo.png", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_rejects_existing_content_that_differs_from_the_committed_export()
    {
        using var fixture = new BundleRoot();
        var writer = new OutlookWatchedBundleWriter(fixture.WatchRoot, fixture.StageRoot);
        var exportId = Guid.NewGuid();
        var bundle = new OutlookWatchedBundle(exportId, null, null, null, "Already retained text", []);
        Assert.True(await writer.PublishAsync(bundle, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.PublishAsync(bundle with { Body = "Different text" }, CancellationToken.None));
        var markdown = await File.ReadAllTextAsync(Path.Combine(fixture.WatchRoot, exportId.ToString("N"), "message.md"));
        Assert.Contains("Already retained text", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_committed_stage_survives_a_lost_reply_and_resumes_without_rebuilding_the_message()
    {
        using var fixture = new BundleRoot();
        var writer = new OutlookWatchedBundleWriter(fixture.WatchRoot, fixture.StageRoot);
        var bundle = new OutlookWatchedBundle(Guid.NewGuid(), "Original subject", "Alice", null, "Original body", []);
        OutlookWatchedBundleReceipt? committed = null;

        await Assert.ThrowsAsync<IOException>(() => writer.PublishAsync(bundle, CancellationToken.None, (receipt, _) =>
        {
            committed = receipt;
            throw new IOException("Simulated lost SQL acknowledgement");
        }));

        Assert.NotNull(committed);
        Assert.Single(Directory.GetDirectories(fixture.StageRoot));
        Assert.Empty(Directory.GetDirectories(fixture.WatchRoot));
        Assert.True(await writer.ResumeAsync(committed, CancellationToken.None));
        Assert.False(await writer.ResumeAsync(committed, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(fixture.StageRoot));
    }

    [Fact]
    public async Task A_replaced_physical_directory_is_rejected_before_staging_private_files()
    {
        using var fixture = new BundleRoot();
        var watchRoot = fixture.WatchRoot;
        var stageRoot = fixture.StageRoot;
        var writer = new OutlookWatchedBundleWriter(watchRoot, stageRoot,
            PhysicalFileIdentity.GetDirectory(watchRoot).IdentityFingerprint,
            PhysicalFileIdentity.GetDirectory(stageRoot).IdentityFingerprint);
        Directory.Move(watchRoot, watchRoot + "-original");
        Directory.CreateDirectory(watchRoot);
        var bundle = new OutlookWatchedBundle(Guid.NewGuid(), null, null, null, "Private text", []);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PublishAsync(bundle, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(stageRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(watchRoot));
    }

    private sealed class BundleRoot : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("outlook-watched-").FullName;
        public string WatchRoot => Directory.CreateDirectory(Path.Combine(_path, "watched")).FullName;
        public string StageRoot => Directory.CreateDirectory(Path.Combine(_path, "stage")).FullName;

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
