using BTCPayServer;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Security;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.LightningManager.Filters;

public sealed class LightningManagerInternalNodeAuthorizationFilter(
    PaymentMethodHandlerDictionary handlers,
    IAuthorizationService authorizationService) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var cryptoCode = context.RouteData.Values["cryptoCode"]?.ToString()?.ToUpperInvariant();
        if (string.IsNullOrEmpty(cryptoCode))
        {
            await next();
            return;
        }

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var config = context.HttpContext.GetStoreData()
            .GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, handlers);
        if (config?.IsInternalNode is not true)
        {
            await next();
            return;
        }

        var authorization = await authorizationService.AuthorizeAsync(
            context.HttpContext.User,
            null,
            [new PolicyRequirement(Policies.CanUseInternalLightningNode)]);
        if (!authorization.Succeeded)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}
