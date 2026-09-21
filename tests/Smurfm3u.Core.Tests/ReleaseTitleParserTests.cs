using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Xunit;

namespace Smurfm3u.Core.Tests;

public class ReleaseTitleParserTests
{
    [Theory]
    // The two shapes called out in the brief.
    [InlineData("Top Gear (2002) S01 E03", "Top Gear", 2002, 1, 3)]
    [InlineData("Pacific Rim - 2013", "Pacific Rim", 2013, null, null)]
    // Season/episode spellings that turn up across providers.
    [InlineData("Breaking Bad S05E14", "Breaking Bad", null, 5, 14)]
    [InlineData("Breaking Bad S05.E14", "Breaking Bad", null, 5, 14)]
    [InlineData("The Wire 3x07", "The Wire", null, 3, 7)]
    [InlineData("Lost Season 2 Episode 11", "Lost", null, 2, 11)]
    [InlineData("Top Gear S01 Ep03", "Top Gear", null, 1, 3)]
    // Language and quality badges on the front.
    [InlineData("EN - Top Gear (2002) S01 E03", "Top Gear", 2002, 1, 3)]
    [InlineData("[4K] Dune (2021)", "Dune", 2021, null, null)]
    [InlineData("|VIP| The Matrix 1999", "The Matrix", 1999, null, null)]
    // Quality noise that must not leak into the title.
    [InlineData("Interstellar 2014 1080p WEB-DL x264", "Interstellar", 2014, null, null)]
    [InlineData("Pacific.Rim.2013.1080p", "Pacific Rim", 2013, null, null)]
    public void Parses_titles_from_playlist_names(
        string raw, string expectedTitle, int? expectedYear, int? expectedSeason, int? expectedEpisode)
    {
        var parsed = ReleaseTitleParser.Parse(raw);

        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedYear, parsed.Year);
        Assert.Equal(expectedSeason, parsed.Season);
        Assert.Equal(expectedEpisode, parsed.Episode);
    }

    [Fact]
    public void Season_and_episode_imply_a_series()
    {
        Assert.Equal(MediaKind.Series, ReleaseTitleParser.Parse("Top Gear (2002) S01 E03").Kind);
    }

    [Fact]
    public void A_bare_title_with_a_year_is_treated_as_a_movie()
    {
        Assert.Equal(MediaKind.Movie, ReleaseTitleParser.Parse("Pacific Rim - 2013").Kind);
    }

    [Fact]
    public void A_url_hint_decides_when_the_name_says_nothing()
    {
        Assert.Equal(MediaKind.Series, ReleaseTitleParser.Parse("Some Show", MediaKind.Series).Kind);
    }

    [Fact]
    public void An_episode_title_after_the_marker_is_kept()
    {
        var parsed = ReleaseTitleParser.Parse("Top Gear S01 E03 - The Winter Olympics");
        Assert.Equal("The Winter Olympics", parsed.EpisodeTitle);
    }

    [Fact]
    public void A_numeric_title_survives_year_stripping()
    {
        var parsed = ReleaseTitleParser.Parse("1917 (2019)");
        Assert.Equal("1917", parsed.Title);
        Assert.Equal(2019, parsed.Year);
    }

    [Fact]
    public void Normalize_collapses_punctuation_for_matching()
    {
        Assert.Equal("marvels agents of shield", ReleaseTitleParser.Normalize("Marvel's Agents of S.H.I.E.L.D."));
    }
}
