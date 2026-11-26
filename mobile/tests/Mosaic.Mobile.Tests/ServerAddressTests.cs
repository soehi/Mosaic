using Mosaic.Mobile.Core.Configuration;

namespace Mosaic.Mobile.Tests;

public sealed class ServerAddressTests
{
    [Fact]
    public void Https_is_required_for_non_loopback_hosts()
    {
        Assert.False(ServerAddress.TryNormalize("http://mosaic.example.com", out _));
        Assert.True(ServerAddress.TryNormalize("https://mosaic.example.com", out var configuration));
        Assert.Equal("https://mosaic.example.com", configuration!.NormalizedOrigin);
        Assert.False(configuration.IsLoopbackDevelopment);
    }

    [Fact]
    public void Loopback_http_is_allowed_for_development_only()
    {
        Assert.True(ServerAddress.TryNormalize("http://localhost:5080", out var loopback));
        Assert.True(loopback!.IsLoopbackDevelopment);
        Assert.True(ServerAddress.TryNormalize("http://127.0.0.1:5080", out _));
        Assert.False(ServerAddress.TryNormalize("http://10.0.2.2:5080", out _));
    }

    [Fact]
    public void Credentials_query_and_fragment_are_rejected()
    {
        Assert.False(ServerAddress.TryNormalize("https://user:pass@mosaic.example.com", out _));
        Assert.False(ServerAddress.TryNormalize("https://mosaic.example.com/?a=1", out _));
        Assert.False(ServerAddress.TryNormalize("https://mosaic.example.com/#frag", out _));
        Assert.False(ServerAddress.TryNormalize("not a url", out _));
        Assert.False(ServerAddress.TryNormalize("   ", out _));
    }

    [Fact]
    public void Path_and_port_are_normalized_without_trailing_slash()
    {
        Assert.True(ServerAddress.TryNormalize("https://mosaic.example.com/mosaic/", out var path));
        Assert.Equal("https://mosaic.example.com/mosaic", path!.NormalizedOrigin);
        Assert.Equal("https://mosaic.example.com/mosaic/", path.BaseUri.AbsoluteUri);

        Assert.True(ServerAddress.TryNormalize("https://mosaic.example.com:8443/", out var port));
        Assert.Equal("https://mosaic.example.com:8443", port!.NormalizedOrigin);
        Assert.Equal("https://mosaic.example.com:8443/", port.BaseUri.AbsoluteUri);
    }
}