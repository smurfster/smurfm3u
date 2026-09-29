using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Reads and writes <see cref="ServiceSettings"/>, cached in memory because the API hot paths
/// consult it on every request.
/// </summary>
public class SettingsService(IDbContextFactory<AppDbContext> dbFactory, ILogger<SettingsService> logger)
{
    private const string SettingsKey = "service-settings";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private ServiceSettings? _cached;

    /// <summary>Raised after a successful save so workers can pick up new limits and paths.</summary>
    public event Action<ServiceSettings>? Changed;

    public async Task<ServiceSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } hit) return hit;

        await _mutex.WaitAsync(ct);
        try
        {
            if (_cached is { } raced) return raced;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingsKey, ct);

            _cached = Deserialize(row?.Value) ?? Defaults();
            return _cached;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(ServiceSettings settings, CancellationToken ct = default)
    {
        DownloadCategories.Normalise(settings);

        await _mutex.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Settings.FirstOrDefaultAsync(x => x.Key == SettingsKey, ct);

            var payload = JsonSerializer.Serialize(settings, Json);
            if (row is null)
                db.Settings.Add(new AppSetting { Key = SettingsKey, Value = payload });
            else
                row.Value = payload;

            await db.SaveChangesAsync(ct);
            _cached = settings;
        }
        finally
        {
            _mutex.Release();
        }

        Changed?.Invoke(settings);
    }

    /// <summary>Applies a mutation to the current settings and persists the result.</summary>
    public async Task<ServiceSettings> UpdateAsync(Action<ServiceSettings> mutate, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        var copy = Deserialize(JsonSerializer.Serialize(current, Json)) ?? Defaults();

        mutate(copy);
        await SaveAsync(copy, ct);
        return copy;
    }

    private static ServiceSettings Defaults()
    {
        var settings = new ServiceSettings();
        DownloadCategories.Normalise(settings);
        return settings;
    }

    private ServiceSettings? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            var settings = JsonSerializer.Deserialize<ServiceSettings>(value, Json);
            if (settings is not null) DownloadCategories.Normalise(settings);
            return settings;
        }
        catch (JsonException ex)
        {
            // A corrupt row must not take the service down; defaults are always usable.
            logger.LogError(ex, "Stored settings could not be read; falling back to defaults");
            return null;
        }
    }
}
