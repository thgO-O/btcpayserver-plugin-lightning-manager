using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
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
}
