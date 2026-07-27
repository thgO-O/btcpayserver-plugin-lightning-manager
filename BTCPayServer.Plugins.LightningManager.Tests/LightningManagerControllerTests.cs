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
    private const string ResultBackendFingerprint = "backend-a";

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
        Assert.Null(model.PaymentConfirmationToken);
    }

    [Fact]
    public async Task PreviewSend_WithValidInvoice_CreatesIndependentConfirmationTokens()
    {
        var service = new CountingSendLightningManagerService
        {
            IsAmountlessPreview = true
        };
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full),
            service);

        var firstToken = await PreviewPaymentAsync(controller, " lnbcrt1test ", "123", "7");
        var secondToken = await PreviewPaymentAsync(controller, "lnbcrt1test", "123", "7");

        Assert.NotEqual(firstToken, secondToken);

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            "123",
            "7",
            CancellationToken.None,
            firstToken);
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            "123",
            "7",
            CancellationToken.None,
            secondToken);

        Assert.Equal(2, service.SendCalls);
    }

    [Fact]
    public async Task ExecuteSend_WithoutConfirmation_DoesNotCallService()
    {
        var service = new CountingSendLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service);

        var executeResult = await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None);

        Assert.Equal(0, service.SendCalls);
        var model = await LoadPaymentResultAsync(controller, executeResult);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal(
            "This payment confirmation is invalid, expired, or already used. If it may already have been submitted, check the Lightning node before previewing again.",
            model.Result.Message);
    }

    [Fact]
    public async Task ExecuteSend_WithInvalidOrExpiredConfirmation_DoesNotCallService()
    {
        var service = new CountingSendLightningManagerService();
        var paymentConfirmationStore = new LightningManagerPaymentConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()),
            TimeSpan.FromMilliseconds(20));
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            paymentConfirmationStore: paymentConfirmationStore);

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            "invalid");

        var expiredToken = await PreviewPaymentAsync(controller);
        await Task.Delay(100);
        var expiredResult = await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            expiredToken);

        Assert.Equal(0, service.SendCalls);
        var model = await LoadPaymentResultAsync(controller, expiredResult);
        Assert.False(model.Result!.IsSuccess);
        Assert.Contains("invalid, expired, or already used", model.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSend_WithTamperedPayload_PreservesTokenForOriginalPayload()
    {
        var service = new CountingSendLightningManagerService
        {
            IsAmountlessPreview = true
        };
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full),
            service);
        var confirmationToken = await PreviewPaymentAsync(
            controller,
            " lnbcrt1test ",
            "123",
            "7");

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1other",
            "123",
            "7",
            CancellationToken.None,
            confirmationToken);
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            "124",
            "7",
            CancellationToken.None,
            confirmationToken);
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            "123",
            "8",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, service.SendCalls);

        await controller.ExecuteSend(
            "BTC",
            "LNBCRT1TEST",
            "000123",
            "007",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.SendCalls);
    }

    [Fact]
    public async Task ExecuteSend_WithChangedCredentials_PreservesTokenForOriginalConfiguration()
    {
        var originalContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-rest;server=https://node-a.example/;macaroon=AABB");
        var changedContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-grpc;server=https://node-a.example;macaroon=CCDD");
        Assert.NotEqual(
            originalContext.BackendFingerprint,
            changedContext.BackendFingerprint);
        Assert.Equal(
            originalContext.BackendIdentityFingerprint,
            changedContext.BackendIdentityFingerprint);
        var contextFactory = new FakeStoreLightningManagerContextFactory
        {
            Context = originalContext
        };
        var service = new CountingSendLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            originalContext,
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            contextFactory: contextFactory);
        var confirmationToken = await PreviewPaymentAsync(
            controller,
            maxFeeSats: "7");

        contextFactory.Context = changedContext;
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            "7",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, service.SendCalls);

        contextFactory.Context = originalContext;
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            "7",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.SendCalls);
        Assert.Equal("7", service.LastMaxFeeSats);
    }

    [Fact]
    public async Task ExecuteSend_WithFixedAmountInvoice_IgnoresPreviewOverrides()
    {
        var service = new CountingSendLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service);
        var confirmationToken = await PreviewPaymentAsync(
            controller,
            amountSats: "999999",
            maxFeeSats: "-10");

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.SendCalls);
        Assert.Null(service.LastAmountSats);
        Assert.Null(service.LastMaxFeeSats);
    }

    [Fact]
    public async Task ExecuteSend_ConsumesConfirmationAndRejectsSequentialReplay()
    {
        var service = new CountingSendLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service);
        var confirmationToken = await PreviewPaymentAsync(controller);

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);
        var replayResult = await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.SendCalls);
        var replayModel = await LoadPaymentResultAsync(controller, replayResult);
        Assert.False(replayModel.Result!.IsSuccess);
        Assert.Contains("already used", replayModel.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSend_DoesNotRestoreConfirmationAfterCancellation()
    {
        var service = new CountingSendLightningManagerService
        {
            ThrowOnSend = true
        };
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service);
        var confirmationToken = await PreviewPaymentAsync(controller);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.ExecuteSend(
                "BTC",
                "lnbcrt1test",
                null,
                null,
                CancellationToken.None,
                confirmationToken));

        service.ThrowOnSend = false;
        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.SendCalls);
    }

    [Fact]
    public async Task ExecuteSend_RedirectsAndNextSendPageShowsPaymentDetails()
    {
        var service = new CountingSendLightningManagerService();
        var paymentConfirmationStore = new LightningManagerPaymentConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()));
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            paymentConfirmationStore: paymentConfirmationStore);
        var confirmationToken = await PreviewPaymentAsync(controller);

        var executeResult = await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        Assert.Equal("Send", redirect.ActionName);
        Assert.Equal(1, service.SendCalls);
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
        var paymentConfirmationStore = new LightningManagerPaymentConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()));
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly()),
            new CountingSendLightningManagerService(),
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            paymentConfirmationStore: paymentConfirmationStore);
        var confirmationToken = await PreviewPaymentAsync(controller);

        await controller.ExecuteSend(
            "BTC",
            "lnbcrt1test",
            null,
            null,
            CancellationToken.None,
            confirmationToken);

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
    public async Task Results_FromPreviousBackendConfiguration_AreNotShownOrConsumed()
    {
        var originalContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-rest;server=https://node.example/;macaroon=AABB");
        var changedContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-grpc;server=https://node.example;macaroon=CCDD");
        Assert.NotEqual(
            originalContext.BackendFingerprint,
            changedContext.BackendFingerprint);
        Assert.Equal(
            originalContext.BackendIdentityFingerprint,
            changedContext.BackendIdentityFingerprint);
        var resultStore = new LightningManagerResultStore(
            new MemoryCache(new MemoryCacheOptions()));
        var paymentResultId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            originalContext.BackendFingerprint,
            CreateSendResult("original-payment"));
        resultStore.StoreChannel(
            "user-1",
            "store-1",
            "BTC",
            originalContext.BackendFingerprint,
            new ActionResultViewModel
            {
                IsSuccess = true,
                Message = "Original channel result"
            });
        var contextFactory = new FakeStoreLightningManagerContextFactory
        {
            Context = changedContext
        };
        var controller = TestControllerFactory.CreateController(
            originalContext,
            new LightningManagerService(),
            resultStore,
            contextFactory: contextFactory);

        var changedSend = Assert.IsType<SendViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Send(
                    "BTC",
                    CancellationToken.None,
                    paymentResultId)).Model);
        Assert.Equal(
            "Payment result is no longer available. Check the Lightning node before retrying.",
            changedSend.Result!.Message);
        var changedChannels = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Channels("BTC", CancellationToken.None)).Model);
        Assert.Null(changedChannels.Result);

        contextFactory.Context = originalContext;
        var originalSend = Assert.IsType<SendViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Send(
                    "BTC",
                    CancellationToken.None,
                    paymentResultId)).Model);
        Assert.Equal("original-payment", originalSend.Payment!.PaymentHash);
        var originalChannels = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Channels("BTC", CancellationToken.None)).Model);
        Assert.Equal("Original channel result", originalChannels.Result!.Message);
    }

    [Fact]
    public async Task PaymentResults_AreScopedReusableAndIndependent()
    {
        var resultStore = new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions()));
        var firstId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("first-hash"));
        var secondId = resultStore.StorePayment(
            "user-2",
            "store-2",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("second-hash"));

        Assert.False(resultStore.TryGetPayment(
            firstId,
            "user-2",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-2",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-1",
            "BTC",
            "backend-b",
            out _));
        Assert.True(resultStore.TryGetPayment(
            secondId,
            "user-2",
            "store-2",
            "BTC",
            ResultBackendFingerprint,
            out var second));
        Assert.Equal("second-hash", second!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-1",
            "btc",
            ResultBackendFingerprint,
            out var first));
        Assert.Equal("first-hash", first!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out var refreshed));
        Assert.Equal("first-hash", refreshed!.Payment!.PaymentHash);

        var concurrentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("concurrent-hash"));
        var concurrentReads = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(index => Task.Run(() =>
                    resultStore.TryGetPayment(
                        concurrentId,
                        "user-1",
                        "store-1",
                        "BTC",
                        ResultBackendFingerprint,
                        out _))));
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
            ResultBackendFingerprint,
            CreateSendResult("first-payment-hash"));
        var secondPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("second-payment-hash"));
        var channelId = resultStore.StoreChannel(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            new ActionResultViewModel { IsSuccess = false, Message = "Channel status is unknown." });

        Assert.False(resultStore.TryGetPendingPayment(
            "user-2",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            "backend-b",
            out _));
        Assert.True(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "btc",
            ResultBackendFingerprint,
            out var firstPayment));
        Assert.Equal("first-payment-hash", firstPayment!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out var secondPayment));
        Assert.Equal("second-payment-hash", secondPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            firstPaymentId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            secondPaymentId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPayment(
            channelId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));

        var pendingPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("pending-payment-hash"));
        var redirectedPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            CreateSendResult("redirected-payment-hash"));
        Assert.True(resultStore.TryGetPayment(
            redirectedPaymentId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out var pendingPayment));
        Assert.Equal("pending-payment-hash", pendingPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            pendingPaymentId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));

        Assert.True(resultStore.TryGetPendingChannel(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out var channel));
        Assert.Equal("Channel status is unknown.", channel!.Message);
        Assert.False(resultStore.TryGetPendingChannel(
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetChannel(
            channelId,
            "user-1",
            "store-1",
            "BTC",
            ResultBackendFingerprint,
            out _));
    }

    [Fact]
    public async Task OpenChannel_WhenRedirectIsLost_RecoversResultOnNextChannelsVisit()
    {
        var confirmationStore = new LightningManagerChannelConfirmationStore(
            new MemoryCache(new MemoryCacheOptions()));
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        var service = new CountingOpenChannelLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            context,
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            confirmationStore);

        var previewResult = await controller.PreviewChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None);
        var preview = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(previewResult).Model);
        var confirmationToken = Assert.IsType<string>(preview.OpenChannelConfirmationToken);

        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        var recoveredResult = await controller.Channels("BTC", CancellationToken.None);
        var recoveredView = Assert.IsType<ViewResult>(recoveredResult);
        var recoveredModel = Assert.IsType<ChannelsViewModel>(recoveredView.Model);
        Assert.True(recoveredModel.Result!.IsSuccess);
        Assert.Equal("Channel opening request submitted.", recoveredModel.Result.Message);
        Assert.Equal(1, service.OpenChannelCalls);
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
    public async Task OpenChannel_WithChangedCredentials_PreservesTokenForOriginalConfiguration()
    {
        var originalContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-rest;server=https://node-a.example/;macaroon=AABB");
        var changedContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-grpc;server=https://node-a.example;macaroon=CCDD");
        Assert.NotEqual(
            originalContext.BackendFingerprint,
            changedContext.BackendFingerprint);
        Assert.Equal(
            originalContext.BackendIdentityFingerprint,
            changedContext.BackendIdentityFingerprint);
        var contextFactory = new FakeStoreLightningManagerContextFactory
        {
            Context = originalContext
        };
        var service = new CountingOpenChannelLightningManagerService();
        var controller = TestControllerFactory.CreateController(
            originalContext,
            service,
            new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions())),
            contextFactory: contextFactory);
        var previewResult = await controller.PreviewChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None);
        var preview = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(previewResult).Model);
        var confirmationToken = Assert.IsType<string>(preview.OpenChannelConfirmationToken);

        contextFactory.Context = changedContext;
        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, service.OpenChannelCalls);

        contextFactory.Context = originalContext;
        await controller.OpenChannel(
            "BTC",
            "node@127.0.0.1:9735",
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, service.OpenChannelCalls);
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
            context.BackendFingerprint,
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

    private static async Task<string> PreviewPaymentAsync(
        Controllers.LightningManagerController controller,
        string bolt11 = "lnbcrt1test",
        string? amountSats = null,
        string? maxFeeSats = null)
    {
        var previewResult = await controller.PreviewSend(
            "BTC",
            bolt11,
            amountSats,
            maxFeeSats,
            CancellationToken.None);
        var view = Assert.IsType<ViewResult>(previewResult);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Preview);
        return Assert.IsType<string>(model.PaymentConfirmationToken);
    }

    private static async Task<SendViewModel> LoadPaymentResultAsync(
        Controllers.LightningManagerController controller,
        IActionResult executeResult)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var resultPage = await controller.Send("BTC", CancellationToken.None, resultId);
        return Assert.IsType<SendViewModel>(Assert.IsType<ViewResult>(resultPage).Model);
    }

    private sealed class CountingSendLightningManagerService : LightningManagerService
    {
        public int SendCalls { get; private set; }
        public bool ThrowOnSend { get; set; }
        public bool IsAmountlessPreview { get; set; }
        public string? LastAmountSats { get; private set; }
        public string? LastMaxFeeSats { get; private set; }

        public override bool TryCreateSendPreview(
            StoreLightningManagerContext context,
            string? bolt11,
            string? amountSats,
            string? maxFeeSats,
            out SendPreviewViewModel? preview,
            out string? error)
        {
            if (string.IsNullOrWhiteSpace(bolt11))
            {
                preview = null;
                error = "A BOLT11 invoice is required.";
                return false;
            }

            long? parsedAmount =
                IsAmountlessPreview && long.TryParse(amountSats, out var amount) ? amount : null;
            long? parsedMaxFee =
                context.Capabilities.CanSetMaxFee && long.TryParse(maxFeeSats, out var maxFee) ? maxFee : null;
            preview = new SendPreviewViewModel
            {
                Bolt11 = bolt11.Trim(),
                PaymentAmount = LightMoney.Satoshis(parsedAmount ?? 2),
                UserAmountSats = parsedAmount,
                IsAmountless = parsedAmount is not null,
                AmountDisplay = $"{parsedAmount ?? 2} sats",
                MaxFeeSats = parsedMaxFee,
                MaxFeeDisplay = parsedMaxFee is null ? null : $"{parsedMaxFee} sats",
                Description = "Test invoice",
                PaymentHash = "test-hash",
                Payee = "test-payee",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            error = null;
            return true;
        }

        public override Task<SendExecutionResult> SendAsync(
            StoreLightningManagerContext context,
            string bolt11,
            string? amountSats,
            string? maxFeeSats,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            LastAmountSats = amountSats;
            LastMaxFeeSats = maxFeeSats;
            if (ThrowOnSend)
            {
                throw new OperationCanceledException();
            }

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
