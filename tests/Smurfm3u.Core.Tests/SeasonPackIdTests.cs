using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class SeasonPackIdTests
{
    [Fact]
    public void RoundTripsThroughItsTextForm()
    {
        var id = new SeasonPackId(7, 2, 2019, "top gear");

        Assert.True(SeasonPackId.TryParse(id.ToString(), out var read));
        Assert.Equal(id, read);
    }

    [Fact]
    public void RoundTripsAShowWithNoYear()
    {
        var id = new SeasonPackId(1, 1, null, "a show");

        Assert.True(SeasonPackId.TryParse(id.ToString(), out var read));
        Assert.Null(read.Year);
        Assert.Equal("a show", read.SearchTitle);
    }

    [Theory]
    [InlineData("lanterns")]
    [InlineData("the.one.with.dots")]
    [InlineData("9-1-1 lone star")]
    [InlineData("shogun 将軍")]
    [InlineData("")]
    public void RoundTripsWhateverTheTitleContains(string title)
    {
        // The title is encoded rather than escaped, so nothing inside it can be read as one
        // of the separators however a client mangles the query string on the way through.
        var id = new SeasonPackId(3, 4, null, title);

        Assert.True(SeasonPackId.TryParse(id.ToString(), out var read));
        Assert.Equal(title, read.SearchTitle);
    }

    [Fact]
    public void IsNotConfusedWithAPlainEntryId()
    {
        Assert.False(SeasonPackId.Looks("12345"));
        Assert.False(SeasonPackId.TryParse("12345", out _));
    }

    [Theory]
    [InlineData("pack.")]
    [InlineData("pack.1.2.3")]
    [InlineData("pack.1.2.3.4.5")]
    [InlineData("pack.x.2.0.dG9w")]
    [InlineData("pack.1.2.0.!!!not base64!!!")]
    public void RefusesAnythingMalformed(string raw)
    {
        Assert.False(SeasonPackId.TryParse(raw, out _));
    }

    [Fact]
    public void SurvivesBeingPutThroughAQueryString()
    {
        var id = new SeasonPackId(2, 1, 2024, "the last of us");
        var escaped = Uri.EscapeDataString(id.ToString());

        Assert.True(SeasonPackId.TryParse(Uri.UnescapeDataString(escaped), out var read));
        Assert.Equal(id, read);
    }
}
