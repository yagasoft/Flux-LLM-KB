using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class OutlookSpoolRootPreflightHostedService(
    SqlOutlookSpoolRootPreflight preflight) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        preflight.ValidateAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
