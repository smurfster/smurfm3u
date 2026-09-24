using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// Builds the rows a test needs, through the same parser the ingest uses.
/// <para>
/// Going through <see cref="ReleaseTitleParser"/> rather than setting the columns by hand is
/// the point: a test that hand-writes a title and a season proves the search works on data
/// nothing produces. These rows are shaped exactly as a refresh would leave them.
/// </para>
/// </summary>
public sealed class Catalogue(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Adds a playlist and returns its id.</summary>
    public async Task<int> PlaylistAsync(string name, M3uSourceKind kind = M3uSourceKind.Remote, bool enabled = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var source = new M3uSource
        {
            Name = name,
            Kind = kind,
            Location = $"https://example.invalid/{name}.m3u",
            Enabled = enabled,
            QualityTag = "WEB-DL",
            ResolutionTag = "1080p",
            ReleaseGroup = "Smurfm3u"
        };

        db.Sources.Add(source);
        await db.SaveChangesAsync();

        return source.Id;
    }

    /// <summary>
    /// Adds entries under a playlist, named as a provider would name them and parsed as the
    /// refresh would parse them.
    /// </summary>
    public async Task EntriesAsync(int sourceId, params string[] providerNames)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var stamp = DateTimeOffset.UtcNow;

        foreach (var name in providerNames)
        {
            var parsed = ReleaseTitleParser.Parse(name);

            db.Items.Add(new M3uItem
            {
                SourceId = sourceId,
                ItemKey = Guid.NewGuid().ToString("n"),
                RawTitle = name,
                StreamUrl = $"https://example.invalid/{Guid.NewGuid():n}.mkv",
                Kind = parsed.Kind,
                Title = parsed.Title,
                SearchTitle = parsed.SearchTitle,
                Year = parsed.Year,
                Season = parsed.Season,
                Episode = parsed.Episode,
                EpisodeTitle = parsed.EpisodeTitle,
                Extension = "mkv",
                DurationSeconds = 2400,
                FirstSeenAt = stamp,
                LastSeenAt = stamp,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Every episode of a season, named the way a provider lists them.</summary>
    public Task SeasonAsync(int sourceId, string show, int year, int season, int episodes) =>
        EntriesAsync(
            sourceId,
            [.. Enumerable.Range(1, episodes).Select(e => $"{show} ({year}) S{season:D2}E{e:D2} - Episode {e}")]);
}
