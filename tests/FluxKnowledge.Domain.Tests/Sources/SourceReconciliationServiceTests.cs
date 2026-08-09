using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceReconciliationServiceTests
{
    [Fact]
    public async Task RunAvailableAsync_uses_a_stable_opaque_lease_owner_for_the_service_lifetime()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ]);

        await RunAsync(control, new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0)));

        Assert.Equal(2, control.LeaseOwners.Count);
        var owner = Assert.Single(control.LeaseOwners.Distinct(StringComparer.Ordinal));
        const string prefix = "source-reconciliation:";
        Assert.StartsWith(prefix, owner, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(owner[prefix.Length..], "N", out _));
    }

    [Fact]
    public async Task RunAvailableAsync_retries_a_transient_claim_failure()
    {
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromException<ClaimedSourceScan?>(new InvalidOperationException("transient claim failure")),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ]);

        await RunAsync(control, new UnusedScanner());

        Assert.Equal(2, control.ClaimAttempts);
        Assert.Empty(control.Completions);
    }

    [Fact]
    public async Task RunAvailableAsync_does_not_reclassify_a_successful_scan_when_completion_persistence_fails()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ],
            throwOnCompletion: true);
        var scanner = new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 3, 2, 1, 0));

        await RunAsync(control, scanner);

        var completion = Assert.Single(control.Completions);
        Assert.Equal(claim, completion.Claim);
        Assert.Equal(3, completion.Result.DiscoveredCount);
        Assert.Equal(2, completion.Result.IndexedCount);
        Assert.Null(completion.FailureReason);
        Assert.Equal(2, control.ClaimAttempts);
    }

    [Fact]
    public async Task RunAvailableAsync_survives_a_failed_scan_completion_persistence_failure()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ],
            throwOnCompletion: true);

        await RunAsync(control, new ThrowingScanner());

        var completion = Assert.Single(control.Completions);
        Assert.Equal(claim, completion.Claim);
        Assert.Equal(claim.SourceRoot.Id, completion.Result.SourceRootId);
        Assert.Equal(claim.ScanRequest.Id, completion.Result.SourceScanRequestId);
        Assert.Equal("InvalidOperationException", completion.FailureReason);
        Assert.Equal(2, control.ClaimAttempts);
    }

    private static async Task RunAsync(RecordingControlStore control, ISourceScanner scanner)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton(scanner);
        await using var provider = services.BuildServiceProvider();
        var service = new SourceReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelSourceScanWakeSignal(),
            TimeProvider.System);

        await service.RunAvailableAsync(CancellationToken.None);
    }

    private static ClaimedSourceScan Claim()
    {
        var rootId = new SourceRootId(Guid.NewGuid());
        var requestId = new SourceScanRequestId(Guid.NewGuid());
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00+00:00");
        return new ClaimedSourceScan(
            Guid.NewGuid(),
            "test-worker",
            1,
            SourceRootConfiguration.Restore(
                rootId,
                "C:\\source-reconciliation-service-tests",
                "Test source",
                true,
                false,
                1024,
                [],
                [],
                [],
                TimeSpan.FromMinutes(15),
                SourceRootState.Enabled,
                1),
            SourceScanRequest.Restore(
                requestId,
                rootId,
                "test",
                now,
                SourceScanRequestState.Running,
                now));
    }

    private sealed class RecordingControlStore(
        IEnumerable<Func<CancellationToken, ValueTask<ClaimedSourceScan?>>> claims,
        bool throwOnCompletion = false) : ISourceScanControlStore
    {
        private readonly Queue<Func<CancellationToken, ValueTask<ClaimedSourceScan?>>> _claims = new(claims);
        private readonly bool _throwOnCompletion = throwOnCompletion;

        public int ClaimAttempts { get; private set; }
        public List<string> LeaseOwners { get; } = [];
        public List<Completion> Completions { get; } = [];

        public ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimAttempts++;
            LeaseOwners.Add(leaseOwner);
            return _claims.Dequeue()(cancellationToken);
        }

        public ValueTask CompleteAsync(
            ClaimedSourceScan claim,
            SourceScanResult result,
            string? failureReason,
            CancellationToken cancellationToken)
        {
            Completions.Add(new Completion(claim, result, failureReason));
            return _throwOnCompletion
                ? ValueTask.FromException(new InvalidOperationException("completion persistence failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed record Completion(ClaimedSourceScan Claim, SourceScanResult Result, string? FailureReason);

    private sealed class ResultScanner(SourceScanResult result) : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class ThrowingScanner : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceScanResult>(new InvalidOperationException("scanner failed"));
    }

    private sealed class UnusedScanner : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceScanResult>(new InvalidOperationException("scanner should not run"));
    }
}
