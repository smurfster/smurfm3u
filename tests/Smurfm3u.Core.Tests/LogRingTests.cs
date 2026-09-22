using Microsoft.Extensions.Logging;
using Smurfm3u.Core.Diagnostics;

namespace Smurfm3u.Core.Tests;

public class LogRingTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static void Write(LogRing ring, string message, LogLevel level = LogLevel.Information,
        string category = "Smurfm3u.App.Services.FileDownloader", string? exception = null) =>
        ring.Add(level, category, message, exception, At);

    [Fact]
    public void KeepsTheNewestLinesAndDropsTheOldest()
    {
        var ring = new LogRing(capacity: 3);

        for (var i = 1; i <= 5; i++) Write(ring, $"line {i}");

        // A log that grows without limit is a memory leak, so the cap is the point.
        Assert.Equal(["line 3", "line 4", "line 5"], ring.Snapshot().Select(x => x.Message));
    }

    [Fact]
    public void HandsBackACopySoAReaderIsNotWalkingLiveState()
    {
        var ring = new LogRing();
        Write(ring, "first");

        var snapshot = ring.Snapshot();
        Write(ring, "second");

        Assert.Single(snapshot);
        Assert.Equal(2, ring.Snapshot().Count);
    }

    [Fact]
    public void NumbersEveryLineSoTwoInTheSameMillisecondStayApart()
    {
        var ring = new LogRing();

        Write(ring, "one");
        Write(ring, "two");

        var entries = ring.Snapshot();

        Assert.Equal(At, entries[0].At);
        Assert.Equal(At, entries[1].At);
        Assert.True(entries[1].Sequence > entries[0].Sequence);
    }

    [Fact]
    public void AnnouncesEachLineAsItIsWritten()
    {
        var ring = new LogRing();
        var seen = new List<string>();

        ring.Written += entry => seen.Add(entry.Message);

        Write(ring, "one");
        Write(ring, "two");

        Assert.Equal(["one", "two"], seen);
    }

    [Fact]
    public void StopsAnnouncingOnceAPageHasGone()
    {
        var ring = new LogRing();
        var seen = 0;

        void Handler(LogEntry _) => seen++;

        ring.Written += Handler;
        Write(ring, "while open");

        ring.Written -= Handler;
        Write(ring, "after closing");

        Assert.Equal(1, seen);
    }

    [Fact]
    public void ClearEmptiesIt()
    {
        var ring = new LogRing();
        Write(ring, "something");

        ring.Clear();

        Assert.Empty(ring.Snapshot());
    }

    [Fact]
    public void FallsBackToADefaultCapacityRatherThanKeepingNothing()
    {
        Assert.Equal(1000, new LogRing(0).Capacity);
        Assert.Equal(1000, new LogRing(-5).Capacity);
    }

    [Fact]
    public void SurvivesConcurrentWriters()
    {
        var ring = new LogRing(capacity: 500);

        Parallel.For(0, 400, i => Write(ring, $"line {i}"));

        Assert.Equal(400, ring.Snapshot().Count);
        Assert.Equal(400, ring.Snapshot().Select(x => x.Sequence).Distinct().Count());
    }

    // ---- What the page filters on ----

    [Theory]
    [InlineData("Smurfm3u.App.Services.FileDownloader", "FileDownloader")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "Diagnostics")]
    [InlineData("Program", "Program")]
    [InlineData("trailing.", "trailing.")]
    public void ShowsTheClassThatWroteItWithoutItsNamespace(string category, string expected)
    {
        Assert.Equal(expected, new LogEntry(1, At, LogLevel.Information, category, "m", null).Source);
    }

    [Fact]
    public void FiltersOutAnythingBelowTheChosenLevel()
    {
        var warning = new LogEntry(1, At, LogLevel.Warning, "c", "m", null);
        var info = new LogEntry(2, At, LogLevel.Information, "c", "m", null);

        Assert.True(warning.Matches(LogLevel.Warning, null));
        Assert.False(info.Matches(LogLevel.Warning, null));
        Assert.True(info.Matches(LogLevel.Information, null));
    }

    [Theory]
    [InlineData("gear", true)]
    [InlineData("GEAR", true)]
    [InlineData("downloader", true)]
    [InlineData("timed out", true)]
    [InlineData("nothing like it", false)]
    public void SearchesTheMessageTheSourceAndTheException(string text, bool expected)
    {
        var entry = new LogEntry(
            1, At, LogLevel.Error, "Smurfm3u.App.Services.FileDownloader",
            "Top Gear failed", "IOException: the connection timed out");

        Assert.Equal(expected, entry.Matches(LogLevel.Trace, text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyFilterKeepsEverything(string? text)
    {
        Assert.True(new LogEntry(1, At, LogLevel.Information, "c", "m", null).Matches(LogLevel.Trace, text));
    }
}
