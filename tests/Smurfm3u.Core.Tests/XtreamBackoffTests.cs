using Smurfm3u.Core.Xtream;

namespace Smurfm3u.Core.Tests;

public class XtreamBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(429, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(200, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void RetriesOnlyWhatIsWorthRetrying(int status, bool expected)
    {
        // 401 and 404 are the panel meaning it; asking again just asks again.
        Assert.Equal(expected, XtreamBackoff.IsWorthRetrying(status));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void DoublesTheWaitWhenThePanelSaysNothing(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), XtreamBackoff.For(attempt, null, Now));
    }

    [Fact]
    public void PrefersWhatThePanelAskedForOverAnyGuess()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), XtreamBackoff.For(1, "30", Now));
        Assert.Equal(TimeSpan.FromSeconds(30), XtreamBackoff.For(4, "30", Now));
    }

    [Fact]
    public void UnderstandsRetryAfterAsADate()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), XtreamBackoff.For(1, Now.AddSeconds(45).ToString("R"), Now));
    }

    [Fact]
    public void TreatsADateAlreadyPastAsNoWaitAtAll()
    {
        Assert.Equal(TimeSpan.Zero, XtreamBackoff.For(1, Now.AddMinutes(-5).ToString("R"), Now));
    }

    [Fact]
    public void NeverWaitsLongerThanARefreshCanAfford()
    {
        // A panel asking for an hour is one we stop waiting for and report on.
        Assert.Equal(XtreamBackoff.Longest, XtreamBackoff.For(1, "3600", Now));
        Assert.Equal(XtreamBackoff.Longest, XtreamBackoff.For(20, null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("not-a-date")]
    public void FallsBackToTheGuessWhenTheHeaderMakesNoSense(string? header)
    {
        Assert.Equal(TimeSpan.FromSeconds(2), XtreamBackoff.For(1, header, Now));
    }

    [Fact]
    public void TreatsANegativeRequestAsNoWait()
    {
        Assert.Equal(TimeSpan.Zero, XtreamBackoff.For(1, "-5", Now));
    }
}
