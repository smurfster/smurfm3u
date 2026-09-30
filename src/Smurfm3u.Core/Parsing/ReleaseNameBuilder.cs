using System.Globalization;
using System.Text;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Core.Parsing;

/// <summary>
/// Renders parsed metadata as a scene-style release name.
/// Dot-separated is deliberate: it is the form Sonarr's and Radarr's parsers handle
/// most reliably, and it survives being used as a folder name on every filesystem.
/// </summary>
public static class ReleaseNameBuilder
{
    private const char Backslash = (char)92;

    /// <param name="qualityTag">Source tag from the M3U source, e.g. "WEB-DL".</param>
    /// <param name="resolutionTag">Resolution token, e.g. "1080p". Blank to omit.</param>
    /// <param name="releaseGroup">Group suffix, e.g. "Smurfm3u". Blank to omit.</param>
    /// <param name="byAirDate">
    /// Name the episode by its air date even though it has a season and episode, because the
    /// client asked for it by date. Sonarr places a daily show's release by the date in its
    /// name, and a panel's numbering for one rarely matches the numbering Sonarr has.
    /// </param>
    public static string Build(
        ParsedTitle parsed, string qualityTag, string? resolutionTag, string? releaseGroup, bool byAirDate = false)
    {
        var parts = new List<string>();

        var title = Tokenize(parsed.Title);
        if (title.Length > 0) parts.Add(title);

        if (parsed.Year is { } year)
            parts.Add(year.ToString(CultureInfo.InvariantCulture));

        // Dated when asked for by date, and whenever a date is all there is to go on.
        if (parsed.Kind == MediaKind.Series
            && parsed.AirDate is { } airDate
            && (byAirDate || parsed.Season is null))
        {
            parts.Add(airDate.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        }
        else if (parsed.Kind == MediaKind.Series && parsed.Season is { } season)
        {
            parts.Add(parsed.Episode is { } episode
                ? $"S{season:D2}E{episode:D2}"
                : $"S{season:D2}");
        }

        if (!string.IsNullOrWhiteSpace(resolutionTag))
            parts.Add(Tokenize(resolutionTag));

        if (!string.IsNullOrWhiteSpace(qualityTag))
            parts.Add(Tokenize(qualityTag));

        var name = string.Join('.', parts.Where(p => p.Length > 0));

        if (!string.IsNullOrWhiteSpace(releaseGroup))
            name = $"{name}-{Tokenize(releaseGroup)}";

        return name.Length > 0 ? name : "Unknown.Release";
    }

    /// <summary>
    /// Reduces free text to the ASCII word characters a release name may contain,
    /// joining words with dots. Hyphens survive so "WEB-DL" stays intact.
    /// </summary>
    private static string Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('.');
                pendingSeparator = false;
                sb.Append(ch);
            }
            else if (ch == '-')
            {
                if (sb.Length > 0) { sb.Append('-'); pendingSeparator = false; }
            }
            else if (char.IsWhiteSpace(ch) || ch is '.' or '_' or ',' or ':' or ';' or '/' || ch == Backslash)
            {
                pendingSeparator = true;
            }
            // Apostrophes, brackets and the rest are dropped without splitting the word.
        }

        return sb.ToString().Trim('.', '-');
    }

    /// <summary>Strips characters that are illegal in a path segment on Windows or Linux.</summary>
    public static string SanitizePathSegment(string value)
    {
        const char doubleQuote = (char)34;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var illegal = ch is '<' or '>' or ':' or '/' or '|' or '?' or '*'
                          || ch == Backslash || ch == doubleQuote || char.IsControl(ch);
            sb.Append(illegal ? '_' : ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length > 0 ? result : "download";
    }
}
