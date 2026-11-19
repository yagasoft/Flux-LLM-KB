using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class WindowsTranscriptSidecarWriterTests
{
    [Fact]
    public async Task Writes_full_utf8_uses_same_sibling_on_retry_and_preserves_conflicting_text()
    {
        using var fixture = new CloudSourceFixture();
        var original = fixture.File("recording.mp4", "recording");
        var root = SourceRootConfiguration.Create(fixture.Root, "Test source", false, false, SourcePolicyDefaults.MaximumTextBytes);
        var reader = new WindowsSourceObservationReader();
        using var observation = reader.Open(root, "recording.mp4");
        var writer = new WindowsTranscriptSidecarWriter();
        const string text = "[00:00:00.000] البداية\n[00:00:15.000] End of the recording";

        var result = await writer.WriteAsync(root, "recording.mp4", observation.Observation, text, default);
        Assert.Equal("recording.mp4.transcript.txt", result.RelativePath);
        var sidecar = Path.Combine(fixture.Root, result.RelativePath);
        Assert.Equal(text, await File.ReadAllTextAsync(sidecar));
        Assert.Equal(result, await writer.WriteAsync(root, "recording.mp4", observation.Observation, text, default));
        Assert.Equal(result, await writer.VerifyPlacedAsync(root, "recording.mp4", observation.Observation,
            result.ContentSha256, result.ByteLength, default));
        await Assert.ThrowsAnyAsync<IOException>(() => writer.VerifyPlacedAsync(root, "recording.mp4", observation.Observation,
            new string('0', 64), result.ByteLength, default).AsTask());
        await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.WriteAsync(root, "recording.mp4", observation.Observation, "Replacement", default).AsTask());
        Assert.Equal(text, await File.ReadAllTextAsync(sidecar));
        Assert.Equal("recording", await File.ReadAllTextAsync(original));

        const string interruptedWrite = ".fluxknowledge-transcript-b426bdf79c54444eb0306642ae66d3bc.tmp";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, interruptedWrite), "interrupted");
        var files = new List<FluxKnowledge.Application.Ports.SourceDiscoveredFile>();
        await foreach (var discovered in new LocalSourceEnumerator().EnumerateAsync(root, default))
            files.Add(discovered);

        var scanned = Assert.Single(files, item => item.RelativePath == result.RelativePath);
        Assert.Equal(result.ContentSha256, scanned.ContentSha256);
        Assert.Equal(result.ByteLength, scanned.ByteLength);
        Assert.Equal(text, scanned.Classification.Text);
        Assert.DoesNotContain(files, item => item.RelativePath == interruptedWrite);
    }

    [Fact]
    public async Task Refuses_a_recording_changed_since_transcription_and_a_sidecar_outside_source_rules()
    {
        using var fixture = new CloudSourceFixture();
        var original = fixture.File("recording.mp4", "recording");
        var root = SourceRootConfiguration.Create(fixture.Root, "Test source", false, false, SourcePolicyDefaults.MaximumTextBytes);
        var reader = new WindowsSourceObservationReader();
        var writer = new WindowsTranscriptSidecarWriter();
        AssertNoSidecar(fixture.Root);
        FluxKnowledge.Application.Ports.SourceFileObservation observed;
        using (var lease = reader.Open(root, "recording.mp4")) observed = lease.Observation;

        var restrictive = SourceRootConfiguration.Create(fixture.Root, "Test source", false, false,
            SourcePolicyDefaults.MaximumTextBytes, includePatterns: ["*.mp4"]);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.WriteAsync(restrictive, "recording.mp4", observed, "Text", default).AsTask());
        await File.AppendAllTextAsync(original, " changed");
        await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.WriteAsync(root, "recording.mp4", observed, "Text", default).AsTask());
        AssertNoSidecar(fixture.Root);
    }

    private static void AssertNoSidecar(string root) => Assert.False(File.Exists(Path.Combine(root, "recording.mp4.transcript.txt")));
}
