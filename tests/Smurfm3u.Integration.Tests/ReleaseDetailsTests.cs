using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// What the page behind a client's info link says a release holds.
/// <para>
/// Worked out from the catalogue at the moment it is asked, which is the same answer a grab
/// arriving then would get - so none of it can be tested without rows.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReleaseDetailsTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Catalogue catalogue = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        catalogue = new Catalogue(fixture.DbFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ReleaseDetails?> DescribeAsync(string downloadId)
    {
        using var scope = fixture.Scope();
        return await fixture.Resolve<ReleaseDetailsService>(scope).DescribeAsync(downloadId);
    }

    private async Task<string> PackIdAsync(int sourceId, string query)
    {
        using var scope = fixture.Scope();
        var search = fixture.Resolve<SearchService>(scope);

        var results = await search.SearchAsync(
            new SearchRequest(SearchKind.Search, query, null, null, [], 0, 100));

        return results.Hits.Single(x => x.FileCount > 1).DownloadId;
    }

    [Fact]
    public async Task A_season_pack_lists_every_episode_it_would_download()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Sherlock & Daughter", 2025, season: 1, episodes: 8);

        var details = await DescribeAsync(await PackIdAsync(id, "sherlock & daughter s01"));

        Assert.NotNull(details);
        Assert.True(details.IsSeasonPack);
        Assert.Equal(8, details.Files.Count);
        Assert.Equal(1, details.Season);
        Assert.Equal("panel", details.PlaylistName);

        // In episode order, because that is the order they are transferred in.
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], [.. details.Files.Select(x => x.Episode)]);

        // The total is what the parts add up to, not a separate guess.
        Assert.Equal(details.Files.Sum(x => x.SizeBytes), details.TotalSizeBytes);
    }

    [Fact]
    public async Task A_single_entry_describes_itself_as_one_file()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");

        long itemId;
        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
            itemId = await db.Items.Select(x => x.Id).SingleAsync();

        var details = await DescribeAsync(itemId.ToString());

        Assert.NotNull(details);
        Assert.False(details.IsSeasonPack);
        Assert.Equal(MediaKind.Movie, details.Kind);
        Assert.Equal("Pacific Rim", details.Title);
        Assert.Equal(2013, details.Year);
        Assert.Single(details.Files);
    }

    [Fact]
    public async Task A_pack_shows_the_season_as_it_stands_rather_than_as_it_was_searched()
    {
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Late Arrival", 2025, season: 1, episodes: 4);

        var packId = await PackIdAsync(id, "late arrival s01");

        // The panel gains one between the search and the click.
        await catalogue.EntriesAsync(id, "Late Arrival (2025) S01E05 - Episode 5");

        var details = await DescribeAsync(packId);

        Assert.NotNull(details);
        Assert.Equal(5, details.Files.Count);
    }

    [Fact]
    public async Task A_withdrawn_release_describes_nothing()
    {
        // Honest rather than a page insisting on something the playlist no longer offers.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.SeasonAsync(id, "Withdrawn", 2025, season: 1, episodes: 4);

        var packId = await PackIdAsync(id, "withdrawn s01");

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            await db.Items.Where(x => x.SourceId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        }

        Assert.Null(await DescribeAsync(packId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-id")]
    [InlineData("999999999")]
    [InlineData("pack.1.1.0.bm90aGluZw")]
    public async Task An_id_that_names_nothing_describes_nothing(string downloadId)
    {
        Assert.Null(await DescribeAsync(downloadId));
    }

    [Fact]
    public async Task A_size_the_provider_declared_is_told_apart_from_one_worked_out()
    {
        // The page says which is which, because an estimate is what the *arr apps judge a
        // release on and a total built from guesses should not read as a measurement.
        var id = await catalogue.PlaylistAsync("panel");
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");

        long itemId;
        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            var item = await db.Items.SingleAsync();
            itemId = item.Id;
            item.SizeBytes = 4_000_000_000;
            await db.SaveChangesAsync();
        }

        var declared = await DescribeAsync(itemId.ToString());
        Assert.True(declared!.Files[0].SizeIsKnown);
        Assert.Equal(4_000_000_000, declared.TotalSizeBytes);

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            await db.Items.Where(x => x.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SizeBytes, 0L));
        }

        var guessed = await DescribeAsync(itemId.ToString());
        Assert.False(guessed!.Files[0].SizeIsKnown);
    }
}
