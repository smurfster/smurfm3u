using Smurfm3u.Core.Diagnostics;

namespace Smurfm3u.Core.Tests;

public class SafeUrlTests
{
    [Fact]
    public void MasksThePasswordInAProvidersPlaylistLink()
    {
        Assert.Equal(
            "http://line.example.com:8080/get.php?username=someone&password=***&type=m3u_plus",
            SafeUrl.Redact("http://line.example.com:8080/get.php?username=someone&password=hunter2&type=m3u_plus"));
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("pwd")]
    [InlineData("token")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("secret")]
    [InlineData("auth")]
    [InlineData("PASSWORD")]
    public void MasksTheOtherNamesTheSameThingGoesBy(string key)
    {
        Assert.Equal(
            $"http://example.com/x?{key}=***",
            SafeUrl.Redact($"http://example.com/x?{key}=hunter2"));
    }

    [Fact]
    public void KeepsTheUsernameSoTheLineIsStillIdentifiable()
    {
        Assert.Contains("username=someone", SafeUrl.Redact("http://example.com/get.php?username=someone&password=x"));
    }

    [Fact]
    public void MasksCredentialsHidingInTheAuthority()
    {
        Assert.DoesNotContain("hunter2", SafeUrl.Redact("http://someone:hunter2@example.com/list.m3u"));
    }

    [Fact]
    public void LeavesAlonePlainLocationsWithNothingToHide()
    {
        Assert.Equal("http://example.com/vod.m3u8", SafeUrl.Redact("http://example.com/vod.m3u8"));
        Assert.Equal("/playlists/vod.m3u", SafeUrl.Redact("/playlists/vod.m3u"));
        // Parses as a file URI on Windows, which is why file locations are checked for first.
        Assert.Equal(@"C:\playlists\vod.m3u", SafeUrl.Redact(@"C:\playlists\vod.m3u"));
        Assert.Equal(string.Empty, SafeUrl.Redact(null));
        Assert.Equal(string.Empty, SafeUrl.Redact("   "));
    }

    [Fact]
    public void DoesNotInventAPortThatWasNotThere()
    {
        // A redacted URL is read back by a person comparing it with what they typed.
        Assert.Equal("http://example.com/get.php?password=***", SafeUrl.Redact("http://example.com/get.php?password=x"));
        Assert.Equal("http://example.com:8080/get.php?password=***", SafeUrl.Redact("http://example.com:8080/get.php?password=x"));
    }

    [Fact]
    public void KeepsQueryPartsItDoesNotUnderstand()
    {
        Assert.Equal("http://example.com/x?flag&type=m3u_plus", SafeUrl.Redact("http://example.com/x?flag&type=m3u_plus"));
    }
}
