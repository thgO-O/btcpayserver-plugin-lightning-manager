using BTCPayServer.Plugins.LightningWallet.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningWallet.Tests;

public class LightningCapabilityServiceTests
{
    private readonly LightningCapabilityService _service = new();

    [Fact]
    public void PhoenixdCapabilities_ArePayFocused()
    {
        var capabilities = _service.GetCapabilities(
            new PhoenixdLikeLightningClient(),
            "type=phoenixd;server=https://example.com;password=test",
            false);

        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
        Assert.False(capabilities.CanCloseChannel);
    }

    [Fact]
    public void BlinkUsdCapabilities_DisableInfoAndBalance()
    {
        var capabilities = _service.GetCapabilities(
            new BlinkLikeLightningClient(),
            "type=blink;server=https://api.blink.sv/graphql;api-key=test;currency=USD",
            false);

        Assert.False(capabilities.CanGetInfo);
        Assert.False(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
        Assert.False(capabilities.CanCloseChannel);
    }

    [Fact]
    public void ClightningCapabilities_AreFull()
    {
        var capabilities = _service.GetCapabilities(
            new FakeLightningClient(),
            "type=clightning;server=tcp://127.0.0.1:9735",
            false);

        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.True(capabilities.CanConnectPeer);
        Assert.True(capabilities.CanOpenChannel);
        Assert.True(capabilities.CanListChannels);
        Assert.True(capabilities.CanCloseChannel);
    }

    [Fact]
    public void LnbankCapabilities_DisableCloseChannel()
    {
        var capabilities = _service.GetCapabilities(
            new FakeLightningClient(),
            "type=lnbank;server=https://example.com/",
            false);

        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.True(capabilities.CanConnectPeer);
        Assert.True(capabilities.CanOpenChannel);
        Assert.True(capabilities.CanListChannels);
        Assert.False(capabilities.CanCloseChannel);
    }
}
