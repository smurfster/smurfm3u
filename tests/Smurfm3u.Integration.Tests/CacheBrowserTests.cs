using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// What the Cache page reads and what clearing actually removes.
/// <para>
/// The part worth pinning down is the fetch stamps. Clearing a panel's episodes without
/// clearing them would leave the entries gone and nothing that would ever think to fetch them
/// again, which is a silent hole rather than a visible failure.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CacheBrowserTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Catalogue catalogue = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        catalogue = new Catalogue(fixture.DbFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CacheBrowserService Browser(IServiceScope scope) => fixture.Resolve<CacheBrowserService>(scope);

    /// <summary>A panel's series index row, already read once, as a fetch would leave it.</summary>
    private async Task IndexAsync(int sourceId, string seriesId, string title, int? year)
    {
        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        db.Series.Add(new M3uSeries
        {
            SourceId = sourceId,
            SeriesId = seriesId,
            Name = title,
            Title = title,
            SearchTitle = Smurfm3u.Core.Parsing.ReleaseTitleParser.Normalize(title),
            Year = year,
            LastModified = 1_700_000_000,
            EpisodesFetchedFor = 1_700_000_000,
            EpisodesFetchedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Ties the entries of a show to the panel series they came from.</summary>
    private async Task LinkAsync(int sourceId, string seriesId, string searchTitle)
    {
        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        await db.Items
            .Where(x => x.SourceId == sourceId && x.SearchTitle == searchTitle)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeriesId, seriesId));
    }

    private async Task<(long? FetchedFor, DateTimeOffset? FetchedAt)> StampsAsync(string seriesId)
    {
        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        var row = await db.Series.AsNoTracking().SingleAsync(x => x.SeriesId == seriesId);
        return (row.EpisodesFetchedFor, row.EpisodesFetchedAt);
    }

    [Fact]
    public async Task A_summary_counts_films_and_episodes_apart()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)", "Dune (2021)");
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 5);

        using var scope = fixture.Scope();
        var summary = Assert.Single(await Browser(scope).SummariesAsync());

        Assert.Equal(2, summary.Films);
        Assert.Equal(5, summary.Episodes);
    }

    [Fact]
    public async Task Clearing_a_show_removes_its_episodes_and_marks_the_series_unread()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await IndexAsync(id, "99", "Top Gear", 2002);
        await LinkAsync(id, "99", "top gear");

        using var scope = fixture.Scope();
        var browser = Browser(scope);

        var show = Assert.Single(await browser.SeriesAsync(id, null, 0, 50));
        var cleared = await browser.ClearSeriesAsync([show]);

        Assert.Equal(4, cleared.Entries);
        Assert.Equal(1, cleared.SeriesReset);

        // The stamps are what a search reads to decide whether to fetch again. Without this
        // the episodes would be gone and nothing would ever go back for them.
        Assert.Equal((null, null), await StampsAsync("99"));

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.Items.CountAsync(x => x.SourceId == id));

        // The index row stays: it comes back on the next refresh regardless, and losing it
        // would make the show unknown rather than unread.
        Assert.Equal(1, await db.Series.CountAsync(x => x.SourceId == id));
    }

    [Fact]
    public async Task Clearing_a_season_leaves_the_other_seasons_alone()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 2, episodes: 6);

        using var scope = fixture.Scope();
        var browser = Browser(scope);

        var show = Assert.Single(await browser.SeriesAsync(id, null, 0, 50));
        var cleared = await browser.ClearSeasonsAsync([(show, 1)]);

        Assert.Equal(4, cleared.Entries);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        Assert.Equal(6, await db.Items.CountAsync(x => x.SourceId == id));
        Assert.All(await db.Items.Where(x => x.SourceId == id).ToListAsync(), x => Assert.Equal(2, x.Season));
    }

    [Fact]
    public async Task Clearing_one_episode_removes_only_it_and_still_marks_the_series_unread()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await IndexAsync(id, "99", "Top Gear", 2002);
        await LinkAsync(id, "99", "top gear");

        using var scope = fixture.Scope();
        var browser = Browser(scope);

        var show = Assert.Single(await browser.SeriesAsync(id, null, 0, 50));
        var episodes = await browser.EpisodesAsync(show, 1);

        var cleared = await browser.ClearEntriesAsync([episodes[2].Id]);

        Assert.Equal(1, cleared.Entries);
        Assert.Equal(1, cleared.SeriesReset);
        Assert.Equal((null, null), await StampsAsync("99"));

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        Assert.Equal(3, await db.Items.CountAsync(x => x.SourceId == id));
    }

    [Fact]
    public async Task Clearing_a_playlist_keeps_the_playlist_and_its_series_list()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await IndexAsync(id, "99", "Top Gear", 2002);

        using var scope = fixture.Scope();
        var cleared = await Browser(scope).ClearSourceAsync(id);

        Assert.Equal(5, cleared.Entries);
        Assert.Equal(1, cleared.SeriesReset);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.Items.CountAsync());
        Assert.Equal(1, await db.Sources.CountAsync());
        Assert.Equal(1, await db.Series.CountAsync());
    }

    [Fact]
    public async Task Clearing_a_show_from_a_playlist_with_no_series_index_still_works()
    {
        // An m3u has no series ids at all, so a show is identified by its title and year.
        var id = await catalogue.PlaylistAsync("file", M3uSourceKind.Local);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);

        using var scope = fixture.Scope();
        var browser = Browser(scope);

        var show = Assert.Single(await browser.SeriesAsync(id, null, 0, 50));
        var cleared = await browser.ClearSeriesAsync([show]);

        Assert.Equal(4, cleared.Entries);
        Assert.Equal(0, cleared.SeriesReset);
    }
}
