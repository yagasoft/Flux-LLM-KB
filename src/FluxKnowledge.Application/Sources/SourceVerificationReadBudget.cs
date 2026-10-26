using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Sources;

/// <summary>One shared token bucket for source and retained checksum reads in a verification batch.</summary>
public sealed class SourceVerificationReadBudget : ISourceVerificationReadBudget, IDisposable
{
    public const int MaximumChunkBytes = 128 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly long _bytesPerSecond;
    private readonly Func<CancellationToken, ValueTask>? _beforeWait;
    private long _lastTimestamp;
    private double _tokens = MaximumChunkBytes;
    private long _sourceBytesRead, _retainedBytesRead;

    public long SourceBytesRead => Interlocked.Read(ref _sourceBytesRead);
    public long RetainedBytesRead => Interlocked.Read(ref _retainedBytesRead);

    public void RecordRead(SourceVerificationReadKind kind, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        switch (kind)
        {
            case SourceVerificationReadKind.Source: Interlocked.Add(ref _sourceBytesRead, bytes); break;
            case SourceVerificationReadKind.Retained: Interlocked.Add(ref _retainedBytesRead, bytes); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public SourceVerificationReadBudget(long bytesPerSecond, TimeProvider? timeProvider = null, Func<CancellationToken, ValueTask>? beforeWait = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _clock = timeProvider ?? TimeProvider.System;
        _beforeWait = beforeWait;
        _lastTimestamp = _clock.GetTimestamp();
    }

    public async ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || bytes > MaximumChunkBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _clock.GetTimestamp();
                var elapsed = Math.Max(0, _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds);
                _tokens = Math.Min(MaximumChunkBytes, _tokens + elapsed * _bytesPerSecond);
                _lastTimestamp = now;
                if (_tokens >= bytes) { _tokens -= bytes; return; }
                // Round up to avoid a hot loop at the timer's millisecond resolution.
                var milliseconds = Math.Max(1, Math.Ceiling((bytes - _tokens) * 1000 / _bytesPerSecond));
                if (_beforeWait is not null)
                {
                    await _beforeWait(cancellationToken).ConfigureAwait(false);
                    milliseconds = Math.Min(1000, milliseconds);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
