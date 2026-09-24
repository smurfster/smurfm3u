using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// What a season is offered as, and what it resolves to when the grab arrives.
/// <para>
/// A pack is not stored: it is a view over the episodes held at the moment it is asked for.
/// Every rule here is about what the database contains, so none of it can be tested without one.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SeasonPackTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Catalogue catalogue = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        catalogue = new Catalogue(fixture.DbFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<IReadOnlyList<SearchHit>> PacksAsync(string query)
    {
        using var scope = fixture.Scope();
        var search = fixture.Resolve<SearchService>(scope);

        var results = await search.SearchAsync(new SearchRequest(SearchKind.Search, query, null, null, [], 0, 200));

        return [.. results.Hits.Where(h => h.FileCount > 1)];
    }

    [Fact]
    public async Task Two_shows_with_the_same_name_and_different_years_are_two_packs()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Top Gear America", 2017, season: 1, episodes: 4);
        await catalogue.SeasonAsync(id, "Top Gear America", 2021, season: 1, episodes: 5);

        var packs = await PacksAsync("top gear america s01");

        Assert.Equal(2, packs.Count);
        Assert.Equal([5, 4], [.. packs.Select(p => p.FileCount)]);
    }

    [Fact]
    public async Task A_season_of_one_episode_is_not_offered_as_a_pack()
    {
        // A "pack" of one is an episode under a name that hides which one it is.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Short Run", 2024, season: 1, episodes: 1);

        Assert.Empty(await PacksAsync("short run s01"));
    }

    [Fact]
    public async Task A_season_of_more_than_two_hundred_episodes_is_not_offered_as_a_pack()
    {
        // Almost always a daily show whose air year has been read as a season number. Several
        // hundred gigabytes under one name nobody meant to ask for is worse than no pack.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Nightly News", 2025, season: 2025, episodes: 201);

        Assert.Empty(await PacksAsync("nightly news s2025"));
    }

    [Fact]
    public async Task Two_hundred_exactly_is_still_offered()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Long Run", 2025, season: 1, episodes: 200);

        var pack = Assert.Single(await PacksAsync("long run s01"));
        Assert.Equal(200, pack.FileCount);
    }

    [Fact]
    public async Task A_pack_covers_one_season_and_leaves_the_others_alone()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 2, episodes: 7);

        var pack = Assert.Single(await PacksAsync("top gear s02"));

        Assert.Equal(7, pack.FileCount);
        Assert.Contains("S02", pack.ReleaseName);
    }

    // ---- What a grab resolves to, which is worked out again at grab time ----

    private async Task<SeasonPack?> ResolveAsync(string packId)
    {
        using var scope = fixture.Scope();
        var packs = fixture.Resolve<SeasonPackService>(scope);
        var settings = await fixture.Resolve<SettingsService>(scope).GetAsync();

        Assert.True(SeasonPackId.TryParse(packId, out var parsed));

        return await packs.ResolveAsync(parsed, settings);
    }

    [Fact]
    public async Task A_grab_picks_up_an_episode_that_turned_up_after_the_search()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Late Arrival", 2025, season: 1, episodes: 4);

        var offered = Assert.Single(await PacksAsync("late arrival s01"));
        Assert.Equal(4, offered.FileCount);

        // The panel gains one between the search and the grab.
        await catalogue.EntriesAsync(id, "Late Arrival (2025) S01E05 - Episode 5");

        var resolved = await ResolveAsync(offered.DownloadId);

        Assert.NotNull(resolved);
        Assert.Equal(5, resolved.Episodes.Count);
    }

    [Fact]
    public async Task A_grab_of_a_season_that_has_gone_resolves_to_nothing()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Withdrawn", 2025, season: 1, episodes: 4);

        var offered = Assert.Single(await PacksAsync("withdrawn s01"));

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            await db.Items.Where(x => x.SourceId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        }

        Assert.Null(await ResolveAsync(offered.DownloadId));
    }

    [Fact]
    public async Task A_repeated_episode_is_only_counted_once()
    {
        // A provider listing the same episode twice would otherwise have the grab download it
        // twice into a folder where the second copy overwrites the first.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Repeats", 2025, season: 1, episodes: 3);
        await catalogue.EntriesAsync(id, "Repeats (2025) S01E02 - Episode 2");

        var pack = Assert.Single(await PacksAsync("repeats s01"));

        Assert.Equal(3, pack.FileCount);
    }
}
