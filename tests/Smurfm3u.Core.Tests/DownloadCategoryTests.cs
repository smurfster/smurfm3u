using Smurfm3u.Core.Options;

namespace Smurfm3u.Core.Tests;

public class DownloadCategoryTests
{
    private static ServiceSettings Settings(params DownloadCategory[] categories)
    {
        var settings = new ServiceSettings { Categories = [.. categories] };
        DownloadCategories.Normalise(settings);
        return settings;
    }

    private static DownloadCategory Category(string name, int priority = SabPriority.Default, string folder = "") =>
        new() { Name = name, Priority = priority, Folder = folder };

    [Fact]
    public void BuildsTheListFromTheTvAndMovieCategoriesWhenNoneWasSaved()
    {
        var settings = new ServiceSettings { TvCategory = "sonarr", MovieCategory = "radarr" };

        DownloadCategories.Normalise(settings);

        Assert.Equal(["*", "sonarr", "radarr", "prowlarr"], settings.Categories.Select(x => x.Name));
        Assert.Equal(SabPriority.Normal, settings.Categories[0].Priority);
        Assert.Equal("sonarr", settings.TvCategory);
    }

    [Fact]
    public void AFreshInstallListsTheCategoryProwlarrAsksFor()
    {
        var settings = new ServiceSettings();

        DownloadCategories.Normalise(settings);

        Assert.Equal(["*", "tv", "movies", "prowlarr"], settings.Categories.Select(x => x.Name));
    }

    [Fact]
    public void ASavedListIsNotGivenTheProwlarrCategoryBack()
    {
        var settings = Settings(Category("tv"));

        Assert.Equal(["*", "tv"], settings.Categories.Select(x => x.Name));
    }

    [Fact]
    public void PutsTheDefaultCategoryFirstAndDropsBlankAndRepeatedNames()
    {
        var settings = Settings(
            Category(" TV "), Category(""), Category("*", SabPriority.High, "incoming"), Category("tv"));

        Assert.Equal(["*", "tv"], settings.Categories.Select(x => x.Name));
        Assert.Equal(SabPriority.High, settings.Categories[0].Priority);
        Assert.Equal("incoming", settings.Categories[0].Folder);
    }

    [Fact]
    public void TheDefaultCategoryCannotDeferToItself()
    {
        var settings = Settings(Category("*", SabPriority.Default));

        Assert.Equal(SabPriority.Normal, DownloadCategories.Fallback(settings).Priority);
    }

    [Fact]
    public void SearchPagePicksNamingNoCategoryFallBackToDefault()
    {
        var settings = new ServiceSettings
        {
            Categories = [Category("tv")],
            TvCategory = "TV",
            MovieCategory = "films"
        };

        DownloadCategories.Normalise(settings);

        Assert.Equal("tv", settings.TvCategory);
        Assert.Equal("*", settings.MovieCategory);
    }

    [Fact]
    public void ResolvesNamesIgnoringCaseAndSendsUnknownOnesToDefault()
    {
        var settings = Settings(Category("tv"));

        Assert.Equal("tv", DownloadCategories.Resolve(settings, "TV").Name);
        Assert.True(DownloadCategories.Resolve(settings, "anime").IsDefault);
        Assert.True(DownloadCategories.Resolve(settings, null).IsDefault);
    }

    [Theory]
    [InlineData(SabPriority.High, SabPriority.Default, SabPriority.High)]
    [InlineData(SabPriority.Default, SabPriority.Default, SabPriority.Low)]
    [InlineData(SabPriority.High, null, SabPriority.High)]
    [InlineData(SabPriority.Default, SabPriority.Force, SabPriority.Force)]
    [InlineData(SabPriority.High, 7, SabPriority.Force)]
    [InlineData(SabPriority.High, SabPriority.Paused, SabPriority.Low)]
    public void AGrabAskingForDefaultTakesItsCategorysPriority(int categoryPriority, int? requested, int expected)
    {
        var settings = Settings(Category("*", SabPriority.Low), Category("tv", categoryPriority));

        var actual = DownloadCategories.EffectivePriority(
            settings, DownloadCategories.Resolve(settings, "tv"), requested);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ABlankFolderIsNamedAfterTheCategoryOrIsTheCompleteDirectoryForDefault()
    {
        var complete = Path.Combine("downloads", "complete");

        Assert.Equal(Path.Combine(complete, "tv"), DownloadCategories.OutputFolder(Category("tv"), complete));
        Assert.Equal(complete, DownloadCategories.OutputFolder(Category("*"), complete));
    }

    [Fact]
    public void ARelativeFolderNestsUnderTheCompleteDirectoryAndCannotClimbOutOfIt()
    {
        var complete = Path.Combine("downloads", "complete");

        Assert.Equal(
            Path.Combine(complete, "media", "tv"),
            DownloadCategories.OutputFolder(Category("tv", folder: "media/tv"), complete));
        Assert.Equal(
            Path.Combine(complete, "tv"),
            DownloadCategories.OutputFolder(Category("tv", folder: "../../tv"), complete));
    }

    [Fact]
    public void AnAbsoluteFolderIsUsedAsItIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "anime");

        Assert.Equal(absolute, DownloadCategories.OutputFolder(Category("anime", folder: absolute), "/downloads/complete"));
    }

    [Fact]
    public void ReportsRelativeFoldersAsTheyAreAndAbsoluteOnesThroughTheMappings()
    {
        PathMapping[] mappings = [new() { From = "/media", To = "/mnt/media" }];

        Assert.Equal("tv", DownloadCategories.ReportedFolder(Category("tv"), mappings));
        Assert.Equal(string.Empty, DownloadCategories.ReportedFolder(Category("*"), mappings));
        Assert.Equal("media/tv", DownloadCategories.ReportedFolder(Category("tv", folder: "media/tv"), mappings));

        if (Path.IsPathRooted("/media/anime"))
            Assert.Equal("/mnt/media/anime",
                DownloadCategories.ReportedFolder(Category("anime", folder: "/media/anime"), mappings));
    }
}
