using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Brings the database up to date and seeds the first admin account and API key.
/// Runs once at startup, before the app starts serving.
/// </summary>
public class DatabaseInitializer(
    IDbContextFactory<AppDbContext> dbFactory,
    UserService users,
    SettingsService settingsService,
    IOptions<BootstrapOptions> bootstrap,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.Database.MigrateAsync(ct);

        var options = bootstrap.Value;
        var settings = await settingsService.GetAsync(ct);

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? GenerateApiKey() : options.ApiKey.Trim();
            await settingsService.UpdateAsync(s => s.ApiKey = apiKey, ct);

            logger.LogInformation("Generated an API key for Prowlarr, Sonarr and Radarr: {ApiKey}", apiKey);
        }

        if (!await users.AnyUsersAsync(ct))
        {
            var password = string.IsNullOrWhiteSpace(options.AdminPassword)
                ? GeneratePassword()
                : options.AdminPassword;

            await users.CreateAsync(options.AdminUsername, password, ct);

            if (string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                // Logged once, on first run only. Anything else would mean a blank admin account.
                logger.LogWarning(
                    "Created the {Username} account with a generated password: {Password} — change it after signing in",
                    options.AdminUsername, password);
            }
            else
            {
                logger.LogInformation("Created the {Username} account from configuration", options.AdminUsername);
            }
        }

        await EnsureDirectoriesAsync(ct);
    }

    private async Task EnsureDirectoriesAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);

        foreach (var path in new[] { settings.IncompletePath, settings.CompletePath })
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A missing volume mount should surface as a clear warning, not a crash at startup.
                logger.LogWarning(ex, "Could not create the download directory {Path}", path);
            }
        }
    }

    private static string GenerateApiKey() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).TrimEnd('=');
}
