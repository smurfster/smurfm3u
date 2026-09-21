using Smurfm3u.Core.Options;

namespace Smurfm3u.Core.Tests;

public class PathMapperTests
{
    private static PathMapping[] Map(params (string From, string To)[] pairs) =>
        pairs.Select(p => new PathMapping { From = p.From, To = p.To }).ToArray();

    [Fact]
    public void LeavesThePathAloneWhenThereAreNoMappings()
    {
        Assert.Equal("/downloads/complete/tv", PathMapper.Apply("/downloads/complete/tv", []));
        Assert.Equal("/downloads/complete/tv", PathMapper.Apply("/downloads/complete/tv", null));
    }

    [Fact]
    public void RewritesTheMatchingPrefixAndKeepsTheRest()
    {
        var mappings = Map(("/downloads/complete", "/mnt/smurfm3u/complete"));

        Assert.Equal(
            "/mnt/smurfm3u/complete/tv/Top.Gear.S01E03",
            PathMapper.Apply("/downloads/complete/tv/Top.Gear.S01E03", mappings));
    }

    [Fact]
    public void MapsTheMappedDirectoryItself()
    {
        var mappings = Map(("/downloads/complete", "/mnt/smurfm3u/complete"));

        Assert.Equal("/mnt/smurfm3u/complete", PathMapper.Apply("/downloads/complete", mappings));
    }

    [Fact]
    public void IgnoresTrailingSeparatorsOnEitherSide()
    {
        var mappings = Map(("/downloads/complete/", "/mnt/smurfm3u/complete/"));

        Assert.Equal("/mnt/smurfm3u/complete/tv", PathMapper.Apply("/downloads/complete/tv", mappings));
    }

    [Fact]
    public void OnlyMatchesOnAWholeFolderName()
    {
        var mappings = Map(("/downloads", "/mnt/media"));

        // "/downloads-old" starts with "/downloads" as text but is a different folder.
        Assert.Equal("/downloads-old/tv", PathMapper.Apply("/downloads-old/tv", mappings));
    }

    [Fact]
    public void PrefersTheLongestMatchingMapping()
    {
        var mappings = Map(
            ("/downloads", "/mnt/everything"),
            ("/downloads/complete", "/mnt/imports"));

        Assert.Equal("/mnt/imports/tv", PathMapper.Apply("/downloads/complete/tv", mappings));
        Assert.Equal("/mnt/everything/incomplete", PathMapper.Apply("/downloads/incomplete", mappings));
    }

    [Fact]
    public void FollowsTheSeparatorStyleOfTheTarget()
    {
        var mappings = Map((@"/downloads/complete", @"D:\media\complete"));

        Assert.Equal(
            @"D:\media\complete\tv\Top.Gear.S01E03",
            PathMapper.Apply("/downloads/complete/tv/Top.Gear.S01E03", mappings));
    }

    [Fact]
    public void MapsAWindowsSourceOntoAUnixTarget()
    {
        var mappings = Map((@"D:\media\complete", "/mnt/media/complete"));

        Assert.Equal(
            "/mnt/media/complete/tv/Show",
            PathMapper.Apply(@"D:\media\complete\tv\Show", mappings));
    }

    [Fact]
    public void SkipsMappingsMissingEitherSide()
    {
        var mappings = Map(
            ("/downloads/complete", "   "),
            ("", "/mnt/nowhere"),
            ("/downloads", "/mnt/media"));

        Assert.Equal("/mnt/media/complete/tv", PathMapper.Apply("/downloads/complete/tv", mappings));
    }

    [Fact]
    public void LeavesAPathWithNoMatchingMappingAlone()
    {
        var mappings = Map(("/downloads/complete", "/mnt/smurfm3u/complete"));

        Assert.Equal("/var/log/other", PathMapper.Apply("/var/log/other", mappings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HandlesAMissingPath(string? path)
    {
        var mappings = Map(("/downloads", "/mnt/media"));

        Assert.Equal(path ?? string.Empty, PathMapper.Apply(path, mappings));
    }
}
