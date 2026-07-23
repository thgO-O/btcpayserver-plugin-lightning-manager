using System.Reflection;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
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

        var result = await controller.PreviewSend("BTC", "invalid", null, null, CancellationToken.None);

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

        var executeResult = await controller.ExecuteSend("BTC", "lnbcrt1test", null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        Assert.Equal("Send", redirect.ActionName);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var sendResult = await controller.Send("BTC", CancellationToken.None, resultId);
        var view = Assert.IsType<ViewResult>(sendResult);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.True(model.Result!.IsSuccess);
        Assert.NotNull(model.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, model.Payment!.Status);
        Assert.False(string.IsNullOrWhiteSpace(model.Payment.PaymentHash));
        Assert.Equal("preimage", model.Payment.Preimage);

        var refreshResult = await controller.Send("BTC", CancellationToken.None, resultId);
        var refreshView = Assert.IsType<ViewResult>(refreshResult);
        var refreshModel = Assert.IsType<SendViewModel>(refreshView.Model);
        Assert.True(refreshModel.Result!.IsSuccess);
        Assert.Equal(LightningPaymentStatus.Complete, refreshModel.Payment!.Status);
    }

    [Fact]
    public async Task ExecuteSend_WhenRedirectIsLost_RecoversResultOnNextSendVisit()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            new SuccessfulSendLightningManagerService());

        await controller.ExecuteSend("BTC", "lnbcrt1test", null, null, CancellationToken.None);

        var recoveredResult = await controller.Send("BTC", CancellationToken.None);
        var recoveredView = Assert.IsType<ViewResult>(recoveredResult);
        var recoveredModel = Assert.IsType<SendViewModel>(recoveredView.Model);
        Assert.True(recoveredModel.Result!.IsSuccess);
        Assert.Equal(LightningPaymentStatus.Complete, recoveredModel.Payment!.Status);

        var nextResult = await controller.Send("BTC", CancellationToken.None);
        var nextView = Assert.IsType<ViewResult>(nextResult);
        var nextModel = Assert.IsType<SendViewModel>(nextView.Model);
        Assert.Null(nextModel.Result);
        Assert.Null(nextModel.Payment);
    }

    [Fact]
    public async Task SendPage_WithMissingResult_ShowsRetryWarning()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = await controller.Send("BTC", CancellationToken.None, Guid.NewGuid().ToString("N"));

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal(
            "Payment result is no longer available. Check the Lightning node before retrying.",
            model.Result.Message);
    }

    [Fact]
    public async Task PaymentResults_AreScopedReusableAndIndependent()
    {
        var resultStore = new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions()));
        var firstId = resultStore.StorePayment("user-1", "store-1", "BTC", CreateSendResult("first-hash"));
        var secondId = resultStore.StorePayment("user-2", "store-2", "BTC", CreateSendResult("second-hash"));

        Assert.False(resultStore.TryGetPayment(firstId, "user-2", "store-1", "BTC", out _));
        Assert.False(resultStore.TryGetPayment(firstId, "user-1", "store-2", "BTC", out _));
        Assert.True(resultStore.TryGetPayment(secondId, "user-2", "store-2", "BTC", out var second));
        Assert.Equal("second-hash", second!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(firstId, "user-1", "store-1", "btc", out var first));
        Assert.Equal("first-hash", first!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(firstId, "user-1", "store-1", "BTC", out var refreshed));
        Assert.Equal("first-hash", refreshed!.Payment!.PaymentHash);

        var concurrentId = resultStore.StorePayment("user-1", "store-1", "BTC", CreateSendResult("concurrent-hash"));
        var concurrentReads = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(index => Task.Run(() =>
                    resultStore.TryGetPayment(concurrentId, "user-1", "store-1", "BTC", out _))));
        Assert.All(concurrentReads, value => Assert.True(value));
    }

    [Fact]
    public void PendingResults_AreUserScopedQueuedAndSeparatedByOperation()
    {
        var resultStore = new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions()));
        var firstPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            CreateSendResult("first-payment-hash"));
        var secondPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            CreateSendResult("second-payment-hash"));
        var channelId = resultStore.StoreChannel(
            "user-1",
            "store-1",
            "BTC",
            new ActionResultViewModel { IsSuccess = false, Message = "Channel status is unknown." });

        Assert.False(resultStore.TryGetPendingPayment("user-2", "store-1", "BTC", out _));
        Assert.True(resultStore.TryGetPendingPayment("user-1", "store-1", "btc", out var firstPayment));
        Assert.Equal("first-payment-hash", firstPayment!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPendingPayment("user-1", "store-1", "BTC", out var secondPayment));
        Assert.Equal("second-payment-hash", secondPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment("user-1", "store-1", "BTC", out _));
        Assert.True(resultStore.TryGetPayment(firstPaymentId, "user-1", "store-1", "BTC", out _));
        Assert.True(resultStore.TryGetPayment(secondPaymentId, "user-1", "store-1", "BTC", out _));
        Assert.False(resultStore.TryGetPayment(channelId, "user-1", "store-1", "BTC", out _));

        var pendingPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            CreateSendResult("pending-payment-hash"));
        var redirectedPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            CreateSendResult("redirected-payment-hash"));
        Assert.True(resultStore.TryGetPayment(
            redirectedPaymentId,
            "user-1",
            "store-1",
            "BTC",
            out _));
        Assert.True(resultStore.TryGetPendingPayment("user-1", "store-1", "BTC", out var pendingPayment));
        Assert.Equal("pending-payment-hash", pendingPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment("user-1", "store-1", "BTC", out _));
        Assert.True(resultStore.TryGetPayment(pendingPaymentId, "user-1", "store-1", "BTC", out _));

        Assert.True(resultStore.TryGetPendingChannel("user-1", "store-1", "BTC", out var channel));
        Assert.Equal("Channel status is unknown.", channel!.Message);
        Assert.False(resultStore.TryGetPendingChannel("user-1", "store-1", "BTC", out _));
        Assert.True(resultStore.TryGetChannel(channelId, "user-1", "store-1", "BTC", out _));
    }

    [Fact]
    public async Task OpenChannel_WhenRedirectIsLost_RecoversResultOnNextChannelsVisit()
    {
        var confirmationStore = new LightningManagerChannelConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()));
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full),
            new LightningManagerService(),
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            confirmationStore);
        var confirmationToken = confirmationStore.Create("user-1", "store-1", "BTC", "invalid", "100000", null);

        await controller.OpenChannel(
            "BTC",
            "invalid",
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        var recoveredResult = await controller.Channels("BTC", CancellationToken.None);
        var recoveredView = Assert.IsType<ViewResult>(recoveredResult);
        var recoveredModel = Assert.IsType<ChannelsViewModel>(recoveredView.Model);
        Assert.False(recoveredModel.Result!.IsSuccess);
        Assert.Equal("The node URI is invalid. Use pubkey@host[:port].", recoveredModel.Result.Message);
    }

    [Fact]
    public async Task OpenChannel_ConsumesPreviewConfirmationAndRejectsReplay()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        var service = new CountingOpenChannelLightningManagerService();
        var confirmationStore = new LightningManagerChannelConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()));
        var controller = TestControllerFactory.CreateController(
            context,
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            confirmationStore);

        var firstPreviewResult = await controller.PreviewChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None);
        var firstPreview = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(firstPreviewResult).Model);
        var firstToken = Assert.IsType<string>(firstPreview.OpenChannelConfirmationToken);

        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "200000",
            null,
            CancellationToken.None,
            firstToken);
        Assert.Equal(0, service.OpenChannelCalls);

        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            firstToken);
        var replayResult = await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            firstToken);

        Assert.Equal(1, service.OpenChannelCalls);
        var replayRedirect = Assert.IsType<RedirectToActionResult>(replayResult);
        var replayResultId = Assert.IsType<string>(replayRedirect.RouteValues!["resultId"]);
        var replayPage = await controller.Channels("BTC", CancellationToken.None, replayResultId);
        var replayModel = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(replayPage).Model);
        Assert.False(replayModel.Result!.IsSuccess);
        Assert.Contains("already used", replayModel.Result.Message, StringComparison.Ordinal);

        var secondPreviewResult = await controller.PreviewChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None);
        var secondPreview = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(secondPreviewResult).Model);
        var secondToken = Assert.IsType<string>(secondPreview.OpenChannelConfirmationToken);
        Assert.NotEqual(firstToken, secondToken);

        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            secondToken);

        Assert.Equal(2, service.OpenChannelCalls);
    }

    [Fact]
    public async Task OpenChannel_WithInvalidOrExpiredConfirmation_DoesNotCallService()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        var service = new CountingOpenChannelLightningManagerService();
        var confirmationStore = new LightningManagerChannelConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()),
            TimeSpan.FromMilliseconds(20));
        var controller = TestControllerFactory.CreateController(
            context,
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            confirmationStore);

        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            "invalid");

        var expiredToken = confirmationStore.Create(
            "user-1",
            "store-1",
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null);
        await Task.Delay(100);
        var expiredResult = await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            expiredToken);

        Assert.Equal(0, service.OpenChannelCalls);
        var redirect = Assert.IsType<RedirectToActionResult>(expiredResult);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var resultPage = await controller.Channels("BTC", CancellationToken.None, resultId);
        var model = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(resultPage).Model);
        Assert.False(model.Result!.IsSuccess);
        Assert.Contains("expired", model.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_RequiresLightningNodeAccess()
    {
        var authorization = Assert.Single(
            typeof(Controllers.LightningManagerController)
                .GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(Policies.CanUseLightningNodeInStore, authorization.Policy);
        Assert.Equal(AuthenticationSchemes.Cookie, authorization.AuthenticationSchemes);
    }

    [Theory]
    [InlineData(nameof(Controllers.LightningManagerController.PreviewSend))]
    [InlineData(nameof(Controllers.LightningManagerController.ExecuteSend))]
    [InlineData(nameof(Controllers.LightningManagerController.ConnectPeer))]
    [InlineData(nameof(Controllers.LightningManagerController.PreviewChannel))]
    [InlineData(nameof(Controllers.LightningManagerController.OpenChannel))]
    public void MutatingActions_DoNotOverrideLightningNodeAccessWithStoreSettingsPermission(string actionName)
    {
        var action = Assert.Single(
            typeof(Controllers.LightningManagerController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name == actionName);
        var authorization = action.GetCustomAttributes<AuthorizeAttribute>();

        Assert.DoesNotContain(
            authorization,
            attribute => attribute.Policy == Policies.CanModifyStoreSettings);
    }

    private static SendExecutionResult CreateSendResult(string paymentHash)
    {
        return new SendExecutionResult
        {
            Result = new ActionResultViewModel { IsSuccess = true, Message = "Payment sent successfully." },
            Payment = new SendResultDetailsViewModel
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = paymentHash
            }
        };
    }

    private sealed class SuccessfulSendLightningManagerService : LightningManagerService
    {
        public override Task<SendExecutionResult> SendAsync(
            StoreLightningManagerContext context,
            string bolt11,
            string? amountSats,
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

    private sealed class CountingOpenChannelLightningManagerService : LightningManagerService
    {
        public int OpenChannelCalls { get; private set; }

        public override bool TryCreateOpenChannelPreview(
            StoreLightningManagerContext context,
            string? nodeUri,
            string? channelAmountSats,
            string? feeRateSatsPerByte,
            out OpenChannelPreviewViewModel? preview,
            out string? error)
        {
            preview = new OpenChannelPreviewViewModel
            {
                NodeUri = nodeUri!,
                ChannelAmountDisplay = "100,000 sats",
                FeeRateDisplay = "1 sat/vB"
            };
            error = null;
            return true;
        }

        public override Task PopulateChannelsAsync(
            ChannelsViewModel model,
            StoreLightningManagerContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public override Task<ActionResultViewModel> OpenChannelAsync(
            StoreLightningManagerContext context,
            string nodeUri,
            string channelAmountSats,
            string? feeRateSatsPerByte,
            CancellationToken cancellationToken = default)
        {
            OpenChannelCalls++;
            return Task.FromResult(new ActionResultViewModel
            {
                IsSuccess = true,
                Message = "Channel opening request submitted."
            });
        }
    }
}
