using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class StoreLightningAccessServiceTests
{
    [Fact]
    public void DisableNativeLightningCheckout_ExcludesLightningAndLnurl()
    {
        var handlers = CreateHandlers();
        var store = CreateStoreWithInternalLightning(handlers);
        var lnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnurlId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

        var changed = StoreLightningAccessService.DisableNativeLightningCheckout(store, "BTC");

        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();
        Assert.True(changed);
        Assert.True(excluded.Match(lnId));
        Assert.True(excluded.Match(lnurlId));
    }

    [Fact]
    public void GetInternalLightningCryptoCodes_ReturnsInternalLightningStoresOnly()
    {
        var handlers = CreateHandlers();
        var internalStore = CreateStoreWithInternalLightning(handlers);
        var externalStore = CreateStoreWithExternalLightning(handlers);
        var litecoinStore = CreateStoreWithInternalLightning(handlers, "LTC");

        var internalCodes = StoreLightningAccessService.GetInternalLightningCryptoCodes(internalStore, handlers);
        var externalCodes = StoreLightningAccessService.GetInternalLightningCryptoCodes(externalStore, handlers);
        var litecoinCodes = StoreLightningAccessService.GetInternalLightningCryptoCodes(litecoinStore, handlers);

        Assert.Equal(["BTC"], internalCodes);
        Assert.Empty(externalCodes);
        Assert.Empty(litecoinCodes);
        Assert.False(StoreLightningAccessService.IsInternalLightningNode(litecoinStore, "LTC", handlers));
    }

    private static PaymentMethodHandlerDictionary CreateHandlers()
    {
        return new PaymentMethodHandlerDictionary(
        [
            CreateLightningHandler("BTC"),
            CreateLnurlHandler("BTC"),
            CreateLightningHandler("LTC"),
            CreateLnurlHandler("LTC")
        ]);
    }

    private static StoreData CreateStoreWithInternalLightning(
        PaymentMethodHandlerDictionary handlers,
        string cryptoCode = "BTC")
    {
        var store = new StoreData { Id = "store-1", StoreName = "Store 1" };
        var lnId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var lnurlId = PaymentTypes.LNURL.GetPaymentMethodId(cryptoCode);
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        store.SetPaymentMethodConfig(handlers[lnId], config);
        store.SetPaymentMethodConfig(handlers[lnurlId], new LNURLPaymentMethodConfig());
        return store;
    }

    private static StoreData CreateStoreWithExternalLightning(PaymentMethodHandlerDictionary handlers)
    {
        var store = new StoreData { Id = "store-2", StoreName = "Store 2" };
        var lnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        store.SetPaymentMethodConfig(handlers[lnId], new LightningPaymentMethodConfig
        {
            ConnectionString = "type=clightning;server=http://127.0.0.1:9835/"
        });
        return store;
    }

    private static TestPaymentMethodHandler CreateLightningHandler(string cryptoCode)
    {
        return new TestPaymentMethodHandler(
            PaymentTypes.LN.GetPaymentMethodId(cryptoCode),
            token => token.ToObject<LightningPaymentMethodConfig>()!);
    }

    private static TestPaymentMethodHandler CreateLnurlHandler(string cryptoCode)
    {
        return new TestPaymentMethodHandler(
            PaymentTypes.LNURL.GetPaymentMethodId(cryptoCode),
            token => token.ToObject<LNURLPaymentMethodConfig>()!);
    }

    private sealed class TestPaymentMethodHandler(
        PaymentMethodId paymentMethodId,
        Func<JToken, object> parsePaymentMethodConfig) : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;
        public JsonSerializer Serializer { get; } = JsonSerializer.CreateDefault();
        public Task ConfigurePrompt(PaymentMethodContext context) => Task.CompletedTask;
        public Task BeforeFetchingRates(PaymentMethodContext context) => Task.CompletedTask;
        public object ParsePaymentPromptDetails(JToken details) => new object();
        public object ParsePaymentMethodConfig(JToken config) => parsePaymentMethodConfig(config);
        public object ParsePaymentDetails(JToken details) => details.ToObject<LightningLikePaymentData>()!;
    }
}
