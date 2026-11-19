using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal sealed class OutlookWatchedExportPublisher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    OutlookCaptureRecoveryOptions options,
    ILogger<OutlookWatchedExportPublisher> logger,
    IDeploymentValidationHold? deploymentValidationHold = null,
    IDatabaseStartupReadiness? databaseStartupReadiness = null) : BackgroundService
{
    private const int BatchSize = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        await (databaseStartupReadiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await (deploymentValidationHold ?? DeploymentValidationHold.None)
                .WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await PublishPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Outlook watched export publication could not complete this pass. Pending exports remain available for retry.");
            }

            await Task.Delay(options.RecoveryCadence, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlOutlookWatchedPublicationStore>();
        var reader = scope.ServiceProvider.GetRequiredService<SqlOutlookWatchedBundleReader>();
        var candidates = await store.ReadPendingAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deploymentValidationHold?.IsHeld == true)
                return;

            try
            {
                var writer = new OutlookWatchedBundleWriter(candidate.WatchRoot, candidate.StageRoot,
                    candidate.WatchIdentity, candidate.StageIdentity);
                if (candidate.PreparedBundle is { } receipt)
                {
                    if (receipt.ExportId != candidate.ExportId)
                        throw new InvalidDataException("The prepared Outlook bundle belongs to a different export.");
                    await writer.ResumeAsync(receipt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var bundle = await reader.ReadAsync(candidate.ProfileId, candidate.ExportId, cancellationToken)
                        .ConfigureAwait(false);
                    await writer.PublishAsync(bundle, cancellationToken,
                        (preparedBundle, token) => store.PrepareWithdrawalAsync(candidate, preparedBundle, token))
                        .ConfigureAwait(false);
                }
                await store.MarkPublishedAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or SqlException or DbUpdateException or TimeoutException)
            {
                await store.DeferAsync(candidate, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("An Outlook watched export could not be published and was deferred. Failure type {FailureType}.",
                    exception.GetType().Name);
            }
        }
    }
}
