using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Parsing;
using Xunit;

namespace Smurfm3u.Core.Tests;

public class AirDateTests
{
    [Theory]
    [InlineData("EastEnders 29/09/2026", 2026, 9, 29)]
    [InlineData("EastEnders 2026-09-29", 2026, 9, 29)]
    [InlineData("EastEnders.2026.09.29", 2026, 9, 29)]
    [InlineData("EastEnders 29.09.2026", 2026, 9, 29)]
    // Day first when either way works, the way the UK providers write it.
    [InlineData("Emmerdale 01/02/2026", 2026, 2, 1)]
    // Month first only when that is the only way it is a date.
    [InlineData("The Daily Show 09/29/2026", 2026, 9, 29)]
    public void Finds_a_date_in_a_name(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), AirDates.Find(text)?.Date);

    [Theory]
    [InlineData("Blade Runner 2049")]
    [InlineData("Top Gear (2002) S01 E03")]
    [InlineData("Show 31/02/2026")]
    [InlineData(null)]
    public void Finds_no_date_where_there_is_none(string? text) =>
        Assert.Null(AirDates.Find(text));

    [Theory]
    [InlineData("2026-09-29", true)]
    [InlineData("2026-09-29 20:00:00", true)]
    [InlineData("0000-00-00", false)]
    [InlineData("", false)]
    public void Reads_a_panel_date_field(string value, bool expected) =>
        Assert.Equal(expected, AirDates.Parse(value) is not null);

    [Fact]
    public void A_dated_name_is_a_daily_episode()
    {
        var parsed = ReleaseTitleParser.Parse("EastEnders 29/09/2026");

        Assert.Equal("EastEnders", parsed.Title);
        Assert.Equal(MediaKind.Series, parsed.Kind);
        Assert.Equal(new DateOnly(2026, 9, 29), parsed.AirDate);
        Assert.Null(parsed.Year);
        Assert.Null(parsed.Season);
    }

    [Fact]
    public void What_follows_the_date_is_the_episode_title()
    {
        var parsed = ReleaseTitleParser.Parse("UK: EastEnders - 29/09/2026 - The Return");

        Assert.Equal("EastEnders", parsed.Title);
        Assert.Equal("The Return", parsed.EpisodeTitle);
    }

    [Fact]
    public void A_film_is_left_as_it_always_read()
    {
        var parsed = ReleaseTitleParser.Parse("Premiere 2026-09-29", MediaKind.Movie);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Null(parsed.AirDate);
    }

    [Fact]
    public void A_date_alone_names_the_release_by_date()
    {
        var name = ReleaseNameBuilder.Build(
            ReleaseTitleParser.Parse("EastEnders 29/09/2026"), "WEB-DL", "1080p", "Smurfm3u");

        Assert.Equal("EastEnders.2026.09.29.1080p.WEB-DL-Smurfm3u", name);
    }

    [Fact]
    public void A_numbered_episode_keeps_its_numbers_unless_asked_by_date()
    {
        var parsed = new ParsedTitle
        {
            Kind = MediaKind.Series, Title = "EastEnders", Season = 42, Episode = 150,
            AirDate = new DateOnly(2026, 9, 29)
        };

        Assert.Equal("EastEnders.S42E150.WEB-DL", ReleaseNameBuilder.Build(parsed, "WEB-DL", null, null));
        Assert.Equal("EastEnders.2026.09.29.WEB-DL",
            ReleaseNameBuilder.Build(parsed, "WEB-DL", null, null, byAirDate: true));
    }

    [Fact]
    public void A_daily_release_id_round_trips()
    {
        Assert.True(DailyReleaseId.TryParse(new DailyReleaseId(123).ToString(), out var id));
        Assert.Equal(123, id.ItemId);

        Assert.False(DailyReleaseId.TryParse("123", out _));
        Assert.False(DailyReleaseId.TryParse("day.x", out _));
    }
}
