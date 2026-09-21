using System.Diagnostics;

namespace Smurfm3u.App.Services;

/// <summary>
/// Rate limiter shared by concurrent downloads. Holds up to one second of burst so a
/// limit that is raised mid-transfer takes effect immediately rather than after a backlog.
/// A rate of zero means unlimited; a negative rate means paused.
/// </summary>
public sealed class TokenBucket
{
    private readonly Lock _gate = new();

    private double _bytesPerSecond;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public double BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }
    }

    public void SetRate(double bytesPerSecond)
    {
        lock (_gate)
        {
            Refill();
            _bytesPerSecond = bytesPerSecond;
            _tokens = Math.Min(_tokens, Capacity);
        }
    }

    private double Capacity => _bytesPerSecond > 0 ? _bytesPerSecond : 0;

    /// <summary>
    /// Waits until <paramref name="bytes"/> may be transferred. Callers pass the size of a
    /// chunk they are about to read, so chunks should stay small enough to keep pauses short.
    /// </summary>
    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_gate)
            {
                Refill();

                if (_bytesPerSecond == 0)
                    return;

                if (_bytesPerSecond > 0 && _tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                wait = _bytesPerSecond > 0
                    ? TimeSpan.FromSeconds((bytes - _tokens) / _bytesPerSecond)
                    : TimeSpan.FromMilliseconds(250); // paused: re-check rather than block forever
            }

            // Cap the sleep so a rate change is noticed promptly.
            await Task.Delay(Clamp(wait), ct);
        }
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5)
        : wait > TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500)
        : wait;

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
        _lastTicks = now;

        if (_bytesPerSecond <= 0) return;

        _tokens = Math.Min(Capacity, _tokens + elapsed * _bytesPerSecond);
    }
}
