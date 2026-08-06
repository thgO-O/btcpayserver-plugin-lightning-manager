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
    public void Create_WithSupportedExternalBackend_CreatesClientAtMinimumHostBaseline()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = "type=clightning;server=tcp://127.0.0.1:30993/"
        };
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = CreateFactory(
            handlers,
            [_ => new CLightningConnectionStringHandler()]);

        var context = factory.Create(store, "btc");

        Assert.True(context.IsConfigured);
        Assert.NotNull(context.Client);
        Assert.Equal("BTC", context.CryptoCode);
        Assert.Equal(LightningBackendTypes.CLightning, context.BackendType);
        Assert.Equal(
            LightningBackendTypes.GetFingerprint(config.ConnectionString),
            context.BackendFingerprint);
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(config.ConnectionString),
            context.BackendIdentityFingerprint);
        Assert.Same(LightningCapabilities.Full, context.Capabilities);
        Assert.Null(context.ConfigurationError);
    }

    [Fact]
    public void Create_WithInternalNodeConfig_ReturnsUnavailableExternalOnlyContext()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = CreateFactory(handlers);

        var context = factory.Create(store, "BTC");

        Assert.False(context.IsConfigured);
        Assert.Null(context.Client);
        Assert.Empty(context.BackendFingerprint);
        Assert.Empty(context.BackendIdentityFingerprint);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning Manager supports external BTC Lightning backends only.", context.ConfigurationError);
    }

    [Fact]
    public void Create_WithSupportedBackendButMissingHandler_ReturnsUnavailable()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = "type=clightning;server=tcp://127.0.0.1:30993/"
        };
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = CreateFactory(handlers);

        var context = factory.Create(store, "BTC");

        Assert.False(context.IsConfigured);
        Assert.Null(context.Client);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning backend unavailable.", context.ConfigurationError);
    }

    [Theory]
    [InlineData("type=blink;ln-address=user@blink.sv")]
    [InlineData("type=blink;username=user@blink.sv;currency=USD")]
    public void Create_WithBlinkReceiveOnly_ReturnsUnsupportedManagerContext(
        string connectionString)
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = connectionString
        };
        var store = new StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, config);
        var factory = CreateFactory(handlers);

        var context = factory.Create(store, "BTC");

        Assert.False(context.IsConfigured);
        Assert.Null(context.Client);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal(
            "Lightning backend is not supported by Lightning Manager.",
            context.ConfigurationError);
    }

    [Fact]
    public void Create_WithUnsupportedCrypto_ReturnsUnavailableContext()
    {
        var store = new StoreData { Id = "store-1" };
        var factory = CreateFactory(new PaymentMethodHandlerDictionary([]));

        var context = factory.Create(store, "LTC");

        Assert.False(context.IsConfigured);
        Assert.Equal("LTC", context.CryptoCode);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal("Lightning Manager only supports BTC Lightning.", context.ConfigurationError);
    }

    private static StoreLightningManagerContextFactory CreateFactory(
        PaymentMethodHandlerDictionary handlers,
        IEnumerable<Func<HttpClient, ILightningConnectionStringHandler>>? handlerFactories = null)
    {
        return new StoreLightningManagerContextFactory(
            new BTCPayNetworkProvider(
                [TestNetworkFactory.GetBitcoinNetwork()],
                new NBXplorerNetworkProvider(ChainName.Regtest),
                new Logs()),
            handlers,
            new LightningClientFactoryService(
                new FakeHttpClientFactory(),
                handlerFactories ?? [],
                []));
    }
}
