using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalNativeTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_capture_and_observation_handles_after_a_seal_does_not_emit_new_content_evidence(bool holdReader)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "SealLifecycle-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(area, "Sources");
        Directory.CreateDirectory(sourcePath);
        try
        {
            var path = Path.Combine(sourcePath, "one.txt");
            await File.WriteAllTextAsync(path, new string('x', 64));
            var stamp = File.GetLastWriteTimeUtc(path);
            var root = SourceRootConfiguration.Create(sourcePath, "Seal lifecycle fixture", true, false, 1024);
            var native = new WindowsSourceChangeJournalReader();
            var observations = new WindowsSourceObservationReader();
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            using var persistent = holdReader ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
            for (var cycle = 0; cycle < 8; cycle++)
            {
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                { writer.Position = cycle; writer.WriteByte((byte)(65 + cycle)); }
                File.SetLastWriteTimeUtc(path, stamp);
                var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "lifecycle", cycle + 1));
                await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, ownership, default).GetAsyncEnumerator();
                Assert.True(await iterator.MoveNextAsync());
                var capture = iterator.Current;
                var file = capture.Snapshot;
                var receipt = await artifacts.PutCapturedFileAsync(capture, new(file.ContentSha256, "application/octet-stream", file.ByteLength), default);
                using (var before = observations.Open(root, "one.txt")) before.ValidateCurrent();
                var blob = await artifacts.OpenObservationAsync(receipt.StoreRelativePath, file.ContentSha256, file.ByteLength, true, default);
                var seal = await Assert.IsAssignableFrom<IJournalSealableSourceCapture>(capture).SealAsync(default);
                var after = observations.Open(root, "one.txt");
                var barrier = await native.QueryAsync(sourcePath, default);
                after.ValidateCurrent();
                after.Dispose();
                await blob.DisposeAsync();
                await receipt.PublicationLease!.DisposeAsync();
                await capture.DisposeAsync();
                await iterator.DisposeAsync();
                var end = await native.QueryAsync(sourcePath, default);
                var cursor = new JournalCursor(barrier.VolumeIdentity, barrier.JournalId, barrier.NextUsn);
                var changes = new List<JournalChange>();
                for (var page = 0; cursor.NextUsn < end.NextUsn && page < 200; page++)
                {
                    var read = await native.ReadAsync(cursor, 65536, default);
                    Assert.True(read.Next.NextUsn > cursor.NextUsn);
                    changes.AddRange(read.Changes.Where(x => x.FileId == seal.Observation.FileId && x.Usn >= barrier.NextUsn && x.Usn < end.NextUsn));
                    cursor = read.Next;
                }
                Assert.True(cursor.NextUsn >= end.NextUsn);
                Assert.True(changes.All(x => !x.ContentChanged), JsonSerializer.Serialize(new { cycle, seal.Usn, barrier.NextUsn, changes }));
            }
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(@"I:\Codex Workspaces\Staging\FluxKnowledge") + Path.DirectorySeparatorChar, Path.GetFullPath(area), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(area, true);
        }
    }
}
