using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Fido2;
using BTCPayServer.Fido2.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Filters;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using WalletRepository = BTCPayServer.Plugins.LightningManager.Wallet.WalletRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.LightningManager.Controllers;

[Route("stores/{storeId}/lightning/BTC/wallet")]
[Authorize(Policy = Policies.CanUseLightningNodeInStore, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[ServiceFilter(typeof(LightningManagerInternalNodeAuthorizationFilter))]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[TypeFilter(typeof(WalletExceptionFilter))]
public sealed class LightningWalletController(
    WalletService wallet, WalletRepository repository, StoreRepository stores,
    ApplicationDbContextFactory users, Fido2Service fido2,
    WalletAuthorizationStore confirmations, TimeProvider clock,
    PermissionService permissions, UserManager<ApplicationUser> userManager) : Controller
{
    private string StoreId => HttpContext.GetStoreData().Id;
    private string UserId => User.GetId()!;

    private async Task<WalletNode> NodeAsync(CancellationToken token)
    {
        if ((await stores.GetSettingAsync<WalletSettings>(StoreId, WalletSettings.Key))?.Enabled != true)
            throw new WalletException("Wallet Mode is disabled for this store.");
        repository.RequireReady();
        var freshStore = await stores.FindStore(StoreId, UserId) ?? throw new WalletException("Store access is unavailable.");
        if (!freshStore.HasPolicy(UserId, Policies.CanUseLightningNodeInStore, permissions))
            throw new WalletException("Lightning node access is unavailable.");
        var user = await userManager.FindByIdAsync(UserId);
        if (user is null || user.IsDisabled || user.RequiresApproval)
            throw new WalletException("Your account cannot access this wallet.");
        var node = await wallet.GetNodeAsync(freshStore, token);
        if (node.Context.IsInternalNode && !await userManager.IsInRoleAsync(user, Roles.ServerAdmin))
            throw new WalletException("The internal node requires Server Admin access.");
        return node;
    }

    private async Task<WalletPageModel> ModelAsync(WalletNode node, CancellationToken token)
    {
        await using var db = users.CreateContext();
        return new WalletPageModel
        {
            StoreId = StoreId, StoreName = HttpContext.GetStoreData().StoreName, Enabled = true, IsInternal = node.Context.IsInternalNode,
            HasPasskey = await db.Fido2Credentials.AnyAsync(x => x.ApplicationUserId == UserId && x.Type == Fido2Credential.CredentialType.Passkey, token)
        };
    }

    [HttpGet("settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Settings()
    {
        return View(new WalletPageModel { StoreId = StoreId, StoreName = HttpContext.GetStoreData().StoreName,
            Enabled = (await stores.GetSettingAsync<WalletSettings>(StoreId, WalletSettings.Key))?.Enabled == true });
    }

    [HttpPost("settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Settings(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            repository.RequireReady();
            await wallet.GetNodeAsync(HttpContext.GetStoreData(), cancellationToken);
        }
        await stores.UpdateSetting(StoreId, WalletSettings.Key, new WalletSettings { Enabled = enabled });
        return RedirectToAction(enabled ? nameof(Home) : nameof(Settings), new { storeId = StoreId });
    }

    [HttpGet("")]
    public async Task<IActionResult> Home(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var model = await ModelAsync(node, cancellationToken);
        model.Operations = await repository.ListAsync(StoreId, node.Identity, 5, cancellationToken);
        try
        {
            var balance = await node.Context.Client!.GetBalance(cancellationToken);
            model.BalanceSats = balance.OffchainBalance?.Local?.ToUnit(LightMoneyUnit.Satoshi);
            if (model.BalanceSats is null) model.BalanceError = "Balance is unavailable.";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { model.BalanceError = "Could not load the node balance. Try again later."; }
        return View(model);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var model = await ModelAsync(node, cancellationToken);
        model.Operations = await repository.ListAsync(StoreId, node.Identity, 100, cancellationToken);
        return View(model);
    }

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var balance = await node.Context.Client!.GetBalance(cancellationToken);
        var sats = balance.OffchainBalance?.Local?.ToUnit(LightMoneyUnit.Satoshi);
        return sats is null ? StatusCode(503, new { error = "Balance is unavailable." }) : Json(new { balanceSats = sats });
    }

    [HttpGet("history/data")]
    public async Task<IActionResult> HistoryData(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var operations = await repository.ListAsync(StoreId, node.Identity, 100, cancellationToken);
        return Json(operations.Select(x => new { id = x.Id, direction = x.Direction, description = x.Description,
            amountMsat = x.AmountMsat, feeMsat = x.FeeMsat, state = x.State, createdAt = x.CreatedAt }));
    }

    [HttpGet("send")]
    public async Task<IActionResult> Send(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        return View(await ModelAsync(node, cancellationToken));
    }

    [HttpPost("send/preview")]
    public async Task<IActionResult> Preview(string? bolt11, string? amountSats, string? maxFeeSats, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var model = await ModelAsync(node, cancellationToken);
        model.Preview = wallet.Preview(node, bolt11, amountSats, maxFeeSats);
        if (model.HasPasskey)
        {
            var options = await fido2.RequestLogin(null);
            if (options is null) throw new WalletException("Passkey verification is unavailable.");
            model.ConfirmationId = confirmations.Create(new WalletAuthorization(UserId, StoreId, node.Identity,
                node.Context.BackendFingerprint, model.Preview, options, clock.GetUtcNow().AddMinutes(2)));
        }
        return View("Send", model);
    }

    [HttpPost("send/challenge")]
    public async Task<IActionResult> Challenge(string confirmationId, CancellationToken cancellationToken)
    {
        // Return the challenge without consuming it. Execution atomically consumes it.
        var node = await NodeAsync(cancellationToken);
        var entry = confirmations.Get(confirmationId, UserId, StoreId, node.Identity, node.Context.BackendFingerprint);
        if (entry is null) throw new WalletException("This confirmation expired. Review the payment again.");
        return Content(entry.Options.ToJson(), "application/json");
    }

    [HttpPost("send/execute")]
    public async Task<IActionResult> Execute(string confirmationId, string assertion, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        if (!ModelState.IsValid || Request.Form.Keys.Any(key => key is not ("confirmationId" or "assertion" or "__RequestVerificationToken")))
            throw new WalletException("Changing payment details requires a new payment review and confirmation.");
        var entry = confirmations.Consume(confirmationId, UserId, StoreId, node.Identity, node.Context.BackendFingerprint);
        if (entry is null) throw new WalletException("Invalid, expired or already used payment confirmation.");
        var result = await fido2.CompleteLogin(UserId, assertion, entry.Options, passKey: true);
        if (result is not Fido2Service.LoginResult.Success success || success.User.Id != UserId)
            throw new WalletException("Passkey verification failed. Review the payment again.");
        if (entry.ExpiresAt <= clock.GetUtcNow())
            throw new WalletException("This confirmation expired. Review the payment again.");
        // Recheck store/backend and session authorization after WebAuthn verification.
        node = await NodeAsync(cancellationToken);
        if (node.Identity != entry.NodeIdentity || node.Context.BackendFingerprint != entry.ConfigurationFingerprint)
            throw new WalletException("The Lightning configuration changed. Review the payment again.");
        var operation = await wallet.PayAsync(node, UserId, entry.Preview);
        return Json(new { url = Url.Action(nameof(Details), new { storeId = StoreId, id = operation.Id }) });
    }

    [HttpGet("receive")]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        return View(await ModelAsync(node, cancellationToken));
    }

    [HttpPost("receive")]
    public async Task<IActionResult> Receive(long amountSats, string? description, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) throw new WalletException("Enter a positive whole number of sats.");
        var node = await NodeAsync(cancellationToken);
        var operation = await wallet.ReceiveAsync(node, UserId, amountSats, description, cancellationToken);
        return RedirectToAction(nameof(Details), new { storeId = StoreId, id = operation.Id });
    }

    [HttpGet("operations/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var operation = await repository.GetAsync(id, StoreId, node.Identity, cancellationToken);
        if (operation is null) return NotFound();
        try { await wallet.ReconcileAsync(node, operation, cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* Preserve the last known state. */ }
        var model = await ModelAsync(node, cancellationToken);
        model.Operation = operation;
        return View(model);
    }

    [HttpGet("operations/{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(cancellationToken);
        var operation = await repository.GetAsync(id, StoreId, node.Identity, cancellationToken);
        if (operation is null) return NotFound();
        await wallet.ReconcileAsync(node, operation, cancellationToken);
        return Json(new { state = operation.State, final = operation.IsFinal });
    }
}

public sealed class WalletExceptionFilter(ILogger<WalletExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var message = context.Exception is WalletException ? context.Exception.Message : "Wallet operation unavailable. Check the history and node before retrying.";
        var status = context.Exception is WalletException ? 400 : 503;
        logger.LogWarning("Wallet request failed ({ErrorType})", context.Exception.GetType().Name);
        context.Result = context.HttpContext.Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.Ordinal) is true)
            ? new JsonResult(new { error = message }) { StatusCode = status }
            : new ViewResult { ViewName = "Error", StatusCode = status,
                ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<WalletPageModel>(
                    new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), context.ModelState)
                { Model = new WalletPageModel { StoreId = context.RouteData.Values["storeId"]?.ToString() ?? "", Error = message } } };
        context.ExceptionHandled = true;
    }
}
