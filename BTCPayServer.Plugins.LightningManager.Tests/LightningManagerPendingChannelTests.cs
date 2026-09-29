using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerPendingChannelTests
{
    private const string Peer = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private static readonly string PendingPoint = new string('1', 64) + ":0";
    private static readonly string ExistingPoint = new string('2', 64) + ":1";

    [Fact]
    public async Task ListsPendingAlongsideExistingChannelWithSamePeer()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(
            IsPending(request) ? PendingJson(PendingPoint) : ChannelsJson(ExistingPoint)))));
        var model = await LoadAsync(http);
        Assert.Equal(2, model.Channels.Count);
        var pending = model.Channels[0];
        Assert.True(pending.IsPending);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(Peer, pending.RemoteNode);
        Assert.Equal(OutPoint.Parse(PendingPoint).ToString(), pending.ChannelPoint);
        Assert.Equal(100000m, pending.CapacitySats);
        Assert.Equal("100,000 sats", pending.CapacityDisplay);
        Assert.Null(pending.IsPublic); // The bundled pending-channel API does not expose visibility.
        Assert.Equal("Active", model.Channels[1].Status);
        Assert.Null(model.PendingChannelListMessage);
    }

    [Fact]
    public async Task ConfirmationBetweenSnapshotsKeepsOnlyConfirmedRow()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(IsPending(request)
                ? PendingJson(PendingPoint) : ChannelsJson(PendingPoint)));
        }));
        var model = await LoadAsync(http);
        Assert.Equal(new[] { "/v1/channels/pending", "/v1/channels" }, requests);
        var channel = Assert.Single(model.Channels);
        Assert.Equal(OutPoint.Parse(PendingPoint).ToString(), channel.ChannelPoint);
        Assert.False(channel.IsPending);
        Assert.Equal("Active", channel.Status);
    }

    [Fact]
    public async Task PendingQueryFailureKeepsConfirmedChannelsAndWarns()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(IsPending(request)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-token") }
            : Json(ChannelsJson(ExistingPoint)))));
        var model = await LoadAsync(http);
        Assert.Single(model.Channels);
        Assert.Equal("Active", model.Channels[0].Status);
        Assert.Equal("Could not load pending channels. The channel list may be incomplete.", model.PendingChannelListMessage);
        Assert.Null(model.ChannelListMessage);
    }

    [Fact]
    public async Task ConfirmedQueryFailureKeepsPendingChannelsAndWarns()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(IsPending(request)
            ? Json(PendingJson(PendingPoint))
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-token") })));
        var model = await LoadAsync(http);
        Assert.True(Assert.Single(model.Channels).IsPending);
        Assert.Equal("Could not load channels.", model.ChannelListMessage);
        Assert.Null(model.PendingChannelListMessage);
    }

    [Fact]
    public async Task DoesNotTreatPendingClosesAsPendingOpens()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(IsPending(request)
            ? """{"pending_open_channels":[],"pending_closing_channels":[{"channel":{"channel_point":"closing"}}]}"""
            : ChannelsJson(ExistingPoint, active: false)))));
        var model = await LoadAsync(http);
        var channel = Assert.Single(model.Channels);
        Assert.False(channel.IsPending);
        Assert.Equal("Inactive", channel.Status);
    }

    [Fact]
    public async Task CancellationDuringPendingQueryPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoadAsync(http, cancellation.Token));
    }

    private static async Task<ChannelsViewModel> LoadAsync(HttpClient http, CancellationToken token = default)
    {
        var client = new LndClient(new LndSwaggerClient(
            new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client,
            connectionString: "type=lnd-rest;server=https://lnd.example.test/");
        var model = new ChannelsViewModel();
        await TestLightningManagerServiceFactory.Create().PopulateChannelsAsync(model, context, token);
        return model;
    }

    private static bool IsPending(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/v1/channels/pending";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string PendingJson(string point) => JsonSerializer.Serialize(new
    {
        pending_open_channels = new[]
        {
            new { channel = new { remote_node_pub = Peer, channel_point = point, capacity = "100000", local_balance = "96530", remote_balance = "0" } }
        }
    });

    private static string ChannelsJson(string point, bool active = true) => JsonSerializer.Serialize(new
    {
        channels = new[]
        {
            new { remote_pubkey = Peer, channel_point = point, capacity = "100000", local_balance = "60000", @private = false, active }
        }
    });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
