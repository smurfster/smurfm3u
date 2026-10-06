using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Integration.Tests;

/// <summary>Reading a playlist into the database, through the refresh the app runs.</summary>
[Collection(DatabaseCollection.Name)]
public class RefreshTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private string playlist = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        playlist = Path.Combine(Path.GetTempPath(), $"smurfm3u-{Guid.NewGuid():n}.m3u");
    }

    public Task DisposeAsync()
    {
        File.Delete(playlist);
        return Task.CompletedTask;
    }

    private async Task<int> LocalPlaylistAsync(string contents)
    {
        await File.WriteAllTextAsync(playlist, contents);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        var source = new M3uSource { Name = "local", Kind = M3uSourceKind.Local, Location = playlist, Enabled = true };
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        return source.Id;
    }

    [Fact]
    public async Task A_stream_listed_twice_is_stored_once()
    {
        // The same film filed under two groups, as providers do.
        var id = await LocalPlaylistAsync("""
            #EXTM3U
            #EXTINF:-1 group-title="Movies",Pacific Rim (2013)
            http://example.invalid/movie/u/p/1001.mkv
            #EXTINF:-1 group-title="Action",Pacific Rim (2013)
            http://example.invalid/movie/u/p/1001.mkv
            #EXTINF:-1 group-title="Movies",Dune (2021)
            http://example.invalid/movie/u/p/1002.mkv
            """);

        using var scope = fixture.Scope();
        await fixture.Resolve<M3uRefreshService>(scope).RefreshAsync(id);

        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        Assert.Equal(["Dune", "Pacific Rim"],
            await db.Items.Where(x => x.SourceId == id).OrderBy(x => x.Title).Select(x => x.Title).ToListAsync());
    }
}
