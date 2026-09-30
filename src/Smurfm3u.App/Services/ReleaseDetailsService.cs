using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Options;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>One file a release would download.</summary>
public sealed record ReleaseFileDetail(
    long ItemId,
    string ReleaseName,
    int? Season,
    int? Episode,
    string? EpisodeTitle,
    string? GroupTitle,
    long SizeBytes,
    string Extension,
    bool IsActive,
    bool SizeIsKnown);

/// <summary>
/// What a release actually contains, for the page a client's info link opens.
/// </summary>
public sealed record ReleaseDetails(
    string DownloadId,
    string ReleaseName,
    bool IsSeasonPack,
    MediaKind Kind,
    string Title,
    int? Year,
    int? Season,
    string? PlaylistName,
    long TotalSizeBytes,
    IReadOnlyList<ReleaseFileDetail> Files);

/// <summary>
/// Describes a release the way its own page shows it.
/// <para>
/// A film or an episode is one file and mostly restates its row. A season pack is the reason
/// this exists: it arrives as a single line in Sonarr, and whether the episodes behind it are
/// the ones you want is not answerable from that line alone.
/// </para>
/// </summary>
public class ReleaseDetailsService(
    IDbContextFactory<AppDbContext> dbFactory,
    SeasonPackService seasonPacks,
    SettingsService settingsService)
{
    /// <summary>
    /// Reads an id offered by a search back into what it stands for. Null when it names
    /// nothing, which is the honest answer for a release that has since been withdrawn.
    /// </summary>
    public async Task<ReleaseDetails?> DescribeAsync(string? downloadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadId)) return null;

        var settings = await settingsService.GetAsync(ct);

        return SeasonPackId.TryParse(downloadId, out var packId)
            ? await DescribePackAsync(packId, settings, ct)
            : await DescribeEntryAsync(downloadId, settings, ct);
    }

    private async Task<ReleaseDetails?> DescribePackAsync(
        SeasonPackId packId, ServiceSettings settings, CancellationToken ct)
    {
        // Resolved now rather than from what the search offered, so the page shows the season
        // as it stands - which is also what a grab arriving at this moment would get.
        var pack = await seasonPacks.ResolveAsync(packId, settings, ct);

        if (pack is null) return null;

        var first = pack.Episodes[0];

        return new ReleaseDetails(
            DownloadId: packId.ToString(),
            ReleaseName: pack.Name,
            IsSeasonPack: true,
            Kind: MediaKind.Series,
            Title: first.Title,
            Year: first.Year,
            Season: packId.Season,
            PlaylistName: first.Source?.Name,
            TotalSizeBytes: pack.SizeBytes,
            Files: [.. pack.Episodes.Select(x => Describe(x, settings))]);
    }

    private async Task<ReleaseDetails?> DescribeEntryAsync(
        string downloadId, ServiceSettings settings, CancellationToken ct)
    {
        // Offered by its air date: the same entry, under the dated name it was offered as.
        var byAirDate = DailyReleaseId.TryParse(downloadId, out var dailyId);

        if (byAirDate)
            downloadId = dailyId.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!long.TryParse(downloadId, System.Globalization.CultureInfo.InvariantCulture, out var itemId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .FirstOrDefaultAsync(x => x.Id == itemId, ct);

        if (item is null) return null;

        return new ReleaseDetails(
            DownloadId: byAirDate ? dailyId.ToString() : downloadId,
            ReleaseName: ReleaseFactory.BuildName(item, item.Source, byAirDate),
            IsSeasonPack: false,
            Kind: item.Kind,
            Title: item.Title,
            Year: item.Year,
            Season: item.Season,
            PlaylistName: item.Source?.Name,
            TotalSizeBytes: SizeEstimator.Estimate(item, settings),
            Files: [Describe(item, settings, byAirDate)]);
    }

    private static ReleaseFileDetail Describe(M3uItem item, ServiceSettings settings, bool byAirDate = false) =>
        new(
            item.Id,
            ReleaseFactory.BuildName(item, item.Source, byAirDate),
            item.Season,
            item.Episode,
            item.EpisodeTitle,
            item.GroupTitle,
            SizeEstimator.Estimate(item, settings),
            item.Extension,
            item.IsActive,
            // Providers rarely declare a length, so most sizes here are worked out from the
            // runtime. Saying which is which matters when the number is what you are judging.
            item.SizeBytes > 0);
}
