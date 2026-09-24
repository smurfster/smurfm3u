using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Smurfm3u.Core.Models;

/// <summary>
/// Identifies one season of one show within one playlist, in a form that survives a round
/// trip through a Newznab link.
/// <para>
/// A pack is not a stored thing: it is every episode of a season that we happen to hold. The
/// id therefore names the season rather than a list of entries, and the list is worked out
/// again when the grab arrives - which is the point, because Sonarr may grab hours after it
/// searched, and an episode that turned up in between should come along.
/// </para>
/// </summary>
public sealed record SeasonPackId(int SourceId, int Season, int? Year, string SearchTitle)
{
    /// <summary>
    /// Dot-separated, because the title is base64url and that alphabet uses both '-' and '_'.
    /// The title is encoded rather than escaped so nothing in it can be read as a separator,
    /// whatever a client does to the query string on the way through.
    /// </summary>
    private const string Prefix = "pack.";

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{SourceId}.{Season}.{Year ?? 0}.{Encode(SearchTitle)}");

    /// <summary>True for anything shaped like one of our pack ids, which a plain entry id is not.</summary>
    public static bool Looks(string? raw) =>
        raw is not null && raw.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? raw, out SeasonPackId id)
    {
        id = null!;

        if (!Looks(raw)) return false;

        var parts = raw![Prefix.Length..].Split('.');
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var sourceId)) return false;
        if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out var season)) return false;
        if (!int.TryParse(parts[2], CultureInfo.InvariantCulture, out var year)) return false;
        if (!TryDecode(parts[3], out var title)) return false;

        id = new SeasonPackId(sourceId, season, year > 0 ? year : null, title);
        return true;
    }

    private static string Encode(string value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;

        try
        {
            decoded = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
