using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.LND;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

internal static class LightningRouteReadiness
{
    // An active channel can precede LND's routing graph update. Never probe by paying.
    public static async Task WaitAsync(ILightningClient payer, PubKey recipient, long sats, CancellationToken token)
    {
        if (payer is not LndClient lnd) return;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var routes = await lnd.SwaggerClient.QueryRoutesAsync(recipient.ToString(), sats.ToString(CultureInfo.InvariantCulture), null, token);
                if (routes.Routes?.Count > 0) return;
            }
            catch (SwaggerException) when (!token.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        Assert.Fail($"The regtest node has no route for {sats} sats to {recipient}.");
    }
}
