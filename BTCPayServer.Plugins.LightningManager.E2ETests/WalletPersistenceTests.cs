using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

[Collection("Lightning Manager E2E")]
public class WalletPersistenceTests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task DurableClaimsSurviveRestartAndPreventConcurrentCrossStoreSubmissions()
    {
        var database = new DatabaseTester(TestLogs, NullLoggerFactory.Instance);
        await database.MigrateAsync();
        var factory = new WalletDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = database.ConnectionString }));
        var repository = new WalletRepository(factory);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var attempts = Enumerable.Range(0, 16).Select(i => new WalletOperation
        {
            StoreId = "store-" + i, UserId = "user", NodeIdentity = "regtest:node1",
            Direction = "Outgoing", PaymentHash = "payment1", State = "Submitting", AmountMsat = 500_000
        }).ToArray();
        var results = await Task.WhenAll(attempts.Select(x => repository.InsertAsync(x, TestContext.Current.CancellationToken)));
        Assert.Single(results, x => x);
        var winner = attempts[Array.IndexOf(results, true)];

        var restarted = new WalletRepository(factory);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        var durable = await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken);
        Assert.NotNull(durable);
        Assert.Equal("Submitting", durable.State);
        Assert.False(await restarted.InsertAsync(new WalletOperation
        { StoreId = "other", NodeIdentity = winner.NodeIdentity, Direction = "Outgoing", PaymentHash = winner.PaymentHash }, TestContext.Current.CancellationToken));
        Assert.Null(await restarted.GetAsync(winner.Id, "other", winner.NodeIdentity, TestContext.Current.CancellationToken));
        Assert.Null(await restarted.GetAsync(winner.Id, winner.StoreId, "regtest:changed-node", TestContext.Current.CancellationToken));
        durable.State = "Settled";
        durable.FeeMsat = 1234;
        await restarted.UpdateAsync(durable, TestContext.Current.CancellationToken);
        winner.State = "Unknown";
        await repository.UpdateAsync(winner, TestContext.Current.CancellationToken);
        Assert.Equal("Settled", (await repository.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(await restarted.PendingAsync(TestContext.Current.CancellationToken));
    }
}
