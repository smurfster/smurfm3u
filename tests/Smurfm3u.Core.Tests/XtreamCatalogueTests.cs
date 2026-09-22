using System.Text.Json;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Xtream;

namespace Smurfm3u.Core.Tests;

public class XtreamCatalogueTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly XtreamCredentials Panel =
        new("http://line.example.com:8080", "me", "secret");

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    // ---- The loose typing panels actually emit ----

    [Fact]
    public void ReadsIdsWhetherTheyAreSentAsNumbersOrStrings()
    {
        var asNumber = Parse<XtreamVodStream>("""{"stream_id":12345,"name":"A Film","container_extension":"mkv"}""");
        var asString = Parse<XtreamVodStream>("""{"stream_id":"12345","name":"A Film","container_extension":"mkv"}""");

        Assert.Equal("12345", asNumber.StreamId);
        Assert.Equal("12345", asString.StreamId);
    }

    [Fact]
    public void ReadsEpisodeNumbersWhetherTheyAreSentAsNumbersOrStrings()
    {
        var episode = Parse<XtreamEpisode>("""{"id":"9","season":"2","episode_num":7}""");

        Assert.Equal(2, episode.Season);
        Assert.Equal(7, episode.EpisodeNumber);
    }

    [Fact]
    public void ReadsAnEpisodeWhoseInfoBlockCameBackAsAnEmptyArray()
    {
        // PHP encodes an empty map as [] rather than {}, so an episode the panel knows no
        // runtime for arrives like this. It used to throw, and took the whole series with it.
        var info = Parse<XtreamSeriesInfo>(
            """{"episodes":{"1":[{"id":"5001","title":"Ep","episode_num":1,"container_extension":"mkv","info":[]}]}}""");

        var episode = Assert.Single(
            XtreamCatalogue.ForSeries(Series("""{"series_id":1,"name":"A Show"}"""), info, null, Panel));

        Assert.Equal("http://line.example.com:8080/series/me/secret/5001.mkv", episode.Entry.Url);
        Assert.Equal(0, episode.Entry.DurationSeconds);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("0")]
    public void TreatsAnyInfoBlockThatIsNotAnObjectAsAbsent(string raw)
    {
        var episode = Parse<XtreamEpisode>(
            "{\"id\":\"1\",\"episode_num\":1,\"info\":" + raw + "}");

        Assert.Null(episode.Info);
    }

    [Fact]
    public void StillReadsTheRuntimeWhenTheInfoBlockIsReal()
    {
        var episode = Parse<XtreamEpisode>("""{"id":"1","episode_num":1,"info":{"duration_secs":3600}}""");

        Assert.Equal(3600, episode.Info?.DurationSeconds);
    }

    [Fact]
    public void TreatsAnAccountBlockThatIsNotAnObjectAsAbsent()
    {
        Assert.Null(Parse<XtreamAuth>("""{"user_info":[]}""").UserInfo);
        Assert.NotNull(Parse<XtreamAuth>("""{"user_info":{"auth":1}}""").UserInfo);
    }

    [Fact]
    public void TreatsAnEmptyEpisodeArrayAsNoEpisodes()
    {
        // Some panels send [] rather than {} for a series with nothing in it, which is the
        // shape that would otherwise throw on the way in.
        var info = Parse<XtreamSeriesInfo>("""{"episodes":[]}""");

        Assert.Empty(info.Episodes);
    }

    // ---- Films ----

    [Fact]
    public void MapsAFilmToItsStreamAndLeavesTheTitleToTheParser()
    {
        var stream = Parse<XtreamVodStream>(
            """{"stream_id":12345,"name":"The Thing (1982)","container_extension":"mkv","stream_icon":"http://img/x.jpg"}""");

        var candidate = XtreamCatalogue.ForMovie(stream, "4K Movies", Panel);

        Assert.Equal("http://line.example.com:8080/movie/me/secret/12345.mkv", candidate.Entry.Url);
        Assert.Equal("The Thing (1982)", candidate.Entry.DisplayName);
        Assert.Equal("4K Movies", candidate.Entry.GroupTitle);
        Assert.Equal("http://img/x.jpg", candidate.Entry.TvgLogo);
        Assert.True(candidate.Verdict.IsVod);
        Assert.Equal(MediaKind.Movie, candidate.Verdict.Hint);
        Assert.Equal("mkv", candidate.Verdict.Extension);

        // Nothing the panel sends beats the parser at finding the year in the name.
        Assert.Null(candidate.Parsed);
    }

    [Fact]
    public void FallsBackToMp4WhenThePanelNamesNoContainer()
    {
        var stream = Parse<XtreamVodStream>("""{"stream_id":1,"name":"A Film"}""");

        Assert.Equal("http://line.example.com:8080/movie/me/secret/1.mp4", XtreamCatalogue.ForMovie(stream, null, Panel).Entry.Url);
    }

    // ---- Series ----

    private static XtreamSeries Series(string json) => Parse<XtreamSeries>(json);

    [Fact]
    public void TakesTheSeasonAndEpisodeFromThePanelRatherThanTheName()
    {
        var series = Series("""{"series_id":99,"name":"Top Gear","cover":"http://img/tg.jpg"}""");
        var info = Parse<XtreamSeriesInfo>(
            """
            {"episodes":{"1":[
              {"id":"5001","title":"Polar Special","season":1,"episode_num":3,
               "container_extension":"mkv","info":{"duration_secs":3600}}
            ]}}
            """);

        var episode = Assert.Single(XtreamCatalogue.ForSeries(series, info, "Documentaries", Panel));

        Assert.Equal("http://line.example.com:8080/series/me/secret/5001.mkv", episode.Entry.Url);
        Assert.Equal("Top Gear S01E03 - Polar Special", episode.Entry.DisplayName);
        Assert.Equal(3600, episode.Entry.DurationSeconds);
        Assert.Equal("Documentaries", episode.Entry.GroupTitle);

        var parsed = Assert.IsType<Smurfm3u.Core.Parsing.ParsedTitle>(episode.Parsed);
        Assert.Equal(MediaKind.Series, parsed.Kind);
        Assert.Equal("Top Gear", parsed.Title);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(3, parsed.Episode);
        Assert.Equal("Polar Special", parsed.EpisodeTitle);
    }

    [Fact]
    public void FallsBackToTheMapKeyWhenTheEpisodeOmitsItsSeason()
    {
        var info = Parse<XtreamSeriesInfo>("""{"episodes":{"4":[{"id":"1","episode_num":2}]}}""");

        var episode = Assert.Single(
            XtreamCatalogue.ForSeries(Series("""{"series_id":1,"name":"A Show"}"""), info, null, Panel));

        Assert.Equal(4, episode.Parsed!.Season);
        Assert.Equal(2, episode.Parsed.Episode);
    }

    [Fact]
    public void SkipsAnEpisodeThatCannotBePlaced()
    {
        // No episode number, and no id: the *arrs cannot use either, so neither do we.
        var info = Parse<XtreamSeriesInfo>(
            """{"episodes":{"1":[{"id":"1"},{"episode_num":2},{"id":"3","episode_num":3}]}}""");

        var episodes = XtreamCatalogue.ForSeries(Series("""{"series_id":1,"name":"A Show"}"""), info, null, Panel).ToList();

        Assert.Single(episodes);
        Assert.Equal(3, episodes[0].Parsed!.Episode);
    }

    [Theory]
    [InlineData("Polar Special", "Polar Special")]
    [InlineData("A Show", null)]
    [InlineData("S01E03", null)]
    [InlineData("Episode 3", null)]
    [InlineData("  ", null)]
    public void DropsAnEpisodeTitleThatSaysNothing(string title, string? expected)
    {
        var info = Parse<XtreamSeriesInfo>(
            "{\"episodes\":{\"1\":[{\"id\":\"1\",\"episode_num\":3,\"title\":" + JsonSerializer.Serialize(title) + "}]}}");

        var episode = Assert.Single(
            XtreamCatalogue.ForSeries(Series("""{"series_id":1,"name":"A Show"}"""), info, null, Panel));

        Assert.Equal(expected, episode.Parsed!.EpisodeTitle);
    }

    [Fact]
    public void TakesTheYearFromTheReleaseDateWhenTheNameHasNone()
    {
        var series = Series("""{"series_id":1,"name":"A Show","releaseDate":"2019-04-01"}""");
        var info = Parse<XtreamSeriesInfo>("""{"episodes":{"1":[{"id":"1","episode_num":1}]}}""");

        var episode = Assert.Single(XtreamCatalogue.ForSeries(series, info, null, Panel));

        Assert.Equal(2019, episode.Parsed!.Year);
    }

    [Fact]
    public void ReturnsNothingForASeriesWithNoName()
    {
        var info = Parse<XtreamSeriesInfo>("""{"episodes":{"1":[{"id":"1","episode_num":1}]}}""");

        Assert.Empty(XtreamCatalogue.ForSeries(Series("""{"series_id":1,"name":""}"""), info, null, Panel));
    }

    // ---- What lets a later refresh skip a series it has already read ----

    [Theory]
    [InlineData("1790085182", 1790085182L)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"not a stamp\"", null)]
    public void ReadsTheLastChangedStampHoweverThePanelSendsIt(string raw, long? expected)
    {
        var series = Parse<XtreamSeries>("{\"series_id\":1,\"name\":\"A Show\",\"last_modified\":" + raw + "}");

        Assert.Equal(expected, series.LastModified);
    }

    [Fact]
    public void ReadsTheStampWhenThePanelSendsItAsANumber()
    {
        Assert.Equal(1790085182L, Parse<XtreamSeries>("""{"series_id":1,"name":"A Show","last_modified":1790085182}""").LastModified);
    }

    [Fact]
    public void CarriesTheSeriesAndItsStampOntoEveryEpisode()
    {
        // Without these on the episode there is no way to mark a series as still present
        // without fetching it again, which is the whole point of storing them.
        var series = Series("""{"series_id":99,"name":"Top Gear","last_modified":"1790085182"}""");
        var info = Parse<XtreamSeriesInfo>("""{"episodes":{"1":[{"id":"1","episode_num":1},{"id":"2","episode_num":2}]}}""");

        var episodes = XtreamCatalogue.ForSeries(series, info, null, Panel).ToList();

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, e =>
        {
            Assert.Equal("99", e.SeriesId);
            Assert.Equal(1790085182L, e.SeriesLastModified);
        });
    }

    [Fact]
    public void LeavesAFilmWithNoSeriesToBelongTo()
    {
        var candidate = XtreamCatalogue.ForMovie(
            Parse<XtreamVodStream>("""{"stream_id":1,"name":"A Film"}"""), null, Panel);

        Assert.Null(candidate.SeriesId);
        Assert.Null(candidate.SeriesLastModified);
    }

    [Fact]
    public void MapsCategoryNamesById()
    {
        var categories = JsonSerializer.Deserialize<List<XtreamCategory>>(
            """[{"category_id":1,"category_name":"Movies"},{"category_id":"2","category_name":" Kids "}]""", Json)!;

        var map = XtreamCatalogue.NameById(categories);

        Assert.Equal("Movies", map["1"]);
        Assert.Equal("Kids", map["2"]);
    }
}
