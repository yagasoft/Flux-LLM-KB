using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Media;
using FluxKnowledge.TestSupport;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class PendingTranscriptFileTests
{
    [Fact]
    public async Task A_later_worker_can_read_the_exact_flushed_pending_transcript_and_refuse_a_conflicting_retry()
    {
        using var fixture = new CloudSourceFixture();
        var layout = LiveRootLayout.CreateForIsolatedTests(fixture.Directory("isolated-runtime"));
        var producer = new MediaTranscriptionLocalExecutor(layout, null!);
        var consumer = new MediaTranscriptionLocalExecutor(layout, null!);
        var execution = Guid.NewGuid();
        const string text = "[00:00:00.000] البداية\n[00:07:54.230] Final words.";
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await producer.PersistTranscriptAsync(execution, text, default);
        await producer.PersistTranscriptAsync(execution, text, default);
        await Assert.ThrowsAnyAsync<IOException>(() => producer.PersistTranscriptAsync(execution,
            text.Replace("Final", "Other"), default).AsTask());
        Assert.Equal(text, await consumer.ReadPendingTranscriptAsync(execution, bytes.Length, hash, default));
        await Assert.ThrowsAsync<IOException>(() => consumer.ReadPendingTranscriptAsync(execution, bytes.Length, new string('0', 64), default).AsTask());

        consumer.RemovePendingTranscript(execution);
        await Assert.ThrowsAnyAsync<IOException>(() => consumer.ReadPendingTranscriptAsync(execution, bytes.Length, hash, default).AsTask());
    }
}
