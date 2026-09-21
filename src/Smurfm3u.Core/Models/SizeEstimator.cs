using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Options;

namespace Smurfm3u.Core.Models;

/// <summary>
/// Works out a plausible byte size for a release. Playlists rarely declare one, and both
/// Sonarr and Radarr reject zero-byte releases and use size to choose between candidates,
/// so an estimate is far better than nothing.
/// </summary>
public static class SizeEstimator
{
    public static long Estimate(M3uItem item, ServiceSettings settings)
    {
        if (item.SizeBytes > 0)
            return item.SizeBytes;

        if (item.DurationSeconds > 0 && settings.AssumedBitrateKbps > 0)
            return (long)item.DurationSeconds * settings.AssumedBitrateKbps * 1000L / 8L;

        return (long)Math.Max(1, settings.FallbackSizeMib) * 1024L * 1024L;
    }
}
