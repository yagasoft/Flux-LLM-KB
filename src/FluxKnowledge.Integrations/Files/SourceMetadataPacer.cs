namespace FluxKnowledge.Integrations.Files;

/// <summary>Owned by one metadata enumeration; never waits while a restrictive namespace guard is held.</summary>
internal sealed class SourceMetadataPacer(int entriesPerSecond, TimeProvider? timeProvider = null)
{
    private readonly int _rate = entriesPerSecond is >= 1 and <= 4096 ? entriesPerSecond : throw new ArgumentOutOfRangeException(nameof(entriesPerSecond));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly int _burst = Math.Min(64, entriesPerSecond);
    private double _tokens = Math.Min(64, entriesPerSecond);
    private long _lastTimestamp = (timeProvider ?? TimeProvider.System).GetTimestamp();

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + Math.Max(0, _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds) * _rate);
            _lastTimestamp = now;
            if (_tokens >= 1) { _tokens--; return; }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling((1 - _tokens) * 1000 / _rate))), _clock, cancellationToken).ConfigureAwait(false);
        }
    }
}
