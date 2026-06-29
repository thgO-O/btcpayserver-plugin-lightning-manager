using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerControllerTests
{
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
    public async Task OverviewPage_WhenLightningManagerUnavailable_ShowsConfigurationMessage()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateUnavailable("Lightning Manager supports external BTC Lightning backends only."));

        var result = await controller.Overview("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OverviewViewModel>(view.Model);
        Assert.False(model.IsConfigured);
        Assert.Equal("Lightning Manager supports external BTC Lightning backends only.", model.ConfigurationMessage);
        Assert.True(model.Tabs.ShowOverview);
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
    public async Task ConnectPeer_WithInvalidNodeUri_RedirectsWithErrorMessage()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.ConnectPeer("BTC", "invalid-node-uri", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Peers", redirect.ActionName);
        Assert.Equal("The node URI is invalid. Use pubkey@host[:port].", controller.TempData[WellKnownTempData.ErrorMessage]);
    }

    [Fact]
    public async Task PeersPage_ShowsUnavailableMessage()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.Peers("BTC", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PeersViewModel>(view.Model);
        Assert.Equal("Peer listing is not available for this backend.", model.PeerListMessage);
    }

    [Fact]
    public async Task PreviewSend_WithInvalidInvoice_ReturnsFailureOnModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.PreviewSend("BTC", "invalid", null, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal("The BOLT11 invoice is invalid.", model.Result.Message);
    }

    [Fact]
    public async Task ExecuteSend_RedirectsAndNextSendPageShowsPaymentDetails()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            new SuccessfulSendLightningManagerService());

        var executeResult = await controller.ExecuteSend("BTC", "lnbcrt1test", null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        Assert.Equal("Send", redirect.ActionName);
        var sendResult = await controller.Send("BTC", CancellationToken.None);
        var view = Assert.IsType<ViewResult>(sendResult);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, model.Payment!.Status);
        Assert.False(string.IsNullOrWhiteSpace(model.Payment.PaymentHash));
        Assert.Equal("preimage", model.Payment.Preimage);
    }

    private sealed class SuccessfulSendLightningManagerService : LightningManagerService
    {
        public override Task<SendExecutionResult> SendAsync(
            StoreLightningManagerContext context,
            string bolt11,
            string? maxFeeSats,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SendExecutionResult
            {
                Result = new ActionResultViewModel { IsSuccess = true, Message = "Payment sent successfully." },
                Payment = new SendResultDetailsViewModel
                {
                    Status = LightningPaymentStatus.Complete,
                    PaymentHash = "test-hash",
                    Preimage = "preimage"
                }
            });
        }
    }
}
