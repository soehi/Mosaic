using Mosaic.Mobile.Core.Configuration;

namespace Mosaic.Mobile.Tests;

public sealed class PairingLinkTests
{
    private const string Host = "mosaic.bansonic.top";
    private const string Reference = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Accepts_the_bare_reference_from_the_desktop_qr()
    {
        Assert.True(PairingLinkParser.TryParse(Reference, Host, out var link));
        Assert.Equal(Reference, link.Reference);
        Assert.Equal("https://" + Host, link.Origin.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("/device/mobile/connect", link.ConnectUri.AbsolutePath);
        Assert.Equal("#" + Reference, link.ConnectUri.Fragment);
    }

    [Fact]
    public void Accepts_the_full_app_link_and_ignores_the_fragment_on_the_wire()
    {
        var url = "https://" + Host + "/device/mobile/connect#" + Reference;
        Assert.True(PairingLinkParser.TryParse(url, Host, out var link));
        Assert.Equal(Reference, link.Reference);
        Assert.Equal(string.Empty, link.ConnectUri.Query);
    }

    [Theory]
    [InlineData("http://mosaic.bansonic.top/device/mobile/connect#" + Reference)]
    [InlineData("https://evil.example/device/mobile/connect#" + Reference)]
    [InlineData("https://mosaic.bansonic.top/device/mobile.apk#" + Reference)]
    [InlineData("https://mosaic.bansonic.top:8443/device/mobile/connect#" + Reference)]
    [InlineData("https://user@mosaic.bansonic.top/device/mobile/connect#" + Reference)]
    [InlineData("https://mosaic.bansonic.top/device/mobile/connect?x=1#" + Reference)]
    [InlineData("short")]
    public void Rejects_links_that_could_send_a_reference_to_the_wrong_place(string input)
    {
        Assert.False(PairingLinkParser.TryParse(input, Host, out _));
    }

    [Fact]
    public void Rejects_a_reference_with_characters_outside_the_url_safe_alphabet()
    {
        var invalid = new string('A', PairingLinkParser.ReferenceLength - 1) + "@";
        Assert.False(PairingLinkParser.TryParse(invalid, Host, out _));
    }
}
