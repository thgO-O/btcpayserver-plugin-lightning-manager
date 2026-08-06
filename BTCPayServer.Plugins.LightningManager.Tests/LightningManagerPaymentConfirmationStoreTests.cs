using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerPaymentConfirmationStoreTests
{
    private const string BackendFingerprint = "backend-a";

    [Theory]
    [InlineData(0L, null, "amountSats")]
    [InlineData(null, 0L, "maxFeeSats")]
    public void Create_RejectsNonPositiveOptionalValues(
        long? amountSats,
        long? maxFeeSats,
        string expectedParameter)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Create(
                "user-1",
                "store-1",
                "BTC",
                BackendFingerprint,
                "lnbcrt1test",
                amountSats,
                maxFeeSats));

        Assert.Equal(expectedParameter, exception.ParamName);
    }

    [Fact]
    public void Confirmation_IsScopedAndPayloadMismatchDoesNotConsumeIt()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache);
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, " LNBCRT1TEST ", 123, 7);

        Assert.False(store.TryConsume(token, "user-2", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-2", "BTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "LTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", "backend-b", "lnbcrt1test", "123", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1other", "123", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "124", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "not-a-number", "7"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "8"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "1.5"));

        Assert.True(store.TryConsume(token, "user-1", "store-1", "btc", BackendFingerprint, "lnbcrt1test", "000123", "007"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
    }

    [Fact]
    public void Confirmation_RejectsValuesThatDoNotApplyWithoutConsumingIt()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache);
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", null, null);

        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "999999", null));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", null, "-10"));
        Assert.True(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", null, null));
    }

    [Fact]
    public async Task Confirmation_ConcurrentConsumptionSucceedsOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache);
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", 123, null);

        var results = await ConcurrentTestRunner.RunAsync(
            20,
            () => store.TryConsume(
                token,
                "user-1",
                "store-1",
                "BTC",
                BackendFingerprint,
                "lnbcrt1test",
                "123",
                null));

        Assert.Single(results, consumed => consumed);
    }

    [Fact]
    public async Task Confirmation_Expires()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache, TimeSpan.FromMilliseconds(20));
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", null, null);

        await Task.Delay(100);

        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", null, null));
    }

    [Fact]
    public void NewConfirmation_AllowsANewOperation()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerPaymentConfirmationStore(cache);
        var first = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", 123, 7);
        var second = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", 123, 7);

        Assert.NotEqual(first, second);
        Assert.True(store.TryConsume(first, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
        Assert.True(store.TryConsume(second, "user-1", "store-1", "BTC", BackendFingerprint, "lnbcrt1test", "123", "7"));
    }
}
