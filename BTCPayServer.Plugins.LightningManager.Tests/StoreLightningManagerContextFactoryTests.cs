using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class StoreLightningManagerContextFactoryTests
{
    [Fact]
    public async Task CreateAsync_WithSupportedExternalBackend_CreatesClientAtMinimumHostBaseline()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = "type=clightning;server=tcp://127.0.0.1:30993/"
        };
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = new StoreLightningManagerContextFactory(
            new BTCPayNetworkProvider(
                [TestNetworkFactory.GetBitcoinNetwork()],
                new NBXplorerNetworkProvider(ChainName.Regtest),
                new Logs()),
            handlers,
            new LightningClientFactoryService(
                new FakeHttpClientFactory(),
                [_ => new CLightningConnectionStringHandler()],
                []),
            new LightningCapabilityService());

        var context = await factory.CreateAsync(store, "btc");

        Assert.True(context.IsConfigured);
        Assert.NotNull(context.Client);
        Assert.Equal("BTC", context.CryptoCode);
        Assert.Equal(config.ConnectionString, context.ConnectionString);
        Assert.Same(LightningCapabilities.Full, context.Capabilities);
        Assert.Null(context.ConfigurationError);
    }

    [Fact]
    public async Task CreateAsync_WithInternalNodeConfig_ReturnsUnavailableExternalOnlyContext()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = new StoreLightningManagerContextFactory(
            new BTCPayNetworkProvider(
                [TestNetworkFactory.GetBitcoinNetwork()],
                new NBXplorerNetworkProvider(ChainName.Regtest),
                new Logs()),
            handlers,
            new LightningClientFactoryService(
                new FakeHttpClientFactory(),
                [],
                []),
            new LightningCapabilityService());

        var context = await factory.CreateAsync(store, "BTC");

        Assert.False(context.IsConfigured);
        Assert.Null(context.Client);
        Assert.Null(context.ConnectionString);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning Manager supports external BTC Lightning backends only.", context.ConfigurationError);
    }

    [Theory]
    [InlineData("type=lnd-rest;server=https://example.test")]
    [InlineData("type=lnd-grpc;server=https://example.test")]
    [InlineData("type=clightning;server=tcp://127.0.0.1:30993/")]
    [InlineData("type=eclair;server=https://example.test;password=test")]
    [InlineData("type=phoenixd;server=https://example.test;password=test")]
    [InlineData("type=blink;server=https://api.example.test/graphql")]
    [InlineData("type=blink;currency=USD;server=https://api.example.test/graphql")]
    public async Task CreateAsync_WithSupportedBackendButMissingHandler_ReturnsUnavailable(string connectionString)
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = connectionString
        };
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = new StoreLightningManagerContextFactory(
            new BTCPayNetworkProvider(
                [TestNetworkFactory.GetBitcoinNetwork()],
                new NBXplorerNetworkProvider(ChainName.Regtest),
                new Logs()),
            handlers,
            new LightningClientFactoryService(
                new FakeHttpClientFactory(),
                [],
                []),
            new LightningCapabilityService());

        var context = await factory.CreateAsync(store, "BTC");

        Assert.False(context.IsConfigured);
        Assert.Null(context.Client);
        Assert.Equal(config.ConnectionString, context.ConnectionString);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning backend unavailable.", context.ConfigurationError);
    }

    [Fact]
    public async Task CreateAsync_WithUnsupportedCrypto_ReturnsUnavailableContext()
    {
        var store = new StoreData { Id = "store-1" };
        var factory = new StoreLightningManagerContextFactory(
            new BTCPayNetworkProvider(
                [TestNetworkFactory.GetBitcoinNetwork()],
                new NBXplorerNetworkProvider(ChainName.Regtest),
                new Logs()),
            new PaymentMethodHandlerDictionary([]),
            new LightningClientFactoryService(new FakeHttpClientFactory(), [], []),
            new LightningCapabilityService());

        var context = await factory.CreateAsync(store, "LTC");

        Assert.False(context.IsConfigured);
        Assert.Equal("LTC", context.CryptoCode);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning Manager only supports BTC Lightning.", context.ConfigurationError);
    }
}
