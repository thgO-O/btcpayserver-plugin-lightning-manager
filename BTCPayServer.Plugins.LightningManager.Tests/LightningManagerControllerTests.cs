using System.Reflection;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerControllerTests
{
    private const string ValidNodeUri =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

    [Fact]
    public void SendPage_ExposesPayOnlyCapabilities()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly));

        var result = controller.Send("BTC");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.Same(LightningCapabilities.BlinkPayOnly, model.Capabilities);
        Assert.Equal("Test Node", model.BackendDisplayName);
        Assert.False(model.HasPaymentResult);
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
        Assert.False(model.Capabilities.HasAny);
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
    public void PeersPage_ReturnsPeersModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = controller.Peers("BTC");

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PeersViewModel>(view.Model);
    }

    [Fact]
    public void PreviewSend_WithInvalidInvoice_ReturnsFailureOnModel()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = controller.PreviewSend("BTC", "invalid", null, null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal("The BOLT11 invoice is invalid.", model.Result.Message);
        Assert.Null(model.PaymentConfirmationToken);
        Assert.False(model.HasPaymentResult);
    }

    [Fact]
    public async Task PreviewSend_WithValidInvoice_CreatesIndependentConfirmationTokens()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));

        var firstToken = PreviewPayment(controller, $" {TestInvoiceData.AmountlessBolt11} ", "123", "7");
        var secondToken = PreviewPayment(controller, TestInvoiceData.AmountlessBolt11, "123", "7");

        Assert.NotEqual(firstToken, secondToken);

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.AmountlessBolt11,
            "123",
            "7",
            CancellationToken.None,
            firstToken);
        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.AmountlessBolt11,
            "123",
            "7",
            CancellationToken.None,
            secondToken);

        Assert.Equal(2, client.SendCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task ExecuteSend_WithMissingOrInvalidConfirmation_DoesNotCallService(
        string? confirmationToken)
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));

        var executeResult = await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, client.SendCalls);
        var model = LoadPaymentResult(controller, executeResult);
        Assert.False(model.Result!.IsSuccess);
        Assert.Equal(
            "This payment confirmation is invalid, expired, or already used. If it may already have been submitted, check the Lightning node before previewing again.",
            model.Result.Message);
    }

    [Fact]
    public async Task ExecuteSend_WithTamperedPayload_PreservesTokenForOriginalPayload()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));
        var confirmationToken = PreviewPayment(
            controller,
            $" {TestInvoiceData.AmountlessBolt11} ",
            "123",
            "7");

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            "123",
            "7",
            CancellationToken.None,
            confirmationToken);
        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.AmountlessBolt11,
            "124",
            "7",
            CancellationToken.None,
            confirmationToken);
        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.AmountlessBolt11,
            "123",
            "8",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, client.SendCalls);

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.AmountlessBolt11.ToUpperInvariant(),
            "000123",
            "007",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, client.SendCalls);
    }

    [Fact]
    public async Task ExecuteSend_WithChangedCredentials_PreservesTokenForOriginalConfiguration()
    {
        var client = new CountingSendLightningClient();
        var originalContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=lnd-rest;server=https://node-a.example/;macaroon=AABB");
        var changedContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
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
        var controller = TestControllerFactory.CreateController(
            originalContext,
            contextFactory: contextFactory);
        var confirmationToken = PreviewPayment(
            controller,
            maxFeeSats: "7");

        contextFactory.Context = changedContext;
        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            "7",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, client.SendCalls);

        contextFactory.Context = originalContext;
        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            "7",
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, client.SendCalls);
        Assert.Equal(7, client.LastMaxFee?.Satoshi);
    }

    [Fact]
    public async Task ExecuteSend_WithFixedAmountInvoice_IgnoresPreviewOverrides()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));
        var confirmationToken = PreviewPayment(
            controller,
            amountSats: "999999",
            maxFeeSats: "-10");

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, client.SendCalls);
        Assert.Null(client.LastAmount);
        Assert.Null(client.LastMaxFee);
    }

    [Fact]
    public async Task ExecuteSend_ConsumesConfirmationAndRejectsSequentialReplay()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));
        var confirmationToken = PreviewPayment(controller);

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);
        var replayResult = await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, client.SendCalls);
        var replayModel = LoadPaymentResult(controller, replayResult);
        Assert.False(replayModel.Result!.IsSuccess);
        Assert.Contains("already used", replayModel.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSend_DoesNotRestoreConfirmationAfterCancellation()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));
        var confirmationToken = PreviewPayment(controller);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.ExecuteSend(
                "BTC",
                TestInvoiceData.FixedAmountBolt11,
                null,
                null,
                cancellation.Token,
                confirmationToken));

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, client.SendCalls);
    }

    [Fact]
    public async Task ExecuteSend_RedirectsAndNextSendPageShowsPaymentDetails()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));
        var confirmationToken = PreviewPayment(controller);

        var executeResult = await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        Assert.Equal("Send", redirect.ActionName);
        Assert.Equal(1, client.SendCalls);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var sendResult = controller.Send("BTC", resultId);
        var view = Assert.IsType<ViewResult>(sendResult);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.True(model.Result!.IsSuccess);
        Assert.NotNull(model.Payment);
        Assert.True(model.HasPaymentResult);
        Assert.Equal(LightningPaymentStatus.Complete, model.Payment!.Status);
        Assert.Equal("2 sats", model.Payment.PaymentAmountDisplay);
        Assert.Equal(
            BOLT11PaymentRequest.Parse(TestInvoiceData.FixedAmountBolt11, Network.RegTest)
                .GetPayeePubKey()
                .ToString(),
            model.Payment.Payee);
        Assert.False(string.IsNullOrWhiteSpace(model.Payment.PaymentHash));
        Assert.Equal("preimage", model.Payment.Preimage);

        var refreshResult = controller.Send("BTC", resultId);
        var refreshView = Assert.IsType<ViewResult>(refreshResult);
        var refreshModel = Assert.IsType<SendViewModel>(refreshView.Model);
        Assert.True(refreshModel.HasPaymentResult);
        Assert.True(refreshModel.Result!.IsSuccess);
        Assert.Equal(LightningPaymentStatus.Complete, refreshModel.Payment!.Status);
    }

    [Fact]
    public async Task ExecuteSend_WhenRedirectIsLost_RecoversResultOnNextSendVisit()
    {
        var client = new CountingSendLightningClient();
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.BlinkPayOnly, client));
        var confirmationToken = PreviewPayment(controller);

        await controller.ExecuteSend(
            "BTC",
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            CancellationToken.None,
            confirmationToken);

        var recoveredResult = controller.Send("BTC");
        var recoveredView = Assert.IsType<ViewResult>(recoveredResult);
        var recoveredModel = Assert.IsType<SendViewModel>(recoveredView.Model);
        Assert.True(recoveredModel.HasPaymentResult);
        Assert.True(recoveredModel.Result!.IsSuccess);
        Assert.Equal(LightningPaymentStatus.Complete, recoveredModel.Payment!.Status);

        var nextResult = controller.Send("BTC");
        var nextView = Assert.IsType<ViewResult>(nextResult);
        var nextModel = Assert.IsType<SendViewModel>(nextView.Model);
        Assert.False(nextModel.HasPaymentResult);
        Assert.Null(nextModel.Result);
        Assert.Null(nextModel.Payment);
    }

    [Fact]
    public void SendPage_WithMissingResult_ShowsRetryWarning()
    {
        var controller = TestControllerFactory.CreateController(
            TestContextFactory.CreateConfigured(LightningCapabilities.Full));

        var result = controller.Send("BTC", Guid.NewGuid().ToString("N"));

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.True(model.HasPaymentResult);
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
            resultStore: resultStore,
            contextFactory: contextFactory);

        var changedSend = Assert.IsType<SendViewModel>(
            Assert.IsType<ViewResult>(
                controller.Send(
                    "BTC",
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
                controller.Send(
                    "BTC",
                    paymentResultId)).Model);
        Assert.Equal("original-payment", originalSend.Payment!.PaymentHash);
        var originalChannels = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Channels("BTC", CancellationToken.None)).Model);
        Assert.Equal("Original channel result", originalChannels.Result!.Message);
    }

    [Fact]
    public async Task OpenChannel_WhenRedirectIsLost_RecoversResultOnNextChannelsVisit()
    {
        var client = new CountingOpenChannelLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var controller = TestControllerFactory.CreateController(context);

        var previewResult = await controller.PreviewChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None);
        var preview = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(previewResult).Model);
        var confirmationToken = Assert.IsType<string>(preview.OpenChannelConfirmationToken);

        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        var recoveredResult = await controller.Channels("BTC", CancellationToken.None);
        var recoveredView = Assert.IsType<ViewResult>(recoveredResult);
        var recoveredModel = Assert.IsType<ChannelsViewModel>(recoveredView.Model);
        Assert.True(recoveredModel.Result!.IsSuccess);
        Assert.Equal("Channel opening request submitted.", recoveredModel.Result.Message);
        Assert.Equal(1, client.OpenChannelCalls);
    }

    [Fact]
    public async Task OpenChannel_ConsumesPreviewConfirmationAndRejectsReplay()
    {
        var client = new CountingOpenChannelLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var controller = TestControllerFactory.CreateController(context);

        var firstPreviewResult = await controller.PreviewChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None);
        var firstPreview = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(firstPreviewResult).Model);
        var firstToken = Assert.IsType<string>(firstPreview.OpenChannelConfirmationToken);

        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "200000",
            null,
            CancellationToken.None,
            firstToken);
        Assert.Equal(0, client.OpenChannelCalls);

        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            firstToken);
        var replayResult = await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            firstToken);

        Assert.Equal(1, client.OpenChannelCalls);
        var replayRedirect = Assert.IsType<RedirectToActionResult>(replayResult);
        var replayResultId = Assert.IsType<string>(replayRedirect.RouteValues!["resultId"]);
        var replayPage = await controller.Channels("BTC", CancellationToken.None, replayResultId);
        var replayModel = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(replayPage).Model);
        Assert.False(replayModel.Result!.IsSuccess);
        Assert.Contains("already used", replayModel.Result.Message, StringComparison.Ordinal);

        var secondPreviewResult = await controller.PreviewChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None);
        var secondPreview = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(secondPreviewResult).Model);
        var secondToken = Assert.IsType<string>(secondPreview.OpenChannelConfirmationToken);
        Assert.NotEqual(firstToken, secondToken);

        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            secondToken);

        Assert.Equal(2, client.OpenChannelCalls);
    }

    [Fact]
    public async Task OpenChannel_WithChangedCredentials_PreservesTokenForOriginalConfiguration()
    {
        var client = new CountingOpenChannelLightningClient();
        var originalContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=lnd-rest;server=https://node-a.example/;macaroon=AABB");
        var changedContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
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
        var controller = TestControllerFactory.CreateController(
            originalContext,
            contextFactory: contextFactory);
        var previewResult = await controller.PreviewChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None);
        var preview = Assert.IsType<ChannelsViewModel>(
            Assert.IsType<ViewResult>(previewResult).Model);
        var confirmationToken = Assert.IsType<string>(preview.OpenChannelConfirmationToken);

        contextFactory.Context = changedContext;
        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(0, client.OpenChannelCalls);

        contextFactory.Context = originalContext;
        await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            confirmationToken);

        Assert.Equal(1, client.OpenChannelCalls);
    }

    [Fact]
    public async Task OpenChannel_WithInvalidConfirmation_DoesNotCallService()
    {
        var client = new CountingOpenChannelLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var controller = TestControllerFactory.CreateController(context);

        var invalidResult = await controller.OpenChannel(
            "BTC",
            ValidNodeUri,
            "100000",
            null,
            CancellationToken.None,
            "invalid");

        Assert.Equal(0, client.OpenChannelCalls);
        var redirect = Assert.IsType<RedirectToActionResult>(invalidResult);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var resultPage = await controller.Channels("BTC", CancellationToken.None, resultId);
        var model = Assert.IsType<ChannelsViewModel>(Assert.IsType<ViewResult>(resultPage).Model);
        Assert.False(model.Result!.IsSuccess);
        Assert.Contains("invalid, expired, or already used", model.Result.Message, StringComparison.Ordinal);
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

    private static string PreviewPayment(
        Controllers.LightningManagerController controller,
        string bolt11 = TestInvoiceData.FixedAmountBolt11,
        string? amountSats = null,
        string? maxFeeSats = null)
    {
        var previewResult = controller.PreviewSend(
            "BTC",
            bolt11,
            amountSats,
            maxFeeSats);
        var view = Assert.IsType<ViewResult>(previewResult);
        var model = Assert.IsType<SendViewModel>(view.Model);
        Assert.NotNull(model.Preview);
        Assert.Equal("Test Node", model.BackendDisplayName);
        Assert.False(model.HasPaymentResult);
        return Assert.IsType<string>(model.PaymentConfirmationToken);
    }

    private static SendViewModel LoadPaymentResult(
        Controllers.LightningManagerController controller,
        IActionResult executeResult)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(executeResult);
        var resultId = Assert.IsType<string>(redirect.RouteValues!["resultId"]);
        var resultPage = controller.Send("BTC", resultId);
        return Assert.IsType<SendViewModel>(Assert.IsType<ViewResult>(resultPage).Model);
    }

    private sealed class CountingSendLightningClient : FakeLightningClient
    {
        public int SendCalls { get; private set; }
        public LightMoney? LastAmount { get; private set; }
        public Money? LastMaxFee { get; private set; }

        public CountingSendLightningClient()
        {
            PayBolt11WithParamsHandler = (_, payParams, _) =>
            {
                SendCalls++;
                LastAmount = payParams.Amount;
                LastMaxFee = payParams.MaxFeeFlat;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            };
            GetPaymentHandler = (paymentHash, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = paymentHash,
                Preimage = "preimage"
            });
        }
    }

    private sealed class CountingOpenChannelLightningClient : FakeLightningClient
    {
        public int OpenChannelCalls { get; private set; }

        public CountingOpenChannelLightningClient()
        {
            ListChannelsHandler = _ => Task.FromResult(Array.Empty<LightningChannel>());
            OpenChannelHandler = (_, _) =>
            {
                OpenChannelCalls++;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            };
        }
    }
}
