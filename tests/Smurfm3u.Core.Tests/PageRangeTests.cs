using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    [InlineData(1204, 50, 25)]
    public void CountsPagesWithoutLosingTheLastPartialOne(int total, int size, int expected)
    {
        Assert.Equal(expected, new PageRange(1, size, total).PageCount);
    }

    [Fact]
    public void SkipsTheRowsBeforeThePage()
    {
        Assert.Equal(0, new PageRange(1, 50, 500).Skip);
        Assert.Equal(50, new PageRange(2, 50, 500).Skip);
        Assert.Equal(450, new PageRange(10, 50, 500).Skip);
    }

    [Fact]
    public void LabelsTheRangeOneBased()
    {
        Assert.Equal("1-50 of 1,204", new PageRange(1, 50, 1204).Label);
        Assert.Equal("51-100 of 1,204", new PageRange(2, 50, 1204).Label);
    }

    [Fact]
    public void StopsTheLastPageAtTheFinalRow()
    {
        var range = new PageRange(25, 50, 1204);

        Assert.Equal(1201, range.First);
        Assert.Equal(1204, range.Last);
        Assert.Equal("1,201-1,204 of 1,204", range.Label);
    }

    [Fact]
    public void ShowsNothingWhenThereIsNothing()
    {
        var range = new PageRange(1, 50, 0);

        Assert.Equal(0, range.First);
        Assert.Equal(0, range.Last);
        Assert.Equal("Nothing to show", range.Label);
        Assert.False(range.HasPrevious);
        Assert.False(range.HasNext);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 25)]
    public void PullsAnOutOfRangePageBackInside(int requested, int expected)
    {
        Assert.Equal(expected, new PageRange(requested, 50, 1204).Clamped);
    }

    [Fact]
    public void KnowsWhichWaysItCanStep()
    {
        Assert.False(new PageRange(1, 50, 120).HasPrevious);
        Assert.True(new PageRange(1, 50, 120).HasNext);

        Assert.True(new PageRange(2, 50, 120).HasPrevious);
        Assert.True(new PageRange(2, 50, 120).HasNext);

        Assert.True(new PageRange(3, 50, 120).HasPrevious);
        Assert.False(new PageRange(3, 50, 120).HasNext);
    }

    [Fact]
    public void SurvivesANonsensePageSize()
    {
        var range = new PageRange(1, 0, 10);

        Assert.Equal(10, range.PageCount);
        Assert.Equal(0, range.Skip);
        Assert.Equal(1, range.Last);
    }

    [Fact]
    public void ASinglePageOfExactlyOneFullPageHasNoNext()
    {
        var range = new PageRange(1, 50, 50);

        Assert.Equal(1, range.PageCount);
        Assert.False(range.HasNext);
        Assert.Equal("1-50 of 50", range.Label);
    }
}
