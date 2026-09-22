using Smurfm3u.Core.Xtream;

namespace Smurfm3u.Core.Tests;

public class XtreamCredentialsTests
{
    private static XtreamCredentials Create(string location, string? user = "me", string? pass = "secret")
    {
        Assert.True(XtreamCredentials.TryCreate(location, user, pass, out var credentials, out var error), error);
        return credentials;
    }

    [Theory]
    [InlineData("line.example.com:8080", "http://line.example.com:8080")]
    [InlineData("http://line.example.com:8080", "http://line.example.com:8080")]
    [InlineData("http://line.example.com:8080/", "http://line.example.com:8080")]
    [InlineData("https://line.example.com", "https://line.example.com")]
    [InlineData("http://line.example.com:8080/iptv", "http://line.example.com:8080/iptv")]
    public void NormalisesWhateverAddressTheProviderGaveOut(string location, string expected)
    {
        Assert.Equal(expected, Create(location).BaseUrl);
    }

    [Theory]
    [InlineData("http://line.example.com:8080/get.php")]
    [InlineData("http://line.example.com:8080/player_api.php")]
    [InlineData("http://line.example.com:8080/panel_api.php")]
    [InlineData("http://line.example.com:8080/xmltv.php")]
    public void DropsTheEndpointFromAPastedLink(string location)
    {
        // Left on, everything would be built as .../get.php/player_api.php.
        Assert.Equal("http://line.example.com:8080", Create(location).BaseUrl);
    }

    [Fact]
    public void TakesTheCredentialsOutOfAPastedGetPhpLink()
    {
        var credentials = Create(
            "http://line.example.com:8080/get.php?username=someone&password=pa%20ss&type=m3u_plus",
            user: null, pass: null);

        Assert.Equal("http://line.example.com:8080", credentials.BaseUrl);
        Assert.Equal("someone", credentials.Username);
        Assert.Equal("pa ss", credentials.Password);
    }

    [Fact]
    public void WhatIsTypedInTheFormBeatsWhatIsInTheLink()
    {
        var credentials = Create(
            "http://line.example.com:8080/get.php?username=fromurl&password=fromurl",
            user: "typed", pass: "alsotyped");

        Assert.Equal("typed", credentials.Username);
        Assert.Equal("alsotyped", credentials.Password);
    }

    [Fact]
    public void RefusesAnAddressItCannotUse()
    {
        Assert.False(XtreamCredentials.TryCreate("", "me", "secret", out _, out var blank));
        Assert.Contains("address", blank);

        Assert.False(XtreamCredentials.TryCreate("ftp://line.example.com", "me", "secret", out _, out var scheme));
        Assert.Contains("http", scheme);
    }

    [Fact]
    public void RefusesAnAddressWithNoCredentialsAnywhere()
    {
        Assert.False(XtreamCredentials.TryCreate("http://line.example.com:8080", null, null, out _, out var error));
        Assert.Contains("username and password", error);
    }

    [Fact]
    public void BuildsAPlayerApiCall()
    {
        Assert.Equal(
            "http://line.example.com:8080/player_api.php?username=me&password=secret",
            Create("line.example.com:8080").Api());

        Assert.Equal(
            "http://line.example.com:8080/player_api.php?username=me&password=secret&action=get_series_info&series_id=42",
            Create("line.example.com:8080").Api("get_series_info", ("series_id", "42")));
    }

    [Fact]
    public void BuildsStreamUrlsUnderTheRightPath()
    {
        var credentials = Create("line.example.com:8080");

        Assert.Equal(
            "http://line.example.com:8080/movie/me/secret/123.mkv",
            credentials.StreamUrl(XtreamStreamKind.Movie, "123", "mkv"));

        Assert.Equal(
            "http://line.example.com:8080/series/me/secret/456.mp4",
            credentials.StreamUrl(XtreamStreamKind.Series, "456", null));

        Assert.Equal(
            "http://line.example.com:8080/movie/me/secret/123.mkv",
            credentials.StreamUrl(XtreamStreamKind.Movie, "123", ".MKV"));
    }

    [Fact]
    public void EscapesCredentialsThatWouldOtherwiseBreakThePath()
    {
        var credentials = Create("line.example.com:8080", "user name", "p/a ss");

        Assert.Equal(
            "http://line.example.com:8080/movie/user%20name/p%2Fa%20ss/7.mp4",
            credentials.StreamUrl(XtreamStreamKind.Movie, "7", "mp4"));
    }
}
