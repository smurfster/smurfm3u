using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class SearchRelaxationTests
{
    private static string[] Tokens(params string[] tokens) => tokens;

    [Fact]
    public void ScoresEachMatchingWordByItsLength()
    {
        Assert.Equal(10, SearchRelaxation.Score("camp crunchlabs", Tokens("mark", "robers", "crunchlabs")));
        Assert.Equal(10, SearchRelaxation.Score("mark robers revengineers", Tokens("mark", "robers", "crunchlabs")));
        Assert.Equal(20, SearchRelaxation.Score("mark robers crunchlabs", Tokens("mark", "robers", "crunchlabs")));
    }

    [Fact]
    public void ScoresNothingWhenNoWordAppears()
    {
        Assert.Equal(0, SearchRelaxation.Score("pacific rim", Tokens("top", "gear")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ScoresNothingForAMissingTitle(string? title)
    {
        Assert.Equal(0, SearchRelaxation.Score(title, Tokens("top", "gear")));
    }

    [Fact]
    public void ScoresNothingWithNoWordsToMatch()
    {
        Assert.Equal(0, SearchRelaxation.Score("top gear", []));
    }

    [Fact]
    public void PrefersALongDistinctiveWordOverAShortCommonOne()
    {
        var tokens = Tokens("the", "crunchlabs");
        string[] candidates = ["the office", "camp crunchlabs"];

        var best = SearchRelaxation.BestTier(candidates, x => x, tokens);

        Assert.Equal(["camp crunchlabs"], best);
    }

    [Fact]
    public void KeepsEveryEntryThatMatchesEquallyWell()
    {
        var tokens = Tokens("top", "gear");
        string[] candidates = ["top gear", "top gear america", "pacific rim"];

        var best = SearchRelaxation.BestTier(candidates, x => x, tokens);

        Assert.Equal(["top gear", "top gear america"], best);
    }

    [Fact]
    public void DropsWeakerPartialMatches()
    {
        var tokens = Tokens("breaking", "bad");
        string[] candidates = ["breaking bad", "bad boys", "breaking point"];

        var best = SearchRelaxation.BestTier(candidates, x => x, tokens);

        Assert.Equal(["breaking bad"], best);
    }

    [Fact]
    public void KeepsNothingWhenNoCandidateMatchesAtAll()
    {
        string[] candidates = ["pacific rim", "the office"];

        Assert.Empty(SearchRelaxation.BestTier(candidates, x => x, Tokens("crunchlabs")));
    }

    [Fact]
    public void HandlesAnEmptyCandidateList()
    {
        Assert.Empty(SearchRelaxation.BestTier([], (string x) => x, Tokens("anything")));
    }
}
