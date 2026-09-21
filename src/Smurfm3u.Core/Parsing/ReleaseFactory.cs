using Smurfm3u.Core.Entities;

namespace Smurfm3u.Core.Parsing;

/// <summary>
/// Single place that turns a stored playlist entry into a release name, so the name a client
/// sees in search results is exactly the name the finished download is written under.
/// </summary>
public static class ReleaseFactory
{
    public static ParsedTitle ToParsedTitle(M3uItem item) => new()
    {
        Kind = item.Kind,
        Title = item.Title,
        Year = item.Year,
        Season = item.Season,
        Episode = item.Episode,
        EpisodeTitle = item.EpisodeTitle,
        SearchTitle = item.SearchTitle
    };

    public static string BuildName(M3uItem item, M3uSource? source) =>
        ReleaseNameBuilder.Build(
            ToParsedTitle(item),
            source?.QualityTag ?? "WEB-DL",
            source?.ResolutionTag,
            source?.ReleaseGroup);
}
