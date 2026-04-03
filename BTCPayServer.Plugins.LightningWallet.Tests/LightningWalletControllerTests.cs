using BTCPayServer.Plugins.LightningWallet.Services;
using BTCPayServer.Plugins.LightningWallet.ViewModels;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningWallet.Tests;

public class LightningWalletControllerTests
{
    private const string SharedBackendNotice =
        "This store uses the server's shared internal Lightning node. Balances shown here are node-wide, not store-specific. Wallet actions are disabled for non-admin users.";
    private const string SharedInternalNodeReadOnlyMessage =
        "Wallet actions are disabled for stores using the server's shared internal Lightning node.";

    [Fact]
    public async Task SendPage_UsesCapabilityDrivenTabs()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly(canGetInfo: false)));

        var result = await controller.Send("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.True(model.Tabs.ShowSend);
        Assert.False(model.Tabs.ShowPeers);
        Assert.False(model.Tabs.ShowChannels);
    }

    [Fact]
    public async Task SendPage_WithReadOnlySharedInternalNode_ShowsNoticeAndHidesMutatingTabs()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(
                new LightningCapabilities
                {
                    CanGetInfo = true,
                    CanGetBalance = true
                },
                isInternalNode: true,
                isSharedBackend: true,
                isReadOnly: true,
                sharedBackendNotice: SharedBackendNotice));

        var result = await controller.Send("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.Contains(SharedBackendNotice, model.Notices);
        Assert.False(model.Tabs.ShowSend);
        Assert.False(model.Tabs.ShowPeers);
        Assert.False(model.Tabs.ShowChannels);
    }

    [Fact]
    public async Task ChannelsPage_WhenUnsupported_ShowsUnavailableMessage()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()));

        var result = await controller.Channels("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChannelsViewModel>(view.Model);
        Assert.Equal("Channel listing is not supported by this backend.", model.ChannelListMessage);
    }

    [Fact]
    public async Task ConnectPeer_WithInvalidNodeUri_ReturnsFailureOnModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.ConnectPeer("BTC", "invalid-node-uri", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PeersViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal("The node URI is invalid. Use pubkey@host[:port].", model.Result.Message);
    }

    [Fact]
    public async Task PeersPage_WithPeerListingSupport_ShowsConnectedPeers()
    {
        var client = new LndLikePeerListingClient
        {
            ListPeersHandler = _ => Task.FromResult(new TestPeerListResponse
            {
                Peers =
                [
                    new TestPeerResponse
                    {
                        PubKey = "02bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        Address = "172.22.0.5:9735",
                        Inbound = true,
                        BytesSent = 100,
                        BytesRecv = 200
                    }
                ]
            })
        };
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));

        var result = await controller.Peers("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PeersViewModel>(view.Model);
        var peer = Assert.Single(model.Peers);
        Assert.Equal("02bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", peer.NodeId);
        Assert.Equal("Inbound", peer.Direction);
        Assert.Null(model.PeerListMessage);
    }

    [Fact]
    public async Task ConnectPeer_WithReadOnlySharedInternalNode_ReturnsFailureOnModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(
                LightningCapabilities.Full,
                isInternalNode: true,
                isSharedBackend: true,
                isReadOnly: true,
                sharedBackendNotice: SharedBackendNotice));

        var result = await controller.ConnectPeer("BTC", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PeersViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal(SharedInternalNodeReadOnlyMessage, model.Result.Message);
    }

    [Fact]
    public async Task PreviewSend_WithInvalidInvoice_ReturnsFailureOnModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.PreviewSend("BTC", "invalid", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal("The BOLT11 invoice is invalid.", model.Result.Message);
    }
}
