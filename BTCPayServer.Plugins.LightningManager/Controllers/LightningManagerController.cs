#nullable enable
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
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
    private readonly LightningManagerResultStore _resultStore;
    private readonly LightningManagerChannelConfirmationStore _channelConfirmationStore;

    public LightningManagerController(
        IStoreLightningManagerContextFactory contextFactory,
        ILightningManagerService lightningManagerService,
        LightningManagerResultStore resultStore,
        LightningManagerChannelConfirmationStore channelConfirmationStore)
    {
        _contextFactory = contextFactory;
        _lightningManagerService = lightningManagerService;
        _resultStore = resultStore;
        _channelConfirmationStore = channelConfirmationStore;
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
    public async Task<IActionResult> Send(
        [FromRoute] string cryptoCode,
        CancellationToken cancellationToken,
        [FromQuery] string? resultId = null)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var userId = User.GetId();
        var model = CreatePageModel<SendViewModel>(context, "Pay", LightningManagerNavPages.Send);
        if (_resultStore.TryGetPayment(resultId, userId, context.StoreId, context.CryptoCode, out var executionResult) ||
            (string.IsNullOrWhiteSpace(resultId) &&
             _resultStore.TryGetPendingPayment(userId, context.StoreId, context.CryptoCode, out executionResult)))
        {
            model.Result = executionResult!.Result;
            model.Payment = executionResult.Payment;
        }
        else if (!string.IsNullOrWhiteSpace(resultId))
        {
            model.Result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = "Payment result is no longer available. Check the Lightning node before retrying."
            };
        }
        return View(model);
    }

    [HttpPost("send/preview")]
    public async Task<IActionResult> PreviewSend(
        [FromRoute] string cryptoCode,
        [FromForm] string? bolt11,
        [FromForm] string? amountSats,
        [FromForm] string? maxFeeSats,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var model = CreatePageModel<SendViewModel>(context, "Pay", LightningManagerNavPages.Send);
        model.Bolt11 = bolt11;
        model.AmountSats = amountSats;
        model.MaxFeeSats = maxFeeSats;

        if (_lightningManagerService.TryCreateSendPreview(context, bolt11, amountSats, maxFeeSats, out var preview, out var error))
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
    public async Task<IActionResult> ExecuteSend(
        [FromRoute] string cryptoCode,
        [FromForm] string bolt11,
        [FromForm] string? amountSats,
        [FromForm] string? maxFeeSats,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var result = await _lightningManagerService.SendAsync(context, bolt11, amountSats, maxFeeSats, cancellationToken);
        var resultId = _resultStore.StorePayment(User.GetId(), context.StoreId, context.CryptoCode, result);
        return RedirectToAction(nameof(Send), new { storeId = context.StoreId, cryptoCode = context.CryptoCode, resultId });
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
    public async Task<IActionResult> ConnectPeer([FromRoute] string cryptoCode, [FromForm] string? nodeUri, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var result = await _lightningManagerService.ConnectPeerAsync(context, nodeUri, cancellationToken);
        SetStatusMessage(result);
        return RedirectToAction(nameof(Peers), new { storeId = context.StoreId, cryptoCode = context.CryptoCode });
    }

    [HttpGet("channels")]
    public async Task<IActionResult> Channels(
        [FromRoute] string cryptoCode,
        CancellationToken cancellationToken,
        [FromQuery] string? resultId = null)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var userId = User.GetId();
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningManagerNavPages.Channels);
        if (_resultStore.TryGetChannel(resultId, userId, context.StoreId, context.CryptoCode, out var channelResult) ||
            (string.IsNullOrWhiteSpace(resultId) &&
             _resultStore.TryGetPendingChannel(userId, context.StoreId, context.CryptoCode, out channelResult)))
        {
            model.Result = channelResult;
        }
        else if (!string.IsNullOrWhiteSpace(resultId))
        {
            model.Result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = "Channel result is no longer available. Check the Lightning node before retrying."
            };
        }
        await _lightningManagerService.PopulateChannelsAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpPost("channels/preview")]
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
            model.OpenChannelConfirmationToken = _channelConfirmationStore.Create(
                User.GetId(),
                context.StoreId,
                context.CryptoCode,
                nodeUri!,
                channelAmountSats!,
                feeRateSatsPerByte);
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
    public async Task<IActionResult> OpenChannel(
        [FromRoute] string cryptoCode,
        [FromForm] string nodeUri,
        [FromForm] string channelAmountSats,
        [FromForm] string? feeRateSatsPerByte,
        CancellationToken cancellationToken,
        [FromForm] string? confirmationToken = null)
    {
        var context = await GetContextAsync(cryptoCode, cancellationToken);
        var userId = User.GetId();
        ActionResultViewModel result;
        if (!_channelConfirmationStore.TryConsume(
                confirmationToken,
                userId,
                context.StoreId,
                context.CryptoCode,
                nodeUri,
                channelAmountSats,
                feeRateSatsPerByte))
        {
            result = new ActionResultViewModel
            {
                IsSuccess = false,
                Message = "This channel confirmation is invalid, expired, or already used. Check the Lightning node before previewing again."
            };
        }
        else
        {
            result = await _lightningManagerService.OpenChannelAsync(
                context,
                nodeUri,
                channelAmountSats,
                feeRateSatsPerByte,
                cancellationToken);
        }

        var resultId = _resultStore.StoreChannel(userId, context.StoreId, context.CryptoCode, result);
        return RedirectToAction(nameof(Channels), new { storeId = context.StoreId, cryptoCode = context.CryptoCode, resultId });
    }

    protected virtual async Task<StoreLightningManagerContext> GetContextAsync(string cryptoCode, CancellationToken cancellationToken)
    {
        return await _contextFactory.CreateAsync(
            HttpContext.GetStoreData(),
            cryptoCode,
            cancellationToken);
    }

    private void SetStatusMessage(ActionResultViewModel? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Message))
        {
            return;
        }

        TempData[result.IsSuccess ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = result.Message;
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
        return model;
    }
}
