using System.Globalization;

namespace Smurfm3u.Core.Models;

/// <summary>
/// One entry offered by its air date, because the client asked for it by date. It is the same
/// entry a plain id names, under the dated name Sonarr can place a daily show's episode by -
/// and the grab has to arrive under that name too, so which one was offered travels in the id.
/// </summary>
public sealed record DailyReleaseId(long ItemId)
{
    private const string Prefix = "day.";

    public override string ToString() =>
        Prefix + ItemId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? raw, out DailyReleaseId id)
    {
        id = null!;

        if (raw is null || !raw.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!long.TryParse(raw.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            return false;

        id = new DailyReleaseId(itemId);
        return true;
    }
}
