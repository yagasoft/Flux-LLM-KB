using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed partial class SourceReconciliationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_database_or_programmer_errors_still_fault_source_hosting(bool databaseError)
    {
        var failure = databaseError ? (Exception)RetainedProcessorActivationHostedServiceTests.SqlFailure(2627) :
            new InvalidOperationException("synthetic invariant failure");
        var control = new RecordingControlStore([_ => ValueTask.FromResult<ClaimedSourceScan?>(null)]);
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddScoped<ISourceActivityRestartStore>(_ => new DatabaseRecoveryRestartStore(() => throw failure, () => { }));
        await using var provider = services.BuildServiceProvider();
        using var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelSourceScanWakeSignal(), TimeProvider.System, databaseFailureClassifier: new SqlHostedDatabaseFailureClassifier());
        await service.StartAsync(default);
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Database_failure_disposes_the_iteration_then_rechecks_hold_or_cancels_without_stopping_the_app(
        bool afterStartup, bool stopDuringBackoff)
    {
        var clock = new RetainedProcessorActivationHostedServiceTests.ManualClock();
        var wake = new ChannelSourceScanWakeSignal();
        var hold = new DatabaseRecoveryHold();
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var disposed = 0;
        var failureAt = afterStartup ? 2 : 1;
        var control = new RecordingControlStore(Enumerable.Repeat<Func<CancellationToken, ValueTask<ClaimedSourceScan?>>>(
            _ => ValueTask.FromResult<ClaimedSourceScan?>(null), 16));
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddScoped<ISourceActivityRestartStore>(_ => new DatabaseRecoveryRestartStore(() =>
        {
            var attempt = Interlocked.Increment(ref calls);
            if (attempt == failureAt) throw RetainedProcessorActivationHostedServiceTests.SqlFailure(-2);
            if (attempt < failureAt) initial.TrySetResult(); else recovered.TrySetResult();
            return 0;
        }, () => Interlocked.Increment(ref disposed)));
        await using var provider = services.BuildServiceProvider();
        using var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            wake, clock, deploymentValidationHold: hold, databaseFailureClassifier: new SqlHostedDatabaseFailureClassifier());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(deadline.Token);
        try
        {
            if (afterStartup) { await initial.Task.WaitAsync(deadline.Token); wake.Notify(); }
            var delay = clock.WaitForTimerAsync(deadline.Token);
            await Task.WhenAny(delay, service.ExecuteTask!).WaitAsync(deadline.Token);
            Assert.False(service.ExecuteTask!.IsCompleted, "A database availability failure must not stop the host.");
            await delay;
            Assert.Equal(failureAt, calls);
            Assert.Equal(failureAt, disposed);
            Assert.Empty(control.Completions);
            if (stopDuringBackoff)
            {
                await service.StopAsync(deadline.Token);
                Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
                Assert.Equal(failureAt, calls);
                return;
            }
            hold.IsHeld = true;
            clock.Fire();
            await hold.Observed.Task.WaitAsync(deadline.Token);
            Assert.Equal(failureAt, calls);
            hold.Release();
            await recovered.Task.WaitAsync(deadline.Token);
            Assert.Equal(failureAt + 1, calls);
            Assert.Empty(control.Completions);
        }
        finally { await service.StopAsync(default); }
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class DatabaseRecoveryRestartStore(Func<int> offer, Action dispose) : ISourceActivityRestartStore, IDisposable
    {
        public ValueTask<int> OfferUnlinkedInProcessActivitiesAsync(CancellationToken ct) => ValueTask.FromResult(offer());
        public void Dispose() => dispose();
    }

    private sealed class DatabaseRecoveryHold : IDeploymentValidationHold
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHeld { get; set; }
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() { IsHeld = false; _released.TrySetResult(); }
        public async ValueTask WaitUntilReleasedAsync(CancellationToken ct)
        {
            if (!IsHeld) return;
            Observed.TrySetResult();
            await _released.Task.WaitAsync(ct);
        }
    }
}
