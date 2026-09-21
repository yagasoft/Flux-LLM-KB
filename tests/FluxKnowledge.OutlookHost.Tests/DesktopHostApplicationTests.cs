using FluxKnowledge.Application.Contracts;
using FluxKnowledge.OutlookHost;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class DesktopHostApplicationTests
{
    [Fact]
    public async Task Scheduled_desktop_run_processes_one_pending_Visio_document_without_a_manual_trigger()
    {
        var order = new List<string>();
        await using var application = new DesktopHostApplication(
            new RecordingVisioRunner(order, processed: true),
            new RecordingOutlookApplication(order, OutlookHostExitReason.NoDurableWork));

        var result = await application.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["visio", "outlook"], order);
        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
    }

    private sealed class RecordingVisioRunner(List<string> order, bool processed) : IPendingVisioDocumentRunner
    {
        public ValueTask<bool> RunOneAsync(CancellationToken cancellationToken)
        {
            order.Add("visio");
            return ValueTask.FromResult(processed);
        }
    }

    private sealed class RecordingOutlookApplication(List<string> order, OutlookHostExitReason reason) : IOutlookHostApplication
    {
        public ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            order.Add("outlook");
            return ValueTask.FromResult(new OutlookHostRunResult(reason));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
