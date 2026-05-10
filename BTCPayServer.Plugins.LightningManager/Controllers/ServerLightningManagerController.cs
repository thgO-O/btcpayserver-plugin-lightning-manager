#nullable enable
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningManager.Controllers;

[Route("server/lightning-manager")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ServerLightningManagerController(
    BTCPayNetworkProvider networkProvider,
    ApplicationDbContextFactory dbContextFactory,
    ILightningLedgerRepository ledgerRepository,
    IStoreLightningLedgerService ledgerService,
    IStoreLightningAccessService storeLightningAccessService,
    ILightningManagerService lightningManagerService,
    ILightningCapabilityService lightningCapabilityService,
    PaymentMethodHandlerDictionary handlers,
    IOptions<LightningNetworkOptions> lightningNetworkOptions) : Controller
{
    private const string PaymentStatusTempDataKey = "LightningManagerServerPaymentStatus";
    private const string PaymentTotalTempDataKey = "LightningManagerServerPaymentTotal";
    private const string PaymentFeeTempDataKey = "LightningManagerServerPaymentFee";
    private const string PaymentHashTempDataKey = "LightningManagerServerPaymentHash";
    private const string PaymentPreimageTempDataKey = "LightningManagerServerPaymentPreimage";

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] string? cryptoCode, CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.Server, cryptoCode, cancellationToken);
        return View(model);
    }

    [HttpGet("send")]
    public async Task<IActionResult> Send([FromQuery] string? cryptoCode, CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerSend, cryptoCode, cancellationToken);
        model.Send.Payment = GetPaymentResult();
        return View(model);
    }

    [HttpGet("peers")]
    public async Task<IActionResult> Peers([FromQuery] string? cryptoCode, CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerPeers, cryptoCode, cancellationToken);
        return View(model);
    }

    [HttpGet("channels")]
    public async Task<IActionResult> Channels([FromQuery] string? cryptoCode, CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerChannels, cryptoCode, cancellationToken);
        return View(model);
    }

    [HttpPost("send/preview")]
    public async Task<IActionResult> PreviewSend(
        [FromForm] string? cryptoCode,
        [FromForm] string? bolt11,
        [FromForm] string? maxFeeSats,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerSend, cryptoCode, cancellationToken);
        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var error))
        {
            model.Send.Bolt11 = bolt11;
            model.Send.MaxFeeSats = maxFeeSats;
            if (lightningManagerService.TryCreateSendPreview(context, bolt11, maxFeeSats, out var preview, out var validationError))
            {
                model.Send.Preview = preview;
            }
            else
            {
                model.Result = Failure(validationError ?? "The invoice is invalid.");
            }
        }
        else
        {
            model.Result = Failure(error ?? "The internal Lightning node is not available.");
        }

        return View("Send", model);
    }

    [HttpPost("send/execute")]
    public async Task<IActionResult> ExecuteSend(
        [FromForm] string? cryptoCode,
        [FromForm] string bolt11,
        [FromForm] string? maxFeeSats,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerSend, cryptoCode, cancellationToken);
        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var error))
        {
            var result = await lightningManagerService.SendAsync(context, bolt11, maxFeeSats, cancellationToken);
            model.Result = result.Result;
            model.Send.Payment = result.Payment;
        }
        else
        {
            model.Result = Failure(error ?? "The internal Lightning node is not available.");
        }

        SetStatusMessage(model.Result);
        SetPaymentResult(model.Send.Payment);
        return RedirectToAction(nameof(Send), new { cryptoCode = model.SelectedCryptoCode });
    }

    [HttpPost("peers/connect")]
    public async Task<IActionResult> ConnectPeer(
        [FromForm] string? cryptoCode,
        [FromForm] string? nodeUri,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerPeers, cryptoCode, cancellationToken);
        model.Peers.NodeUri = nodeUri;
        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var error))
        {
            model.Result = await lightningManagerService.ConnectPeerAsync(context, nodeUri, cancellationToken);
        }
        else
        {
            model.Result = Failure(error ?? "The internal Lightning node is not available.");
        }

        SetStatusMessage(model.Result);
        return RedirectToAction(nameof(Peers), new { cryptoCode = model.SelectedCryptoCode });
    }

    [HttpPost("channels/preview")]
    public async Task<IActionResult> PreviewChannel(
        [FromForm] string? cryptoCode,
        [FromForm] string? nodeUri,
        [FromForm] string? channelAmountSats,
        [FromForm] string? feeRateSatsPerByte,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerChannels, cryptoCode, cancellationToken);
        model.Channels.NodeUri = nodeUri;
        model.Channels.ChannelAmountSats = channelAmountSats;
        model.Channels.FeeRateSatsPerByte = feeRateSatsPerByte;
        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var error))
        {
            if (lightningManagerService.TryCreateOpenChannelPreview(
                    context,
                    nodeUri,
                    channelAmountSats,
                    feeRateSatsPerByte,
                    out var preview,
                    out var validationError))
            {
                model.Channels.Preview = preview;
            }
            else
            {
                model.Result = Failure(validationError ?? "The channel request is invalid.");
            }
        }
        else
        {
            model.Result = Failure(error ?? "The internal Lightning node is not available.");
        }

        return View("Channels", model);
    }

    [HttpPost("channels/open")]
    public async Task<IActionResult> OpenChannel(
        [FromForm] string? cryptoCode,
        [FromForm] string nodeUri,
        [FromForm] string channelAmountSats,
        [FromForm] string? feeRateSatsPerByte,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync(LightningManagerNavPages.ServerChannels, cryptoCode, cancellationToken);
        model.Channels.NodeUri = nodeUri;
        model.Channels.ChannelAmountSats = channelAmountSats;
        model.Channels.FeeRateSatsPerByte = feeRateSatsPerByte;
        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var error))
        {
            model.Result = await lightningManagerService.OpenChannelAsync(
                context,
                nodeUri,
                channelAmountSats,
                feeRateSatsPerByte,
                cancellationToken);
            model.Channels = CreateNodeModel<ChannelsViewModel>(context, "Channels");
            await lightningManagerService.PopulateChannelsAsync(model.Channels, context, cancellationToken);
        }
        else
        {
            model.Result = Failure(error ?? "The internal Lightning node is not available.");
        }

        SetStatusMessage(model.Result);
        return RedirectToAction(nameof(Channels), new { cryptoCode = model.SelectedCryptoCode });
    }

    [HttpPost("adjust")]
    public async Task<IActionResult> AddAdjustment(
        [FromForm] string? account,
        [FromForm] string? amountSats,
        [FromForm] string? memo,
        [FromForm] string? operationId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveInternalStoreAccountAsync(
            account,
            requireCurrentInternalNode: false,
            cancellationToken);
        if (!resolved.IsSuccess)
        {
            SetStatusMessage(Failure(resolved.ErrorMessage ?? "Select a store account."));
            return RedirectToAction(nameof(Index));
        }

        var result = await ledgerService.AddServerAdminAdjustmentAsync(
            resolved.StoreId,
            resolved.CryptoCode,
            amountSats,
            memo,
            operationId,
            cancellationToken);
        SetStatusMessage(result);
        return RedirectToAction(nameof(Index), new { cryptoCode = resolved.CryptoCode });
    }

    [HttpPost("accounts/access")]
    public async Task<IActionResult> SetStoreAccess(
        [FromForm] string? account,
        [FromForm] bool enabled,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveInternalStoreAccountAsync(
            account,
            requireCurrentInternalNode: true,
            cancellationToken);
        if (!resolved.IsSuccess)
        {
            SetStatusMessage(Failure(resolved.ErrorMessage ?? "Select a store account."));
            return RedirectToAction(nameof(Index));
        }

        var updated = await storeLightningAccessService.SetStoreAccessAsync(resolved.StoreId, resolved.CryptoCode, enabled, cancellationToken);
        SetStatusMessage(updated
            ? new ActionResultViewModel
            {
                IsSuccess = true,
                Message = enabled ? "Store Lightning access enabled." : "Store Lightning access disabled."
            }
            : Failure("Store access was not updated."));
        return RedirectToAction(nameof(Index), new { cryptoCode = resolved.CryptoCode });
    }

    private async Task<ServerLightningManagerViewModel> CreateModelAsync(
        string activePage,
        string? selectedCryptoCode,
        CancellationToken cancellationToken)
    {
        ViewData.SetLayoutModel(new LayoutModel(activePage, "Lightning Manager")
            .SetCategory(WellKnownCategories.Server));

        var model = new ServerLightningManagerViewModel();
        model.InternalCryptoCodes.AddRange(GetInternalCryptoCodes());
        model.SelectedCryptoCode = NormalizeSelectedCryptoCode(model.InternalCryptoCodes, selectedCryptoCode);
        ViewData["SelectedCryptoCode"] = model.SelectedCryptoCode;

        if (activePage == LightningManagerNavPages.Server &&
            LightningManagerCrypto.IsSupported(model.SelectedCryptoCode))
        {
            var internalStoreIds = await GetInternalLightningStoreIdsAsync(
                model.SelectedCryptoCode,
                cancellationToken);
            var accounts = await ledgerRepository.GetStoreAccountSnapshotsAsync(cancellationToken: cancellationToken);
            foreach (var account in accounts.Where(account => ShouldShowServerAccount(account, internalStoreIds)))
            {
                var isInternalNode = internalStoreIds.Contains(account.StoreId);
                model.Accounts.Add(new ServerLightningAccountViewModel
                {
                    StoreId = account.StoreId,
                    StoreName = string.IsNullOrWhiteSpace(account.StoreName) ? account.StoreId : account.StoreName,
                    CryptoCode = account.CryptoCode,
                    Enabled = account.Enabled,
                    CanManageAccess = isInternalNode,
                    AvailableDisplay = StoreLightningLedgerService.FormatMSat(account.AvailableMSat),
                    PendingDisplay = StoreLightningLedgerService.FormatMSat(account.ReservedMSat),
                    TotalDisplay = StoreLightningLedgerService.FormatMSat(account.TotalMSat)
                });
            }

            foreach (var cryptoCode in model.InternalCryptoCodes)
            {
                var ledgerTotal = await ledgerRepository.GetBitcoinLedgerTotalMSatAsync(cancellationToken);
                model.Nodes.Add(new ServerLightningNodeViewModel
                {
                    CryptoCode = cryptoCode,
                    LedgerTotalDisplay = StoreLightningLedgerService.FormatMSat(ledgerTotal)
                });
            }
        }

        if (TryGetNodeContext(model.SelectedCryptoCode, out var context, out var contextError))
        {
            switch (activePage)
            {
                case LightningManagerNavPages.Server:
                    model.NodeOverview = CreateNodeModel<OverviewViewModel>(context, "Overview");
                    await lightningManagerService.PopulateOverviewAsync(model.NodeOverview, context, cancellationToken);
                    break;
                case LightningManagerNavPages.ServerSend:
                    model.Send = CreateNodeModel<SendViewModel>(context, "Send");
                    break;
                case LightningManagerNavPages.ServerPeers:
                    model.Peers = CreateNodeModel<PeersViewModel>(context, "Peers");
                    await lightningManagerService.PopulatePeersAsync(model.Peers, context, cancellationToken);
                    break;
                case LightningManagerNavPages.ServerChannels:
                    model.Channels = CreateNodeModel<ChannelsViewModel>(context, "Channels");
                    await lightningManagerService.PopulateChannelsAsync(model.Channels, context, cancellationToken);
                    break;
            }
        }
        else
        {
            SetUnavailableNodeModel(model, activePage, contextError);
        }

        return model;
    }

    private async Task<ResolvedStoreAccount> ResolveInternalStoreAccountAsync(
        string? accountSelection,
        bool requireCurrentInternalNode,
        CancellationToken cancellationToken)
    {
        if (!TryParseAccount(accountSelection, out var storeId, out var cryptoCode))
        {
            return ResolvedStoreAccount.Failure("Select a store account.");
        }

        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return ResolvedStoreAccount.Failure(LightningManagerCrypto.UnsupportedMessage);
        }

        if (!TryGetInternalLightningClient(cryptoCode, out _))
        {
            return ResolvedStoreAccount.Failure("Selected account is not using an internal Lightning node.");
        }

        await using var ctx = dbContextFactory.CreateContext();
        var store = await ctx.Stores.SingleOrDefaultAsync(s => s.Id == storeId, cancellationToken);
        if (store is null)
        {
            return ResolvedStoreAccount.Failure("Selected account is not available for internal Lightning access.");
        }

        if (StoreLightningAccessService.IsInternalLightningNode(store, cryptoCode, handlers))
        {
            return ResolvedStoreAccount.Success(store.Id, cryptoCode);
        }

        if (requireCurrentInternalNode)
        {
            return ResolvedStoreAccount.Failure("Selected account is not using an internal Lightning node.");
        }

        var account = await ledgerRepository.GetAccountAsync(store.Id, cryptoCode, cancellationToken);
        return account is null
            ? ResolvedStoreAccount.Failure("Selected account is not available for internal Lightning access.")
            : ResolvedStoreAccount.Success(store.Id, cryptoCode);
    }

    internal static bool ShouldShowServerAccount(
        LightningLedgerAccountSnapshot account,
        IReadOnlySet<string> internalStoreIds)
    {
        return internalStoreIds.Contains(account.StoreId) ||
               account.Enabled ||
               account.TotalMSat != 0 ||
               account.ReservedMSat != 0;
    }

    private async Task<HashSet<string>> GetInternalLightningStoreIdsAsync(
        string? cryptoCode,
        CancellationToken cancellationToken)
    {
        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var ctx = dbContextFactory.CreateContext();
        var stores = await ctx.Stores.ToListAsync(cancellationToken);
        return stores
            .Where(store => StoreLightningAccessService.IsInternalLightningNode(store, cryptoCode, handlers))
            .Select(store => store.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private bool TryGetNodeContext(string? cryptoCode, out StoreLightningManagerContext context, out string? error)
    {
        context = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(cryptoCode))
        {
            error = "No internal Lightning node is configured.";
            return false;
        }

        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            error = LightningManagerCrypto.UnsupportedMessage;
            return false;
        }

        if (!TryGetInternalLightningClient(cryptoCode, out var client))
        {
            error = "The internal Lightning node is not available.";
            return false;
        }

        var network = networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        if (network is null)
        {
            error = "Unsupported cryptocurrency.";
            return false;
        }

        var capabilities = lightningCapabilityService.GetCapabilities(client, null, true);
        context = new StoreLightningManagerContext
        {
            Store = new StoreData { Id = "server", StoreName = "Server" },
            StoreId = "server",
            CryptoCode = cryptoCode,
            Network = network,
            Client = client,
            IsInternalNode = true,
            IsSharedBackend = true,
            IsReadOnly = false,
            Capabilities = capabilities,
            BackendCapabilities = capabilities,
            DisplayName = "Internal node"
        };
        return true;
    }

    private static T CreateNodeModel<T>(StoreLightningManagerContext context, string title)
        where T : LightningManagerPageViewModel, new()
    {
        return new T
        {
            StoreId = context.StoreId,
            CryptoCode = context.CryptoCode,
            Title = title,
            Capabilities = context.Capabilities,
            IsConfigured = context.IsConfigured,
            ConfigurationMessage = context.ConfigurationError,
            NodeDisplayName = context.DisplayName,
            NodeHost = context.NodeHost
        };
    }

    private static void SetUnavailableNodeModel(
        ServerLightningManagerViewModel model,
        string activePage,
        string? error)
    {
        switch (activePage)
        {
            case LightningManagerNavPages.Server:
                model.NodeOverview = CreateUnavailableNodeModel<OverviewViewModel>(model.SelectedCryptoCode, "Overview", error);
                break;
            case LightningManagerNavPages.ServerSend:
                model.Send = CreateUnavailableNodeModel<SendViewModel>(model.SelectedCryptoCode, "Send", error);
                break;
            case LightningManagerNavPages.ServerPeers:
                model.Peers = CreateUnavailableNodeModel<PeersViewModel>(model.SelectedCryptoCode, "Peers", error);
                break;
            case LightningManagerNavPages.ServerChannels:
                model.Channels = CreateUnavailableNodeModel<ChannelsViewModel>(model.SelectedCryptoCode, "Channels", error);
                break;
        }
    }

    private static T CreateUnavailableNodeModel<T>(string? cryptoCode, string title, string? error)
        where T : LightningManagerPageViewModel, new()
    {
        return new T
        {
            StoreId = "server",
            CryptoCode = cryptoCode ?? string.Empty,
            Title = title,
            Capabilities = LightningCapabilities.None,
            IsConfigured = false,
            ConfigurationMessage = error ?? "The internal Lightning node is not available.",
            NodeDisplayName = "Internal node"
        };
    }

    private static ActionResultViewModel Failure(string message)
    {
        return new ActionResultViewModel { IsSuccess = false, Message = message };
    }

    private void SetStatusMessage(ActionResultViewModel? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Message))
        {
            return;
        }

        TempData[result.IsSuccess ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = result.Message;
    }

    private void SetPaymentResult(SendResultDetailsViewModel? payment)
    {
        if (payment is null)
        {
            return;
        }

        TempData[PaymentStatusTempDataKey] = payment.Status.ToString();
        SetOptionalTempData(PaymentTotalTempDataKey, payment.TotalAmountDisplay);
        SetOptionalTempData(PaymentFeeTempDataKey, payment.FeeAmountDisplay);
        SetOptionalTempData(PaymentHashTempDataKey, payment.PaymentHash);
        SetOptionalTempData(PaymentPreimageTempDataKey, payment.Preimage);
    }

    private SendResultDetailsViewModel? GetPaymentResult()
    {
        if (TempData[PaymentStatusTempDataKey] is not string statusText ||
            !Enum.TryParse<LightningPaymentStatus>(statusText, out var status))
        {
            return null;
        }

        return new SendResultDetailsViewModel
        {
            Status = status,
            TotalAmountDisplay = TempData[PaymentTotalTempDataKey] as string,
            FeeAmountDisplay = TempData[PaymentFeeTempDataKey] as string,
            PaymentHash = TempData[PaymentHashTempDataKey] as string,
            Preimage = TempData[PaymentPreimageTempDataKey] as string
        };
    }

    private void SetOptionalTempData(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            TempData[key] = value;
        }
    }

    private IReadOnlyList<string> GetInternalCryptoCodes()
    {
        return lightningNetworkOptions.Value.InternalLightningByCryptoCode.Keys
            .Where(LightningManagerCrypto.IsSupported)
            .Select(_ => LightningManagerCrypto.Bitcoin)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cryptoCode => cryptoCode, StringComparer.Ordinal)
            .ToList();
    }

    private bool TryGetInternalLightningClient(string cryptoCode, out ILightningClient client)
    {
        if (!LightningManagerCrypto.IsSupported(cryptoCode))
        {
            client = null!;
            return false;
        }

        foreach (var pair in lightningNetworkOptions.Value.InternalLightningByCryptoCode)
        {
            if (pair.Key.Equals(cryptoCode, StringComparison.OrdinalIgnoreCase))
            {
                client = pair.Value;
                return true;
            }
        }

        client = null!;
        return false;
    }

    private static bool TryParseAccount(string? account, out string storeId, out string cryptoCode)
    {
        storeId = string.Empty;
        cryptoCode = string.Empty;
        var parts = account?.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 })
        {
            return false;
        }

        storeId = parts[0];
        cryptoCode = parts[1];
        return !string.IsNullOrWhiteSpace(storeId) && !string.IsNullOrWhiteSpace(cryptoCode);
    }

    private static string? NormalizeSelectedCryptoCode(
        IReadOnlyList<string> internalCryptoCodes,
        string? selectedCryptoCode)
    {
        if (!string.IsNullOrWhiteSpace(selectedCryptoCode))
        {
            var normalized = selectedCryptoCode.ToUpperInvariant();
            if (internalCryptoCodes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return internalCryptoCodes.FirstOrDefault();
    }

    private sealed record ResolvedStoreAccount(
        bool IsSuccess,
        string StoreId,
        string CryptoCode,
        string? ErrorMessage)
    {
        public static ResolvedStoreAccount Success(string storeId, string cryptoCode)
        {
            return new ResolvedStoreAccount(true, storeId, cryptoCode, null);
        }

        public static ResolvedStoreAccount Failure(string message)
        {
            return new ResolvedStoreAccount(false, string.Empty, string.Empty, message);
        }
    }
}
