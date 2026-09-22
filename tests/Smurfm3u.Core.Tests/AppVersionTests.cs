using Smurfm3u.Core;

namespace Smurfm3u.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void KeepsAPlainVersionAsItIs()
    {
        Assert.Equal(("1.0.0", null), AppVersion.Split("1.0.0"));
    }

    [Fact]
    public void SplitsTheCommitOffAndShortensIt()
    {
        Assert.Equal(
            ("1.0.0", "34d3742"),
            AppVersion.Split("1.0.0+34d3742f1e9c4b6a8d2e5f7c9b1a3d5e7f9c1b3d"));
    }

    [Fact]
    public void KeepsAPrereleaseSuffixOnTheVersion()
    {
        Assert.Equal(("1.1.0-rc.1", "abc1234"), AppVersion.Split("1.1.0-rc.1+abc1234def"));
    }

    [Fact]
    public void CopesWithAShortOrEmptyCommit()
    {
        Assert.Equal(("1.0.0", "abc"), AppVersion.Split("1.0.0+abc"));
        Assert.Equal(("1.0.0", null), AppVersion.Split("1.0.0+"));
    }

    [Fact]
    public void FallsBackWhenTheAttributeIsMissing()
    {
        Assert.Equal(("0.0.0", null), AppVersion.Split(null));
        Assert.Equal(("0.0.0", null), AppVersion.Split("   "));
    }

    [Fact]
    public void ReportsTheVersionItWasBuiltWith()
    {
        // Guards the wiring, not the number: a missing Directory.Build.props would read 1.0.0
        // as well, but anything else means the assembly attribute is not being read at all.
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Display);
    }
}
