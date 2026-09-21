using Smurfm3u.Core.Parsing;
using Xunit;

namespace Smurfm3u.Core.Tests;

public class ReleaseNameBuilderTests
{
    [Fact]
    public void Builds_a_scene_style_episode_name()
    {
        var parsed = ReleaseTitleParser.Parse("Top Gear (2002) S01 E03");
        var name = ReleaseNameBuilder.Build(parsed, "WEB-DL", "1080p", "Smurfm3u");

        Assert.Equal("Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u", name);
    }

    [Fact]
    public void Builds_a_scene_style_movie_name()
    {
        var parsed = ReleaseTitleParser.Parse("Pacific Rim - 2013");
        var name = ReleaseNameBuilder.Build(parsed, "WEB-DL", "1080p", "Smurfm3u");

        Assert.Equal("Pacific.Rim.2013.1080p.WEB-DL-Smurfm3u", name);
    }

    [Fact]
    public void Omits_optional_tags_when_they_are_blank()
    {
        var parsed = ReleaseTitleParser.Parse("Pacific Rim - 2013");
        Assert.Equal("Pacific.Rim.2013.WEB-DL", ReleaseNameBuilder.Build(parsed, "WEB-DL", "", ""));
    }

    [Fact]
    public void Drops_apostrophes_without_splitting_the_word()
    {
        var parsed = ReleaseTitleParser.Parse("Marvel's Agents of SHIELD S01E01");
        var name = ReleaseNameBuilder.Build(parsed, "WEB-DL", null, null);

        Assert.Equal("Marvels.Agents.of.SHIELD.S01E01.WEB-DL", name);
    }
}
