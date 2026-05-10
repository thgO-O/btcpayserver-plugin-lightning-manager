#nullable enable
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using UpdatePaymentMethodRequest = BTCPayServer.Client.Models.UpdatePaymentMethodRequest;

namespace BTCPayServer.Plugins.LightningManager.Services;

public class LightningManagerStoreAccessFilter(
    PaymentMethodHandlerDictionary handlers,
    ILightningLedgerRepository ledgerRepository,
    ApplicationDbContextFactory? dbContextFactory = null) : IAsyncActionFilter
{
    private const string AccessDisabledMessage = "Lightning access is disabled for this store by the server administrator.";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (await BlocksDisabledInternalLightningNodeApiAsync(context) ||
            await BlocksNativeLightningEnableAsync(context))
        {
            context.Result = IsGreenfieldRequest(context)
                ? new ObjectResult(new
                {
                    code = "lightning-manager-access-disabled",
                    message = AccessDisabledMessage
                })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                }
                : new ContentResult
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                    Content = AccessDisabledMessage
                };
            return;
        }

        await next();
    }

    private async Task<bool> BlocksDisabledInternalLightningNodeApiAsync(ActionExecutingContext context)
    {
        if (!IsGreenfieldRequest(context) ||
            !TryGetGreenfieldLightningCryptoCode(context, out var cryptoCode))
        {
            return false;
        }

        return await IsDisabledInternalAccountAsync(
            context,
            GetActionValue(context, "storeId"),
            cryptoCode);
    }

    private async Task<bool> BlocksNativeLightningEnableAsync(ActionExecutingContext context)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) &&
            !HttpMethods.IsPut(context.HttpContext.Request.Method))
        {
            return false;
        }

        if (context.ActionArguments.Values.OfType<LightningSettingsViewModel>().FirstOrDefault() is { } settings)
        {
            return settings.Enabled &&
                   await IsDisabledInternalAccountAsync(context, settings.StoreId, settings.CryptoCode);
        }

        if (context.ActionArguments.Values.OfType<LightningNodeViewModel>().FirstOrDefault() is { } nodeSettings)
        {
            var storeId = string.IsNullOrWhiteSpace(nodeSettings.StoreId)
                ? GetActionValue(context, "storeId")
                : nodeSettings.StoreId;
            var nodeCryptoCode = string.IsNullOrWhiteSpace(nodeSettings.CryptoCode)
                ? GetActionValue(context, "cryptoCode")
                : nodeSettings.CryptoCode;
            return nodeSettings.LightningNodeType == LightningNodeType.Internal &&
                   await IsDisabledInternalAccountAsync(context, storeId, nodeCryptoCode, incomingInternalConfig: true);
        }

        if (!context.ActionArguments.TryGetValue("paymentMethodId", out var paymentMethodIdValue) ||
            paymentMethodIdValue is not PaymentMethodId paymentMethodId ||
            context.ActionArguments.Values.OfType<UpdatePaymentMethodRequest>().FirstOrDefault() is not { } request ||
            !TryGetNativeLightningCryptoCode(paymentMethodId, out var cryptoCode))
        {
            return false;
        }

        var hasIncomingLightningConfig = TryGetIncomingInternalLightningConfig(
            paymentMethodId,
            request,
            out var incomingInternalConfig);
        if (hasIncomingLightningConfig && !incomingInternalConfig)
        {
            return false;
        }

        if (request.Enabled is not true && !incomingInternalConfig)
        {
            return false;
        }

        return await IsDisabledInternalAccountAsync(
            context,
            GetActionValue(context, "storeId"),
            cryptoCode,
            incomingInternalConfig);
    }

    private async Task<bool> IsDisabledInternalAccountAsync(
        ActionExecutingContext context,
        string? storeId,
        string? cryptoCode,
        bool incomingInternalConfig = false)
    {
        if (string.IsNullOrWhiteSpace(cryptoCode))
        {
            return false;
        }

        var store = await GetStoreAsync(context, storeId);
        if (store is null || (storeId is not null && store.Id != storeId))
        {
            return false;
        }

        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return false;
        }

        if (!incomingInternalConfig &&
            !StoreLightningAccessService.IsInternalLightningNode(store, cryptoCode, handlers))
        {
            return false;
        }

        var account = await ledgerRepository.GetAccountAsync(
            store.Id,
            cryptoCode,
            context.HttpContext.RequestAborted);
        return account is not { Enabled: true };
    }

    private bool TryGetIncomingInternalLightningConfig(
        PaymentMethodId paymentMethodId,
        UpdatePaymentMethodRequest request,
        out bool isInternalConfig)
    {
        isInternalConfig = false;
        if (request.Config is null ||
            !paymentMethodId.ToString().EndsWith("-LN", StringComparison.OrdinalIgnoreCase) ||
            !handlers.TryGetValue(paymentMethodId, out var handler))
        {
            return false;
        }

        try
        {
            if (handler.ParsePaymentMethodConfig(request.Config) is not LightningPaymentMethodConfig config)
            {
                return false;
            }

            isInternalConfig = config is
            {
                IsInternalNode: true
            } && config.GetExternalLightningUrl() is null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<StoreData?> GetStoreAsync(ActionExecutingContext context, string? storeId)
    {
        var store = context.HttpContext.GetStoreDataOrNull();
        if (store is not null || string.IsNullOrWhiteSpace(storeId) || dbContextFactory is null)
        {
            return store;
        }

        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.Stores.SingleOrDefaultAsync(
            store => store.Id == storeId,
            context.HttpContext.RequestAborted);
    }

    private static string? GetActionValue(ActionExecutingContext context, string name)
    {
        if (context.ActionArguments.TryGetValue(name, out var value))
        {
            return value?.ToString();
        }

        return context.RouteData.Values.TryGetValue(name, out value)
            ? value?.ToString()
            : null;
    }

    private static bool TryGetNativeLightningCryptoCode(PaymentMethodId paymentMethodId, out string cryptoCode)
    {
        var value = paymentMethodId.ToString();
        foreach (var suffix in new[] { "-LNURL", "-LN" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return LightningManagerCrypto.TryNormalizeSupported(
                    value[..^suffix.Length],
                    out cryptoCode);
            }
        }

        cryptoCode = string.Empty;
        return false;
    }

    private static bool IsGreenfieldRequest(ActionExecutingContext context)
    {
        return context.HttpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetGreenfieldLightningCryptoCode(ActionExecutingContext context, out string cryptoCode)
    {
        cryptoCode = GetActionValue(context, "cryptoCode") ?? string.Empty;
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cryptoCode) &&
            context.HttpContext.Request.Path.StartsWithSegments("/api/v1/stores", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/lightning/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
