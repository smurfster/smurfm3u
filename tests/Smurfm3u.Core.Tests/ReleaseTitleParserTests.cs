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

    // ---- A season with no episode: a marker when typed, part of the name when given ----

    [Theory]
    [InlineData("Open Season 2 - 2008")]
    [InlineData("Making The Witcher: Season 2 (2021)")]
    [InlineData("Hunting Season 2: Ups and Downs - 2024")]
    public void A_name_keeps_a_season_that_is_part_of_the_title(string name)
    {
        // Measured against 143,440 real films: reading a bare season as a marker in a
        // provider's name misfiled exactly these, and gained nothing, because what we
        // ingest is episodes rather than a provider's season listings.
        var parsed = ReleaseTitleParser.Parse(name);

        Assert.Null(parsed.Season);
        Assert.Equal(MediaKind.Movie, parsed.Kind);
    }

    [Theory]
    [InlineData("sherlock & daughter s01", "sherlock & daughter", 1)]
    [InlineData("Top Gear Season 3", "Top Gear", 3)]
    public void A_query_reads_a_bare_season_as_the_season_it_asks_for(string query, string title, int season)
    {
        var parsed = ReleaseTitleParser.ParseQuery(query);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Equal(MediaKind.Series, parsed.Kind);
    }

    [Fact]
    public void A_query_that_opens_with_something_season_shaped_is_not_a_season()
    {
        // "S4" here is the film's name, and there is no title in front of it to search for.
        var parsed = ReleaseTitleParser.ParseQuery("S4: The Bob Lazar Story");

        Assert.Null(parsed.Season);
    }

    [Fact]
    public void A_query_naming_both_still_reads_both()
    {
        var parsed = ReleaseTitleParser.ParseQuery("sherlock & daughter s01e03");

        Assert.Equal(1, parsed.Season);
        Assert.Equal(3, parsed.Episode);
    }
}
