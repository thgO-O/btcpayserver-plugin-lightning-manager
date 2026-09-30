using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Plugins.LightningManager.Wallet;
using Fido2NetLib;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class WalletAuthorizationTests
{
    [Theory]
    [InlineData("other-user", "store", "node", "configuration")]
    [InlineData("user", "other-store", "node", "configuration")]
    [InlineData("user", "store", "other-node", "configuration")]
    [InlineData("user", "store", "node", "other-configuration")]
    public void ConfirmationCannotBeUsedOutsideItsOriginalContext(string user, string store, string node, string configuration)
    {
        var clock = new TestClock();
        var confirmations = new WalletAuthorizationStore(clock);
        var preview = new SendPreviewViewModel { Bolt11 = "original", UserAmountSats = 123, MaxFeeSats = 10 };
        var id = confirmations.Create(new WalletAuthorization("user", "store", "node", "configuration", preview, new AssertionOptions(), clock.GetUtcNow().AddMinutes(2)));
        Assert.Null(confirmations.Get(id, user, store, node, configuration));
        Assert.Null(confirmations.Consume(id, user, store, node, configuration));
        Assert.Same(preview, confirmations.Consume(id, "user", "store", "node", "configuration")!.Preview);
        Assert.Null(confirmations.Consume(id, "user", "store", "node", "configuration"));
    }

    [Fact]
    public async Task OnlyOneConcurrentExecutionCanConsumeAConfirmation()
    {
        var clock = new TestClock();
        var store = new WalletAuthorizationStore(clock);
        var id = store.Create(new WalletAuthorization("user", "store", "node", "config", new SendPreviewViewModel(), new AssertionOptions(), clock.GetUtcNow().AddMinutes(2)));
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => store.Consume(id, "user", "store", "node", "config"))));
        Assert.Single(results, x => x is not null);
    }

    [Fact]
    public void ExpiredAndPreRestartConfirmationsCannotExecute()
    {
        var clock = new TestClock();
        var store = new WalletAuthorizationStore(clock);
        var id = store.Create(new WalletAuthorization("user", "store", "node", "config", new SendPreviewViewModel(), new AssertionOptions(), clock.GetUtcNow().AddMinutes(2)));
        Assert.Null(new WalletAuthorizationStore(clock).Consume(id, "user", "store", "node", "config"));
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Null(store.Get(id, "user", "store", "node", "config"));
        Assert.Null(store.Consume(id, "user", "store", "node", "config"));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
