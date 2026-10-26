using System.Collections.Concurrent;
using System.Diagnostics;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;

/// <summary>Read-only, model-free staging measurements. No production registration or SQL/source text logging.</summary>
internal sealed class StagingSourceDiagnostics : ILoggerProvider
{
    private long _enumerationSource, _retentionSource, _verificationSource, _retained, _sql, _sqlFailed, _queries, _reads, _records;
    private readonly ConcurrentQueue<ScanMeasurement> _scans = new();
    internal sealed record Counters(long EnumerationSourceBytes, long RetentionSourceBytes, long VerificationSourceBytes,
        long RetainedBytes, long SqlCommands, long SqlFailedCommands, long JournalQueries, long JournalReads, long JournalRecords);
    private sealed record ScanMeasurement(Guid RootId, Guid RequestId, bool Verification, DateTimeOffset StartedAtUtc,
        double ElapsedMilliseconds, int EnumeratedEntries, Counters Before, Counters After, bool ReturnedNormally);

    internal static StagingSourceDiagnostics Register(IServiceCollection services, ILoggingBuilder logging)
    {
        var metrics = new StagingSourceDiagnostics();
        logging.AddProvider(metrics);
        logging.AddFilter<StagingSourceDiagnostics>("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
        var artifacts = services.Last(x => x.ServiceType == typeof(ISourceArtifactStore));
        var journal = services.Last(x => x.ServiceType == typeof(ISourceChangeJournalReader));
        if (artifacts.Lifetime != ServiceLifetime.Singleton || artifacts.ImplementationFactory is null ||
            journal.Lifetime != ServiceLifetime.Singleton || journal.ImplementationFactory is null)
            throw new InvalidOperationException("Staging source diagnostics composition changed.");
        services.Remove(artifacts); services.Remove(journal);
        services.AddSingleton<ISourceArtifactStore>(provider =>
        {
            // Retain the normal protected paths, publication gate and lifetime ownership.
            var store = (ContentAddressedSourceArtifactStore)artifacts.ImplementationFactory(provider);
            store.SourceBytesRead = bytes => Interlocked.Add(ref metrics._retentionSource, bytes);
            store.RetainedBytesRead = bytes => Interlocked.Add(ref metrics._retained, bytes);
            return store;
        });
        services.AddSingleton<ISourceChangeJournalReader>(provider => new JournalMeasurements(
            (ISourceChangeJournalReader)journal.ImplementationFactory(provider), metrics));
        services.AddScoped<ISourceFileEnumerator>(provider =>
        {
            var reader = ActivatorUtilities.CreateInstance<LocalSourceEnumerator>(provider);
            reader.SourceBytesRead = bytes => Interlocked.Add(ref metrics._enumerationSource, bytes);
            return reader;
        });
        services.AddSingleton<ISourceVerificationContentReader>(provider =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native source verification diagnostics require Windows.");
            var reader = ActivatorUtilities.CreateInstance<WindowsSourceVerificationContentReader>(provider);
            reader.SourceBytesRead = bytes => Interlocked.Add(ref metrics._verificationSource, bytes);
            return reader;
        });
        services.AddScoped<SourceWorkScanner>();
        services.AddScoped<ISourceScanner>(provider => new ScanMeasurements(provider.GetRequiredService<SourceWorkScanner>(),
            (LocalSourceEnumerator)provider.GetRequiredService<ISourceFileEnumerator>(), metrics));
        return metrics;
    }

    private Counters Snapshot() => new(Interlocked.Read(ref _enumerationSource), Interlocked.Read(ref _retentionSource),
        Interlocked.Read(ref _verificationSource), Interlocked.Read(ref _retained), Interlocked.Read(ref _sql),
        Interlocked.Read(ref _sqlFailed), Interlocked.Read(ref _queries), Interlocked.Read(ref _reads), Interlocked.Read(ref _records));

    internal object Read() => new
    {
        observedAtUtc = DateTimeOffset.UtcNow,
        scope = "Process-wide logical checksum bytes during each interval; overlapping scan deltas must not be summed. Enumeration counts managed entries processed, not native directory buffers. SQL counts EF command completion/failure events, not all SQL operations.",
        counters = Snapshot(), scans = _scans.ToArray()
    };

    public ILogger CreateLogger(string categoryName) => new CommandLogger(this,
        categoryName == "Microsoft.EntityFrameworkCore.Database.Command");
    public void Dispose() { }

    private sealed class CommandLogger(StagingSourceDiagnostics metrics, bool commands) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => commands && level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            // Do not inspect state or invoke the formatter: it may contain SQL text/private parameters.
            if (!commands) return;
            if (eventId.Id == 20101) Interlocked.Increment(ref metrics._sql);
            else if (eventId.Id == 20102) Interlocked.Increment(ref metrics._sqlFailed);
        }
    }

    private sealed class JournalMeasurements(ISourceChangeJournalReader inner, StagingSourceDiagnostics metrics) : ISourceChangeJournalReader
    {
        public ValueTask<JournalCapability> QueryAsync(string volume, CancellationToken ct)
        { Interlocked.Increment(ref metrics._queries); return inner.QueryAsync(volume, ct); }
        public async ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct)
        {
            Interlocked.Increment(ref metrics._reads);
            var page = await inner.ReadAsync(cursor, maximumBytes, ct).ConfigureAwait(false);
            Interlocked.Add(ref metrics._records, page.Changes.Count);
            return page;
        }
    }

    private sealed class ScanMeasurements(ISourceScanner inner, LocalSourceEnumerator enumerator, StagingSourceDiagnostics metrics) : ISourceScanner
    {
        public async ValueTask<SourceScanResult> ScanAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
        {
            var before = metrics.Snapshot(); var startedAt = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
            var returned = false;
            try
            {
                var result = await inner.ScanAsync(root, request, ct).ConfigureAwait(false);
                returned = true; return result;
            }
            finally
            {
                metrics._scans.Enqueue(new(root.Id.Value, request.Id.Value, request.IsVerification, startedAt,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, request.IsVerification ? 0 : enumerator.LastEnumeratedEntries,
                    before, metrics.Snapshot(), returned));
                while (metrics._scans.Count > 256) metrics._scans.TryDequeue(out _);
            }
        }
    }
}
