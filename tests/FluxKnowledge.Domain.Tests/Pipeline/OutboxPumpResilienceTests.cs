using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Pipeline;

public sealed class OutboxPumpResilienceTests
{
    [Fact]
    public async Task Claim_failure_does_not_terminate_the_hosted_pump()
    {
        var outbox = new FailOnceOutboxStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOutboxStore>(outbox);
        services.AddScoped<IJobClaimStore, UnusedJobClaimStore>();
        services.AddScoped<OutboxWorkerRegistration>();
        services.AddScoped<IStageTransitionStore, UnusedTransitionStore>();
        services.AddSingleton<IStatusEventPublisher, UnusedStatusPublisher>();
        services.AddSingleton<IOutboxWakeSignal, ChannelOutboxWakeSignal>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<StageTransitionService>();
        await using var provider = services.BuildServiceProvider();
        var wakeSignal = new ChannelOutboxWakeSignal();
        var pump = new OutboxPumpService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            wakeSignal,
            TimeProvider.System);

        await pump.StartAsync(CancellationToken.None);
        try
        {
            await outbox.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            wakeSignal.Notify();

            await outbox.SecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
            pump.Dispose();
        }

        Assert.Equal(2, outbox.AttemptCount);
    }

    private sealed class FailOnceOutboxStore : IOutboxStore
    {
        public TaskCompletionSource FirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount { get; private set; }

        public ValueTask EnqueueAsync(
            DispatchMessage message,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<ClaimedDispatchMessage?> ClaimNextDueAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            IReadOnlyCollection<string> registeredOperations,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                FirstAttempt.SetResult();
                return ValueTask.FromException<ClaimedDispatchMessage?>(
                    new InvalidOperationException("transient claim failure"));
            }

            SecondAttempt.SetResult();
            return ValueTask.FromResult<ClaimedDispatchMessage?>(null);
        }

        public ValueTask ReleaseAsync(
            ClaimedDispatchMessage claim,
            DateTimeOffset dueAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class UnusedJobClaimStore : IJobClaimStore
    {
        public ValueTask<Job?> ClaimWorkerAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Job?>(null);

        public ValueTask<Job?> ClaimGpuAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Job?>(null);

        public ValueTask<ClaimedJob?> ClaimNextDueAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ClaimedJob?>(null);

        public ValueTask<ClaimedJob?> ClaimForDispatchAsync(
            ClaimedDispatchMessage dispatchMessage,
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ClaimedJob?>(null);
    }

    private sealed class UnusedTransitionStore : IStageTransitionStore
    {
        public ValueTask<StageTransitionResult> TransitionAsync(
            StageTransitionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<StageTransitionResult>(
                new InvalidOperationException("unused"));

        public ValueTask FailAsync(
            StageFailureRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("unused"));
    }

    private sealed class UnusedStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(
            StatusChanged statusChanged,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
