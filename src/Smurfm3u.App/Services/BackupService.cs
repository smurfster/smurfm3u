using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Everything worth keeping, in one file.
/// <para>
/// Only what cannot be got back: the settings, the playlists, the speed limit windows and the
/// logins. The cache is left out on purpose - it is most of the database and a refresh
/// rebuilds all of it - and so is the download and search history, which describes this
/// install rather than configures it.
/// </para>
/// </summary>
public sealed record Backup
{
    public const string FormatName = "smurfm3u-backup";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public string? AppVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public ServiceSettings? Settings { get; init; }
    public List<PlaylistBackup> Playlists { get; init; } = [];
    public List<SpeedLimitBackup> SpeedLimits { get; init; } = [];
    public List<UserBackup> Users { get; init; } = [];
}

/// <summary>A playlist's configuration, without what it last fetched or how that went.</summary>
public sealed record PlaylistBackup
{
    public string Name { get; init; } = string.Empty;
    public M3uSourceKind Kind { get; init; }
    public string Location { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool IncludeSeries { get; init; } = true;
    public int SeriesConcurrency { get; init; } = 4;
    public bool Enabled { get; init; } = true;
    public string QualityTag { get; init; } = "WEB-DL";
    public string ResolutionTag { get; init; } = "1080p";
    public string ReleaseGroup { get; init; } = "Smurfm3u";
    public int MaxConcurrentDownloads { get; init; } = 1;
    public int StartDelaySeconds { get; init; }
    public string? RefreshCron { get; init; }
    public int SpeedLimitKibps { get; init; }
    public string? Headers { get; init; }
}

public sealed record SpeedLimitBackup
{
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int DaysOfWeek { get; init; } = 127;
    public TimeOnly StartTime { get; init; }
    public TimeOnly EndTime { get; init; }
    public int LimitKibps { get; init; }
}

/// <summary>A login, carried as its hash: the password itself is never known here.</summary>
public sealed record UserBackup
{
    public string Username { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
}

/// <summary>What a restore changed.</summary>
public sealed record BackupRestored(
    bool Settings,
    int PlaylistsAdded,
    int PlaylistsUpdated,
    int SpeedLimits,
    int UsersAdded,
    int UsersUpdated);

public class BackupService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsStore,
    ILogger<BackupService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Big enough for any real backup; small enough that a wrong file is refused rather than read.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public async Task<Backup> CreateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlists = await db.Sources
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new PlaylistBackup
            {
                Name = x.Name,
                Kind = x.Kind,
                Location = x.Location,
                Username = x.Username,
                Password = x.Password,
                IncludeSeries = x.IncludeSeries,
                SeriesConcurrency = x.SeriesConcurrency,
                Enabled = x.Enabled,
                QualityTag = x.QualityTag,
                ResolutionTag = x.ResolutionTag,
                ReleaseGroup = x.ReleaseGroup,
                MaxConcurrentDownloads = x.MaxConcurrentDownloads,
                StartDelaySeconds = x.StartDelaySeconds,
                RefreshCron = x.RefreshCron,
                SpeedLimitKibps = x.SpeedLimitKibps,
                Headers = x.Headers
            })
            .ToListAsync(ct);

        var speedLimits = await db.SpeedLimits
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new SpeedLimitBackup
            {
                Name = x.Name,
                Enabled = x.Enabled,
                DaysOfWeek = x.DaysOfWeek,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                LimitKibps = x.LimitKibps
            })
            .ToListAsync(ct);

        var users = await db.Users
            .AsNoTracking()
            .OrderBy(x => x.Username)
            .Select(x => new UserBackup { Username = x.Username, PasswordHash = x.PasswordHash })
            .ToListAsync(ct);

        return new Backup
        {
            AppVersion = AppVersion.Display,
            CreatedAt = DateTimeOffset.UtcNow,
            Settings = await settingsStore.GetAsync(ct),
            Playlists = playlists,
            SpeedLimits = speedLimits,
            Users = users
        };
    }

    /// <summary>Reads a backup file, refusing anything that is not one this version understands.</summary>
    public static async Task<Backup> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        Backup? backup;

        try
        {
            backup = await JsonSerializer.DeserializeAsync<Backup>(stream, Json, ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"That is not a readable backup file: {ex.Message}", ex);
        }

        if (backup is null || backup.Format != Backup.FormatName)
            throw new InvalidDataException("That is not a Smurfm3u backup file.");

        if (backup.Version > Backup.CurrentVersion)
            throw new InvalidDataException(
                $"That backup was made by a newer version of Smurfm3u ({backup.AppVersion ?? "unknown"}). Update first, then restore it.");

        return backup;
    }

    /// <summary>
    /// Puts a backup's configuration back.
    /// <para>
    /// Playlists and logins are matched by name and updated in place, so a playlist that
    /// already exists keeps its id - and with it its cache and its download history - and one
    /// that is not in the backup is left alone rather than deleted along with everything it
    /// holds. Speed limit windows have no identity worth matching on, so they are replaced
    /// as a set. The settings are replaced whole, API key included, so Prowlarr, Sonarr and
    /// Radarr carry on without being told anything.
    /// </para>
    /// </summary>
    public async Task<BackupRestored> RestoreAsync(Backup backup, CancellationToken ct = default)
    {
        int playlistsAdded = 0, playlistsUpdated = 0, usersAdded = 0, usersUpdated = 0;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var sources = await db.Sources.ToDictionaryAsync(x => x.Name, StringComparer.Ordinal, ct);

            foreach (var playlist in backup.Playlists.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
            {
                if (!sources.TryGetValue(playlist.Name, out var source))
                {
                    source = new M3uSource { Name = playlist.Name };
                    db.Sources.Add(source);
                    sources[playlist.Name] = source;
                    playlistsAdded++;
                }
                else
                {
                    playlistsUpdated++;
                }

                source.Kind = playlist.Kind;
                source.Location = playlist.Location;
                source.Username = playlist.Username;
                source.Password = playlist.Password;
                source.IncludeSeries = playlist.IncludeSeries;
                source.SeriesConcurrency = playlist.SeriesConcurrency;
                source.Enabled = playlist.Enabled;
                source.QualityTag = playlist.QualityTag;
                source.ResolutionTag = playlist.ResolutionTag;
                source.ReleaseGroup = playlist.ReleaseGroup;
                source.MaxConcurrentDownloads = playlist.MaxConcurrentDownloads;
                source.StartDelaySeconds = playlist.StartDelaySeconds;
                source.RefreshCron = playlist.RefreshCron;
                source.SpeedLimitKibps = playlist.SpeedLimitKibps;
                source.Headers = playlist.Headers;
            }

            await db.SpeedLimits.ExecuteDeleteAsync(ct);

            db.SpeedLimits.AddRange(backup.SpeedLimits.Select(x => new SpeedLimitWindow
            {
                Name = x.Name,
                Enabled = x.Enabled,
                DaysOfWeek = x.DaysOfWeek,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                LimitKibps = x.LimitKibps
            }));

            var users = await db.Users.ToDictionaryAsync(x => x.Username, StringComparer.Ordinal, ct);

            // A user missing from the backup is kept: dropping the login someone is restoring
            // with would sign them out of the page they are standing on.
            foreach (var user in backup.Users.Where(x =>
                         !string.IsNullOrWhiteSpace(x.Username) && !string.IsNullOrWhiteSpace(x.PasswordHash)))
            {
                if (users.TryGetValue(user.Username, out var existing))
                {
                    existing.PasswordHash = user.PasswordHash;
                    usersUpdated++;
                }
                else
                {
                    var added = new AppUser { Username = user.Username, PasswordHash = user.PasswordHash };
                    db.Users.Add(added);
                    users[user.Username] = added;
                    usersAdded++;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Through the settings service rather than the table, so its cache and everything
        // listening for a change - the proxy, the download limits - pick the new values up.
        if (backup.Settings is { } settings)
            await settingsStore.SaveAsync(settings, ct);

        var restored = new BackupRestored(
            backup.Settings is not null,
            playlistsAdded, playlistsUpdated,
            backup.SpeedLimits.Count,
            usersAdded, usersUpdated);

        logger.LogWarning("Restored a backup from {CreatedAt} (v{Version}): {Restored}",
            backup.CreatedAt, backup.AppVersion ?? "unknown", restored);

        return restored;
    }
}
