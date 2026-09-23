using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;

namespace Smurfm3u.Core.Xtream;

/// <summary>
/// Turns what the panel returns into the same candidates a playlist file produces.
/// <para>
/// This is the whole point of talking to the API rather than reading the provider's m3u: a
/// panel states the season, the episode and the container outright, where a playlist leaves
/// them to be guessed out of a display name. Where it states them, they are used as given.
/// </para>
/// </summary>
public static class XtreamCatalogue
{
    /// <summary>A film. The name is still parsed, because the year is only ever in the name.</summary>
    public static IngestCandidate ForMovie(
        XtreamVodStream stream, string? category, XtreamCredentials credentials)
    {
        var extension = Extension(stream.ContainerExtension);
        var name = (stream.Name ?? string.Empty).Trim();

        var entry = new M3uEntry
        {
            DisplayName = name,
            Url = credentials.StreamUrl(XtreamStreamKind.Movie, stream.StreamId ?? string.Empty, extension),
            Attributes = Attributes(category, stream.Icon)
        };

        // Parsed left null on purpose: nothing here beats the title parser at finding a year.
        return new IngestCandidate(entry, new VodVerdict(true, MediaKind.Movie, extension, "xtream vod stream"));
    }

    /// <summary>
    /// Every episode the panel lists for one series. The series name is parsed once for its
    /// title and year, and each episode then overrides what the panel actually knows.
    /// </summary>
    public static IEnumerable<IngestCandidate> ForSeries(
        XtreamSeries series, XtreamSeriesInfo info, string? category, XtreamCredentials credentials)
    {
        var showName = (series.Name ?? string.Empty).Trim();
        if (showName.Length == 0) yield break;

        var show = ReleaseTitleParser.Parse(showName, MediaKind.Series);
        var year = show.Year ?? YearFrom(series.ReleaseDate);

        foreach (var (seasonKey, episodes) in info.Episodes)
        {
            foreach (var episode in episodes)
            {
                if (string.IsNullOrWhiteSpace(episode.Id)) continue;

                // The key is the season for panels that leave it off the episode itself.
                var season = episode.Season ?? (int.TryParse(seasonKey, out var fromKey) ? fromKey : null);
                var number = episode.EpisodeNumber;

                // Without a season and an episode the *arr apps cannot place it, and a title
                // parsed back out of an episode name is worse than not offering it at all.
                if (season is null || number is null) continue;

                var extension = Extension(episode.ContainerExtension);
                var episodeTitle = Clean(episode.Title, show.Title, season.Value, number.Value);

                var parsed = show with
                {
                    Kind = MediaKind.Series,
                    Year = year,
                    Season = season,
                    Episode = number,
                    EpisodeTitle = episodeTitle
                };

                var entry = new M3uEntry
                {
                    DisplayName = Describe(show.Title, season.Value, number.Value, episodeTitle),
                    Url = credentials.StreamUrl(XtreamStreamKind.Series, episode.Id!, extension),
                    DurationSeconds = episode.Info?.DurationSeconds ?? 0,
                    Attributes = Attributes(category, series.Cover)
                };

                yield return new IngestCandidate(
                    entry,
                    new VodVerdict(true, MediaKind.Series, extension, "xtream series episode"),
                    parsed,
                    series.SeriesId);
            }
        }
    }

    /// <summary>Category id to name, for the group title an entry is filed under.</summary>
    public static Dictionary<string, string> NameById(IEnumerable<XtreamCategory> categories)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.Name)) continue;
            map[category.Id] = category.Name.Trim();
        }

        return map;
    }

    /// <summary>What gets stored as the raw title, and what a search result reads as.</summary>
    private static string Describe(string show, int season, int episode, string? episodeTitle)
    {
        var name = $"{show} S{season:00}E{episode:00}";
        return string.IsNullOrWhiteSpace(episodeTitle) ? name : $"{name} - {episodeTitle}";
    }

    /// <summary>
    /// Panels often set the episode title to the whole filename, or just repeat "S01E02" or
    /// the show's own name. None of those say anything, so they are dropped rather than
    /// carried into a release name.
    /// </summary>
    private static string? Clean(string? title, string show, int season, int episode)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length == 0) return null;

        var normalized = ReleaseTitleParser.Normalize(value);

        if (normalized.Length == 0) return null;
        if (normalized == ReleaseTitleParser.Normalize(show)) return null;
        if (normalized == ReleaseTitleParser.Normalize($"S{season:00}E{episode:00}")) return null;
        if (normalized == ReleaseTitleParser.Normalize($"Episode {episode}")) return null;

        return value;
    }

    private static Dictionary<string, string> Attributes(string? category, string? icon)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(category)) attributes["group-title"] = category.Trim();
        if (!string.IsNullOrWhiteSpace(icon)) attributes["tvg-logo"] = icon.Trim();

        return attributes;
    }

    private static string Extension(string? container) =>
        string.IsNullOrWhiteSpace(container) ? "mp4" : container.Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>The release date is "2019-01-01" on most panels and blank on the rest.</summary>
    private static int? YearFrom(string? releaseDate)
    {
        if (releaseDate is null || releaseDate.Length < 4) return null;

        return int.TryParse(releaseDate[..4], out var year) && year is >= 1900 and <= 2200 ? year : null;
    }
}
