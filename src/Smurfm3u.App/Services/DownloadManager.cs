using System.Collections.Concurrent;

namespace Smurfm3u.App.Services;

/// <summary>
/// Live state for one download in flight. The database row lags by a couple of seconds,
/// so the queue endpoint reads progress from here to stay responsive.
/// </summary>
public sealed class ActiveDownload(long id, int? sourceId, CancellationTokenSource cts)
{
    public long Id { get; } = id;
    public int? SourceId { get; } = sourceId;
    public CancellationTokenSource Cancellation { get; } = cts;

    /// <summary>Set to stop cleanly and keep the partial file for a later resume.</summary>
    public volatile bool PauseRequested;

    public long DownloadedBytes;
    public long TotalBytes;
    public long BytesPerSecond;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Registry of downloads currently running. Singleton, because the API, the UI and the
/// worker all need to see and steer the same set.
/// </summary>
public class DownloadManager
{
    private readonly ConcurrentDictionary<long, ActiveDownload> _active = new();

    public int Count => _active.Count;

    public IReadOnlyCollection<ActiveDownload> Snapshot() => _active.Values.ToArray();

    public int CountForSource(int sourceId) => _active.Values.Count(x => x.SourceId == sourceId);

    public ActiveDownload? Get(long id) => _active.GetValueOrDefault(id);

    public ActiveDownload Register(long id, int? sourceId)
    {
        var handle = new ActiveDownload(id, sourceId, new CancellationTokenSource());
        _active[id] = handle;
        return handle;
    }

    public void Unregister(long id)
    {
        if (_active.TryRemove(id, out var handle))
            handle.Cancellation.Dispose();
    }

    /// <summary>Stops the transfer but leaves the partial file in place.</summary>
    public bool Pause(long id)
    {
        if (!_active.TryGetValue(id, out var handle)) return false;

        handle.PauseRequested = true;
        handle.Cancellation.Cancel();
        return true;
    }

    /// <summary>Stops the transfer and lets the caller discard the partial file.</summary>
    public bool Cancel(long id)
    {
        if (!_active.TryGetValue(id, out var handle)) return false;

        handle.Cancellation.Cancel();
        return true;
    }

    public long TotalBytesPerSecond() => _active.Values.Sum(x => Interlocked.Read(ref x.BytesPerSecond));
}
