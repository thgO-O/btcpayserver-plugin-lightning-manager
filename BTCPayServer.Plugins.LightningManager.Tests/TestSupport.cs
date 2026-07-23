#nullable enable
using System.Security.Claims;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.LightningManager.Tests;

internal static class TestNetworkFactory
{
    private static readonly Lazy<BTCPayNetwork> Network = new(CreateNetwork);

    public static BTCPayNetwork GetBitcoinNetwork()
    {
        return Network.Value;
    }

    private static BTCPayNetwork CreateNetwork()
    {
        return new BTCPayNetwork
        {
            CryptoCode = "BTC",
            NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC()
        };
    }
}

internal class FakeLightningClient : ILightningClient
{
    public Func<CancellationToken, Task<LightningNodeInformation>>? GetInfoHandler { get; set; }
    public Func<CancellationToken, Task<LightningNodeBalance>>? GetBalanceHandler { get; set; }
    public Func<string, CancellationToken, Task<LightningInvoice>>? GetInvoiceHandler { get; set; }
    public Func<uint256, CancellationToken, Task<LightningInvoice>>? GetInvoiceByPaymentHashHandler { get; set; }
    public Func<string, CancellationToken, Task<PayResponse>>? PayBolt11Handler { get; set; }
    public Func<string, PayInvoiceParams, CancellationToken, Task<PayResponse>>? PayBolt11WithParamsHandler { get; set; }
    public Func<NodeInfo, CancellationToken, Task<ConnectionResult>>? ConnectToHandler { get; set; }
    public Func<OpenChannelRequest, CancellationToken, Task<OpenChannelResponse>>? OpenChannelHandler { get; set; }
    public Func<CancellationToken, Task<LightningChannel[]>>? ListChannelsHandler { get; set; }
    public Func<string, CancellationToken, Task<LightningPayment>>? GetPaymentHandler { get; set; }

    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default) =>
        GetInvoiceHandler is null ? throw new NotSupportedException() : GetInvoiceHandler(invoiceId, cancellation);
    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) =>
        GetInvoiceByPaymentHashHandler is null ? throw new NotSupportedException() : GetInvoiceByPaymentHashHandler(paymentHash, cancellation);
    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default) =>
        GetPaymentHandler is null ? Task.FromResult<LightningPayment>(null!) : GetPaymentHandler(paymentHash, cancellation);
    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
        GetInfoHandler is null ? throw new NotSupportedException() : GetInfoHandler(cancellation);
    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default) =>
        GetBalanceHandler is null ? throw new NotSupportedException() : GetBalanceHandler(cancellation);
    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        PayBolt11WithParamsHandler is not null
            ? PayBolt11WithParamsHandler(bolt11, payParams, cancellation)
            : PayBolt11Handler is null ? throw new NotSupportedException() : PayBolt11Handler(bolt11, cancellation);
    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        PayBolt11Handler is null ? throw new NotSupportedException() : PayBolt11Handler(bolt11, cancellation);
    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) =>
        OpenChannelHandler is null ? throw new NotSupportedException() : OpenChannelHandler(openChannelRequest, cancellation);
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        ConnectToHandler is null ? throw new NotSupportedException() : ConnectToHandler(nodeInfo, cancellation);
    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        ListChannelsHandler is null ? throw new NotSupportedException() : ListChannelsHandler(cancellation);
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient();
    }
}

internal sealed class FakeLightningPaymentMethodHandler : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId { get; } = PaymentTypes.LN.GetPaymentMethodId("BTC");
    public JsonSerializer Serializer { get; } = JsonSerializer.CreateDefault();

    public Task ConfigurePrompt(PaymentMethodContext context)
    {
        return Task.CompletedTask;
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        return Task.CompletedTask;
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        throw new NotSupportedException();
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<LightningPaymentMethodConfig>(Serializer) ?? new LightningPaymentMethodConfig();
    }

    public object ParsePaymentDetails(JToken details)
    {
        throw new NotSupportedException();
    }
}

internal sealed class FakeStoreLightningManagerContextFactory : IStoreLightningManagerContextFactory
{
    public required StoreLightningManagerContext Context { get; init; }

    public Task<StoreLightningManagerContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Context);
    }
}

internal static class TestContextFactory
{
    public static StoreLightningManagerContext CreateConfigured(
        LightningCapabilities capabilities,
        ILightningClient? client = null,
        string? connectionString = null)
    {
        return new StoreLightningManagerContext
        {
            Store = new StoreData { Id = "store-1", StoreName = "Test Store" },
            StoreId = "store-1",
            CryptoCode = "BTC",
            Network = TestNetworkFactory.GetBitcoinNetwork(),
            Client = client ?? new FakeLightningClient(),
            ConnectionString = connectionString,
            Capabilities = capabilities,
            DisplayName = "Test Node"
        };
    }

    public static StoreLightningManagerContext CreateUnavailable(string message)
    {
        return new StoreLightningManagerContext
        {
            Store = new StoreData { Id = "store-1", StoreName = "Test Store" },
            StoreId = "store-1",
            CryptoCode = "BTC",
            Network = TestNetworkFactory.GetBitcoinNetwork(),
            Capabilities = LightningCapabilities.None,
            ConfigurationError = message
        };
    }
}

internal static class TestControllerFactory
{
    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context)
    {
        return CreateController(context, new LightningManagerService());
    }

    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context,
        ILightningManagerService lightningManagerService)
    {
        return CreateController(
            context,
            lightningManagerService,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())));
    }

    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context,
        ILightningManagerService lightningManagerService,
        LightningManagerResultStore resultStore,
        LightningManagerChannelConfirmationStore? channelConfirmationStore = null,
        LightningManagerPaymentConfirmationStore? paymentConfirmationStore = null)
    {
        var controller = new Controllers.LightningManagerController(
            new FakeStoreLightningManagerContextFactory { Context = context },
            lightningManagerService,
            resultStore,
            channelConfirmationStore ?? new LightningManagerChannelConfirmationStore(
                new MemoryCache(new MemoryCacheOptions())),
            paymentConfirmationStore ?? new LightningManagerPaymentConfirmationStore(
                new MemoryCache(new MemoryCacheOptions())));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                "Test"));
        httpContext.SetStoreData(context.Store);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider());

        return controller;
    }
}

internal sealed class InMemoryTempDataProvider : ITempDataProvider
{
    private readonly Dictionary<string, object> _values = new();

    public IDictionary<string, object> LoadTempData(HttpContext context)
    {
        return _values;
    }

    public void SaveTempData(HttpContext context, IDictionary<string, object> values)
    {
        _values.Clear();
        foreach (var value in values)
        {
            _values[value.Key] = value.Value;
        }
    }
}
