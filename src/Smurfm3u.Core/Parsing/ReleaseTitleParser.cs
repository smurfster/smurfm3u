using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Core.Parsing;

/// <summary>
/// Turns provider-style playlist names into structured metadata.
/// Playlists are written for humans browsing an app, so the input is wildly inconsistent:
/// "EN - Top Gear (2002) S01 E03", "Pacific Rim - 2013", "[4K] Dune 2021 (2160p)".
/// Everything here is best-effort and falls back to a usable title rather than failing.
/// </summary>
public static partial class ReleaseTitleParser
{
    [GeneratedRegex(@"\bS(\d{1,3})\s*[._\- ]?\s*E(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\b(\d{1,2})\s*[xX]\s*(\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodeCross();

    [GeneratedRegex(@"\bSeason\s*(\d{1,3})\b\s*[-,:._ ]*\s*\bEp(?:isode)?\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodeWordy();

    [GeneratedRegex(@"\b(?:S|Season\s*)(\d{1,3})\b(?!\s*[._\- ]?\s*E\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonOnly();

    [GeneratedRegex(@"\bEp(?:isode)?\s*[._\- ]?\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"[\(\[\{]\s*((?:19|20)\d{2})\s*[\)\]\}]", RegexOptions.Compiled)]
    private static partial Regex BracketedYear();

    [GeneratedRegex(@"\b((?:19|20)\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex BareYear();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();

    /// <summary>Language/region/quality badges providers bolt onto the front of every name.</summary>
    private static readonly HashSet<string> PrefixBadges = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "us", "usa", "uk", "gb", "ca", "au", "nz", "ie", "fr", "de", "es", "it", "nl", "be",
        "pt", "br", "tr", "pl", "ro", "ru", "se", "no", "dk", "fi", "gr", "in", "pk", "ar", "sa", "za",
        "lat", "latino", "vip", "4k", "uhd", "hd", "fhd", "sd", "hevc", "multi", "dual", "sub", "dub",
        "adult", "new", "vod"
    };

    /// <summary>Tokens that describe the file rather than the work, stripped from titles.</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "2160p", "1080p", "1080i", "720p", "576p", "480p", "360p", "4k", "uhd", "fhd", "hd", "sd",
        "hdr", "hdr10", "dv", "dolbyvision", "sdr", "10bit", "8bit",
        "webdl", "web-dl", "webrip", "web", "bluray", "blu-ray", "bdrip", "brrip", "dvdrip", "dvd",
        "hdtv", "pdtv", "hdrip", "cam", "ts", "tc", "screener",
        "x264", "x265", "h264", "h265", "avc", "hevc", "xvid", "divx",
        "aac", "aac2", "ac3", "eac3", "dts", "dd", "ddp", "dd5", "ddp5", "mp3", "flac", "atmos",
        "multi", "dual", "subbed", "dubbed", "vostfr", "vf", "vo",
        "vip", "hq", "lq"
    };

    /// <summary>
    /// Reads a name a provider gave an entry.
    /// <para>
    /// A season stated without an episode is left alone here, because in a name it is usually
    /// part of the title rather than a marker: "Open Season 2", "Making The Witcher: Season 2".
    /// Measured against a catalogue of 143,440 films, reading it as a marker misfiled eight of
    /// them and gained nothing, since a provider's season listings are not what we ingest.
    /// </para>
    /// </summary>
    public static ParsedTitle Parse(string rawTitle, MediaKind hint = MediaKind.Unknown) =>
        Parse(rawTitle, hint, seasonAlone: false);

    /// <summary>
    /// Reads text someone typed to search with, where a season on its own is deliberate:
    /// "sherlock &amp; daughter s01" is a request for that season, not for a show whose name
    /// ends in "s01". Nothing stored has such a word in its title, so left in the query it
    /// matched nothing at all.
    /// </summary>
    public static ParsedTitle ParseQuery(string query) =>
        Parse(query, MediaKind.Unknown, seasonAlone: true);

    private static ParsedTitle Parse(string rawTitle, MediaKind hint, bool seasonAlone)
    {
        var work = StripPrefixBadges(rawTitle ?? string.Empty).Trim();
        if (work.Length == 0)
            return new ParsedTitle { Kind = hint, Title = string.Empty, SearchTitle = string.Empty };

        // A whole date is taken out first. Left in, its year reads as the show's year and its
        // day and month stay behind in the title: "EastEnders 29/09/2026" became "EastEnders 29 09".
        // Not for something already known to be a film, whose name is left as it always read.
        var airDate = hint == MediaKind.Movie ? null : AirDates.Find(work);
        var afterDate = string.Empty;

        if (airDate is { } found)
        {
            afterDate = work[(found.Index + found.Length)..];
            work = work[..found.Index] + " " + afterDate;
        }

        var (year, yearIndex, yearLength) = ExtractYear(work);
        var se = MatchSeasonEpisode(work, seasonAlone);

        string titlePart;
        string? episodeTitle = null;
        int? season = null;
        int? episode = null;

        if (se is null && airDate is { } dated && work[..dated.Index].Trim().Length > 0)
        {
            // Dated and nothing else: the show is what comes before the date, and anything
            // after it is the episode's own name.
            titlePart = work[..dated.Index];
            episodeTitle = Clean(afterDate, year);
        }
        else if (se is { } m)
        {
            season = m.Season;
            episode = m.Episode;
            titlePart = work[..m.Start];
            episodeTitle = Clean(work[(m.Start + m.Length)..], year);
        }
        else if (yearIndex >= 0)
        {
            titlePart = work[..yearIndex];
            // Whatever trails a year is nearly always quality noise, not a subtitle,
            // so it is only promoted when there was no title in front of the year.
            var trailing = Clean(work[(yearIndex + yearLength)..], year);
            if (trailing.Length > 0 && titlePart.Trim().Length == 0)
                titlePart = trailing;
        }
        else
        {
            titlePart = work;
        }

        var title = Clean(titlePart, year);

        // A name that was nothing but markers still needs something to show and match on.
        if (title.Length == 0) title = Clean(work, null);
        if (title.Length == 0) title = work.Trim();

        // A date on its own marks an episode of a daily show.
        var kind = season is not null || episode is not null || airDate is not null
            ? MediaKind.Series
            : hint != MediaKind.Unknown ? hint : MediaKind.Movie;

        if (kind == MediaKind.Series && season is null && episode is not null)
            season = 1;

        if (episodeTitle is { Length: 0 })
            episodeTitle = null;

        return new ParsedTitle
        {
            Kind = kind,
            Title = title,
            Year = year,
            Season = season,
            Episode = episode,
            EpisodeTitle = episodeTitle,
            AirDate = airDate?.Date,
            SearchTitle = Normalize(title)
        };
    }

    [GeneratedRegex(@"\b(?:\p{L}\.){2,}\p{L}?\.?", RegexOptions.Compiled)]
    private static partial Regex DottedAcronym();

    /// <summary>
    /// Collapses a title to lowercase words for matching, so "Marvel's Agents of S.H.I.E.L.D."
    /// and "Marvels Agents of SHIELD" land on the same key.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // A dot separates words ("Pacific.Rim") except inside an acronym ("S.H.I.E.L.D."),
        // so acronyms are welded back together before the general pass.
        var input = DottedAcronym().Replace(value, m => m.Value.Replace(".", string.Empty));

        // Spelled out rather than dropped. Sonarr sends "Sherlock and Daughter" where the
        // provider wrote "Sherlock & Daughter", so an ampersand that simply vanished left the
        // word "and" as a term the stored title could never contain - and every title with an
        // ampersand in it was unfindable by the one client that matters most.
        input = input.Replace("&", " and ");

        var sb = new StringBuilder(input.Length);
        var lastWasSpace = true;

        foreach (var ch in input.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (ch is ' ' or '.' or '_' or '-' or ':' or '/')
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            // Apostrophes and everything else simply vanish, joining the surrounding letters.
        }

        return sb.ToString().Trim();
    }

    private static string StripPrefixBadges(string input)
    {
        var s = input.Trim();

        // Peel one badge at a time: "EN - ", "[4K] ", "|VIP| ", "US: ".
        for (var guard = 0; guard < 6; guard++)
        {
            var before = s;

            if (s.Length > 0 && s[0] is '[' or '(' or '|')
            {
                var close = s.IndexOfAny([']', ')', '|'], 1);
                if (close is > 1 and <= 12 && PrefixBadges.Contains(s[1..close].Trim()))
                    s = s[(close + 1)..].TrimStart(' ', '-', ':', '|');
            }

            if (before == s)
            {
                var sep = s.IndexOfAny(['-', ':', '|']);
                if (sep is > 0 and <= 12 && PrefixBadges.Contains(s[..sep].Trim()))
                    s = s[(sep + 1)..].TrimStart(' ', '-', ':', '|');
            }

            if (before == s) break;
        }

        return s;
    }

    private static (int? Year, int Index, int Length) ExtractYear(string work)
    {
        var bracketed = BracketedYear().Match(work);
        if (bracketed.Success)
            return (int.Parse(bracketed.Groups[1].Value), bracketed.Index, bracketed.Length);

        // Prefer the last bare year so "2 Fast 2 Furious 2003" does not read the 2 as a year.
        Match? best = null;
        foreach (Match m in BareYear().Matches(work))
        {
            var value = int.Parse(m.Groups[1].Value);
            if (value < 1900 || value > DateTime.UtcNow.Year + 2) continue;
            best = m;
        }

        return best is null ? (null, -1, 0) : (int.Parse(best.Groups[1].Value), best.Index, best.Length);
    }

    /// <summary>
    /// Where a season and, when there is one, an episode were found. A null episode means the
    /// name covers a whole season - which is both what a provider's season listing looks like
    /// and what a release we build for a season pack is named.
    /// </summary>
    private readonly record struct SeasonEpisodeMatch(int Season, int? Episode, int Start, int Length);

    private static SeasonEpisodeMatch? MatchSeasonEpisode(string work, bool seasonAlone)
    {
        var m = SeasonEpisodeWordy().Match(work);
        if (m.Success)
            return new SeasonEpisodeMatch(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Index, m.Length);

        m = SeasonEpisode().Match(work);
        if (m.Success)
            return new SeasonEpisodeMatch(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Index, m.Length);

        m = SeasonEpisodeCross().Match(work);
        if (m.Success)
            return new SeasonEpisodeMatch(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Index, m.Length);

        // Fall back to a season and an episode stated separately, e.g. "Top Gear S01 Ep03".
        var seasonOnly = SeasonOnly().Match(work);
        var episodeOnly = EpisodeOnly().Match(work);

        if (seasonOnly.Success && episodeOnly.Success)
        {
            var start = Math.Min(seasonOnly.Index, episodeOnly.Index);
            var end = Math.Max(seasonOnly.Index + seasonOnly.Length, episodeOnly.Index + episodeOnly.Length);
            return new SeasonEpisodeMatch(
                int.Parse(seasonOnly.Groups[1].Value), int.Parse(episodeOnly.Groups[1].Value), start, end - start);
        }

        // A season with no episode after it: "Top Gear S01", "Sherlock & Daughter Season 1".
        // Only for a query, where it was deliberately typed. In a provider's name the same
        // words are usually the title itself - "Open Season 2" - so reading them as a marker
        // there costs more than it earns.
        //
        // It also has to leave a title behind. "S4: The Bob Lazar Story" opens with something
        // shaped like a season and is not one, and a query of nothing but a season number is
        // not a search for anything.
        if (seasonAlone && seasonOnly.Success && work[..seasonOnly.Index].Trim().Length > 0)
            return new SeasonEpisodeMatch(int.Parse(seasonOnly.Groups[1].Value), null, seasonOnly.Index, seasonOnly.Length);

        return null;
    }

    private static string Clean(string fragment, int? knownYear)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return string.Empty;

        var s = fragment;

        // Brackets are structural, not meaningful: keep what is inside, drop the walls.
        foreach (var ch in new[] { '(', ')', '[', ']', '{', '}' })
            s = s.Replace(ch, ' ');

        // Dot- and underscore-separated names only make sense once separators become spaces.
        if (!s.Contains(' '))
            s = s.Replace('.', ' ');
        s = s.Replace('_', ' ');

        var kept = new List<string>();
        foreach (var rawToken in Whitespace().Split(s))
        {
            var token = rawToken.Trim(' ', '-', ':', ',', '.', '|', '*', '~');
            if (token.Length == 0) continue;
            if (NoiseTokens.Contains(token)) continue;

            // Only the year we already extracted is dropped; titles like "1917" survive.
            if (knownYear is { } y && token.Equals(y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                continue;

            kept.Add(token);
        }

        return string.Join(' ', kept).Trim();
    }
}
