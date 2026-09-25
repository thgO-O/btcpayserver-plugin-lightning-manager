using System.Net;
using System.Text;
using BTCPayServer.Lightning.Eclair;
using BTCPayServer.Plugins.LightningManager.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerEclairFeeTests
{
    [Fact]
    public async Task SendAsync_WithEclairMaximumFee_DisablesProportionalFeeAllowanceOnWire()
    {
        using var handler = new CapturingEclairHandler();
        using var httpClient = new HttpClient(handler);
        var client = new EclairLightningClient(
            new Uri("https://eclair.test/"),
            "unused",
            Network.RegTest,
            httpClient);
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=eclair;server=https://eclair.test/;password=unused");
        var service = TestLightningManagerServiceFactory.Create();

        await service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, "10");

        var form = Assert.Single(handler.PaymentForms);
        Assert.Equal("10", form["maxFeeFlatSat"]);
        // Eclair accepts either fee limit, so its default percentage must be disabled.
        Assert.True(form.TryGetValue("maxFeePct", out var maxFeePercent),
            "The Eclair request must explicitly disable its proportional fee allowance.");
        Assert.Equal("0", maxFeePercent);
    }

    private sealed class CapturingEclairHandler : HttpMessageHandler
    {
        public List<Dictionary<string, string>> PaymentForms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/payinvoice")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                PaymentForms.Add(body.Split('&')
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(
                        pair => WebUtility.UrlDecode(pair[0]),
                        pair => WebUtility.UrlDecode(pair[1]),
                        StringComparer.OrdinalIgnoreCase));

                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"error\":\"Test payment rejected\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }
    }
}
