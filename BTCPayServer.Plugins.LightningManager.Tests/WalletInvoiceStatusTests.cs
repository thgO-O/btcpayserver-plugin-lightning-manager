using System.Net;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Plugins.LightningManager.Services;
using NBitcoin;
using Newtonsoft.Json;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class WalletInvoiceStatusTests
{
    [Theory]
    [InlineData("OPEN", LightningInvoiceStatus.Unpaid)]
    [InlineData("ACCEPTED", LightningInvoiceStatus.Unpaid)]
    [InlineData("SETTLED", LightningInvoiceStatus.Paid)]
    [InlineData("CANCELED", LightningInvoiceStatus.Expired)]
    public async Task NativeStateWinsOverExpiryClock(string state, LightningInvoiceStatus expected)
    {
        using var http = new HttpClient(new InvoiceHandler(state));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = new WalletNode(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, "type=lnd-rest;server=https://lnd.example.test/"), "node");
        Assert.Equal(expected, await WalletService.InvoiceStatusAsync(node, new string('0', 64), CancellationToken.None));
    }

    [Theory]
    [InlineData("UNKNOWN", HttpStatusCode.OK)]
    [InlineData("OPEN", HttpStatusCode.ServiceUnavailable)]
    public async Task UnknownOrUnavailableStateIsNeverReportedAsExpiry(string state, HttpStatusCode response)
    {
        using var http = new HttpClient(new InvoiceHandler(state, response));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = new WalletNode(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, "type=lnd-rest;server=https://lnd.example.test/"), "node");
        await Assert.ThrowsAnyAsync<Exception>(() => WalletService.InvoiceStatusAsync(node, new string('0', 64), CancellationToken.None));
    }

    private sealed class InvoiceHandler(string state, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.StartsWith("/v1/invoice/", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { state, settled = state == "SETTLED", creation_date = "1", expiry = "1" }))
            });
        }
    }
}
