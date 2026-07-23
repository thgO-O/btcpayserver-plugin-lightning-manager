using BTCPayServer.Plugins.LightningManager.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningCapabilityServiceTests
{
    private readonly LightningCapabilityService _service = new();

    [Theory]
    [InlineData("type=lnd-rest;server=https://127.0.0.1:8080")]
    [InlineData("type=lnd-grpc;server=https://127.0.0.1:10009")]
    [InlineData("type=clightning;server=tcp://127.0.0.1:9735")]
    [InlineData("server=http://127.0.0.1:8080;password=test;TYPE=ECLAIR")]
    public void FullNodeBackends_HaveFullCapabilities(string connectionString)
    {
        var capabilities = _service.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.Full, capabilities);
        Assert.True(capabilities.HasAny);
        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.True(capabilities.CanSetMaxFee);
        Assert.True(capabilities.CanConnectPeer);
        Assert.True(capabilities.CanOpenChannel);
        Assert.True(capabilities.CanListChannels);
    }

    [Fact]
    public void Phoenixd_HasInfoBalanceAndPayWithoutMaxFeeOrChannelManagement()
    {
        var capabilities = _service.GetCapabilities(
            "server=https://example.com;password=test;TYPE=PHOENIXD");

        Assert.Same(LightningCapabilities.Phoenixd, capabilities);
        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Fact]
    public void BlinkBitcoin_HasBalanceAndPayWithoutInfoOrMaxFee()
    {
        var capabilities = _service.GetCapabilities(
            "currency=btc;api-key=test;TYPE=BLINK;server=https://api.blink.sv/graphql");

        Assert.Same(LightningCapabilities.BlinkBitcoin, capabilities);
        Assert.False(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Theory]
    [InlineData("type=blink;server=https://api.blink.sv/graphql;api-key=test")]
    [InlineData("CURRENCY=usd;type=blink;server=https://api.blink.sv/graphql;api-key=test")]
    public void BlinkWithoutCurrencyOrUsd_HasPayOnlyWithoutMaxFee(string connectionString)
    {
        var capabilities = _service.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.BlinkPayOnly, capabilities);
        Assert.False(capabilities.CanGetInfo);
        Assert.False(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Fact]
    public void BlinkUnknownCurrency_ReturnsNoCapabilities()
    {
        var capabilities = _service.GetCapabilities(
            "type=blink;currency=EUR;server=https://api.blink.sv/graphql;api-key=test");

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Fact]
    public void BlinkEmptyCurrency_ReturnsNoCapabilities()
    {
        var capabilities = _service.GetCapabilities(
            "type=blink;currency=;server=https://api.blink.sv/graphql;api-key=test");

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Theory]
    [InlineData("type=deprecated;server=https://example.com/")]
    [InlineData("type=breez;server=https://example.com/")]
    [InlineData("type=charge;server=https://example.com/")]
    [InlineData("type=micro;server=https://example.com/")]
    [InlineData("type=nwc;server=https://example.com/")]
    [InlineData("type=lndhub;server=https://example.com/")]
    public void UnsupportedBackendTypes_ReturnNoCapabilities(string connectionString)
    {
        var capabilities = _service.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("server=https://example.com/")]
    [InlineData("type")]
    [InlineData("type=")]
    [InlineData("type=lnd-rest;type=eclair;server=https://example.com/")]
    [InlineData("type=lnd-rest;broken;server=https://example.com/")]
    [InlineData("=broken;type=lnd-rest;server=https://example.com/")]
    [InlineData("xtype=lnd-rest;server=https://example.com/")]
    [InlineData("tcp://127.0.0.1:9735")]
    public void MissingMalformedOrLegacyType_ReturnsNoCapabilities(string? connectionString)
    {
        var capabilities = _service.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }
}
