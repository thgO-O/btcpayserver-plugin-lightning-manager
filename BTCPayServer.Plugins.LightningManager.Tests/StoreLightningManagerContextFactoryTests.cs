using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Options;
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
        Assert.False(context.IsInternalNode);
        Assert.Null(context.ConfigurationError);
    }

    [Theory]
    [InlineData("type=clightning;server=tcp://127.0.0.1:30993/", LightningBackendTypes.CLightning)]
    [InlineData("type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=00", LightningBackendTypes.LndRest)]
    [InlineData("type=eclair;server=http://127.0.0.1:4570/;password=secret", LightningBackendTypes.Eclair)]
    public void Create_WithSupportedInternalNode_ReusesRegisteredClient(
        string connectionString,
        string expectedBackendType)
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var client = new ConnectionStringLightningClient(connectionString);
        var factory = CreateFactory(
            handlers,
            lightningOptions: Options.Create(new LightningNetworkOptions
            {
                InternalLightningByCryptoCode = { ["BTC"] = client }
            }));

        var context = factory.Create(CreateInternalStore(handler, "store-1"), "BTC");

        Assert.True(context.IsConfigured);
        Assert.True(context.IsInternalNode);
        Assert.Same(client, context.Client);
        Assert.Equal(expectedBackendType, context.BackendType);
        Assert.Same(LightningCapabilities.Full, context.Capabilities);
        Assert.Equal(
            LightningBackendTypes.GetFingerprint($"internal:{connectionString}"),
            context.BackendFingerprint);
        Assert.Equal(
            LightningBackendTypes.GetIdentityFingerprint(connectionString),
            context.BackendIdentityFingerprint);
        Assert.EndsWith(" · Internal", context.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, context.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("server=", context.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("127.0.0.1", context.NodeHost);
        Assert.Null(context.ConfigurationError);
    }

    [Fact]
    public void Create_WithInternalPhoenixd_UsesPhoenixdCapabilities()
    {
        const string connectionString =
            "type=phoenixd;server=http://127.0.0.1:9740/;password=secret";
        var handler = new FakeLightningPaymentMethodHandler();
        var client = new ConnectionStringLightningClient(connectionString);
        var factory = CreateFactory(
            new PaymentMethodHandlerDictionary([handler]),
            lightningOptions: Options.Create(new LightningNetworkOptions
            {
                InternalLightningByCryptoCode = { ["BTC"] = client }
            }));

        var context = factory.Create(CreateInternalStore(handler, "store-1"), "btc");

        Assert.True(context.IsConfigured);
        Assert.True(context.IsInternalNode);
        Assert.Equal(LightningBackendTypes.Phoenixd, context.BackendType);
        Assert.Same(LightningCapabilities.Phoenixd, context.Capabilities);
    }

    [Fact]
    public void Create_WithSameInternalNodeAcrossStores_SharesIdentity()
    {
        const string connectionString = "type=clightning;server=tcp://127.0.0.1:30993/";
        var handler = new FakeLightningPaymentMethodHandler();
        var client = new ConnectionStringLightningClient(connectionString);
        var factory = CreateFactory(
            new PaymentMethodHandlerDictionary([handler]),
            lightningOptions: Options.Create(new LightningNetworkOptions
            {
                InternalLightningByCryptoCode = { ["BTC"] = client }
            }));

        var first = factory.Create(CreateInternalStore(handler, "store-1"), "BTC");
        var second = factory.Create(CreateInternalStore(handler, "store-2"), "BTC");

        Assert.Same(first.Client, second.Client);
        Assert.Equal(first.BackendIdentityFingerprint, second.BackendIdentityFingerprint);
        Assert.Equal(first.BackendFingerprint, second.BackendFingerprint);
    }

    [Fact]
    public void Create_WithSameNodeInternalAndExternal_SeparatesConfigurationButSharesIdentity()
    {
        const string connectionString = "type=clightning;server=tcp://127.0.0.1:30993/";
        var handler = new FakeLightningPaymentMethodHandler();
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var externalStore = new StoreData { Id = "external-store" };
        externalStore.SetPaymentMethodConfig(handler, new LightningPaymentMethodConfig
        {
            ConnectionString = connectionString
        });
        var factory = CreateFactory(
            handlers,
            [_ => new CLightningConnectionStringHandler()],
            Options.Create(new LightningNetworkOptions
            {
                InternalLightningByCryptoCode =
                {
                    ["BTC"] = new ConnectionStringLightningClient(connectionString)
                }
            }));

        var externalContext = factory.Create(externalStore, "BTC");
        var internalContext = factory.Create(CreateInternalStore(handler, "internal-store"), "BTC");

        Assert.NotEqual(externalContext.BackendFingerprint, internalContext.BackendFingerprint);
        Assert.Equal(
            externalContext.BackendIdentityFingerprint,
            internalContext.BackendIdentityFingerprint);
    }

    [Fact]
    public void Create_WithMissingInternalNode_ReturnsUnavailableInternalContext()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var factory = CreateFactory(new PaymentMethodHandlerDictionary([handler]));

        var context = factory.Create(CreateInternalStore(handler, "store-1"), "BTC");

        Assert.False(context.IsConfigured);
        Assert.True(context.IsInternalNode);
        Assert.Null(context.Client);
        Assert.Empty(context.BackendFingerprint);
        Assert.Empty(context.BackendIdentityFingerprint);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal(
            "BTCPay Server's internal BTC Lightning node is not configured.",
            context.ConfigurationError);
    }

    [Fact]
    public void Create_WithUnknownInternalBackend_ReturnsUnavailableInternalContext()
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var client = new ConnectionStringLightningClient(
            "type=unknown;server=http://127.0.0.1:1234/");
        var factory = CreateFactory(
            new PaymentMethodHandlerDictionary([handler]),
            lightningOptions: Options.Create(new LightningNetworkOptions
            {
                InternalLightningByCryptoCode = { ["BTC"] = client }
            }));

        var context = factory.Create(CreateInternalStore(handler, "store-1"), "BTC");

        Assert.False(context.IsConfigured);
        Assert.True(context.IsInternalNode);
        Assert.Null(context.Client);
        Assert.Same(LightningCapabilities.None, context.Capabilities);
        Assert.Equal(
            "BTCPay Server's internal Lightning backend is not supported by Lightning Manager.",
            context.ConfigurationError);
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
        IEnumerable<Func<HttpClient, ILightningConnectionStringHandler>>? handlerFactories = null,
        IOptions<LightningNetworkOptions>? lightningOptions = null)
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
                []),
            lightningOptions ?? Options.Create(new LightningNetworkOptions()));
    }

    private static StoreData CreateInternalStore(
        FakeLightningPaymentMethodHandler handler,
        string storeId)
    {
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var store = new StoreData { Id = storeId };
        store.SetPaymentMethodConfig(handler, config);
        return store;
    }
}
