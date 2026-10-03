using System.Net;
using System.Net.Sockets;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Plugins.LightningManager.Services;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        Assert.Equal(expected, (await WalletService.InvoiceAsync(node, new string('0', 64), CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData("UNKNOWN", HttpStatusCode.OK)]
    [InlineData("OPEN", HttpStatusCode.ServiceUnavailable)]
    public async Task UnknownOrUnavailableStateIsNeverReportedAsExpiry(string state, HttpStatusCode response)
    {
        using var http = new HttpClient(new InvoiceHandler(state, response));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = new WalletNode(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, "type=lnd-rest;server=https://lnd.example.test/"), "node");
        await Assert.ThrowsAnyAsync<Exception>(() => WalletService.InvoiceAsync(node, new string('0', 64), CancellationToken.None));
    }

    [Theory]
    [InlineData("501000", 501_000L)]
    [InlineData(null, null)]
    [InlineData("invalid", null)]
    [InlineData("-1", null)]
    public async Task LndReturnsActualReceivedAmountWithoutInferringItFromInvoice(string? received, long? expected)
    {
        using var http = new HttpClient(new InvoiceHandler("SETTLED", received: received));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = new WalletNode(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, "type=lnd-rest;server=https://lnd.example.test/"), "node");
        var invoice = await WalletService.InvoiceAsync(node, new string('0', 64), CancellationToken.None);
        Assert.Equal(LightningInvoiceStatus.Paid, invoice!.Status);
        Assert.Equal(expected, invoice.AmountReceived?.MilliSatoshi);
    }

    [Theory]
    [InlineData("501000msat", 501_000L)]
    [InlineData(null, null)]
    public async Task ClnReturnsActualReceivedAmountWithoutInferringItFromInvoice(string? received, long? expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var server = RespondAsync();
            var connection = $"type=clightning;server=tcp://127.0.0.1:{endpoint.Port}/";
            var client = new CLightningClient(new Uri($"tcp://127.0.0.1:{endpoint.Port}/"), Network.RegTest);
            var node = new WalletNode(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, connection), "node");
            var invoice = await WalletService.InvoiceAsync(node, "receiving", timeout.Token);
            Assert.Equal(LightningInvoiceStatus.Paid, invoice!.Status);
            Assert.Equal(500_000, invoice.Amount.MilliSatoshi);
            Assert.Equal(expected, invoice.AmountReceived?.MilliSatoshi);
            await server;

            async Task RespondAsync()
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var jsonReader = new JsonTextReader(reader);
                var request = await JObject.LoadAsync(jsonReader, timeout.Token);
                Assert.Equal("listinvoices", request["method"]!.Value<string>());
                Assert.Equal("receiving", request["params"]![0]!.Value<string>());
                var response = JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0", id = request["id"],
                    result = new { invoices = new[] { new { label = "receiving", status = "paid", payment_hash = new string('0', 64),
                        amount_msat = "500000msat", amount_received_msat = received } } }
                });
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), timeout.Token);
            }
        }
        finally { listener.Stop(); }
    }

    private sealed class InvoiceHandler(string state, HttpStatusCode status = HttpStatusCode.OK, string? received = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.StartsWith("/v1/invoice/", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { state, settled = state == "SETTLED", creation_date = "1", expiry = "1",
                    value_msat = "500000", amt_paid_msat = received }))
            });
        }
    }
}
