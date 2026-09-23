using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class NzbDocumentTests
{
    [Fact]
    public void RoundTripsASingleEntry()
    {
        var xml = NzbDocument.Build(12345, "Top.Gear.S01E03.1080p.WEB-DL", 2_000_000_000);

        Assert.True(NzbDocument.TryParse(xml, out var payload));
        Assert.Equal([12345L], payload.ItemIds);
        Assert.Equal("Top.Gear.S01E03.1080p.WEB-DL", payload.ReleaseName);
    }

    [Fact]
    public void RoundTripsAWholeSeasonInEpisodeOrder()
    {
        var xml = NzbDocument.Build(
            [11, 22, 33],
            ["Show.S01E01", "Show.S01E02", "Show.S01E03"],
            "Show.S01.1080p.WEB-DL",
            3_000_000_000);

        Assert.True(NzbDocument.TryParse(xml, out var payload));

        // Order matters: it is the order the episodes are transferred in.
        Assert.Equal([11L, 22L, 33L], payload.ItemIds);
        Assert.Equal("Show.S01.1080p.WEB-DL", payload.ReleaseName);
    }

    [Fact]
    public void GivesEachEpisodeItsOwnFileNamedAfterIt()
    {
        // Sonarr reads the finished folder file by file, so each one carries the episode's
        // own release name rather than the pack's.
        var xml = NzbDocument.Build([11, 22], ["Show.S01E01", "Show.S01E02"], "Show.S01", 1000);

        var subjects = System.Xml.Linq.XDocument.Parse(xml)
            .Descendants()
            .Where(e => e.Name.LocalName == "file")
            .Select(e => (string?)e.Attribute("subject"))
            .ToList();

        Assert.Equal(["\"Show.S01E01\" yEnc (1/1)", "\"Show.S01E02\" yEnc (1/1)"], subjects);
    }

    [Fact]
    public void SplitsTheDeclaredSizeAcrossTheFiles()
    {
        var xml = NzbDocument.Build([1, 2, 3, 4], ["a", "b", "c", "d"], "Pack", 4000);

        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(xml, "bytes=\"1000\"").Count);
    }

    [Fact]
    public void StillReadsADocumentWhoseHeadWasStripped()
    {
        // The ids live in the segments too, which is what an older document carried and what
        // a client that reserializes the file leaves behind.
        var xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="&quot;A.Release&quot; yEnc (1/1)">
                <segments><segment bytes="1" number="1">smurfm3u-777</segment></segments>
              </file>
            </nzb>
            """;

        Assert.True(NzbDocument.TryParse(xml, out var payload));
        Assert.Equal([777L], payload.ItemIds);
        Assert.Null(payload.ReleaseName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    public void RefusesWhatIsNotADocument(string xml)
    {
        Assert.False(NzbDocument.TryParse(xml, out _));
    }

    [Fact]
    public void RefusesARealUsenetNzb()
    {
        var xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file poster="someone@example.com" subject="Something yEnc (1/2)">
                <groups><group>alt.binaries.test</group></groups>
                <segments><segment bytes="100" number="1">abc123@news</segment></segments>
              </file>
            </nzb>
            """;

        Assert.False(NzbDocument.TryParse(xml, out _));
    }
}
