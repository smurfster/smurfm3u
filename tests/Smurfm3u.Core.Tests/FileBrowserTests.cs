using Smurfm3u.Core.Models;

namespace Smurfm3u.Core.Tests;

/// <summary>
/// Exercised against a real directory tree rather than a stub, because the whole job of this
/// class is talking to a filesystem.
/// </summary>
public sealed class FileBrowserTests : IDisposable
{
    private readonly string _root;

    public FileBrowserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "smurfm3u-browse-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(_root, "uk"));
        Directory.CreateDirectory(Path.Combine(_root, "Anime"));

        File.WriteAllText(Path.Combine(_root, "vod.m3u"), "#EXTM3U");
        File.WriteAllText(Path.Combine(_root, "Extra.M3U8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a playlist");
        File.WriteAllText(Path.Combine(_root, "uk", "inner.m3u"), "#EXTM3U");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static string[] Playlists => [".m3u", ".m3u8"];

    [Fact]
    public void ListsFoldersBeforeFiles()
    {
        var listing = FileBrowser.List(_root);

        var names = listing.Items.Select(x => x.Name).ToList();

        Assert.Equal(["Anime", "uk"], names.Take(2));
        Assert.All(listing.Items.Take(2), x => Assert.True(x.IsDirectory));
    }

    [Fact]
    public void FiltersFilesByExtensionIgnoringCase()
    {
        var listing = FileBrowser.List(_root, Playlists);

        var files = listing.Items.Where(x => !x.IsDirectory).Select(x => x.Name).ToList();

        // "Extra.M3U8" is upper case on disk and must still be offered.
        Assert.Equal(["Extra.M3U8", "vod.m3u"], files);
        Assert.DoesNotContain("notes.txt", files);
    }

    [Fact]
    public void ListsEveryFileWhenNoFilterIsGiven()
    {
        var listing = FileBrowser.List(_root);

        Assert.Contains("notes.txt", listing.Items.Select(x => x.Name));
    }

    [Fact]
    public void ListsNoFilesAtAllForAFolderOnlyPicker()
    {
        var listing = FileBrowser.List(_root, []);

        Assert.All(listing.Items, x => Assert.True(x.IsDirectory));
        Assert.Equal(2, listing.Items.Count);
    }

    [Fact]
    public void ReportsSizeAndFullPathForAFile()
    {
        var listing = FileBrowser.List(_root, Playlists);
        var file = listing.Items.Single(x => x.Name == "vod.m3u");

        Assert.Equal(Path.Combine(_root, "vod.m3u"), file.FullPath);
        Assert.Equal(7, file.SizeBytes);
        Assert.NotEqual(default, file.ModifiedAt);
    }

    [Fact]
    public void StepsIntoAndBackOutOfASubfolder()
    {
        var inner = FileBrowser.List(Path.Combine(_root, "uk"), Playlists);

        Assert.Equal("inner.m3u", inner.Items.Single().Name);
        Assert.Equal(_root, inner.Parent);

        var back = FileBrowser.List(inner.Parent, Playlists);
        Assert.Contains("uk", back.Items.Select(x => x.Name));
    }

    [Fact]
    public void ExplainsAFolderThatIsNotThere()
    {
        var listing = FileBrowser.List(Path.Combine(_root, "nope"));

        Assert.Empty(listing.Items);
        Assert.Contains("does not exist", listing.Error);
    }

    [Fact]
    public void IgnoresATrailingSeparator()
    {
        var listing = FileBrowser.List(_root + Path.DirectorySeparatorChar, Playlists);

        Assert.Null(listing.Error);
        Assert.Equal(_root, listing.Path);
    }

    [Fact]
    public void HasNoParentAtTheTopOfTheTree()
    {
        var top = Path.GetPathRoot(_root)!;

        Assert.Null(FileBrowser.List(top).Parent);
    }

    [Fact]
    public void OpensWhereAnExistingFolderAlreadyIs()
    {
        Assert.Equal(_root, FileBrowser.StartingFolderFor(_root, "/fallback"));
    }

    [Fact]
    public void OpensInTheFolderHoldingAnExistingFile()
    {
        Assert.Equal(_root, FileBrowser.StartingFolderFor(Path.Combine(_root, "vod.m3u"), "/fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackWhenThereIsNoValueToOpenAt(string? value)
    {
        Assert.Equal("/fallback", FileBrowser.StartingFolderFor(value, "/fallback"));
    }
}
