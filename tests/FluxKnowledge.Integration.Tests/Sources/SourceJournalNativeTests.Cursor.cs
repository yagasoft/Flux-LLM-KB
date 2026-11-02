using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using System.ComponentModel;
using System.Security.Principal;
using System.Runtime.Versioning;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalNativeTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task A_rejected_in_range_cursor_enters_recovery_instead_of_permanent_unavailability(bool throughPipe)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "RejectedCursor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            ISourceChangeJournalReader reader = new WindowsSourceChangeJournalReader();
            var before = await reader.QueryAsync(area, default);
            await File.WriteAllTextAsync(Path.Combine(area, "synthetic.txt"), "Native cursor boundary fixture.");
            var start = new JournalCursor(before.VolumeIdentity, before.JournalId, before.NextUsn);
            var valid = await reader.ReadAsync(start, 65536, default);
            Assert.NotEmpty(valid.Changes);
            // The position is in range and aligned, but inside a record rather than at its boundary.
            var rejected = start with { NextUsn = valid.Changes[0].Usn + 8 };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task? server = null;
            if (throughPipe)
            {
                var sid = WindowsIdentity.GetCurrent().User!;
                var name = "FluxKnowledge.SourceJournal.rejected-test-" + Guid.NewGuid().ToString("N");
                server = SourceJournalPipeServer.RunAsync(new(1, name, [before.VolumeIdentity]), sid.Value, () => { }, cancellation.Token, sid);
                reader = new SourceJournalPipeClient(new() { PipeName = name, ApprovedVolumes = [before.VolumeIdentity] }, sid);
            }
            try
            {
                var error = await Assert.ThrowsAsync<SourceJournalGapException>(() => reader.ReadAsync(rejected, 65536, cancellation.Token).AsTask());
                Assert.Equal("SourceJournalCursorRejected", error.Message);
                Assert.Equal(87, (error.InnerException is Win32Exception native ? native : (Win32Exception)error.InnerException!.InnerException!).NativeErrorCode);
                var current = await reader.QueryAsync(before.VolumeIdentity, cancellation.Token);
                var head = new JournalCursor(current.VolumeIdentity, current.JournalId, current.NextUsn);
                Assert.True((await reader.ReadAsync(head, 65536, cancellation.Token)).Next.NextUsn >= head.NextUsn);
            }
            finally
            {
                await cancellation.CancelAsync();
                if (server is not null) try { await server; } catch (OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(area, true); }
    }
}
