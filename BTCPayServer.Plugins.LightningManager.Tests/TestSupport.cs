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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.LightningManager.Tests;

internal static class TestInvoiceData
{
    public const string AmountlessBolt11 =
        "lnbcrt1p49g38fsp5pt3nfdequqzynepp4fnqkjky3vpnnjy7k4aw4qsrpjx8uauslj8spp5e0ecjegxuc8k760w6cluj2tvrgytwpp6ls9wwata4wx6qkj043lsdpyd35kw6r5de5kueedd4skuct8v4ez6ar9wd6qxqxfvcqcqcqp29qxpqysgqstrrcpnyws5g93th9wu7ngjvu29pa9tattwpj6q6nsq0ep2wv4ax964t0hgnjszwspy3udg88xft902rudt9mvc4trj8l2tv0uukf9gpgzqz2r";
    public const string FixedAmountBolt11 =
        "lnbcrt20n1p49g3gqsp5ne4g7vrg5my5m7ur9u9curv7hfvn0tfx9h4k65uwwwk2gezwt5gqpp5umaryhan5kynzu7xd56zaqc9g32ahul3ehdh9cn2uuz0yh8nd5gqdpdd35kw6r5de5kueedd4skuct8v4ez6enf0pjkgtt5v4ehgxqxfvcqcqcqp29qxpqysgqu7ku8yv7szhps4005y5lh6yulv8heln8ztfwj4urk8lhrws20d7nncm8965ctns7c920kn7k46egwcmmuuhtt6cyyyqzg7vf485klzqpa09hcc";
}

internal static class TestLightningManagerServiceFactory
{
    public static LightningManagerService Create(ILogger<LightningManagerService>? logger = null)
    {
        return new LightningManagerService(
            logger ?? NullLogger<LightningManagerService>.Instance,
            new LightningManagerOperationGuard());
    }
}

internal static class ConcurrentTestRunner
{
    public static async Task<T[]> RunAsync<T>(int workerCount, Func<T> action)
    {
        var readyCount = 0;
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == workerCount)
                {
                    allReady.SetResult();
                }

                await release.Task;
                return action();
            }))
            .ToArray();

        await allReady.Task;
        release.SetResult();
        return await Task.WhenAll(attempts);
    }
}

internal static class TestNetworkFactory
{
    private static readonly BTCPayNetwork Network = new()
    {
        CryptoCode = "BTC",
        NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC()
    };

    public static BTCPayNetwork GetBitcoinNetwork()
    {
        return Network;
    }
}

internal class FakeLightningClient : ILightningClient
{
    public Func<CancellationToken, Task<LightningNodeInformation>>? GetInfoHandler { get; set; }
    public Func<CancellationToken, Task<LightningNodeBalance>>? GetBalanceHandler { get; set; }
    public Func<string, CancellationToken, Task<PayResponse>>? PayBolt11Handler { get; set; }
    public Func<string, PayInvoiceParams, CancellationToken, Task<PayResponse>>? PayBolt11WithParamsHandler { get; set; }
    public Func<NodeInfo, CancellationToken, Task<ConnectionResult>>? ConnectToHandler { get; set; }
    public Func<OpenChannelRequest, CancellationToken, Task<OpenChannelResponse>>? OpenChannelHandler { get; set; }
    public Func<CancellationToken, Task<LightningChannel[]>>? ListChannelsHandler { get; set; }
    public Func<string, CancellationToken, Task<LightningPayment>>? GetPaymentHandler { get; set; }

    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) => throw new NotSupportedException();
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
    public required StoreLightningManagerContext Context { get; set; }

    public StoreLightningManagerContext Create(StoreData store, string cryptoCode)
    {
        return Context;
    }
}

internal static class TestContextFactory
{
    public static StoreLightningManagerContext CreateConfigured(
        LightningCapabilities capabilities,
        ILightningClient? client = null,
        string? connectionString = null,
        string storeId = "store-1")
    {
        var backendConnectionString = connectionString ?? "type=clightning;server=tcp://127.0.0.1:9735/";
        return new StoreLightningManagerContext
        {
            StoreId = storeId,
            CryptoCode = "BTC",
            Network = TestNetworkFactory.GetBitcoinNetwork(),
            Client = client ?? new FakeLightningClient(),
            BackendType = LightningBackendTypes.TryGet(backendConnectionString),
            BackendFingerprint = LightningBackendTypes.GetFingerprint(backendConnectionString),
            BackendIdentityFingerprint = LightningBackendTypes.GetIdentityFingerprint(backendConnectionString),
            Capabilities = capabilities,
            DisplayName = "Test Node"
        };
    }

    public static StoreLightningManagerContext CreateUnavailable(string message)
    {
        return new StoreLightningManagerContext
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            BackendFingerprint = string.Empty,
            BackendIdentityFingerprint = string.Empty,
            Capabilities = LightningCapabilities.None,
            ConfigurationError = message
        };
    }
}

internal static class TestControllerFactory
{
    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context,
        LightningManagerResultStore? resultStore = null,
        LightningManagerChannelConfirmationStore? channelConfirmationStore = null,
        LightningManagerPaymentConfirmationStore? paymentConfirmationStore = null,
        IStoreLightningManagerContextFactory? contextFactory = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = new Controllers.LightningManagerController(
            contextFactory ?? new FakeStoreLightningManagerContextFactory { Context = context },
            TestLightningManagerServiceFactory.Create(),
            resultStore ?? new LightningManagerResultStore(memoryCache),
            channelConfirmationStore ?? new LightningManagerChannelConfirmationStore(memoryCache),
            paymentConfirmationStore ?? new LightningManagerPaymentConfirmationStore(memoryCache));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                "Test"));
        httpContext.SetStoreData(new StoreData
        {
            Id = context.StoreId,
            StoreName = "Test Store"
        });
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
