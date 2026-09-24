using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Options;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>One season offered as a single release, and the episodes it is made of.</summary>
public sealed record SeasonPack(SeasonPackId Id, string Name, IReadOnlyList<M3uItem> Episodes, long SizeBytes);

/// <summary>
/// Builds the season-pack releases a season search is answered with, and resolves one back
/// into its episodes when a client grabs it.
/// <para>
/// Nothing about a pack is stored. It is a view over the episodes we already hold, worked out
/// again at grab time - so a grab that arrives an hour after the search picks up anything that
/// turned up in between, and a season that has since been withdrawn resolves to nothing rather
/// than to a list of dead links.
/// </para>
/// </summary>
public class SeasonPackService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// How many episode rows one season search reads to group. A season is tens of episodes,
    /// but a vague query can match many shows at once, and this is work done on every search.
    /// </summary>
    private const int ScanLimit = 600;

    /// <summary>
    /// The largest season worth offering whole. Past this it is almost always a daily show
    /// whose air year has been read as a season number - hundreds of episodes and several
    /// hundred gigabytes under one name nobody meant to ask for. Those seasons are offered
    /// episode by episode instead, which is the only honest thing to do with them.
    /// </summary>
    private const int MostPerPack = 200;

    /// <summary>Groups the episodes a season search matched into one release per show.</summary>
    public async Task<IReadOnlyList<SeasonPack>> BuildAsync(
        IQueryable<M3uItem> matched, int season, ServiceSettings settings, CancellationToken ct = default)
    {
        var episodes = await matched
            .OrderBy(x => x.SearchTitle)
            .ThenBy(x => x.Episode)
            .ThenBy(x => x.Id)
            .Take(ScanLimit)
            .ToListAsync(ct);

        var floor = Math.Max(2, settings.MinSeasonPackEpisodes);
        var packs = new List<SeasonPack>();

        // A show is its title, its year and the playlist it came from: same title, different
        // year is a different show, and the same show on two playlists is two packs because a
        // grab can only stream from one of them.
        foreach (var group in episodes.GroupBy(x => (x.SourceId, x.SearchTitle, x.Year)))
        {
            var members = Distinct(group).ToList();
            if (members.Count < floor || members.Count > MostPerPack) continue;

            packs.Add(Describe(
                new SeasonPackId(group.Key.SourceId, season, group.Key.Year, group.Key.SearchTitle),
                members,
                settings));
        }

        // Biggest first: the fullest season is the one a client most likely wants.
        return packs.OrderByDescending(x => x.Episodes.Count).ThenBy(x => x.Name).ToList();
    }

    /// <summary>
    /// Reads a pack id back into the episodes it stands for. Null when the season no longer
    /// has anything in it, which is the honest answer to a grab of a withdrawn season.
    /// </summary>
    public async Task<SeasonPack?> ResolveAsync(
        SeasonPackId id, ServiceSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .Where(x => x.IsActive
                        && x.SourceId == id.SourceId
                        && x.Kind == MediaKind.Series
                        && x.SearchTitle == id.SearchTitle
                        && x.Season == id.Season);

        // Written as two branches rather than one comparison: a parameter holding null
        // compares as "= NULL", which is never true, and would resolve every pack to nothing.
        query = id.Year is { } year
            ? query.Where(x => x.Year == year)
            : query.Where(x => x.Year == null);

        var episodes = await query
            .OrderBy(x => x.Episode)
            .ThenBy(x => x.Id)
            .Take(ScanLimit)
            .ToListAsync(ct);

        var members = Distinct(episodes).ToList();

        // Nothing left, or grown past what is offered whole: either way this is not a release
        // any more, and saying so beats queueing something we would never have offered.
        return members.Count == 0 || members.Count > MostPerPack
            ? null
            : Describe(id, members, settings);
    }

    private static SeasonPack Describe(SeasonPackId id, List<M3uItem> members, ServiceSettings settings)
    {
        var first = members[0];

        var name = ReleaseNameBuilder.Build(
            new ParsedTitle
            {
                Kind = MediaKind.Series,
                Title = first.Title,
                Year = first.Year,
                Season = id.Season,
                // The whole point: no episode number is what makes Sonarr read this as a season.
                Episode = null
            },
            first.Source?.QualityTag ?? "WEB-DL",
            first.Source?.ResolutionTag,
            first.Source?.ReleaseGroup);

        var size = members.Sum(x => SizeEstimator.Estimate(x, settings));

        return new SeasonPack(id, name, members, size);
    }

    /// <summary>
    /// One entry per episode number. A playlist that carries the same episode twice - a repeat
    /// under a second group, most often - would otherwise have us download it twice into a
    /// folder where the second copy overwrites the first.
    /// </summary>
    private static IEnumerable<M3uItem> Distinct(IEnumerable<M3uItem> episodes) =>
        episodes
            .Where(x => x.Episode is not null)
            .GroupBy(x => x.Episode!.Value)
            .Select(g => g.First())
            .OrderBy(x => x.Episode);

    /// <summary>
    /// The stand-in entry a pack is presented as. Search results are shaped around an entry,
    /// and a pack has no single one - so it borrows the season's: same show, same playlist,
    /// no episode number.
    /// </summary>
    public static M3uItem Representative(SeasonPack pack)
    {
        var first = pack.Episodes[0];

        return new M3uItem
        {
            SourceId = first.SourceId,
            Source = first.Source,
            Kind = MediaKind.Series,
            Title = first.Title,
            SearchTitle = first.SearchTitle,
            Year = first.Year,
            Season = pack.Id.Season,
            Episode = null,
            Extension = first.Extension,
            RawTitle = $"{first.Title} - season {pack.Id.Season}, {pack.Episodes.Count} episodes",
            // Newest member: a season is "new" when its latest episode is.
            FirstSeenAt = pack.Episodes.Max(x => x.FirstSeenAt),
            LastSeenAt = pack.Episodes.Max(x => x.LastSeenAt),
            IsActive = true
        };
    }
}
