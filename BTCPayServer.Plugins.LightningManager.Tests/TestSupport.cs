#nullable enable
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NBitcoin;
using NBXplorer;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Reflection;

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

internal sealed class BlinkLikeLightningClient : FakeLightningClient;
internal sealed class PhoenixdLikeLightningClient : FakeLightningClient;
internal sealed class LndLikeLightningClient : FakeLightningClient;
internal sealed class LndHubLikeLightningClient : FakeLightningClient;

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
        bool isInternalNode = false,
        bool isSharedBackend = false,
        bool isReadOnly = false,
        string? sharedBackendNotice = null,
        string? connectionString = null,
        LightningCapabilities? backendCapabilities = null)
    {
        return new StoreLightningManagerContext
        {
            Store = new StoreData { Id = "store-1", StoreName = "Test Store" },
            StoreId = "store-1",
            CryptoCode = "BTC",
            Network = TestNetworkFactory.GetBitcoinNetwork(),
            Client = client ?? new FakeLightningClient(),
            ConnectionString = connectionString,
            IsInternalNode = isInternalNode,
            IsSharedBackend = isSharedBackend,
            IsReadOnly = isReadOnly,
            Capabilities = capabilities,
            BackendCapabilities = backendCapabilities ?? capabilities,
            DisplayName = "Test Node",
            SharedBackendNotice = sharedBackendNotice
        };
    }
}

internal static class TestControllerFactory
{
    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context,
        params string[] grantedPolicies)
    {
        return CreateController(context, new LightningManagerService(), grantedPolicies);
    }

    public static Controllers.LightningManagerController CreateController(
        StoreLightningManagerContext context,
        ILightningManagerService lightningManagerService,
        params string[] grantedPolicies)
    {
        var controller = new Controllers.LightningManagerController(
            new FakeStoreLightningManagerContextFactory { Context = context },
            lightningManagerService,
            new NoopStoreLightningLedgerService(),
            new FakeAuthorizationService(grantedPolicies.Length == 0
                ? [BTCPayServer.Client.Policies.CanModifyStoreSettings, BTCPayServer.Client.Policies.CanModifyServerSettings]
                : grantedPolicies));

        var httpContext = new DefaultHttpContext();
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

internal sealed class NoopStoreLightningLedgerService : IStoreLightningLedgerService
{
    public Task PopulateStoreBalanceAsync(
        StoreBalanceViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default)
    {
        model.IsInternalNode = context.IsInternalNode;
        model.IsServerAdmin = canModifyServerSettings;
        model.AccountEnabled = context.IsInternalNode && context.IsConfigured;
        return Task.CompletedTask;
    }

    public Task PopulateStoreHistoryAsync(
        StoreHistoryViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default)
    {
        model.IsInternalNode = context.IsInternalNode;
        model.IsServerAdmin = canModifyServerSettings;
        model.AccountEnabled = context.IsInternalNode && context.IsConfigured;
        model.CanViewHistory = context.IsInternalNode && context.IsConfigured;
        return Task.CompletedTask;
    }

    public Task<ActionResultViewModel> AddServerAdminAdjustmentAsync(string storeId, string cryptoCode, string? amountSats, string? memo, string? operationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ActionResultViewModel { IsSuccess = true, Message = "ok" });
    }

    public Task<ManagedSendPreviewResult> CreateManagedSendPreviewAsync(StoreLightningManagerContext context, string? bolt11, string? maxFeeSats, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ManagedSendPreviewResult.Failure("not implemented"));
    }

    public Task<SendExecutionResult> SendFromLedgerAsync(StoreLightningManagerContext context, string bolt11, string? maxFeeSats, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SendExecutionResult
        {
            Result = new ActionResultViewModel { IsSuccess = false, Message = "not implemented" }
        });
    }

    public Task<bool> CreditInvoicePaymentAsync(string storeId, string cryptoCode, string invoiceId, string paymentId, string? paymentHash, long amountMSat, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task ReconcilePendingSendsAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

internal sealed class FakeAuthorizationService(IReadOnlyCollection<string> grantedPolicies) : IAuthorizationService
{
    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements)
    {
        var granted = requirements
            .OfType<PolicyRequirement>()
            .All(requirement => grantedPolicies.Contains(requirement.Policy));

        return Task.FromResult(granted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        string policyName)
    {
        return Task.FromResult(grantedPolicies.Contains(policyName)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed());
    }
}
