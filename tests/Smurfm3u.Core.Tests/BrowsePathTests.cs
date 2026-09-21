using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

public class BrowsePathTests
{
    /// <summary>A rooted path the tests can build on, whichever platform they run on.</summary>
    private static readonly string Root =
        Path.GetFullPath(OperatingSystem.IsWindows() ? @"C:\playlists" : "/playlists");

    private static string Under(params string[] parts) =>
        Path.GetFullPath(Path.Combine([Root, .. parts]));

    [Fact]
    public void ResolvesTheRootItself()
    {
        Assert.Equal(Root, BrowsePath.Resolve(Root, null));
        Assert.Equal(Root, BrowsePath.Resolve(Root, ""));
        Assert.Equal(Root, BrowsePath.Resolve(Root, "   "));
    }

    [Fact]
    public void ResolvesAFolderInside()
    {
        Assert.Equal(Under("uk"), BrowsePath.Resolve(Root, "uk"));
        Assert.Equal(Under("uk", "vod"), BrowsePath.Resolve(Root, "uk/vod"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("../etc")]
    [InlineData("uk/../..")]
    [InlineData("uk/../../etc/passwd")]
    [InlineData("./../../root")]
    public void RefusesToClimbOutOfTheRoot(string relative)
    {
        Assert.Null(BrowsePath.Resolve(Root, relative));
    }

    [Fact]
    public void RefusesASiblingThatMerelySharesThePrefix()
    {
        // "/playlists-other" starts with "/playlists" as text but is a different folder.
        var sibling = Root + "-other";

        Assert.Null(BrowsePath.Resolve(Root, sibling));
    }

    [Fact]
    public void RefusesARootedPathThatWouldReplaceTheRoot()
    {
        var elsewhere = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc";

        Assert.Null(BrowsePath.Resolve(Root, elsewhere));
    }

    [Fact]
    public void RefusesAnEmptyRoot()
    {
        Assert.Null(BrowsePath.Resolve("", "anything"));
        Assert.Null(BrowsePath.Resolve("   ", "anything"));
    }

    [Fact]
    public void DescribesAPathRelativeToTheRoot()
    {
        Assert.Equal(string.Empty, BrowsePath.RelativeTo(Root, Root));
        Assert.Equal("uk", BrowsePath.RelativeTo(Root, Under("uk")));
        Assert.Equal("uk/vod", BrowsePath.RelativeTo(Root, Under("uk", "vod")));
    }

    [Fact]
    public void WalksBackUpToTheRootAndNoFurther()
    {
        Assert.Equal("uk", BrowsePath.ParentOf(Root, Under("uk", "vod")));
        Assert.Equal(string.Empty, BrowsePath.ParentOf(Root, Under("uk")));
        Assert.Null(BrowsePath.ParentOf(Root, Root));
    }

    [Fact]
    public void IgnoresATrailingSeparatorOnTheRoot()
    {
        var withSlash = Root + Path.DirectorySeparatorChar;

        Assert.Equal(Under("uk"), BrowsePath.Resolve(withSlash, "uk"));
        Assert.Null(BrowsePath.Resolve(withSlash, ".."));
    }
}
