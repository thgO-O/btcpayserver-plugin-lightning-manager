using BTCPayServer.Plugins.LightningManager.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningCapabilityServiceTests
{
    [Theory]
    [InlineData("type=lnd-rest;server=https://127.0.0.1:8080")]
    [InlineData("type=lnd-grpc;server=https://127.0.0.1:10009")]
    [InlineData("type=clightning;server=tcp://127.0.0.1:9735")]
    [InlineData("server=http://127.0.0.1:8080;password=test;TYPE=ECLAIR")]
    public void FullNodeBackends_HaveFullCapabilities(string connectionString)
    {
        var capabilities = LightningCapabilityService.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.Full, capabilities);
        Assert.True(capabilities.HasAny);
        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.True(capabilities.CanPayAmountless);
        Assert.True(capabilities.CanSetMaxFee);
        Assert.True(capabilities.CanConnectPeer);
        Assert.True(capabilities.CanOpenChannel);
        Assert.True(capabilities.CanListChannels);
    }

    [Fact]
    public void Phoenixd_HasInfoBalanceAndPayWithoutMaxFeeOrChannelManagement()
    {
        var capabilities = LightningCapabilityService.GetCapabilities(
            "server=https://example.com;password=test;TYPE=PHOENIXD");

        Assert.Same(LightningCapabilities.Phoenixd, capabilities);
        Assert.True(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.True(capabilities.CanPayAmountless);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Fact]
    public void BlinkBitcoin_HasBalanceAndPayWithoutInfoOrMaxFee()
    {
        var capabilities = LightningCapabilityService.GetCapabilities(
            "currency=btc;api-key=test;TYPE=BLINK;server=https://api.blink.sv/graphql");

        Assert.Same(LightningCapabilities.BlinkBitcoin, capabilities);
        Assert.False(capabilities.CanGetInfo);
        Assert.True(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanPayAmountless);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Theory]
    [InlineData("type=blink;server=https://api.blink.sv/graphql;api-key=test")]
    [InlineData("CURRENCY=usd;type=blink;server=https://api.blink.sv/graphql;api-key=test")]
    [InlineData("type=blink;ln-address=user@blink.sv;api-key=test")]
    public void BlinkWithoutCurrencyOrUsd_HasPayOnlyWithoutMaxFee(string connectionString)
    {
        var capabilities = LightningCapabilityService.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.BlinkPayOnly, capabilities);
        Assert.False(capabilities.CanGetInfo);
        Assert.False(capabilities.CanGetBalance);
        Assert.True(capabilities.CanPayBolt11);
        Assert.False(capabilities.CanPayAmountless);
        Assert.False(capabilities.CanSetMaxFee);
        Assert.False(capabilities.CanConnectPeer);
        Assert.False(capabilities.CanOpenChannel);
        Assert.False(capabilities.CanListChannels);
    }

    [Theory]
    [InlineData("type=blink;ln-address=user@blink.sv")]
    [InlineData("TYPE=BLINK;USERNAME=user;currency=USD")]
    public void BlinkReceiveOnly_ReturnsNoCapabilities(string connectionString)
    {
        var capabilities = LightningCapabilityService.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    public void BlinkUnsupportedCurrency_ReturnsNoCapabilities(string currency)
    {
        var capabilities = LightningCapabilityService.GetCapabilities(
            $"type=blink;currency={currency};server=https://api.blink.sv/graphql;api-key=test");

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Theory]
    [InlineData("TYPE=CLIGHTNING;server=tcp://127.0.0.1:9735", LightningBackendTypes.CLightning)]
    [InlineData("type=LND-REST;server=https://example.test", LightningBackendTypes.LndRest)]
    [InlineData("type=PHOENIXD;server=https://example.test", LightningBackendTypes.Phoenixd)]
    public void BackendType_IsNormalized(string connectionString, string expected)
    {
        Assert.Equal(expected, LightningBackendTypes.TryGet(connectionString));
    }

    [Fact]
    public void AmountlessCapability_IsSubordinateToBolt11Payments()
    {
        var capabilities = new LightningCapabilities
        {
            CanPayAmountless = true
        };

        Assert.False(capabilities.CanPayAmountless);
    }

    [Fact]
    public void BackendFingerprint_UsesExactConnectionString()
    {
        var first = LightningBackendTypes.GetFingerprint(
            "type=lnd-rest;server=https://example.test");
        var second = LightningBackendTypes.GetFingerprint(
            "server=https://example.test;type=lnd-rest");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BackendIdentityFingerprint_NormalizesClnUnixSocket()
    {
        var first = LightningBackendTypes.GetIdentityFingerprint(
            "type=clightning;server=/tmp/lightning-rpc");
        var second = LightningBackendTypes.GetIdentityFingerprint(
            "type=clightning;server=unix:///tmp/lightning-rpc");

        Assert.Equal(first, second);
    }

    [Fact]
    public void BackendIdentityFingerprint_NormalizesBlinkConfiguration()
    {
        const string firstConnectionString =
            " TYPE=BLINK ; SERVER=https://EXAMPLE.test/graphql ; API-KEY=secret ; CURRENCY=btc ; allowinsecure=FALSE";
        const string secondConnectionString =
            "api-key=secret;currency=BTC;server=https://example.test/graphql;type=blink";

        Assert.NotEqual(
            LightningBackendTypes.GetFingerprint(firstConnectionString),
            LightningBackendTypes.GetFingerprint(secondConnectionString));
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(firstConnectionString),
            LightningBackendTypes.GetIdentityFingerprint(secondConnectionString));
    }

    [Fact]
    public void BackendIdentityFingerprint_RejectsUnknownBackend()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LightningBackendTypes.GetIdentityFingerprint(
                "type=future;server=/tmp/node;api-key=secret"));

        Assert.Equal("connectionString", exception.ParamName);
    }

    [Fact]
    public void BackendIdentityFingerprint_IgnoresSelfHostedCredentialsAndDefaults()
    {
        const string firstConnectionString =
            "type=lnd-rest;server=https://node.example/;macaroon=AABB;allowinsecure=false";
        const string secondConnectionString =
            "type=lnd-grpc;server=https://node.example;macaroon=CCDD";

        Assert.NotEqual(
            LightningBackendTypes.GetFingerprint(firstConnectionString),
            LightningBackendTypes.GetFingerprint(secondConnectionString));
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(firstConnectionString),
            LightningBackendTypes.GetIdentityFingerprint(secondConnectionString));

        const string firstBasicAuth =
            "type=lnd-rest;server=https://alice:secret-a@node.example/";
        const string secondBasicAuth =
            "type=lnd-rest;server=https://bob:secret-b@node.example/";
        Assert.NotEqual(
            LightningBackendTypes.GetFingerprint(firstBasicAuth),
            LightningBackendTypes.GetFingerprint(secondBasicAuth));
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(firstBasicAuth),
            LightningBackendTypes.GetIdentityFingerprint(secondBasicAuth));
    }

    [Fact]
    public void BackendIdentityFingerprint_DistinguishesSelfHostedEndpoints()
    {
        var first = LightningBackendTypes.GetIdentityFingerprint(
            "type=lnd-rest;server=https://node-a.example/;macaroon=AABB");
        var second = LightningBackendTypes.GetIdentityFingerprint(
            "type=lnd-rest;server=https://node-b.example/;macaroon=AABB");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BackendIdentityFingerprint_NormalizesClnTcpSocket()
    {
        const string firstConnectionString =
            "type=clightning;server=tcp://node.example:9735/";
        const string secondConnectionString =
            "type=clightning;server=tcp://alice@NODE.example:9735/ignored?token=test#fragment";

        Assert.NotEqual(
            LightningBackendTypes.GetFingerprint(firstConnectionString),
            LightningBackendTypes.GetFingerprint(secondConnectionString));
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(firstConnectionString),
            LightningBackendTypes.GetIdentityFingerprint(secondConnectionString));
    }

    [Theory]
    [InlineData(
        "type=lnd-rest;server=https://node.example/proxy;macaroon=AABB",
        "type=lnd-grpc;server=https://node.example/proxy/;macaroon=CCDD")]
    [InlineData(
        "type=eclair;server=https://node.example/proxy/one?token=a#fragment;password=a",
        "type=eclair;server=https://node.example/proxy/two?token=b;password=b")]
    [InlineData(
        "type=phoenixd;server=https://node.example/proxy/one?token=a#fragment;password=a",
        "type=phoenixd;server=https://node.example/proxy/two?token=b;password=b")]
    public void BackendIdentityFingerprint_NormalizesEffectiveHttpBase(
        string firstConnectionString,
        string secondConnectionString)
    {
        Assert.NotEqual(
            LightningBackendTypes.GetFingerprint(firstConnectionString),
            LightningBackendTypes.GetFingerprint(secondConnectionString));
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(firstConnectionString),
            LightningBackendTypes.GetIdentityFingerprint(secondConnectionString));
    }

    [Theory]
    [InlineData(
        "type=lnd-rest;server=https://node.example/proxy-a;macaroon=AABB",
        "type=lnd-rest;server=https://node.example/proxy-b;macaroon=AABB")]
    [InlineData(
        "type=eclair;server=https://node.example/proxy-a/;password=a",
        "type=eclair;server=https://node.example/proxy-b/;password=a")]
    [InlineData(
        "type=phoenixd;server=https://node.example/proxy-a/;password=a",
        "type=phoenixd;server=https://node.example/proxy-b/;password=a")]
    public void BackendIdentityFingerprint_PreservesSignificantHttpPath(
        string firstConnectionString,
        string secondConnectionString)
    {
        Assert.NotEqual(
            LightningBackendTypes.GetIdentityFingerprint(firstConnectionString),
            LightningBackendTypes.GetIdentityFingerprint(secondConnectionString));
    }

    [Fact]
    public void BackendFingerprint_ChangesWithBackendConfigurationWithoutExposingIt()
    {
        const string connectionString =
            "type=blink;server=https://api.example.test/graphql;api-key=secret-a;currency=BTC";
        var first = LightningBackendTypes.GetFingerprint(connectionString);
        var second = LightningBackendTypes.GetFingerprint(
            "type=blink;server=https://api.example.test/graphql;api-key=secret-b;currency=BTC");

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            LightningBackendTypes.GetIdentityFingerprint(connectionString),
            LightningBackendTypes.GetIdentityFingerprint(
                "type=blink;server=https://api.example.test/graphql;api-key=secret-b;currency=BTC"));
        Assert.DoesNotContain("api.example.test", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-a", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LndHub_ReturnsNoCapabilities()
    {
        var capabilities = LightningCapabilityService.GetCapabilities(
            "type=lndhub;server=https://example.com/");

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }

    [Theory]
    [InlineData(null)]
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
        var capabilities = LightningCapabilityService.GetCapabilities(connectionString);

        Assert.Same(LightningCapabilities.None, capabilities);
        Assert.False(capabilities.HasAny);
    }
}
