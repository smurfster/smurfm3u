using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Options;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// The provider answered that the file is not there. Distinct from the transient failures
/// because retrying cannot help: nothing about a 404 gets better by asking again.
/// </summary>
public sealed class ContentGoneException(string message) : Exception(message);

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
    NotificationService notifications,
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
            .Include(x => x.Files.OrderBy(f => f.Position))
            .FirstOrDefaultAsync(x => x.Id == downloadId, CancellationToken.None);

        if (download is null)
        {
            logger.LogWarning("Download {DownloadId} vanished before it started", downloadId);
            return;
        }

        if (download.Files.Count == 0)
        {
            logger.LogWarning("{Name} has nothing to download", download.Name);
            await FailAsync(db, download, settings, new InvalidOperationException("This grab has no files."));
            return;
        }

        // One folder per grab, named after the nzo id so two grabs of the same release cannot
        // collide. A pack's episodes are all written into it, side by side.
        var workFolder = Path.Combine(settings.IncompletePath, download.NzoId);

        try
        {
            download.Status = DownloadStatus.Downloading;
            download.StartedAt ??= clock.GetUtcNow();
            download.FailureMessage = null;
            download.IncompletePath = workFolder;
            await db.SaveChangesAsync(CancellationToken.None);

            // Only on the first attempt: a resume after a restart is not a new start.
            if (download.AttemptCount == 0)
            {
                notifications.Notify(NotificationEvent.DownloadStarted, download.Name,
                [
                    new("Category", download.Category),
                    new("Playlist", download.Source?.Name),
                    new("Files", download.Files.Count > 1 ? download.Files.Count.ToString() : null),
                    new("Size", NotificationComposer.FormatBytes(download.TotalBytes))
                ]);
            }

            if (!await TransferAllAsync(db, download, handle, workFolder, ct))
            {
                // Paused: keep the partial files so the next start resumes from where we stopped.
                download.Status = DownloadStatus.Paused;
                download.BytesPerSecond = 0;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogInformation("Paused {Name}", download.Name);
                return;
            }

            var transferred = download.Files.Where(x => x.Status == DownloadFileStatus.Completed).ToList();

            // Every file gone is not a download that finished with nothing in it; it is a
            // release that is not there any more, and the client needs to hear that as a
            // failure so it goes and looks somewhere else.
            if (transferred.Count == 0)
            {
                throw new ContentGoneException(download.Files.Count == 1
                    ? download.Files[0].FailureMessage ?? "The provider no longer has this file."
                    : $"The provider no longer has any of the {download.Files.Count} files in this release.");
            }

            download.CompletedPath = await FinishAsync(settings, download, transferred, workFolder);
            download.Status = DownloadStatus.Completed;
            download.CompletedAt = clock.GetUtcNow();
            download.BytesPerSecond = 0;
            download.DownloadedBytes = transferred.Sum(x => x.DownloadedBytes);
            download.TotalBytes = Math.Max(download.TotalBytes, download.DownloadedBytes);

            var missing = download.Files.Count - transferred.Count;

            // Recorded but not fatal: the *arr apps import a folder file by file and will go
            // looking for whatever is not in it, which is a better outcome than failing a
            // whole season because one episode has been taken down.
            download.FailureMessage = missing > 0
                ? $"{missing} of {download.Files.Count} files were no longer available and were left out."
                : null;

            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation("Completed {Name} into {Path}{Partial}",
                download.Name, download.CompletedPath,
                missing > 0 ? $" ({missing} file(s) missing)" : string.Empty);

            notifications.Notify(NotificationEvent.DownloadCompleted, download.Name,
            [
                new("Category", download.Category),
                new("Playlist", download.Source?.Name),
                new("Files", download.Files.Count > 1 ? $"{transferred.Count} of {download.Files.Count}" : null),
                new("Size", NotificationComposer.FormatBytes(download.DownloadedBytes)),
                new("Took", Describe(download.StartedAt, download.CompletedAt)),
                new("Folder", download.CompletedPath)
            ]);
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
            await FailAsync(db, download, settings, ex);
        }
        finally
        {
            Interlocked.Exchange(ref handle.BytesPerSecond, 0);
        }
    }

    /// <summary>
    /// Transfers each of the grab's outstanding files in turn. Returns false if the user
    /// paused partway, which leaves everything on disk for the next start to carry on from.
    /// <para>
    /// A file the provider no longer has is set aside and the rest carry on; any other failure
    /// is thrown, which puts the whole grab back in the queue with its finished files intact.
    /// </para>
    /// </summary>
    private async Task<bool> TransferAllAsync(
        AppDbContext db, DownloadItem download, ActiveDownload handle, string workFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(workFolder);

        // Shared across the whole grab, so a source's own speed cap governs the pack rather
        // than being handed out afresh to each episode in it.
        var sourceBucket = BuildSourceBucket(download.Source);

        var files = download.Files.OrderBy(x => x.Position).ToList();

        // Files settled on an earlier attempt are not fetched again; their bytes still count
        // towards what the client sees, because they are still part of this grab.
        var carried = files
            .Where(x => x.Status == DownloadFileStatus.Completed)
            .Sum(x => x.DownloadedBytes);

        Interlocked.Exchange(ref handle.TotalBytes, download.TotalBytes);
        Interlocked.Exchange(ref handle.DownloadedBytes, carried);

        foreach (var file in files)
        {
            if (file.Status != DownloadFileStatus.Pending) continue;

            var path = Path.Combine(
                workFolder,
                $"{ReleaseNameBuilder.SanitizePathSegment(file.Name)}.{Extension(file)}");

            file.IncompletePath = path;

            try
            {
                if (!await TransferAsync(db, download, file, handle, sourceBucket, carried, path, ct))
                    return false;

                file.Status = DownloadFileStatus.Completed;
                carried += file.DownloadedBytes;
            }
            catch (ContentGoneException ex)
            {
                // One episode withdrawn should not cost the season. Set it aside, note why,
                // and carry on; the caller fails the grab only if nothing at all survives.
                file.Status = DownloadFileStatus.Skipped;
                file.FailureMessage = ex.Message;
                file.DownloadedBytes = 0;

                TryDelete(path);

                logger.LogWarning("{Name}: {File} is no longer available and has been left out",
                    download.Name, file.Name);
            }
            finally
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }

            // Recalculated from what the files actually declared, so a pack's estimate is
            // replaced by real lengths as they come in rather than only at the end.
            download.DownloadedBytes = carried;
            download.TotalBytes = files.Sum(x => x.TotalBytes);
            Interlocked.Exchange(ref handle.TotalBytes, download.TotalBytes);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return true;
    }

    private static string Extension(DownloadFile file) =>
        string.IsNullOrWhiteSpace(file.Extension) ? "mp4" : file.Extension;

    private async Task<bool> TransferAsync(
        AppDbContext db, DownloadItem download, DownloadFile file, ActiveDownload handle,
        TokenBucket? sourceBucket, long carried, string incompleteFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(incompleteFile)!);

        var resumeFrom = File.Exists(incompleteFile) ? new FileInfo(incompleteFile).Length : 0L;

        var client = httpClientFactory.CreateClient("download");
        using var request = new HttpRequestMessage(HttpMethod.Get, file.StreamUrl);
        M3uRefreshService.ApplyHeaders(request, download.Source?.Headers);

        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // A server that ignores our Range gives us the whole body again, so start over.
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            logger.LogInformation("{Name}: server does not support resume, restarting", file.Name);
            resumeFrom = 0;
        }

        // A provider that answers "gone" is not having a bad moment, it has taken the file
        // down. That is the clearest evidence there is that the entry behind it is stale, and
        // the only moment staleness actually costs anything, so the entry is retired here and
        // the download stops rather than spending its retries proving the same point.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            if (file.M3uItemId is { } itemId)
            {
                await db.Items
                    .Where(x => x.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), CancellationToken.None);

                logger.LogWarning(
                    "{Name}: the provider no longer has this file ({Status}); the entry has been retired",
                    file.Name, (int)response.StatusCode);
            }

            throw new ContentGoneException(
                $"The provider no longer has this file ({(int)response.StatusCode} {response.StatusCode}).");
        }

        response.EnsureSuccessStatusCode();

        // Only a length the server actually declared can be checked against at the end;
        // TotalBytes may be our own estimate, which a real transfer will rarely match.
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var expected = contentLength > 0 ? contentLength + resumeFrom : 0;
        var total = expected > 0 ? expected : file.TotalBytes;

        if (total > 0) file.TotalBytes = total;
        file.DownloadedBytes = resumeFrom;

        // What the client sees is the whole grab, so a file's progress is added to whatever
        // the files before it already contributed.
        Interlocked.Exchange(ref handle.DownloadedBytes, carried + resumeFrom);
        await db.SaveChangesAsync(CancellationToken.None);

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var stream = new FileStream(
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
                await CheckpointAsync(db, download, file, stream, downloaded, carried, 0);
                return false;
            }

            var read = await httpStream.ReadAsync(buffer, ct);
            if (read == 0) break;

            // Both gates apply: the global schedule and the source's own cap.
            await speedLimits.Global.ConsumeAsync(read, ct);
            if (sourceBucket is not null)
                await sourceBucket.ConsumeAsync(read, ct);

            await stream.WriteAsync(buffer.AsMemory(0, read), ct);

            downloaded += read;
            Interlocked.Exchange(ref handle.DownloadedBytes, carried + downloaded);

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
                await CheckpointAsync(db, download, file, stream, downloaded, carried, Interlocked.Read(ref handle.BytesPerSecond));
                lastFlush = Stopwatch.GetTimestamp();
            }
        }

        await CheckpointAsync(db, download, file, stream, downloaded, carried, 0);

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
        AppDbContext db, DownloadItem download, DownloadFile file, FileStream stream,
        long downloaded, long carried, long rate)
    {
        stream.Flush(flushToDisk: true);

        file.DownloadedBytes = downloaded;

        // The grab's counter is the files already finished plus how far this one has got,
        // because that is the single number the queue shows for the whole slot.
        download.DownloadedBytes = carried + downloaded;
        download.BytesPerSecond = rate;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Moves the finished files into one folder under the category directory, which is the
    /// layout SABnzbd produces and the *arr apps expect to scan. A single episode or film
    /// lands as one file named after the release; a pack lands as one file per episode, each
    /// named after its own, which is what lets the importer place them individually.
    /// </summary>
    private static async Task<string> FinishAsync(
        Core.Options.ServiceSettings settings, DownloadItem download,
        IReadOnlyList<DownloadFile> transferred, string workFolder)
    {
        var category = ReleaseNameBuilder.SanitizePathSegment(
            string.IsNullOrWhiteSpace(download.Category) ? "other" : download.Category);
        var folderName = ReleaseNameBuilder.SanitizePathSegment(download.Name);

        var targetFolder = Path.Combine(settings.CompletePath, category, folderName);
        Directory.CreateDirectory(targetFolder);

        foreach (var file in transferred)
        {
            if (file.IncompletePath is not { Length: > 0 } source || !File.Exists(source)) continue;

            var targetFile = Path.Combine(
                targetFolder,
                ReleaseNameBuilder.SanitizePathSegment(file.Name) + Path.GetExtension(source));

            // File.Move across volumes fails on some container setups, so fall back to a copy.
            try
            {
                File.Move(source, targetFile, overwrite: true);
            }
            catch (IOException)
            {
                await using (var from = File.OpenRead(source))
                await using (var to = File.Create(targetFile))
                    await from.CopyToAsync(to);

                File.Delete(source);
            }
        }

        TryCleanUpFolder(workFolder);
        return targetFolder;
    }

    private async Task FailAsync(
        AppDbContext db, DownloadItem download, Core.Options.ServiceSettings settings, Exception ex)
    {
        // Counted here rather than at the start of a run, so a restart that resumes a
        // download does not burn one of its retries. Only a real failure costs an attempt.
        download.AttemptCount++;

        // A file the provider says is gone will still be gone on the next attempt, so the
        // retries are spent rather than used. It fails now, with the reason.
        var giveUp = ex is ContentGoneException || download.AttemptCount >= settings.MaxDownloadAttempts;

        logger.Log(giveUp ? LogLevel.Error : LogLevel.Warning, ex,
            "Download {Name} failed on attempt {Attempt} of {Max}",
            download.Name, download.AttemptCount, settings.MaxDownloadAttempts);

        download.BytesPerSecond = 0;
        download.FailureMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

        if (giveUp)
        {
            download.Status = DownloadStatus.Failed;
            download.CompletedAt = clock.GetUtcNow();

            if (settings.DeleteFailedFiles && download.IncompletePath is { Length: > 0 } workFolder)
                TryDeleteFolder(workFolder);

            // Only once it has given up. Notifying per attempt would send three of these
            // for every download that was always going to fail.
            notifications.Notify(NotificationEvent.DownloadFailed, download.Name,
            [
                new("Category", download.Category),
                new("Playlist", download.Source?.Name),
                new("Attempts", download.AttemptCount.ToString()),
                new("Reason", download.FailureMessage)
            ]);
        }
        else
        {
            // Back into the queue; the partial file lets the retry resume.
            download.Status = DownloadStatus.Queued;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>How long a transfer took, phrased for a notification rather than a log.</summary>
    private static string Describe(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null) return string.Empty;

        var elapsed = to.Value - from.Value;
        return elapsed < TimeSpan.FromMinutes(1)
            ? $"{elapsed.TotalSeconds:0} seconds"
            : $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked partial file is not worth failing the whole download over.
        }
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
