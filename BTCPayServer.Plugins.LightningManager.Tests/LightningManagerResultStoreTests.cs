using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerResultStoreTests
{
    private const string BackendFingerprint = "backend-a";

    [Fact]
    public void PaymentResults_AreScopedReusableAndIndependent()
    {
        var resultStore = new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions()));
        var firstId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            CreateSendResult("first-hash"));
        var secondId = resultStore.StorePayment(
            "user-2",
            "store-2",
            "BTC",
            BackendFingerprint,
            CreateSendResult("second-hash"));

        Assert.False(resultStore.TryGetPayment(
            firstId,
            "user-2",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-2",
            "BTC",
            BackendFingerprint,
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
            BackendFingerprint,
            out var second));
        Assert.Equal("second-hash", second!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-1",
            "btc",
            BackendFingerprint,
            out var first));
        Assert.Equal("first-hash", first!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPayment(
            firstId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out var refreshed));
        Assert.Equal("first-hash", refreshed!.Payment!.PaymentHash);
    }

    [Fact]
    public void PendingResults_AreUserScopedQueuedAndSeparatedByOperation()
    {
        var resultStore = new LightningManagerResultStore(new MemoryCache(new MemoryCacheOptions()));
        var firstPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            CreateSendResult("first-payment-hash"));
        var secondPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            CreateSendResult("second-payment-hash"));
        var channelId = resultStore.StoreChannel(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            new ActionResultViewModel { IsSuccess = false, Message = "Channel status is unknown." });

        Assert.False(resultStore.TryGetPendingPayment(
            "user-2",
            "store-1",
            "BTC",
            BackendFingerprint,
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
            BackendFingerprint,
            out var firstPayment));
        Assert.Equal("first-payment-hash", firstPayment!.Payment!.PaymentHash);
        Assert.True(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out var secondPayment));
        Assert.Equal("second-payment-hash", secondPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            firstPaymentId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            secondPaymentId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.False(resultStore.TryGetPayment(
            channelId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));

        var pendingPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            CreateSendResult("pending-payment-hash"));
        var redirectedPaymentId = resultStore.StorePayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            CreateSendResult("redirected-payment-hash"));
        Assert.True(resultStore.TryGetPayment(
            redirectedPaymentId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out var pendingPayment));
        Assert.Equal("pending-payment-hash", pendingPayment!.Payment!.PaymentHash);
        Assert.False(resultStore.TryGetPendingPayment(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetPayment(
            pendingPaymentId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));

        Assert.True(resultStore.TryGetPendingChannel(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out var channel));
        Assert.Equal("Channel status is unknown.", channel!.Message);
        Assert.False(resultStore.TryGetPendingChannel(
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
        Assert.True(resultStore.TryGetChannel(
            channelId,
            "user-1",
            "store-1",
            "BTC",
            BackendFingerprint,
            out _));
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
}
