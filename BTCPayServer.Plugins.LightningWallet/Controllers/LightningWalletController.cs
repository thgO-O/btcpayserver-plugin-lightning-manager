#nullable enable
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.LightningWallet.Services;
using BTCPayServer.Plugins.LightningWallet.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LightningWallet.Controllers;

[Route("stores/{storeId}/lightning/{cryptoCode}/wallet")]
[Authorize(Policy = Policies.CanUseLightningNodeInStore, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class LightningWalletController : Controller
{
    private readonly IStoreLightningWalletContextFactory _contextFactory;
    private readonly ILightningWalletService _lightningWalletService;

    public LightningWalletController(
        IStoreLightningWalletContextFactory contextFactory,
        ILightningWalletService lightningWalletService)
    {
        _contextFactory = contextFactory;
        _lightningWalletService = lightningWalletService;
    }

    [HttpGet("")]
    public IActionResult Index([FromRoute] string storeId, [FromRoute] string cryptoCode)
    {
        return RedirectToAction(nameof(Overview), new { storeId, cryptoCode });
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<OverviewViewModel>(context, "Overview", LightningWalletNavPages.Overview);
        await _lightningWalletService.PopulateOverviewAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpGet("send")]
    public async Task<IActionResult> Send([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        return View(CreatePageModel<SendViewModel>(context, "Send", LightningWalletNavPages.Send));
    }

    [HttpPost("send/preview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PreviewSend([FromRoute] string cryptoCode, [FromForm] string? bolt11, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<SendViewModel>(context, "Send", LightningWalletNavPages.Send);
        model.Bolt11 = bolt11;

        if (_lightningWalletService.TryCreateSendPreview(context, bolt11, out var preview, out var error))
        {
            model.Preview = preview;
        }
        else
        {
            model.Result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = error ?? "The invoice is invalid."
            };
        }

        return View("Send", model);
    }

    [HttpPost("send/execute")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ExecuteSend([FromRoute] string cryptoCode, [FromForm] string bolt11, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<SendViewModel>(context, "Send", LightningWalletNavPages.Send);
        model.Bolt11 = bolt11;
        var result = await _lightningWalletService.SendAsync(context, bolt11, cancellationToken);
        model.Result = result.Result;
        model.Payment = result.Payment;
        return View("Send", model);
    }

    [HttpGet("peers")]
    public async Task<IActionResult> Peers([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<PeersViewModel>(context, "Peers", LightningWalletNavPages.Peers);
        await _lightningWalletService.PopulatePeersAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpPost("peers")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConnectPeer([FromRoute] string cryptoCode, [FromForm] string? nodeUri, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<PeersViewModel>(context, "Peers", LightningWalletNavPages.Peers);
        model.NodeUri = nodeUri;
        model.Result = await _lightningWalletService.ConnectPeerAsync(context, nodeUri, cancellationToken);
        await _lightningWalletService.PopulatePeersAsync(model, context, cancellationToken);
        return View("Peers", model);
    }

    [HttpGet("channels")]
    public async Task<IActionResult> Channels([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningWalletNavPages.Channels);
        await _lightningWalletService.PopulateChannelsAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpPost("channels/preview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PreviewChannel(
        [FromRoute] string cryptoCode,
        [FromForm] string? nodeUri,
        [FromForm] string? channelAmountSats,
        [FromForm] string? feeRateSatsPerByte,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningWalletNavPages.Channels);
        model.NodeUri = nodeUri;
        model.ChannelAmountSats = channelAmountSats;
        model.FeeRateSatsPerByte = feeRateSatsPerByte;

        if (_lightningWalletService.TryCreateOpenChannelPreview(
                context,
                nodeUri,
                channelAmountSats,
                feeRateSatsPerByte,
                out var preview,
                out var error))
        {
            model.Preview = preview;
        }
        else
        {
            model.Result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = error ?? "The channel request is invalid."
            };
        }

        await _lightningWalletService.PopulateChannelsAsync(model, context, cancellationToken);
        return View("Channels", model);
    }

    [HttpPost("channels/open")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> OpenChannel(
        [FromRoute] string cryptoCode,
        [FromForm] string nodeUri,
        [FromForm] string channelAmountSats,
        [FromForm] string? feeRateSatsPerByte,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningWalletNavPages.Channels);
        model.NodeUri = nodeUri;
        model.ChannelAmountSats = channelAmountSats;
        model.FeeRateSatsPerByte = feeRateSatsPerByte;
        model.Result = await _lightningWalletService.OpenChannelAsync(
            context,
            nodeUri,
            channelAmountSats,
            feeRateSatsPerByte,
            cancellationToken);
        await _lightningWalletService.PopulateChannelsAsync(model, context, cancellationToken);
        return View("Channels", model);
    }

    [HttpPost("channels/close/preview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PreviewCloseChannel(
        [FromRoute] string cryptoCode,
        [FromForm] string? channelId,
        [FromForm] string? channelPoint,
        [FromForm] string? remoteNode,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningWalletNavPages.Channels);

        if (_lightningWalletService.TryCreateCloseChannelPreview(
                context,
                channelId,
                channelPoint,
                remoteNode,
                out var preview,
                out var error))
        {
            model.ClosePreview = preview;
        }
        else
        {
            model.Result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = error ?? "The close channel request is invalid."
            };
        }

        await _lightningWalletService.PopulateChannelsAsync(model, context, cancellationToken);
        return View("Channels", model);
    }

    [HttpPost("channels/close")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> CloseChannel(
        [FromRoute] string cryptoCode,
        [FromForm] string? channelId,
        [FromForm] string? channelPoint,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningWalletNavPages.Channels);
        model.Result = await _lightningWalletService.CloseChannelAsync(
            context,
            channelId,
            channelPoint,
            cancellationToken);
        await _lightningWalletService.PopulateChannelsAsync(model, context, cancellationToken);
        return View("Channels", model);
    }

    protected virtual async Task<StoreLightningWalletContext> GetContextAsync(string cryptoCode, CancellationToken cancellationToken)
    {
        return await _contextFactory.CreateAsync(
            HttpContext.GetStoreData(),
            cryptoCode,
            User,
            HttpContext,
            cancellationToken);
    }

    private T CreatePageModel<T>(StoreLightningWalletContext context, string title, string activePage)
        where T : LightningWalletPageViewModel, new()
    {
        ViewData.SetLayoutModel(new LayoutModel(activePage, $"{context.CryptoCode} Lightning Wallet: {title}")
            .SetCategory(WellKnownCategories.ForLightning(context.CryptoCode)));

        var model = new T
        {
            StoreId = context.StoreId,
            CryptoCode = context.CryptoCode,
            Title = title,
            Capabilities = context.Capabilities,
            Tabs = _lightningWalletService.CreateTabs(context, activePage),
            IsConfigured = context.IsConfigured,
            ConfigurationMessage = context.ConfigurationError,
            NodeDisplayName = context.DisplayName,
            NodeHost = context.NodeHost
        };

        if (!string.IsNullOrWhiteSpace(context.SharedBackendNotice))
        {
            model.Notices.Add(context.SharedBackendNotice);
        }

        return model;
    }
}
