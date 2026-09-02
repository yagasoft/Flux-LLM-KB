using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Integrations.Outlook;

public sealed class OutlookCaptureRecoveryOptions
{
    public const string ConfigurationSectionName = "OutlookCapture";

    public bool Enabled { get; init; }
    public TimeSpan HintDebounce { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RecoveryCadence { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan StaleLeaseAge { get; init; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        RequireRange(HintDebounce, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), nameof(HintDebounce));
        RequireRange(RecoveryCadence, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), nameof(RecoveryCadence));
        RequireRange(StaleLeaseAge, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), nameof(StaleLeaseAge));
    }

    private static void RequireRange(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed record OutlookCaptureRecoveryResult(int ReleasedStaleLeases, int ReplayedPendingHints)
{
    public static OutlookCaptureRecoveryResult Empty { get; } = new(0, 0);
}

/// <summary>
/// Reconciles only durable Outlook hint receipts and expired catch-up leases. This service has no
/// host, COM, mailbox, cursor, spool or deferred-processor dependency.
/// </summary>
public sealed class OutlookCaptureRecoveryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    OutlookCaptureRecoveryOptions options,
    ILogger<OutlookCaptureRecoveryService>? logger = null,
    IDeploymentValidationHold? deploymentValidationHold = null) : BackgroundService
{
    private readonly ILogger<OutlookCaptureRecoveryService> _logger =
        logger ?? NullLogger<OutlookCaptureRecoveryService>.Instance;

    public async ValueTask<OutlookCaptureRecoveryResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        if (!options.Enabled)
        {
            return OutlookCaptureRecoveryResult.Empty;
        }

        var observedAtUtc = timeProvider.GetUtcNow();
        var staleBeforeUtc = observedAtUtc.Subtract(options.StaleLeaseAge);
        var pendingHintBeforeUtc = observedAtUtc.Subtract(options.HintDebounce);
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutlookCaptureRecoveryStore>();
        var snapshot = await store
            .ReadRecoverySnapshotAsync(staleBeforeUtc, pendingHintBeforeUtc, cancellationToken)
            .ConfigureAwait(false);

        var released = 0;
        foreach (var candidate in snapshot.CatchUpLeases)
        {
            candidate.Validate();
            if (candidate.LeaseExpiresAtUtc > staleBeforeUtc)
            {
                continue;
            }

            var (operationId, fingerprint) = ReleaseIdentity(candidate);
            var receipt = await store.ReleaseStaleCatchUpLeaseAsync(
                new OutlookStaleCatchUpLeaseReleaseRequest(
                    operationId,
                    fingerprint,
                    candidate.CatchUpId,
                    candidate.ProfileId,
                    candidate.FencingToken,
                    candidate.LeaseExpiresAtUtc),
                cancellationToken).ConfigureAwait(false);
            if (receipt.Accepted)
            {
                released++;
            }
        }

        var replayed = 0;
        foreach (var candidate in snapshot.PendingHints)
        {
            candidate.Validate();
            if (candidate.RecordedAtUtc > pendingHintBeforeUtc)
            {
                continue;
            }

            var receipt = await store.ReplayHintAsync(candidate.Hint, cancellationToken).ConfigureAwait(false);
            if (receipt.Accepted)
            {
                replayed++;
            }
        }

        return new OutlookCaptureRecoveryResult(released, replayed);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        await (deploymentValidationHold ?? DeploymentValidationHold.None)
            .WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                _logger.LogWarning(
                    "Outlook durable recovery could not complete; durable work remains available for a later pass.");
            }

            await Task.Delay(options.RecoveryCadence, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private static (Guid OperationId, string Fingerprint) ReleaseIdentity(
        OutlookCatchUpLeaseRecoveryCandidate candidate)
    {
        var payload = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"release-stale-catchup|{candidate.CatchUpId:N}|{candidate.FencingToken}|{candidate.LeaseExpiresAtUtc.UtcTicks}"));
        var digest = SHA256.HashData(payload);
        return (new Guid(digest.AsSpan(0, 16)), Convert.ToHexStringLower(digest));
    }
}
