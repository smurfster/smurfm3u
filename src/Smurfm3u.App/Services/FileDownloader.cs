using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Streams one VOD entry to disk. Writes into the incomplete directory and only moves the
/// finished file into the complete directory, so the *arr apps never import a partial file.
/// </summary>
public class FileDownloader(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    SpeedLimitService speedLimits,
    SettingsService settingsService,
    TimeProvider clock,
    ILogger<FileDownloader> logger)
{
    private const int BufferSize = 128 * 1024;

    /// <summary>How often in-flight progress is flushed to the database.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    public async Task RunAsync(long downloadId, ActiveDownload handle, CancellationToken shutdownToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(handle.Cancellation.Token, shutdownToken);
        var ct = linked.Token;

        var settings = await settingsService.GetAsync(CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var download = await db.Downloads
            .Include(x => x.Source)
            .Include(x => x.M3uItem)
            .FirstOrDefaultAsync(x => x.Id == downloadId, CancellationToken.None);

        if (download is null)
        {
            logger.LogWarning("Download {DownloadId} vanished before it started", downloadId);
            return;
        }

        var incompleteFile = BuildIncompletePath(settings, download);

        try
        {
            download.Status = DownloadStatus.Downloading;
            download.StartedAt ??= clock.GetUtcNow();
            download.FailureMessage = null;
            download.IncompletePath = incompleteFile;
            await db.SaveChangesAsync(CancellationToken.None);

            var completed = await TransferAsync(db, download, handle, incompleteFile, ct);

            if (!completed)
            {
                // Paused: keep the partial file so the next start resumes from where we stopped.
                download.Status = DownloadStatus.Paused;
                download.BytesPerSecond = 0;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogInformation("Paused {Name}", download.Name);
                return;
            }

            download.CompletedPath = await FinishAsync(settings, download, incompleteFile);
            download.Status = DownloadStatus.Completed;
            download.CompletedAt = clock.GetUtcNow();
            download.BytesPerSecond = 0;
            download.DownloadedBytes = Interlocked.Read(ref handle.DownloadedBytes);
            download.TotalBytes = Math.Max(download.TotalBytes, download.DownloadedBytes);
            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation("Completed {Name} into {Path}", download.Name, download.CompletedPath);
        }
        catch (OperationCanceledException) when (handle.PauseRequested)
        {
            download.Status = DownloadStatus.Paused;
            download.BytesPerSecond = 0;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Paused {Name}", download.Name);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Shutting down: leave it queued so it resumes on the next start.
            download.Status = DownloadStatus.Queued;
            download.BytesPerSecond = 0;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; DownloadService has already recorded the outcome.
            logger.LogInformation("Cancelled {Name}", download.Name);
        }
        catch (Exception ex)
        {
            await FailAsync(db, download, settings, incompleteFile, ex);
        }
        finally
        {
            Interlocked.Exchange(ref handle.BytesPerSecond, 0);
        }
    }

    private async Task<bool> TransferAsync(
        AppDbContext db, DownloadItem download, ActiveDownload handle, string incompleteFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(incompleteFile)!);

        var resumeFrom = File.Exists(incompleteFile) ? new FileInfo(incompleteFile).Length : 0L;

        var client = httpClientFactory.CreateClient("download");
        using var request = new HttpRequestMessage(HttpMethod.Get, download.StreamUrl);
        M3uRefreshService.ApplyHeaders(request, download.Source?.Headers);

        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // A server that ignores our Range gives us the whole body again, so start over.
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            logger.LogInformation("{Name}: server does not support resume, restarting", download.Name);
            resumeFrom = 0;
        }

        response.EnsureSuccessStatusCode();

        // Only a length the server actually declared can be checked against at the end;
        // TotalBytes may be our own estimate, which a real transfer will rarely match.
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var expected = contentLength > 0 ? contentLength + resumeFrom : 0;
        var total = expected > 0 ? expected : download.TotalBytes;

        Interlocked.Exchange(ref handle.TotalBytes, total);
        Interlocked.Exchange(ref handle.DownloadedBytes, resumeFrom);

        if (total > 0) download.TotalBytes = total;
        download.DownloadedBytes = resumeFrom;
        await db.SaveChangesAsync(CancellationToken.None);

        var sourceBucket = BuildSourceBucket(download.Source);

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            incompleteFile,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        var downloaded = resumeFrom;

        var lastFlush = Stopwatch.GetTimestamp();
        var lastSampleAt = Stopwatch.GetTimestamp();
        var lastSampleBytes = downloaded;

        while (true)
        {
            if (handle.PauseRequested)
            {
                await CheckpointAsync(db, download, file, downloaded, 0);
                return false;
            }

            var read = await httpStream.ReadAsync(buffer, ct);
            if (read == 0) break;

            // Both gates apply: the global schedule and the source's own cap.
            await speedLimits.Global.ConsumeAsync(read, ct);
            if (sourceBucket is not null)
                await sourceBucket.ConsumeAsync(read, ct);

            await file.WriteAsync(buffer.AsMemory(0, read), ct);

            downloaded += read;
            Interlocked.Exchange(ref handle.DownloadedBytes, downloaded);

            var sampleElapsed = Stopwatch.GetElapsedTime(lastSampleAt);
            if (sampleElapsed >= TimeSpan.FromSeconds(1))
            {
                var rate = (long)((downloaded - lastSampleBytes) / sampleElapsed.TotalSeconds);
                Interlocked.Exchange(ref handle.BytesPerSecond, rate);
                lastSampleAt = Stopwatch.GetTimestamp();
                lastSampleBytes = downloaded;
            }

            if (Stopwatch.GetElapsedTime(lastFlush) >= ProgressInterval)
            {
                await CheckpointAsync(db, download, file, downloaded, Interlocked.Read(ref handle.BytesPerSecond));
                lastFlush = Stopwatch.GetTimestamp();
            }
        }

        await CheckpointAsync(db, download, file, downloaded, 0);

        // A connection dropped mid-transfer ends the stream early and is indistinguishable
        // from a clean finish. Anything short of the declared length is a failure, not a
        // completed file the *arr apps should import.
        if (expected > 0 && downloaded < expected)
            throw new IOException($"Transfer ended after {downloaded} of {expected} bytes");

        return true;
    }

    private static TokenBucket? BuildSourceBucket(M3uSource? source)
    {
        if (source is null || source.SpeedLimitKibps <= 0) return null;

        var bucket = new TokenBucket();
        bucket.SetRate(source.SpeedLimitKibps * 1024d);
        return bucket;
    }

    /// <summary>
    /// Pushes the file all the way to disk before recording how far we have got, so the
    /// recorded offset is never ahead of what would survive a power cut. Startup recovery
    /// trims a partial file back to this offset, and that pairing is what makes resuming
    /// after a hard reset safe rather than merely likely to work.
    /// </summary>
    private static async Task CheckpointAsync(
        AppDbContext db, DownloadItem download, FileStream file, long downloaded, long rate)
    {
        file.Flush(flushToDisk: true);

        download.DownloadedBytes = downloaded;
        download.BytesPerSecond = rate;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Moves the finished file into its own folder under the category directory, which is the
    /// layout SABnzbd produces and the *arr apps expect to scan.
    /// </summary>
    private static async Task<string> FinishAsync(
        Core.Options.ServiceSettings settings, DownloadItem download, string incompleteFile)
    {
        var category = ReleaseNameBuilder.SanitizePathSegment(
            string.IsNullOrWhiteSpace(download.Category) ? "other" : download.Category);
        var folderName = ReleaseNameBuilder.SanitizePathSegment(download.Name);

        var targetFolder = Path.Combine(settings.CompletePath, category, folderName);
        Directory.CreateDirectory(targetFolder);

        var targetFile = Path.Combine(targetFolder, folderName + Path.GetExtension(incompleteFile));

        // File.Move across volumes fails on some container setups, so fall back to a copy.
        try
        {
            File.Move(incompleteFile, targetFile, overwrite: true);
        }
        catch (IOException)
        {
            await using (var from = File.OpenRead(incompleteFile))
            await using (var to = File.Create(targetFile))
                await from.CopyToAsync(to);

            File.Delete(incompleteFile);
        }

        TryCleanUpFolder(Path.GetDirectoryName(incompleteFile));
        return targetFolder;
    }

    private async Task FailAsync(
        AppDbContext db, DownloadItem download, Core.Options.ServiceSettings settings,
        string incompleteFile, Exception ex)
    {
        // Counted here rather than at the start of a run, so a restart that resumes a
        // download does not burn one of its retries. Only a real failure costs an attempt.
        download.AttemptCount++;

        var giveUp = download.AttemptCount >= settings.MaxDownloadAttempts;

        logger.Log(giveUp ? LogLevel.Error : LogLevel.Warning, ex,
            "Download {Name} failed on attempt {Attempt} of {Max}",
            download.Name, download.AttemptCount, settings.MaxDownloadAttempts);

        download.BytesPerSecond = 0;
        download.FailureMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

        if (giveUp)
        {
            download.Status = DownloadStatus.Failed;
            download.CompletedAt = clock.GetUtcNow();

            if (settings.DeleteFailedFiles)
            {
                TryDelete(incompleteFile);
                TryCleanUpFolder(Path.GetDirectoryName(incompleteFile));
            }
        }
        else
        {
            // Back into the queue; the partial file lets the retry resume.
            download.Status = DownloadStatus.Queued;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string BuildIncompletePath(Core.Options.ServiceSettings settings, DownloadItem download)
    {
        var folderName = ReleaseNameBuilder.SanitizePathSegment(download.Name);
        var extension = download.M3uItem?.Extension is { Length: > 0 } ext ? ext : "mp4";

        // Keyed by nzo id so two grabs of the same release cannot collide.
        return Path.Combine(settings.IncompletePath, download.NzoId, $"{folderName}.{extension}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A locked partial file is not worth failing the whole download over.
        }
    }

    private static void TryCleanUpFolder(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
    }
}
