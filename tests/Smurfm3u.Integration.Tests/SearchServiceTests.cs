using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// How a query is read, against a real database.
/// <para>
/// These cases cannot be unit tested: which reading of a query wins depends on what the
/// catalogue holds, which is the whole point of the rule being tested.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SearchServiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Catalogue catalogue = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        catalogue = new Catalogue(fixture.DbFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<SearchResults> SearchAsync(
        string? query, int? season = null, int? episode = null,
        SearchKind kind = SearchKind.Search, IReadOnlyCollection<int>? sources = null)
    {
        using var scope = fixture.Scope();
        var search = fixture.Resolve<SearchService>(scope);

        return await search.SearchAsync(new SearchRequest(kind, query, season, episode, [], 0, 100, sources));
    }

    // ---- Which reading of a query wins ----

    [Fact]
    public async Task A_title_that_really_contains_the_words_beats_reading_them_as_a_season()
    {
        // The regression this project exists for. "Open Season 2" is a film; reading the
        // season out first turned the query into "open" + season 2, which matches season 2 of
        // every show with "open" in its name and not the film the words actually spell.
        var id = await catalogue.PlaylistAsync("panel");

        await catalogue.EntriesAsync(id, "Open Season 2 (2008)");
        await catalogue.SeasonAsync(id, "Open All Hours", 1976, season: 2, episodes: 6);

        var results = await SearchAsync("open season 2");

        Assert.False(results.Relaxed);
        Assert.All(results.Hits, h => Assert.Equal(MediaKind.Movie, h.Item.Kind));
        Assert.Contains(results.Hits, h => h.Item.Title == "Open Season 2");
    }

    [Fact]
    public async Task A_season_written_into_a_query_is_read_when_the_words_match_nothing()
    {
        // No stored title contains "s01", so the literal reading can never match and the
        // season reading gets its turn. Before this, the query sank entirely.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Sherlock & Daughter", 2025, season: 1, episodes: 8);

        var results = await SearchAsync("sherlock & daughter s01");

        Assert.False(results.Relaxed);
        Assert.Equal(8, results.Hits.Count(h => h.Item.Episode is not null));
    }

    [Theory]
    [InlineData("sherlock & daughter season 1")]
    [InlineData("sherlock & daughter s01")]
    public async Task A_season_query_is_offered_the_season_as_one_release(string query)
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Sherlock & Daughter", 2025, season: 1, episodes: 8);

        var results = await SearchAsync(query);

        var pack = Assert.Single(results.Hits, h => h.FileCount > 1);

        Assert.Equal(8, pack.FileCount);
        Assert.Contains("S01", pack.ReleaseName);
        Assert.DoesNotContain("S01E", pack.ReleaseName);
    }

    [Fact]
    public async Task An_episode_written_into_a_query_still_narrows_it_to_that_episode()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Lanterns", 2026, season: 1, episodes: 6);

        var results = await SearchAsync("lanterns s01e03");

        var hit = Assert.Single(results.Hits);

        Assert.Equal(1, hit.Item.Season);
        Assert.Equal(3, hit.Item.Episode);
    }

    [Fact]
    public async Task A_season_a_client_sent_explicitly_beats_anything_in_the_words()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 1, episodes: 4);
        await catalogue.SeasonAsync(id, "Top Gear", 2002, season: 2, episodes: 4);

        var results = await SearchAsync("top gear", season: 2, kind: SearchKind.TvSearch);

        Assert.All(results.Hits, h => Assert.Equal(2, h.Item.Season));
    }

    // ---- How Sonarr spells things ----

    [Theory]
    [InlineData("Sherlock and Daughter")]
    [InlineData("Sherlock & Daughter")]
    public async Task A_show_with_an_ampersand_is_found_whichever_way_it_is_spelled(string query)
    {
        // Sonarr sends "and" where the provider wrote "&". Dropping the ampersand made "and"
        // a word the stored title could never contain, so a season search fell through to
        // near matches - with no season pack among them, because packs are only built on a
        // match that stood up. Two percent of a real catalogue was unfindable this way.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Sherlock & Daughter", 2025, season: 1, episodes: 8);

        var results = await SearchAsync(query, season: 1, kind: SearchKind.TvSearch);

        Assert.False(results.Relaxed);

        var pack = Assert.Single(results.Hits, h => h.FileCount > 1);
        Assert.Equal(8, pack.FileCount);
    }

    [Fact]
    public async Task An_ampersand_query_still_finds_a_show_that_spells_it_out()
    {
        // The same rule in reverse: a provider writing "and" and a client sending "&".
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Law and Order", 1990, season: 1, episodes: 5);

        var results = await SearchAsync("law & order", season: 1, kind: SearchKind.TvSearch);

        Assert.False(results.Relaxed);
        Assert.Single(results.Hits, h => h.FileCount == 5);
    }

    // ---- Falling back ----

    [Fact]
    public async Task The_closest_entries_come_back_only_when_no_reading_matched()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Camp CrunchLabs (2023)");

        var results = await SearchAsync("mark robers crunchlabs");

        Assert.True(results.Relaxed);
        Assert.Single(results.Hits);
    }

    [Fact]
    public async Task A_query_that_matches_in_full_is_never_labelled_a_near_match()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Camp CrunchLabs (2023)", "Pacific Rim (2013)");

        var results = await SearchAsync("camp crunchlabs");

        Assert.False(results.Relaxed);
        Assert.Single(results.Hits);
    }

    // ---- Which playlists answer ----

    [Fact]
    public async Task Naming_no_playlist_answers_from_all_of_them()
    {
        var one = await catalogue.PlaylistAsync("one");
        var two = await catalogue.PlaylistAsync("two");

        await catalogue.EntriesAsync(one, "Pacific Rim (2013)");
        await catalogue.EntriesAsync(two, "Pacific Rim (2013)");

        var results = await SearchAsync("pacific rim");

        Assert.Equal(2, results.Hits.Count);
    }

    [Fact]
    public async Task Naming_a_playlist_answers_only_from_that_one()
    {
        var one = await catalogue.PlaylistAsync("one");
        var two = await catalogue.PlaylistAsync("two");

        await catalogue.EntriesAsync(one, "Pacific Rim (2013)");
        await catalogue.EntriesAsync(two, "Pacific Rim (2013)");

        var results = await SearchAsync("pacific rim", sources: [two]);

        var hit = Assert.Single(results.Hits);
        Assert.Equal(two, hit.Item.SourceId);
    }

    [Fact]
    public async Task Naming_a_playlist_that_holds_nothing_answers_nothing()
    {
        var one = await catalogue.PlaylistAsync("one");
        var two = await catalogue.PlaylistAsync("two");

        await catalogue.EntriesAsync(one, "Pacific Rim (2013)");

        var results = await SearchAsync("pacific rim", sources: [two]);

        Assert.Empty(results.Hits);
    }

    [Fact]
    public async Task A_disabled_playlist_answers_nothing_even_when_it_is_named()
    {
        // Choosing it explicitly does not make it answer: it is switched off.
        var id = await catalogue.PlaylistAsync("off", enabled: false);
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");

        Assert.Empty((await SearchAsync("pacific rim")).Hits);
        Assert.Empty((await SearchAsync("pacific rim", sources: [id])).Hits);
    }

    // ---- What a search will not do ----

    [Fact]
    public async Task A_film_search_never_answers_with_an_episode()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");
        await catalogue.SeasonAsync(id, "Pacific Rim Uprising", 2018, season: 1, episodes: 3);

        var results = await SearchAsync("pacific rim", kind: SearchKind.MovieSearch);

        Assert.All(results.Hits, h => Assert.Equal(MediaKind.Movie, h.Item.Kind));
    }

    [Fact]
    public async Task A_retired_entry_is_never_offered()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            var item = db.Items.Single();
            item.IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Empty((await SearchAsync("pacific rim")).Hits);
    }
}
