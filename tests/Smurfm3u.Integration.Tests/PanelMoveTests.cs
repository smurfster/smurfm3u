using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Xtream;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// Moving a panel's stored stream addresses when its address or login changes. The rewrite
/// and the rehash happen in SQL, so whether the key it computes is the key the refresh would
/// compute is only answerable against Postgres.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PanelMoveTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly XtreamCredentials Old = new("https://old.example", "user", "p@ss/word");
    private static readonly XtreamCredentials New = new("https://new.example", "user", "p@ss/word");

    private int sourceId;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        sourceId = await new Catalogue(fixture.DbFactory).PlaylistAsync("panel", M3uSourceKind.Xtream);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> EntryAsync(string url, bool active = true)
    {
        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        var item = new M3uItem
        {
            SourceId = sourceId,
            ItemKey = M3uRefreshService.ItemKeyFor(url),
            StreamUrl = url,
            RawTitle = url,
            Title = "Show",
            SearchTitle = "show",
            Kind = MediaKind.Series,
            IsActive = active
        };

        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private async Task<PanelMoveResult> MoveAsync(XtreamCredentials from, XtreamCredentials to)
    {
        using var scope = fixture.Scope();
        return await fixture.Resolve<PanelMoveService>(scope).MoveAsync(sourceId, from, to);
    }

    [Fact]
    public async Task Entries_move_to_the_new_address_with_the_key_a_refresh_would_give_them()
    {
        var film = await EntryAsync(Old.StreamUrl(XtreamStreamKind.Movie, "11", "mkv"));
        var episode = await EntryAsync(Old.StreamUrl(XtreamStreamKind.Series, "22", "mp4"));

        var result = await MoveAsync(Old, New);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        var items = await db.Items.AsNoTracking().ToDictionaryAsync(x => x.Id);

        Assert.Equal(2, result.Entries);
        Assert.Equal(New.StreamUrl(XtreamStreamKind.Movie, "11", "mkv"), items[film].StreamUrl);
        Assert.Equal(New.StreamUrl(XtreamStreamKind.Series, "22", "mp4"), items[episode].StreamUrl);

        foreach (var item in items.Values)
            Assert.Equal(M3uRefreshService.ItemKeyFor(item.StreamUrl), item.ItemKey);
    }

    [Fact]
    public async Task A_new_login_moves_them_as_a_new_server_does()
    {
        var relogged = New with { Password = "fresh" };
        var id = await EntryAsync(New.StreamUrl(XtreamStreamKind.Series, "22", "mp4"));

        await MoveAsync(New, relogged);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        Assert.Equal(relogged.StreamUrl(XtreamStreamKind.Series, "22", "mp4"),
            (await db.Items.SingleAsync(x => x.Id == id)).StreamUrl);
    }

    [Fact]
    public async Task An_entry_already_fetched_from_the_new_address_is_not_duplicated()
    {
        var stale = await EntryAsync(Old.StreamUrl(XtreamStreamKind.Series, "22", "mp4"), active: false);
        await EntryAsync(New.StreamUrl(XtreamStreamKind.Series, "22", "mp4"));

        var result = await MoveAsync(Old, New);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        Assert.Equal(0, result.Entries);
        Assert.Equal(1, result.Superseded);
        Assert.StartsWith(Old.BaseUrl, (await db.Items.SingleAsync(x => x.Id == stale)).StreamUrl);
    }

    [Fact]
    public async Task Waiting_downloads_move_and_finished_ones_are_left()
    {
        var url = Old.StreamUrl(XtreamStreamKind.Movie, "11", "mkv");

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            db.Downloads.Add(new DownloadItem
            {
                NzoId = "queued", Name = "Queued", SourceId = sourceId, Status = DownloadStatus.Queued,
                Files = [new DownloadFile { Name = "Queued", StreamUrl = url }]
            });
            db.Downloads.Add(new DownloadItem
            {
                NzoId = "done", Name = "Done", SourceId = sourceId, Status = DownloadStatus.Completed,
                Files = [new DownloadFile { Name = "Done", StreamUrl = url }]
            });
            await db.SaveChangesAsync();
        }

        var result = await MoveAsync(Old, New);

        await using var check = await fixture.DbFactory.CreateDbContextAsync();
        var files = await check.Downloads.AsNoTracking().Include(x => x.Files).ToDictionaryAsync(x => x.NzoId);

        Assert.Equal(1, result.Downloads);
        Assert.Equal(New.StreamUrl(XtreamStreamKind.Movie, "11", "mkv"), files["queued"].Files[0].StreamUrl);
        Assert.Equal(url, files["done"].Files[0].StreamUrl);
    }

    [Fact]
    public async Task Nothing_changes_when_the_address_does_not()
    {
        await EntryAsync(Old.StreamUrl(XtreamStreamKind.Movie, "11", "mkv"));

        Assert.Equal(new PanelMoveResult(0, 0, 0), await MoveAsync(Old, Old));
    }
}
