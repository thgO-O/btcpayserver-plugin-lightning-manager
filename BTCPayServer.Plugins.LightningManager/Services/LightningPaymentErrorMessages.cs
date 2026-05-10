#nullable enable
namespace BTCPayServer.Plugins.LightningManager.Services;

internal static class LightningPaymentErrorMessages
{
    public static string NormalizePayError(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
        {
            return "Lightning payment failed.";
        }

        if (errorDetail.Contains("route", StringComparison.OrdinalIgnoreCase))
        {
            return "No route to the invoice destination was found.";
        }

        if (errorDetail.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            errorDetail.Contains("balance", StringComparison.OrdinalIgnoreCase))
        {
            return "Insufficient balance to send the payment.";
        }

        return "Lightning payment failed.";
    }
}
