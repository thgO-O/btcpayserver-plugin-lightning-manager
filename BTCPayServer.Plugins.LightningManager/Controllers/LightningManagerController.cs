#nullable enable
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LightningManager.Controllers;

[Route("stores/{storeId}/lightning/{cryptoCode}/manager")]
[Authorize(Policy = Policies.CanUseLightningNodeInStore, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class LightningManagerController : Controller
{
    private readonly IStoreLightningManagerContextFactory _contextFactory;
    private readonly ILightningManagerService _lightningManagerService;

    public LightningManagerController(
        IStoreLightningManagerContextFactory contextFactory,
        ILightningManagerService lightningManagerService)
    {
        _contextFactory = contextFactory;
        _lightningManagerService = lightningManagerService;
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
        var model = CreatePageModel<OverviewViewModel>(context, "Overview", LightningManagerNavPages.Overview);
        await _lightningManagerService.PopulateOverviewAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpGet("send")]
    public async Task<IActionResult> Send([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        return View(CreatePageModel<SendViewModel>(context, "Send", LightningManagerNavPages.Send));
    }

    [HttpPost("send/preview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PreviewSend([FromRoute] string cryptoCode, [FromForm] string? bolt11, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<SendViewModel>(context, "Send", LightningManagerNavPages.Send);
        model.Bolt11 = bolt11;

        if (_lightningManagerService.TryCreateSendPreview(context, bolt11, out var preview, out var error))
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
        var model = CreatePageModel<SendViewModel>(context, "Send", LightningManagerNavPages.Send);
        model.Bolt11 = bolt11;
        var result = await _lightningManagerService.SendAsync(context, bolt11, cancellationToken);
        model.Result = result.Result;
        model.Payment = result.Payment;
        return View("Send", model);
    }

    [HttpGet("peers")]
    public async Task<IActionResult> Peers([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<PeersViewModel>(context, "Peers", LightningManagerNavPages.Peers);
        await _lightningManagerService.PopulatePeersAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpPost("peers")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConnectPeer([FromRoute] string cryptoCode, [FromForm] string? nodeUri, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<PeersViewModel>(context, "Peers", LightningManagerNavPages.Peers);
        model.NodeUri = nodeUri;
        model.Result = await _lightningManagerService.ConnectPeerAsync(context, nodeUri, cancellationToken);
        await _lightningManagerService.PopulatePeersAsync(model, context, cancellationToken);
        return View("Peers", model);
    }

    [HttpGet("channels")]
    public async Task<IActionResult> Channels([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningManagerNavPages.Channels);
        await _lightningManagerService.PopulateChannelsAsync(model, context, cancellationToken);
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
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningManagerNavPages.Channels);
        model.NodeUri = nodeUri;
        model.ChannelAmountSats = channelAmountSats;
        model.FeeRateSatsPerByte = feeRateSatsPerByte;

        if (_lightningManagerService.TryCreateOpenChannelPreview(
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

        await _lightningManagerService.PopulateChannelsAsync(model, context, cancellationToken);
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
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningManagerNavPages.Channels);
        model.NodeUri = nodeUri;
        model.ChannelAmountSats = channelAmountSats;
        model.FeeRateSatsPerByte = feeRateSatsPerByte;
        model.Result = await _lightningManagerService.OpenChannelAsync(
            context,
            nodeUri,
            channelAmountSats,
            feeRateSatsPerByte,
            cancellationToken);
        await _lightningManagerService.PopulateChannelsAsync(model, context, cancellationToken);
        return View("Channels", model);
    }

    protected virtual async Task<StoreLightningManagerContext> GetContextAsync(string cryptoCode, CancellationToken cancellationToken)
    {
        return await _contextFactory.CreateAsync(
            HttpContext.GetStoreData(),
            cryptoCode,
            User,
            HttpContext,
            cancellationToken);
    }

    private T CreatePageModel<T>(StoreLightningManagerContext context, string title, string activePage)
        where T : LightningManagerPageViewModel, new()
    {
        ViewData.SetLayoutModel(new LayoutModel(activePage, $"{context.CryptoCode} Lightning Manager: {title}")
            .SetCategory(WellKnownCategories.ForLightning(context.CryptoCode)));

        var model = new T
        {
            StoreId = context.StoreId,
            CryptoCode = context.CryptoCode,
            Title = title,
            Capabilities = context.Capabilities,
            Tabs = _lightningManagerService.CreateTabs(context, activePage),
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
