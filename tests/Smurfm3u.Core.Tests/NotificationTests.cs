using Smurfm3u.Core.Options;

namespace Smurfm3u.Core.Tests;

public class NotificationTests
{
    private static SmtpSettings UsableSmtp() => new()
    {
        Host = "smtp.example.com",
        FromAddress = "smurfm3u@example.com",
        ToAddresses = "me@example.com"
    };

    [Fact]
    public void SubjectCarriesTheHeadlineAndTheName()
    {
        var message = NotificationComposer.Compose(
            NotificationEvent.DownloadCompleted, "Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u");

        Assert.Equal(
            "[Smurfm3u] Download completed: Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u",
            message.Subject);
    }

    [Fact]
    public void BodyListsOnlyTheDetailsThatHaveAValue()
    {
        var message = NotificationComposer.Compose(
            NotificationEvent.DownloadFailed,
            "Some.Release",
            [
                new("Category", "tv"),
                new("Playlist", null),
                new("Reason", "  Connection reset  "),
                new("Attempts", "   ")
            ]);

        Assert.Contains("Category: tv", message.Body);
        Assert.Contains("Reason: Connection reset", message.Body);
        Assert.DoesNotContain("Playlist", message.Body);
        Assert.DoesNotContain("Attempts", message.Body);
    }

    [Fact]
    public void FallsBackWhenTheNameIsMissing()
    {
        var message = NotificationComposer.Compose(NotificationEvent.DownloadStarted, "   ");

        Assert.Equal("[Smurfm3u] Download started: (unnamed)", message.Subject);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(2147483648, "2 GiB")]
    public void FormatsSizesForReading(long bytes, string expected)
    {
        Assert.Equal(expected, NotificationComposer.FormatBytes(bytes));
    }

    [Fact]
    public void SendsNothingWhileDisabled()
    {
        var settings = new NotificationSettings { Enabled = false, Smtp = UsableSmtp() };

        Assert.False(settings.ShouldSend(NotificationEvent.DownloadCompleted));
    }

    [Fact]
    public void SendsNothingForAnEventThatIsNotSelected()
    {
        var settings = new NotificationSettings
        {
            Enabled = true,
            Smtp = UsableSmtp(),
            Events = [NotificationEvent.DownloadFailed]
        };

        Assert.True(settings.ShouldSend(NotificationEvent.DownloadFailed));
        Assert.False(settings.ShouldSend(NotificationEvent.DownloadCompleted));
    }

    [Fact]
    public void SendsNothingWhileSmtpIsIncomplete()
    {
        var settings = new NotificationSettings
        {
            Enabled = true,
            Events = [NotificationEvent.DownloadCompleted],
            Smtp = new SmtpSettings { Host = "smtp.example.com" }
        };

        Assert.False(settings.ShouldSend(NotificationEvent.DownloadCompleted));
    }

    [Fact]
    public void DefaultsToTheEventsWorthSendingUnprompted()
    {
        var settings = new NotificationSettings { Enabled = true, Smtp = UsableSmtp() };

        Assert.True(settings.ShouldSend(NotificationEvent.DownloadCompleted));
        Assert.True(settings.ShouldSend(NotificationEvent.DownloadFailed));
        Assert.True(settings.ShouldSend(NotificationEvent.RefreshFailed));
        Assert.False(settings.ShouldSend(NotificationEvent.DownloadQueued));
        Assert.False(settings.ShouldSend(NotificationEvent.DownloadStarted));
    }

    [Theory]
    [InlineData("me@example.com", 1)]
    [InlineData("a@example.com, b@example.com", 2)]
    [InlineData("a@example.com; b@example.com ;c@example.com", 3)]
    [InlineData("a@example.com,a@example.com", 1)]
    [InlineData("", 0)]
    public void SplitsRecipientsOnEitherSeparator(string input, int expected)
    {
        var smtp = new SmtpSettings { ToAddresses = input };

        Assert.Equal(expected, smtp.Recipients().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void RejectsAPortOutsideTheValidRange(int port)
    {
        var smtp = UsableSmtp();
        smtp.Port = port;

        Assert.False(smtp.IsUsable);
    }
}
