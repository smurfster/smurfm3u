using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// A backup survives the trip through a file, and restoring one puts the configuration back
/// without taking anything else with it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class BackupTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private Catalogue catalogue = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();

        // Not in the fixture's reset, which leaves configuration alone for every other test.
        await using var db = await fixture.DbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "SpeedLimits", "Users" RESTART IDENTITY;""");

        catalogue = new Catalogue(fixture.DbFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Written out and read back in, exactly as the download and the upload do it.</summary>
    private static async Task<Backup> ThroughAFileAsync(Backup backup)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, BackupService.Json);
        return await BackupService.ReadAsync(new MemoryStream(bytes));
    }

    private async Task SeedConfigurationAsync()
    {
        await using var db = await fixture.DbFactory.CreateDbContextAsync();

        db.SpeedLimits.Add(new SpeedLimitWindow
        {
            Name = "Evenings", DaysOfWeek = 62, StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(23, 30),
            LimitKibps = 2048
        });
        db.Users.Add(new AppUser { Username = "admin", PasswordHash = "hash-one" });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_restore_puts_back_what_was_changed_since()
    {
        var id = await catalogue.PlaylistAsync("panel", M3uSourceKind.Xtream);
        await catalogue.EntriesAsync(id, "Pacific Rim (2013)");
        await SeedConfigurationAsync();

        using var scope = fixture.Scope();
        var backups = fixture.Resolve<BackupService>(scope);

        var backup = await ThroughAFileAsync(await backups.CreateAsync());

        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            await db.Sources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Location, "https://elsewhere.invalid"));
            await db.SpeedLimits.ExecuteDeleteAsync();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(x => x.PasswordHash, "hash-two"));
        }

        var restored = await backups.RestoreAsync(backup);

        Assert.Equal(0, restored.PlaylistsAdded);
        Assert.Equal(1, restored.PlaylistsUpdated);
        Assert.Equal(1, restored.UsersUpdated);

        await using var check = await fixture.DbFactory.CreateDbContextAsync();

        var source = await check.Sources.SingleAsync();
        Assert.Equal(id, source.Id);
        Assert.Equal("https://example.invalid/panel.m3u", source.Location);

        // Updated in place, so the cache it already held is still its own.
        Assert.Equal(1, await check.Items.CountAsync(x => x.SourceId == id));

        var window = await check.SpeedLimits.SingleAsync();
        Assert.Equal(("Evenings", 62, new TimeOnly(18, 0), new TimeOnly(23, 30), 2048),
            (window.Name, window.DaysOfWeek, window.StartTime, window.EndTime, window.LimitKibps));

        Assert.Equal("hash-one", (await check.Users.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task A_restore_adds_what_is_missing_and_leaves_the_rest_alone()
    {
        await catalogue.PlaylistAsync("kept");
        await SeedConfigurationAsync();

        using var scope = fixture.Scope();
        var backups = fixture.Resolve<BackupService>(scope);

        var backup = await ThroughAFileAsync(await backups.CreateAsync());

        // A fresh install: no playlists, and a different login from the one in the backup.
        await fixture.ResetAsync();
        await using (var db = await fixture.DbFactory.CreateDbContextAsync())
        {
            await db.Users.ExecuteDeleteAsync();
            db.Users.Add(new AppUser { Username = "someone", PasswordHash = "theirs" });
            await catalogue.PlaylistAsync("local only");
            await db.SaveChangesAsync();
        }

        var restored = await backups.RestoreAsync(backup);

        Assert.Equal(1, restored.PlaylistsAdded);
        Assert.Equal(1, restored.UsersAdded);

        await using var check = await fixture.DbFactory.CreateDbContextAsync();

        Assert.Equal(["kept", "local only"], await check.Sources.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync());
        Assert.Equal(["admin", "someone"], await check.Users.OrderBy(x => x.Username).Select(x => x.Username).ToListAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_refused()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{ "format": "something-else", "version": 1 }"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(stream));
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_is_refused()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{ "format": "smurfm3u-backup", "version": 99 }"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(stream));
    }
}
