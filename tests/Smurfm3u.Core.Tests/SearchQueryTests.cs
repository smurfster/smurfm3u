using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class SearchQueryTests
{
    [Theory]
    [InlineData("lanterns s01e01", "lanterns", 1, 1)]
    [InlineData("Top Gear S01E03", "Top Gear", 1, 3)]
    [InlineData("the wire 3x07", "the wire", 3, 7)]
    [InlineData("Lost Season 2 Episode 11", "Lost", 2, 11)]
    public void TakesTheSeasonAndEpisodeOutOfTheWords(string query, string title, int season, int episode)
    {
        // Casing is left as written; the matching lowercases both sides anyway.
        var interpreted = SearchQuery.Interpret(query);

        Assert.Equal(title, interpreted.Text);
        Assert.Equal(season, interpreted.Season);
        Assert.Equal(episode, interpreted.Episode);
    }

    [Theory]
    [InlineData("sherlock & daughter s01", "sherlock & daughter", 1)]
    [InlineData("sherlock & daughter season 1", "sherlock & daughter", 1)]
    [InlineData("Top Gear S03", "Top Gear", 3)]
    [InlineData("the wire Season 4", "the wire", 4)]
    public void TakesASeasonOutOfTheWordsEvenWithNoEpisodeAfterIt(string query, string title, int season)
    {
        // The half left behind when "lanterns s01e01" was fixed. No stored title contains
        // "s01", so left in the words it is a term that can never match and the whole query
        // sinks - which is what a season search written this way used to do.
        var interpreted = SearchQuery.Interpret(query);

        Assert.Equal(title, interpreted.Text);
        Assert.Equal(season, interpreted.Season);
        Assert.Null(interpreted.Episode);
    }

    [Theory]
    [InlineData("s01")]
    [InlineData("Season 2")]
    [InlineData("S4")]
    public void RefusesToReadAQueryThatIsNothingButASeason(string query)
    {
        // There is no title left to search for, so the words are better used as written.
        var interpreted = SearchQuery.Interpret(query);

        Assert.Equal(query, interpreted.Text);
        Assert.Null(interpreted.Season);
    }

    [Theory]
    [InlineData("lanterns")]
    [InlineData("Interstellar 2014")]
    [InlineData("Pacific Rim")]
    [InlineData("the matrix 1080p")]
    public void LeavesAQueryWithNeitherInItExactlyAsWritten(string query)
    {
        // Nothing that already worked should start behaving differently, and the year in a
        // film search is matched against its own column by the search itself.
        var interpreted = SearchQuery.Interpret(query);

        Assert.Equal(query, interpreted.Text);
        Assert.Null(interpreted.Season);
        Assert.Null(interpreted.Episode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PassesAnEmptyQueryStraightThrough(string? query)
    {
        var interpreted = SearchQuery.Interpret(query);

        Assert.Equal(query, interpreted.Text);
        Assert.Null(interpreted.Season);
        Assert.Null(interpreted.Episode);
    }
}
