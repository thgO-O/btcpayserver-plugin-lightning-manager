using System.Net;
using System.Net.Sockets;
using System.Text;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.Wallet;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class WalletNodeIdentityTests
{
    private const string PublicKey = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    [Theory]
    [InlineData("lnd-rest", false)]
    [InlineData("lnd-rest", true)]
    [InlineData("lnd-grpc", false)]
    [InlineData("lnd-grpc", true)]
    public async Task LndIdentityDoesNotDependOnAdvertisedUris(string backend, bool advertised)
    {
        using var http = new HttpClient(new InfoHandler(JsonConvert.SerializeObject(new
        {
            identity_pubkey = PublicKey.ToUpperInvariant(),
            uris = advertised ? new[] { PublicKey + "@127.0.0.1:9735" } : []
        })));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = await GetNodeAsync(client, $"type={backend};server=https://lnd.example.test/", CancellationToken.None);
        Assert.Equal("RegTest:" + PublicKey, node.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClnIdentityDoesNotDependOnAdvertisedAddresses(bool advertised)
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
            var node = await GetNodeAsync(client, connection, timeout.Token);
            Assert.Equal("RegTest:" + PublicKey, node.Identity);
            await server;

            async Task RespondAsync()
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var jsonReader = new JsonTextReader(reader);
                var request = await JObject.LoadAsync(jsonReader, timeout.Token);
                Assert.Equal("getinfo", request["method"]!.Value<string>());
                var response = JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0", id = 0,
                    result = new { id = PublicKey.ToUpperInvariant(), address = advertised
                        ? new[] { new { type = "ipv4", address = "127.0.0.1", port = 9735 } } : [] }
                });
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), timeout.Token);
            }
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task MissingNativeIdentityDoesNotFallBackToAnAdvertisedAddress()
    {
        using var http = new HttpClient(new InfoHandler(JsonConvert.SerializeObject(new
        {
            uris = new[] { PublicKey + "@127.0.0.1:9735" }
        })));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        await Assert.ThrowsAsync<WalletException>(() => GetNodeAsync(client, "type=lnd-rest;server=https://lnd.example.test/", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeCarriesTheStoresPrivateRouteHintSetting(bool enabled)
    {
        using var http = new HttpClient(new InfoHandler(JsonConvert.SerializeObject(new { identity_pubkey = PublicKey })));
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, "type=lnd-rest;server=https://lnd.example.test/");
        var service = new WalletService(new FakeStoreLightningManagerContextFactory { Context = context },
            TestLightningManagerServiceFactory.Create(), null!, null!, NullLogger<WalletService>.Instance, null!);
        var store = new StoreData { Id = context.StoreId };
        store.SetStoreBlob(new StoreBlob { LightningPrivateRouteHints = enabled });
        Assert.Equal(enabled, (await service.GetNodeAsync(store, CancellationToken.None)).PrivateRouteHints);
    }

    private static Task<WalletNode> GetNodeAsync(ILightningClient client, string connection, CancellationToken token)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client, connection);
        var service = new WalletService(new FakeStoreLightningManagerContextFactory { Context = context },
            TestLightningManagerServiceFactory.Create(), null!, null!, NullLogger<WalletService>.Instance, null!);
        return service.GetNodeAsync(new StoreData { Id = context.StoreId }, token);
    }

    private sealed class InfoHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/getinfo", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
