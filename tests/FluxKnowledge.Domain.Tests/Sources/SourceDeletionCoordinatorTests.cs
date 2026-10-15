using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceDeletionCoordinatorTests
{
    [Fact]
    public async Task A_claim_failure_leaves_durable_work_recoverable_without_terminating_the_pump()
    {
        var store = new FailingStore(new InvalidOperationException("unused"))
        {
            ClaimError = new TimeoutException("unavailable-test-provider")
        };
        var logger = new CapturingLogger();
        var coordinator = new SourceDeletionCoordinator(store, logger: logger);

        Assert.False(await coordinator.RunOneAsync(default));
        Assert.Null(store.RecordedReason);
        Assert.Contains(logger.Errors, error => ReferenceEquals(error, store.ClaimError));
    }

    [Fact]
    public async Task The_original_failure_remains_logged_if_recording_attention_also_fails()
    {
        var original = new IOException("private-cleanup-detail");
        var recording = new IOException("private-diagnostic-write-detail");
        var store = new FailingStore(new SourceDeletionFailureException("source-delete-sql-timeout", original))
        {
            FailureRecordingError = recording
        };
        var logger = new CapturingLogger();
        var coordinator = new SourceDeletionCoordinator(store, logger: logger);

        Assert.False(await coordinator.RunOneAsync(default));
        Assert.Equal("source-delete-sql-timeout", store.RecordedReason);
        Assert.Contains(logger.Errors, error => ReferenceEquals(error?.InnerException, original));
        Assert.Contains(logger.Errors, error => ReferenceEquals(error, recording));
    }

    [Fact]
    public async Task Failure_recording_failure_does_not_terminate_the_deletion_pump()
    {
        var store = new FailingStore(new InvalidOperationException("original-cleanup-failure"))
        {
            FailureRecordingError = new IOException("diagnostic-write-failed")
        };
        var coordinator = new SourceDeletionCoordinator(store);

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));
        Assert.Equal("source-delete-cleanup-failed", store.RecordedReason);
    }

    [Fact]
    public async Task A_cleanup_timeout_retains_a_distinct_safe_failure_reason()
    {
        var store = new FailingStore(new TimeoutException("private-provider-detail"));
        var coordinator = new SourceDeletionCoordinator(store);

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));
        Assert.Equal("source-delete-timeout", store.RecordedReason);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_recording_attention()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FailingStore(new OperationCanceledException(cancellation.Token));
        var coordinator = new SourceDeletionCoordinator(store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunOneAsync(cancellation.Token).AsTask());
        Assert.Null(store.RecordedReason);
    }

    private sealed class FailingStore(Exception cleanupError) : ISourceDeletionStore
    {
        private bool _claimed;
        public Exception? ClaimError { get; init; }
        public Exception? FailureRecordingError { get; init; }
        public string? RecordedReason { get; private set; }

        public ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
        {
            if (ClaimError is not null) return ValueTask.FromException<SourceDeletionWorkItem?>(ClaimError);
            if (_claimed) return ValueTask.FromResult<SourceDeletionWorkItem?>(null);
            _claimed = true;
            return ValueTask.FromResult<SourceDeletionWorkItem?>(
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cleanup-memberships"));
        }

        public ValueTask<SourceDeletionRunResult> PurgeAsync(SourceDeletionWorkItem workItem,
            IndexGenerationCandidateSnapshot? survivorGeneration, CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceDeletionRunResult>(cleanupError);

        public ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
            SourceDeletionWorkItem workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem workItem, Guid cleanupItemId,
            SourceDeletionFileResult result, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken)
        {
            RecordedReason = reasonCode;
            return FailureRecordingError is null ? ValueTask.CompletedTask : ValueTask.FromException(FailureRecordingError);
        }
        public ValueTask DeferAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<SourceDeletionCoordinator>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add(exception);
        }
    }
}
