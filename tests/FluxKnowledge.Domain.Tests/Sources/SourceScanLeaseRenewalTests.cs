using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceScanLeaseRenewalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_Git_scan_renews_past_original_expiry_or_cancels_when_ownership_is_lost(bool loseOwnership)
    {
        var clock = new ManualClock();
        var root = SourceRootConfiguration.Create("C:\\disposable", "Git", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        var claim = new ClaimedSourceScan(Guid.NewGuid(), "owner", 1, root,
            SourceScanRequest.Restore(SourceScanRequestId.New(), root.Id, "test", clock.GetUtcNow(), SourceScanRequestState.Running, clock.GetUtcNow()));
        var control = new Control(claim, clock, loseOwnership);
        var scanner = new WaitingScanner();
        var services = new ServiceCollection(); services.AddSingleton<ISourceScanControlStore>(control); services.AddSingleton<ISourceScanner>(scanner);
        await using var provider = services.BuildServiceProvider();
        var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(), new ChannelSourceScanWakeSignal(), clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = service.RunAvailableAsync(stop.Token);
        await scanner.Started.Task.WaitAsync(stop.Token);
        var renewals = loseOwnership ? 1 : 4;
        for (var i = 0; i < renewals; i++)
        {
            await clock.FireNextAsync(stop.Token);
            Assert.Equal(!loseOwnership, await control.Renewed.Reader.ReadAsync(stop.Token));
        }
        if (loseOwnership) await scanner.Cancelled.Task.WaitAsync(stop.Token);
        else { Assert.True(clock.GetUtcNow() > DateTimeOffset.UnixEpoch.AddMinutes(15)); scanner.Release.TrySetResult(); }
        await run.WaitAsync(stop.Token);
        Assert.Equal(loseOwnership ? 0 : 1, control.Completed);
    }

    private sealed class Control(ClaimedSourceScan claim, ManualClock clock, bool loseOwnership) : ISourceScanControlStore
    {
        private bool claimed;
        private DateTimeOffset expires = clock.GetUtcNow().AddMinutes(15);
        public Channel<bool> Renewed { get; } = Channel.CreateUnbounded<bool>();
        public int Completed { get; private set; }
        public ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(string owner, DateTimeOffset now, TimeSpan duration, CancellationToken token)
        { var result = claimed ? null : claim; claimed = true; return ValueTask.FromResult(result); }
        public ValueTask<bool> RenewLeaseAsync(ClaimedSourceScan value, TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(claim, value);
            var owned = !loseOwnership && clock.GetUtcNow() < expires;
            if (owned) expires = clock.GetUtcNow().Add(duration);
            Renewed.Writer.TryWrite(owned); return ValueTask.FromResult(owned);
        }
        public ValueTask CompleteAsync(ClaimedSourceScan value, SourceScanResult result, string? failure, CancellationToken token)
        { Assert.Null(failure); Assert.True(clock.GetUtcNow() < expires); Completed++; return ValueTask.CompletedTask; }
    }

    private sealed class WaitingScanner : ISourceScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SourceScanResult> ScanAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new(root.Id, request.Id, 1, 1, 0, 0);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state, dueTime); timers.Writer.TryWrite(timer); return timer; }
        public async Task FireNextAsync(CancellationToken token)
        { var timer = await timers.Reader.ReadAsync(token); now += timer.Due; timer.Fire(); }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool disposed;
            public TimeSpan Due { get; } = due;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
