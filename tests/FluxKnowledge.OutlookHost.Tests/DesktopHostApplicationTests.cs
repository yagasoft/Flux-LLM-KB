using FluxKnowledge.Application.Contracts;
using FluxKnowledge.OutlookHost;
using Xunit;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class DesktopHostApplicationTests
{
    [Fact]
    public async Task Database_unavailable_during_read_only_Visio_discovery_does_not_start_Outlook_or_replay_work()
    {
        var order = new List<string>();
        var application = new DesktopHostApplication(new UnavailableVisioRunner(order),
            new RecordingOutlookApplication(order, OutlookHostExitReason.NoDurableWork));

        var exitCode = await Program.RunAsync(["--run-once"], new Factory(application), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(["visio-read"], order);
    }

    private sealed class UnavailableVisioRunner(List<string> order) : IPendingVisioDocumentRunner
    {
        public ValueTask<bool> RunOneAsync(CancellationToken ct)
        {
            order.Add("visio-read");
            return ValueTask.FromException<bool>(new DatabaseStartupUnavailableException(2));
        }
    }
    private sealed class Factory(IOutlookHostApplication application) : IOutlookHostApplicationFactory
    {
        public IOutlookHostApplication Create(OutlookHostOptions options) => application;
    }

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
