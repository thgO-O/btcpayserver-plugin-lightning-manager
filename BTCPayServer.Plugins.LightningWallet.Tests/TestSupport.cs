#nullable enable
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningWallet.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBXplorer;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace BTCPayServer.Plugins.LightningWallet.Tests;

internal static class TestNetworkFactory
{
    private static readonly Lazy<BTCPayNetwork> Network = new(CreateNetwork);

    public static BTCPayNetwork GetBitcoinNetwork()
    {
        return Network.Value;
    }

    private static BTCPayNetwork CreateNetwork()
    {
        var networkInstance = (NBXplorerNetwork)RuntimeHelpers.GetUninitializedObject(typeof(NBXplorerNetwork));
        SetNetwork(networkInstance, NBitcoin.Network.RegTest);
        return new BTCPayNetwork
        {
            NBXplorerNetwork = networkInstance
        };
    }

    private static void SetNetwork(NBXplorerNetwork networkInstance, NBitcoin.Network nbitcoinNetwork)
    {
        var property = typeof(NBXplorerNetwork).GetProperty("NBitcoinNetwork", BindingFlags.Public | BindingFlags.Instance);
        if (property?.SetMethod is not null)
        {
            property.SetValue(networkInstance, nbitcoinNetwork);
            return;
        }

        var field = typeof(NBXplorerNetwork).GetField("<NBitcoinNetwork>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) ??
                    typeof(NBXplorerNetwork).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                        .FirstOrDefault(f => f.FieldType == typeof(NBitcoin.Network));
        if (field is null)
        {
            throw new InvalidOperationException("Could not initialize NBXplorerNetwork for tests.");
        }

        field.SetValue(networkInstance, nbitcoinNetwork);
    }
}

internal class FakeLightningClient : ILightningClient
{
    public Func<CancellationToken, Task<LightningNodeInformation>>? GetInfoHandler { get; set; }
    public Func<CancellationToken, Task<LightningNodeBalance>>? GetBalanceHandler { get; set; }
    public Func<string, CancellationToken, Task<PayResponse>>? PayBolt11Handler { get; set; }
    public Func<NodeInfo, CancellationToken, Task<ConnectionResult>>? ConnectToHandler { get; set; }
    public Func<OpenChannelRequest, CancellationToken, Task<OpenChannelResponse>>? OpenChannelHandler { get; set; }
    public Func<CloseChannelRequest, CancellationToken, Task<CloseChannelResponse>>? CloseChannelHandler { get; set; }
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
        PayBolt11Handler is null ? throw new NotSupportedException() : PayBolt11Handler(bolt11, cancellation);
    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        PayBolt11Handler is null ? throw new NotSupportedException() : PayBolt11Handler(bolt11, cancellation);
    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) =>
        OpenChannelHandler is null ? throw new NotSupportedException() : OpenChannelHandler(openChannelRequest, cancellation);
    public Task<CloseChannelResponse> CloseChannel(CloseChannelRequest closeChannelRequest, CancellationToken cancellation = default) =>
        CloseChannelHandler is null ? throw new NotSupportedException() : CloseChannelHandler(closeChannelRequest, cancellation);
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        ConnectToHandler is null ? throw new NotSupportedException() : ConnectToHandler(nodeInfo, cancellation);
    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        ListChannelsHandler is null ? throw new NotSupportedException() : ListChannelsHandler(cancellation);
}

internal sealed class BlinkLikeLightningClient : FakeLightningClient;
internal sealed class PhoenixdLikeLightningClient : FakeLightningClient;
internal sealed class LndLikeLightningClient : FakeLightningClient;
internal sealed class LndLikePeerListingClient : FakeLightningClient
{
    public Func<CancellationToken, Task<TestPeerListResponse>>? ListPeersHandler { get; set; }

    public Task<TestPeerListResponse> ListPeersAsync(CancellationToken cancellationToken = default)
    {
        return ListPeersHandler is null
            ? Task.FromResult(new TestPeerListResponse())
            : ListPeersHandler(cancellationToken);
    }
}

internal sealed class WrappedPeerListingClient : FakeLightningClient
{
    public required WrappedPeerListingApi Api { get; init; }
}

internal sealed class DeepWrappedPeerListingClient : FakeLightningClient
{
    public required DeepWrappedPeerListingLayer Layer { get; init; }
}

internal sealed class SnakeCasePeerListingClient : FakeLightningClient
{
    public Func<CancellationToken, Task<SnakeCasePeerListResponse>>? ListPeersHandler { get; set; }

    public Task<SnakeCasePeerListResponse> ListPeersAsync(CancellationToken cancellationToken = default)
    {
        return ListPeersHandler is null
            ? Task.FromResult(new SnakeCasePeerListResponse())
            : ListPeersHandler(cancellationToken);
    }
}

internal sealed class DeepWrappedPeerListingLayer
{
    public required WrappedPeerListingApi Api { get; init; }
}

internal sealed class SnakeCasePeerListResponse
{
    public List<SnakeCasePeerResponse> peers { get; init; } = [];
}

internal sealed class SnakeCasePeerResponse
{
    public string? pub_key { get; init; }
    public string? address { get; init; }
    public string? bytes_sent { get; init; }
    public string? bytes_recv { get; init; }
    public bool inbound { get; init; }
}

internal sealed class WrappedPeerListingApi
{
    public Func<CancellationToken, Task<TestPeerListResponse>>? ListPeersHandler { get; set; }

    public Task<TestPeerListResponse> ListPeersAsync(CancellationToken cancellationToken = default)
    {
        return ListPeersHandler is null
            ? Task.FromResult(new TestPeerListResponse())
            : ListPeersHandler(cancellationToken);
    }
}

internal class TestPeerListResponse
{
    public List<TestPeerResponse> Peers { get; init; } = [];
}

internal class TestPeerResponse
{
    public string? PubKey { get; init; }
    public string? Address { get; init; }
    public bool Inbound { get; init; }
    public long BytesSent { get; init; }
    public long BytesRecv { get; init; }
}

internal sealed class FakeStoreLightningWalletContextFactory : IStoreLightningWalletContextFactory
{
    public required StoreLightningWalletContext Context { get; init; }

    public Task<StoreLightningWalletContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        System.Security.Claims.ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Context);
    }
}

internal static class TestContextFactory
{
    public static StoreLightningWalletContext CreateConfigured(
        LightningCapabilities capabilities,
        ILightningClient? client = null,
        bool isInternalNode = false,
        bool isSharedBackend = false,
        bool isReadOnly = false,
        string? sharedBackendNotice = null)
    {
        return new StoreLightningWalletContext
        {
            Store = new StoreData { Id = "store-1", StoreName = "Test Store" },
            StoreId = "store-1",
            CryptoCode = "BTC",
            Network = TestNetworkFactory.GetBitcoinNetwork(),
            Client = client ?? new FakeLightningClient(),
            IsInternalNode = isInternalNode,
            IsSharedBackend = isSharedBackend,
            IsReadOnly = isReadOnly,
            Capabilities = capabilities,
            DisplayName = "Test Node",
            SharedBackendNotice = sharedBackendNotice
        };
    }
}

internal static class TestControllerFactory
{
    public static Controllers.LightningWalletController CreateController(StoreLightningWalletContext context)
    {
        var controller = new Controllers.LightningWalletController(
            new FakeStoreLightningWalletContextFactory { Context = context },
            new LightningWalletService());

        var httpContext = new DefaultHttpContext();
        httpContext.SetStoreData(context.Store);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }
}
