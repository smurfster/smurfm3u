using Smurfm3u.Core.Options;

namespace Smurfm3u.Core.Tests;

public class ProxySettingsTests
{
    private static ProxySettings Proxy(string host, int port = 1080, ProxyKind kind = ProxyKind.Socks5) =>
        new() { Enabled = true, Kind = kind, Host = host, Port = port };

    [Theory]
    [InlineData(ProxyKind.Http, "http://10.0.0.5:8080/")]
    [InlineData(ProxyKind.Socks4, "socks4://10.0.0.5:8080/")]
    [InlineData(ProxyKind.Socks4a, "socks4a://10.0.0.5:8080/")]
    [InlineData(ProxyKind.Socks5, "socks5://10.0.0.5:8080/")]
    public void UsesTheSchemeForItsKind(ProxyKind kind, string expected)
    {
        Assert.Equal(expected, Proxy("10.0.0.5", 8080, kind).BuildAddress()?.ToString());
    }

    [Fact]
    public void HasNoAddressUntilAHostIsFilledIn()
    {
        Assert.Null(Proxy(string.Empty).BuildAddress());
        Assert.Null(Proxy("   ").BuildAddress());
        Assert.False(Proxy(string.Empty).IsUsable);
    }

    [Fact]
    public void IsOnlyUsableWhenSwitchedOn()
    {
        var proxy = Proxy("10.0.0.5");
        Assert.True(proxy.IsUsable);

        proxy.Enabled = false;
        Assert.False(proxy.IsUsable);

        // Still buildable, because the test button works on a proxy that is not on yet.
        Assert.NotNull(proxy.BuildAddress());
    }

    [Fact]
    public void RejectsAPortOutsideTheValidRange()
    {
        Assert.Null(Proxy("10.0.0.5", 0).BuildAddress());
        Assert.Null(Proxy("10.0.0.5", 70000).BuildAddress());
    }

    [Theory]
    [InlineData("socks5://proxy.example.com")]
    [InlineData("http://proxy.example.com/")]
    [InlineData("proxy.example.com/some/path")]
    public void PicksAPastedUrlApartAndKeepsItsOwnScheme(string host)
    {
        Assert.Equal("socks5://proxy.example.com:1080/", Proxy(host).BuildAddress()?.ToString());
    }

    [Fact]
    public void APortTypedIntoTheHostWins()
    {
        Assert.Equal("socks5://proxy.example.com:9050/", Proxy("proxy.example.com:9050", 1080).BuildAddress()?.ToString());
        Assert.Equal("socks5://proxy.example.com:9050/", Proxy("socks5://proxy.example.com:9050/", 1080).BuildAddress()?.ToString());
    }

    [Fact]
    public void BracketsABareIpv6Literal()
    {
        Assert.Equal("socks5://[fd00::1]:1080/", Proxy("fd00::1").BuildAddress()?.ToString());
        Assert.Equal("socks5://[fd00::1]:9050/", Proxy("[fd00::1]:9050").BuildAddress()?.ToString());
    }

    [Fact]
    public void BypassesNothingWhenTheListIsEmpty()
    {
        var proxy = Proxy("10.0.0.5");

        Assert.False(proxy.IsBypassed("provider.example.com"));
        Assert.False(proxy.IsBypassed(null));
    }

    [Theory]
    [InlineData("nas.local", true)]
    [InlineData("NAS.LOCAL", true)]
    [InlineData("nas.local.example.com", false)]
    [InlineData("provider.example.com", false)]
    public void MatchesABypassPatternOnWholeHostsOnly(string host, bool expected)
    {
        var proxy = Proxy("10.0.0.5");
        proxy.BypassHosts = "nas.local";

        Assert.Equal(expected, proxy.IsBypassed(host));
    }

    [Theory]
    [InlineData("192.168.1.4", true)]
    [InlineData("192.168.50.200", true)]
    [InlineData("192.169.1.4", false)]
    [InlineData("10.192.168.1", false)]
    public void AnchorsAWildcardAtTheStart(string host, bool expected)
    {
        var proxy = Proxy("10.0.0.5");
        proxy.BypassHosts = "192.168.*";

        Assert.Equal(expected, proxy.IsBypassed(host));
    }

    [Theory]
    [InlineData("cdn.example.com", true)]
    [InlineData("a.b.example.com", true)]
    [InlineData("example.com.evil.net", false)]
    public void AnchorsAWildcardAtTheEnd(string host, bool expected)
    {
        var proxy = Proxy("10.0.0.5");
        proxy.BypassHosts = "*.example.com";

        Assert.Equal(expected, proxy.IsBypassed(host));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("cdn.example.com", true)]
    [InlineData("notexample.com", false)]
    public void ALeadingDotCoversTheDomainItselfAsWell(string host, bool expected)
    {
        var proxy = Proxy("10.0.0.5");
        proxy.BypassHosts = ".example.com";

        Assert.Equal(expected, proxy.IsBypassed(host));
    }

    [Fact]
    public void SplitsTheBypassListOnCommasAndNewlinesAlike()
    {
        var proxy = Proxy("10.0.0.5");
        proxy.BypassHosts = "nas.local, 192.168.*\nlocalhost;  \n";

        Assert.Equal(["nas.local", "192.168.*", "localhost"], proxy.BypassPatterns());
        Assert.True(proxy.IsBypassed("localhost"));
        Assert.True(proxy.IsBypassed("192.168.1.9"));
    }

    [Fact]
    public void OnlyCountsCredentialsWhenThereIsAUsername()
    {
        var proxy = Proxy("10.0.0.5");
        Assert.False(proxy.HasCredentials);

        proxy.Password = "secret";
        Assert.False(proxy.HasCredentials);

        proxy.Username = "me";
        Assert.True(proxy.HasCredentials);
    }
}
