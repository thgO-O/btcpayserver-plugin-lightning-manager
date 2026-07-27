using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerChannelConfirmationStoreTests
{
    private const string BackendFingerprint = "backend-a";

    [Fact]
    public void Confirmation_IsScopedAndCanOnlyBeConsumedOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerChannelConfirmationStore(cache);
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", "1");

        Assert.False(store.TryConsume(token, "user-2", "store-1", "BTC", BackendFingerprint, "node-a", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-2", "BTC", BackendFingerprint, "node-a", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "LTC", BackendFingerprint, "node-a", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", "backend-b", "node-a", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-b", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "200000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", "2"));
        Assert.True(store.TryConsume(token, "user-1", "store-1", "btc", BackendFingerprint, "node-a", "100000", "1"));
        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", "1"));
    }

    [Fact]
    public async Task Confirmation_ConcurrentConsumptionSucceedsOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerChannelConfirmationStore(cache);
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() =>
                    store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null))));

        Assert.Single(results, consumed => consumed);
    }

    [Fact]
    public async Task Confirmation_Expires()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerChannelConfirmationStore(cache, TimeSpan.FromMilliseconds(20));
        var token = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null);

        await Task.Delay(100);

        Assert.False(store.TryConsume(token, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null));
    }

    [Fact]
    public void NewConfirmation_AllowsANewOperation()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new LightningManagerChannelConfirmationStore(cache);
        var first = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null);
        var second = store.Create("user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null);

        Assert.NotEqual(first, second);
        Assert.True(store.TryConsume(first, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null));
        Assert.True(store.TryConsume(second, "user-1", "store-1", "BTC", BackendFingerprint, "node-a", "100000", null));
    }
}
