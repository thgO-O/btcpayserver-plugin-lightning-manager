using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerLndFundingRejectionTests
{
    private const string NodeUri =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";
    private const string ReserveError =
        "reserved wallet balance invalidated: transaction would leave insufficient funds for fee bumping anchor channel closings (see debug log for details)";

    [Theory]
    [InlineData("reserved wallet balance invalidated")]
    [InlineData(ReserveError)]
    public async Task ReserveRejectionOffersFundingThroughRealAdapterAndController(string message)
    {
        using var http = new HttpClient(new Handler(Error(message)));
        var controller = TestControllerFactory.CreateController(CreateContext(http));
        var preview = Assert.IsType<ChannelsViewModel>(Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(
            await controller.PreviewChannel("BTC", NodeUri, "100000", "1", CancellationToken.None)).Model);
        await controller.OpenChannel("BTC", NodeUri, "100000", "1", CancellationToken.None,
            preview.OpenChannelConfirmationToken);
        var model = Assert.IsType<ChannelsViewModel>(Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(
            await controller.Channels("BTC", CancellationToken.None)).Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result.IsSuccess);
        Assert.True(model.Result.InsufficientOnchainBalance);
        Assert.Contains("required reserve", model.Result.Message);
        Assert.Contains("Add funds or reduce the channel amount", model.Result.Message);
        Assert.DoesNotContain(message, model.Result.Message);
    }

    [Theory]
    [InlineData("{\"code\":2,\"message\":\"backend unavailable: secret-macaroon\"}")]
    [InlineData("{\"code\":2,\"message\":\"request failed after reserved wallet balance invalidated\"}")]
    [InlineData("{\"code\":2,\"message\":\"reserved wallet balance invalidated unexpectedly after broadcast\"}")]
    [InlineData("not JSON: reserved wallet balance invalidated")]
    [InlineData("null")]
    public async Task OtherOrMalformedResponsesRemainUnknown(string response)
    {
        using var http = new HttpClient(new Handler(response));
        var result = await TestLightningManagerServiceFactory.Create()
            .OpenChannelAsync(CreateContext(http), NodeUri, "100000", "1");
        Assert.False(result.IsSuccess);
        Assert.False(result.InsufficientOnchainBalance);
        Assert.Equal("Channel opening status is unknown. Check the Lightning node before retrying.", result.Message);
    }

    [Fact]
    public async Task ConnectionErrorCannotBeMistakenForFundingRejection()
    {
        var handler = new Handler(Error(ReserveError), failConnection: true);
        using var http = new HttpClient(handler);
        var result = await TestLightningManagerServiceFactory.Create()
            .OpenChannelAsync(CreateContext(http), NodeUri, "100000", "1");
        Assert.False(result.InsufficientOnchainBalance);
        Assert.Contains("No channel was opened", result.Message);
        Assert.Equal(0, handler.OpenCalls);
    }

    [Fact]
    public async Task ExceptionMessageAloneCannotBeMistakenForFundingRejection()
    {
        var client = new FakeLightningClient
        {
            ConnectToHandler = (_, _) => Task.FromResult(BTCPayServer.Lightning.ConnectionResult.Ok),
            OpenChannelHandler = (_, _) => throw new Exception(ReserveError)
        };
        var result = await TestLightningManagerServiceFactory.Create().OpenChannelAsync(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full, client), NodeUri, "100000", "1");
        Assert.False(result.InsufficientOnchainBalance);
        Assert.Equal("Channel opening status is unknown. Check the Lightning node before retrying.", result.Message);
    }

    private static StoreLightningManagerContext CreateContext(HttpClient http) =>
        TestContextFactory.CreateConfigured(LightningCapabilities.Full,
            new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest),
            connectionString: "type=lnd-rest;server=https://lnd.example.test/");

    private static string Error(string message) => JsonSerializer.Serialize(new { code = 2, message });

    private sealed class Handler(string response, bool failConnection = false) : HttpMessageHandler
    {
        public int OpenCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/channels")
                OpenCalls++;
            var failure = request.Method == HttpMethod.Post && (path == "/v1/channels" || failConnection);
            var body = failure ? response : path == "/v1/channels/pending"
                ? "{\"pending_open_channels\":[]}" : path == "/v1/channels" ? "{\"channels\":[]}" : "{}";
            return Task.FromResult(new HttpResponseMessage(failure ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
