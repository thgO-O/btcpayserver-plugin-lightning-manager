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
    private readonly LightningManagerService _lightningManagerService;
    private readonly LightningManagerResultStore _resultStore;
    private readonly LightningManagerChannelConfirmationStore _channelConfirmationStore;
    private readonly LightningManagerPaymentConfirmationStore _paymentConfirmationStore;

    public LightningManagerController(
        IStoreLightningManagerContextFactory contextFactory,
        LightningManagerService lightningManagerService,
        LightningManagerResultStore resultStore,
        LightningManagerChannelConfirmationStore channelConfirmationStore,
        LightningManagerPaymentConfirmationStore paymentConfirmationStore)
    {
        _contextFactory = contextFactory;
        _lightningManagerService = lightningManagerService;
        _resultStore = resultStore;
        _channelConfirmationStore = channelConfirmationStore;
        _paymentConfirmationStore = paymentConfirmationStore;
    }

    [HttpGet("")]
    public IActionResult Index([FromRoute] string storeId, [FromRoute] string cryptoCode)
    {
        return RedirectToAction(nameof(Overview), new { storeId, cryptoCode });
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview([FromRoute] string cryptoCode, CancellationToken cancellationToken)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var model = CreatePageModel<OverviewViewModel>(context, "Overview", LightningManagerNavPages.Overview);
        await _lightningManagerService.PopulateOverviewAsync(model, context, cancellationToken);
        return View(model);
    }

    [HttpGet("send")]
    public IActionResult Send(
        [FromRoute] string cryptoCode,
        [FromQuery] string? resultId = null)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var userId = User.GetId();
        var model = CreatePageModel<SendViewModel>(context, "Pay", LightningManagerNavPages.Send);
        if (_resultStore.TryGetPayment(
                resultId,
                userId,
                context.StoreId,
                context.CryptoCode,
                context.BackendFingerprint,
                out var executionResult) ||
            (string.IsNullOrWhiteSpace(resultId) &&
             _resultStore.TryGetPendingPayment(
                 userId,
                 context.StoreId,
                 context.CryptoCode,
                 context.BackendFingerprint,
                 out executionResult)))
        {
            model.Result = executionResult!.Result;
            model.Payment = executionResult.Payment;
        }
        else if (!string.IsNullOrWhiteSpace(resultId))
        {
            model.Result = Failure("Payment result is no longer available. Check the Lightning node before retrying.");
        }
        return View(model);
    }

    [HttpPost("send/preview")]
    public IActionResult PreviewSend(
        [FromRoute] string cryptoCode,
        [FromForm] string? bolt11,
        [FromForm] string? amountSats,
        [FromForm] string? maxFeeSats)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var model = CreatePageModel<SendViewModel>(context, "Pay", LightningManagerNavPages.Send);
        model.Bolt11 = bolt11;
        model.AmountSats = amountSats;
        model.MaxFeeSats = maxFeeSats;

        if (_lightningManagerService.TryCreateSendPreview(context, bolt11, amountSats, maxFeeSats, out var preview, out var error))
        {
            model.Preview = preview;
            model.PaymentConfirmationToken = _paymentConfirmationStore.Create(
                User.GetId(),
                context.StoreId,
                context.CryptoCode,
                context.BackendFingerprint,
                preview!.Bolt11,
                preview.UserAmountSats,
                preview.MaxFeeSats);
        }
        else
        {
            model.Result = Failure(error ?? "The invoice is invalid.");
        }

        return View("Send", model);
    }

    [HttpPost("send/execute")]
    public async Task<IActionResult> ExecuteSend(
        [FromRoute] string cryptoCode,
        [FromForm] string bolt11,
        [FromForm] string? amountSats,
        [FromForm] string? maxFeeSats,
        CancellationToken cancellationToken,
        [FromForm] string? confirmationToken = null)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var userId = User.GetId();
        SendExecutionResult result;
        if (!_paymentConfirmationStore.TryConsume(
                confirmationToken,
                userId,
                context.StoreId,
                context.CryptoCode,
                context.BackendFingerprint,
                bolt11,
                amountSats,
                maxFeeSats))
        {
            result = new SendExecutionResult
            {
                Result = Failure("This payment confirmation is invalid, expired, or already used. If it may already have been submitted, check the Lightning node before previewing again.")
            };
        }
        else
        {
            result = await _lightningManagerService.SendAsync(
                context,
                bolt11,
                amountSats,
                maxFeeSats,
                cancellationToken);
        }

        var resultId = _resultStore.StorePayment(
            userId,
            context.StoreId,
            context.CryptoCode,
            context.BackendFingerprint,
            result);
        return RedirectToAction(nameof(Send), new { storeId = context.StoreId, cryptoCode = context.CryptoCode, resultId });
    }

    [HttpGet("peers")]
    public IActionResult Peers([FromRoute] string cryptoCode)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var model = CreatePageModel<PeersViewModel>(context, "Peers", LightningManagerNavPages.Peers);
        return View(model);
    }

    [HttpPost("peers")]
    public async Task<IActionResult> ConnectPeer([FromRoute] string cryptoCode, [FromForm] string? nodeUri, CancellationToken cancellationToken)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var result = await _lightningManagerService.ConnectPeerAsync(context, nodeUri, cancellationToken);
        TempData[result.IsSuccess ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = result.Message;
        return RedirectToAction(nameof(Peers), new { storeId = context.StoreId, cryptoCode = context.CryptoCode });
    }

    [HttpGet("channels")]
    public async Task<IActionResult> Channels(
        [FromRoute] string cryptoCode,
        CancellationToken cancellationToken,
        [FromQuery] string? resultId = null)
    {
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var userId = User.GetId();
        var model = CreatePageModel<ChannelsViewModel>(context, "Channels", LightningManagerNavPages.Channels);
        if (_resultStore.TryGetChannel(
                resultId,
                userId,
                context.StoreId,
                context.CryptoCode,
                context.BackendFingerprint,
                out var channelResult) ||
            (string.IsNullOrWhiteSpace(resultId) &&
             _resultStore.TryGetPendingChannel(
                 userId,
                 context.StoreId,
                 context.CryptoCode,
                 context.BackendFingerprint,
                 out channelResult)))
        {
            model.Result = channelResult;
        }
        else if (!string.IsNullOrWhiteSpace(resultId))
        {
            model.Result = Failure("Channel result is no longer available. Check the Lightning node before retrying.");
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
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
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
                context.BackendFingerprint,
                nodeUri!,
                channelAmountSats!,
                feeRateSatsPerByte);
        }
        else
        {
            model.Result = Failure(error ?? "The channel request is invalid.");
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
        var context = _contextFactory.Create(HttpContext.GetStoreData(), cryptoCode);
        var userId = User.GetId();
        ActionResultViewModel result;
        if (!_channelConfirmationStore.TryConsume(
                confirmationToken,
                userId,
                context.StoreId,
                context.CryptoCode,
                context.BackendFingerprint,
                nodeUri,
                channelAmountSats,
                feeRateSatsPerByte))
        {
            result = Failure("This channel confirmation is invalid, expired, or already used. Check the Lightning node before previewing again.");
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

        var resultId = _resultStore.StoreChannel(
            userId,
            context.StoreId,
            context.CryptoCode,
            context.BackendFingerprint,
            result);
        return RedirectToAction(nameof(Channels), new { storeId = context.StoreId, cryptoCode = context.CryptoCode, resultId });
    }

    private static ActionResultViewModel Failure(string message)
    {
        return new ActionResultViewModel
        {
            IsSuccess = false,
            Message = message
        };
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
            Capabilities = context.Capabilities,
            IsConfigured = context.IsConfigured,
            ConfigurationMessage = context.ConfigurationError
        };
        return model;
    }
}
