using System.Globalization;
using System.Text.RegularExpressions;

namespace Smurfm3u.Core.Parsing;

/// <summary>
/// The day an episode went out, where a name or a panel states one. Daily shows - soaps, news,
/// chat shows - are known by date rather than by number, and it is the date Sonarr asks for.
/// </summary>
public static partial class AirDates
{
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d{2})[-./](\d{1,2})[-./](\d{1,2})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex YearFirst();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})[-./](\d{1,2})[-./]((?:19|20)\d{2})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex YearLast();

    /// <summary>Where in a text a date was found, so a caller can take it out of the title.</summary>
    public readonly record struct Found(DateOnly Date, int Index, int Length);

    /// <summary>
    /// The first whole date in a text: 2026-09-29, 2026.09.29, 29/09/2026 or 29.09.2026. A day
    /// and month that could be either way round are read day first, the way the UK providers
    /// that carry soaps write them; one that only works month first is read that way.
    /// </summary>
    public static Found? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = YearFirst().Match(text);
        if (m.Success && Make(Int(m, 1), Int(m, 2), Int(m, 3)) is { } isoDate)
            return new Found(isoDate, m.Index, m.Length);

        m = YearLast().Match(text);
        if (m.Success)
        {
            var (first, second, year) = (Int(m, 1), Int(m, 2), Int(m, 3));

            if ((Make(year, second, first) ?? Make(year, first, second)) is { } date)
                return new Found(date, m.Index, m.Length);
        }

        return null;
    }

    /// <summary>
    /// A date field from a panel: "2026-09-29", sometimes with a time after it. Anything else,
    /// including the "0000-00-00" some panels use for none, reads as no date.
    /// </summary>
    public static DateOnly? Parse(string? value) =>
        Find(value) is { } found && found.Index == 0 ? found.Date : null;

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static DateOnly? Make(int year, int month, int day)
    {
        if (year < 1900 || year > DateTime.UtcNow.Year + 1) return null;
        if (month is < 1 or > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;

        return new DateOnly(year, month, day);
    }
}
