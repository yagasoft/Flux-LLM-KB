using System.Reflection;
using System.Threading.Channels;
using System.Security.Cryptography;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class RetainedProcessorActivationHostedServiceTests
{
    [Fact]
    public async Task Prelogin_failure_during_inspection_defers_without_claiming_and_revalidates_in_a_fresh_scope()
    {
        var clock = new ManualClock();
        var scopesDisposed = 0;
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04 };
        var candidate = new RetainedProcessorPromotionCandidate(Guid.NewGuid(), new(Guid.NewGuid()),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), ".zip");
        var branches = new InspectionBranches(candidate);
        var reader = new InspectionReader(candidate, bytes);
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopeOwner(() => Interlocked.Increment(ref scopesDisposed)));
        services.AddScoped(provider =>
        {
            _ = provider.GetRequiredService<ScopeOwner>();
            var zip = new ZipArchiveRetainedProcessor(null!);
            return new RetainedProcessorActivationService(new SourceCapabilityService(new Capabilities(),
                new LocalSourceCapabilityHandlerRegistry([zip])), branches, reader, zip,
                new RetainedProcessorOptions { ArchiveZipExpandEnabled = true, CsharpCodeEnabled = false }, clock);
        });
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        using var service = new RetainedProcessorActivationHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            failureClassifier: new SqlRetainedProcessorFailureClassifier(), logger: logger, timeProvider: clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(timeout.Token);
        try
        {
            var deferred = clock.WaitForTimerAsync(timeout.Token);
            await Task.WhenAny(deferred, service.ExecuteTask!).WaitAsync(timeout.Token);
            Assert.False(service.ExecuteTask!.IsCompleted, "The inspection availability failure must defer rather than stop the host.");
            await deferred;
            Assert.Equal(1, scopesDisposed);
            Assert.Equal(0, branches.Promotions);
            Assert.Equal(0, branches.Claims);
            Assert.Equal(0, reader.ByteReads);
            clock.Fire();
            await clock.WaitForTimerAsync(timeout.Token);
            Assert.Equal(2, scopesDisposed);
            Assert.Equal(2, reader.Inspections);
            Assert.Equal(1, reader.ByteReads);
            Assert.Equal(1, branches.Promotions);
            Assert.Equal(1, branches.Claims);
            Assert.Contains("SQL error -2146893055", Assert.Single(logger.Messages));
        }
        finally { await service.StopAsync(timeout.Token); }
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Observed_SQL_failures_delay_then_resume_with_a_fresh_disposed_scope_and_stop_cleanly()
    {
        var clock = new ManualClock();
        var attempts = Channel.CreateUnbounded<int>();
        var scopesDisposed = 0;
        var calls = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopeOwner(() => Interlocked.Increment(ref scopesDisposed)));
        services.AddScoped(provider =>
        {
            _ = provider.GetRequiredService<ScopeOwner>();
            return Activation(new Branches(() =>
            {
                var attempt = Interlocked.Increment(ref calls);
                attempts.Writer.TryWrite(attempt);
                return attempt switch { 1 => SqlFailure(-2), 2 => SqlFailure(1205), _ => null };
            }), clock);
        });
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        using var service = new RetainedProcessorActivationHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            failureClassifier: new SqlRetainedProcessorFailureClassifier(),
            logger: logger, timeProvider: clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(timeout.Token);
            Assert.Equal(1, await attempts.Reader.ReadAsync(timeout.Token));
            await clock.WaitForTimerAsync(timeout.Token);
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Equal(1, scopesDisposed);
            Assert.Equal(1, calls);
            clock.Fire();
            Assert.Equal(2, await attempts.Reader.ReadAsync(timeout.Token));
            await clock.WaitForTimerAsync(timeout.Token);
            Assert.Equal(2, scopesDisposed);
            clock.Fire();
            Assert.Equal(3, await attempts.Reader.ReadAsync(timeout.Token));
            await clock.WaitForTimerAsync(timeout.Token);
            Assert.Equal(3, scopesDisposed);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally { await service.StopAsync(timeout.Token); }
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("SQL error -2", logger.Messages[0]);
        Assert.Contains("SQL error 1205", logger.Messages[1]);
        Assert.All(logger.Messages, value => Assert.DoesNotContain("synthetic provider failure", value));
    }

    [Fact]
    public void SQL_classifier_rejects_mixed_or_unrelated_errors_and_is_registered_by_the_production_worker_composition()
    {
        var classifier = new SqlRetainedProcessorFailureClassifier();
        Assert.False(classifier.TryClassify(SqlFailure(-2, 2627), out _));
        Assert.False(classifier.TryClassify(SqlFailure(-2146893055, 18456), out _));
        Assert.False(classifier.TryClassify(new InvalidOperationException("wrapper", SqlFailure(-2)), out _));
        Assert.True(classifier.TryClassify(new DbUpdateException("synthetic wrapper", SqlFailure(1205)), out var number));
        Assert.Equal(1205, number);
        using var provider = new ServiceCollection().AddFluxKnowledgeOutboxWorkers().BuildServiceProvider();
        Assert.IsType<SqlRetainedProcessorFailureClassifier>(provider.GetRequiredService<IRetainedProcessorFailureClassifier>());
    }

    [Theory]
    [InlineData(2627)]
    [InlineData(1222)]
    [InlineData(4060)]
    [InlineData(0)]
    public async Task Unexpected_failures_remain_host_faults(int sqlError)
    {
        var failure = sqlError == 0 ? new InvalidOperationException("synthetic invariant violation") : (Exception)SqlFailure(sqlError);
        using var provider = new ServiceCollection().AddScoped(_ => Activation(new Branches(() => failure), TimeProvider.System)).BuildServiceProvider();
        using var service = new RetainedProcessorActivationHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            failureClassifier: new SqlRetainedProcessorFailureClassifier());
        await service.StartAsync(CancellationToken.None);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, thrown);
    }

    private static RetainedProcessorActivationService Activation(IRetainedProcessorBranchStore branches, TimeProvider clock) => new(
        null!, branches, null!, new ZipArchiveRetainedProcessor(null!), new RetainedProcessorOptions { CsharpCodeEnabled = false }, clock);

    private static SqlException SqlFailure(params int[] numbers)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        foreach (var number in numbers)
        {
            var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
                [number, (byte)0, (byte)14, "synthetic", "synthetic provider failure", string.Empty, 1, 0, null], null)!;
            typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        }
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2).Invoke(null, [errors, "synthetic"])!;
    }

    private sealed class ScopeOwner(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class Capabilities : ISourceCapabilityStore
    {
        public ValueTask<RegisteredSourceCapability> RegisterAsync(RegisteredSourceCapability value, CancellationToken ct) => ValueTask.FromResult(value);
        public ValueTask<RegisteredSourceCapability?> FindAsync(Guid id, CancellationToken ct) => ValueTask.FromResult<RegisteredSourceCapability?>(null);
    }
    private sealed class InspectionReader(RetainedProcessorPromotionCandidate candidate, byte[] bytes) : IRetainedSourceReader
    {
        public int Inspections { get; private set; }
        public int ByteReads { get; private set; }
        public ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId id, CancellationToken ct)
        {
            Assert.Equal(candidate.SourceRevisionId, id);
            return ++Inspections == 1 ? ValueTask.FromException<RetainedArtifactInspection>(SqlFailure(-2146893055)) :
                ValueTask.FromResult(new RetainedArtifactInspection(id, candidate.InputSha256, bytes.Length));
        }
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId id, CancellationToken ct)
        {
            ByteReads++;
            return ValueTask.FromResult(new RetainedSourceBytes(id, bytes, candidate.InputSha256, bytes.Length));
        }
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class InspectionBranches(RetainedProcessorPromotionCandidate candidate) : IRetainedProcessorBranchStore
    {
        public int Promotions { get; private set; }
        public int Claims { get; private set; }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int count, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([candidate]);
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken ct)
        { Assert.Equal(candidate, value); Promotions++; return ValueTask.FromResult(false); }
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string owner, int count, CancellationToken ct)
        { Claims++; return ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]); }
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcome, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcome, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class RecordingLogger : ILogger<RetainedProcessorActivationHostedService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }
    private sealed class Branches(Func<Exception?> failure) : IRetainedProcessorBranchStore
    {
        public ValueTask<int> ReconcileForceRequestsAsync(bool enabled, CancellationToken ct) => failure() is { } exception
            ? ValueTask.FromException<int>(exception) : ValueTask.FromResult(0);
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int count, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate candidate, SourceCapabilityDescriptor capability, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcome, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string owner, int count, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcome, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();
        private ManualTimer? _current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Writer.TryWrite(timer);
            return timer;
        }
        public async Task WaitForTimerAsync(CancellationToken ct) => _current = await _timers.Reader.ReadAsync(ct);
        public void Fire() => _current!.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
