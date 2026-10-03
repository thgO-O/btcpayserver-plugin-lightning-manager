using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using System.Net;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Plugins.LightningManager.Services;
using NBitcoin;
using NBXplorer;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BTCPayServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
        // Exercise an upgrade from the original wallet schema, not only a fresh database.
        await using (var original = factory.CreateContext())
            await original.GetService<IMigrator>().MigrateAsync("20260930000100_WalletOperations", TestContext.Current.CancellationToken);
        var repository = new WalletRepository(factory);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var attempts = Enumerable.Range(0, 16).Select(i => new WalletOperation
        {
            StoreId = "store-" + i, UserId = "user", NodeIdentity = "regtest:node1",
            Direction = "Outgoing", PaymentHash = new string('a', 64), State = "Submitting", AmountMsat = 1_000_000
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
        // A lookup of a previous backend attempt can arrive before this executor's success.
        winner.State = "Failed";
        await repository.UpdateAsync(winner, TestContext.Current.CancellationToken);
        Assert.Equal("Failed", (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!.State);
        // A timeout/pending result must replace an older failure, and failed rows
        // must remain discoverable even when the executor never saved its result.
        foreach (var uncertain in new[] { "Pending", "Unknown" })
        {
            durable.State = uncertain;
            await restarted.UpdateAsync(durable, TestContext.Current.CancellationToken);
            Assert.Equal(uncertain, (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!.State);
            await repository.UpdateAsync(winner, TestContext.Current.CancellationToken);
            Assert.Single(await restarted.PendingAsync(TestContext.Current.CancellationToken));
        }
        var handler = new PaymentHandler();
        using var http = new HttpClient(handler);
        var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
        var node = new WalletNode(new StoreLightningManagerContext
        {
            StoreId = winner.StoreId, CryptoCode = "BTC", BackendFingerprint = "test", BackendIdentityFingerprint = "test",
            Capabilities = LightningCapabilities.Full, Client = client
        }, winner.NodeIdentity);
        var wallet = new WalletService(null!, null!, restarted, null!, NullLogger<WalletService>.Instance, null!);
        durable.State = "Submitting";
        await restarted.UpdateAsync(durable, TestContext.Current.CancellationToken);
        handler.State = "FAILED";
        await wallet.ReconcileAsync(node, durable, TestContext.Current.CancellationToken);
        Assert.Equal("Submitting", (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!.State);
        // After the executor's grace period, a crashed submission can resolve to
        // failure and still be revisited if a late backend settlement follows.
        await using (var db = factory.CreateContext())
            await db.Operations.Where(x => x.Id == winner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-3)), TestContext.Current.CancellationToken);
        var abandoned = (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!;
        await wallet.ReconcileAsync(node, abandoned, TestContext.Current.CancellationToken);
        Assert.Equal("Failed", abandoned.State);
        handler.State = "SUCCEEDED";
        var failed = (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!;
        Assert.Equal("Failed", failed.State);
        await wallet.ReconcileAsync(node, failed, TestContext.Current.CancellationToken);
        Assert.Equal("Settled", failed.State);
        Assert.Equal(1234, failed.FeeMsat);
        Assert.Equal(1_000_000, failed.AmountMsat); // authorized amount is retained
        Assert.Equal(500_000, failed.SettledAmountMsat); // native backend amount wins
        Assert.Equal(500_000, failed.DisplayAmountMsat);
        foreach (var staleState in new[] { "Unknown", "Failed", "Pending", "Expired", "Settled" })
        {
            winner.State = staleState;
            winner.FeeMsat = null;
            winner.SettledAmountMsat = 999_000;
            await repository.UpdateAsync(winner, TestContext.Current.CancellationToken);
            var settled = await repository.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken);
            Assert.Equal("Settled", settled!.State);
            Assert.Equal(1234, settled.FeeMsat);
            Assert.Equal(500_000, settled.SettledAmountMsat);
            Assert.Equal(1_000_000, settled.AmountMsat);
        }
        Assert.Empty(await restarted.PendingAsync(TestContext.Current.CancellationToken));
        // Legacy settled rows and transiently unavailable amounts are backfilled without resend.
        await using (var db = factory.CreateContext())
            await db.Operations.Where(x => x.Id == winner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettledAmountMsat, (long?)null), TestContext.Current.CancellationToken);
        var legacy = Assert.Single(await restarted.PendingAsync(TestContext.Current.CancellationToken));
        Assert.Null(legacy.DisplayAmountMsat);
        handler.State = "FAILED";
        await wallet.ReconcileAsync(node, legacy, TestContext.Current.CancellationToken);
        Assert.Equal("Settled", legacy.State);
        Assert.Null(legacy.DisplayAmountMsat);
        Assert.Equal("Settled", (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!.State);
        handler.State = "SUCCEEDED";
        await wallet.ReconcileAsync(node, legacy, TestContext.Current.CancellationToken);
        var backfilled = (await restarted.GetAsync(winner.Id, winner.StoreId, winner.NodeIdentity, TestContext.Current.CancellationToken))!;
        Assert.Equal("Settled", backfilled.State);
        Assert.Equal(500_000, backfilled.SettledAmountMsat);
        Assert.Equal(1_000_000, backfilled.AmountMsat);
        Assert.Equal(1234, backfilled.FeeMsat);
        Assert.Empty(await restarted.PendingAsync(TestContext.Current.CancellationToken));
        await AssertNativeReceivingAsync(restarted, factory);
    }

    private static async Task AssertNativeReceivingAsync(WalletRepository repository, WalletDbContextFactory repositoryFactory)
    {
        var token = TestContext.Current.CancellationToken;
        var peer = new LightningClientFactory(Network.RegTest).Create(Environment.GetEnvironmentVariable("TEST_CUSTOMERLIGHTNINGD") ?? "type=clightning;server=tcp://127.0.0.1:30992/");
        var network = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC() };
        foreach (var privateHints in new[] { false, true })
        foreach (var state in new[] { "CANCELED", "SETTLED" })
        {
            // Use an actual, correctly signed BOLT11; observe native request serialization.
            var invoice = await peer.CreateInvoice(LightMoney.Satoshis(500), "Route hint regression", TimeSpan.FromHours(1), token);
            var handler = new InvoiceHandler(invoice, privateHints) { State = state };
            using var http = new HttpClient(handler);
            var client = new LndClient(new LndSwaggerClient(new LndRestSettings(new Uri("https://lnd.example.test/")), http), Network.RegTest);
            var node = new WalletNode(new StoreLightningManagerContext
            {
                StoreId = "receiving", CryptoCode = "BTC", BackendFingerprint = "test", BackendIdentityFingerprint = "test",
                Network = network, Capabilities = LightningCapabilities.Full, Client = client
            }, "regtest:receiving", privateHints);
            var wallet = new WalletService(null!, null!, repository, null!, NullLogger<WalletService>.Instance, null!);
            var operation = await wallet.ReceiveAsync(node, "user", 500, "Route hint regression", token);
            handler.Unavailable = true;
            await Assert.ThrowsAsync<SwaggerException>(() => wallet.ReconcileAsync(node, operation, token));
            Assert.Equal("Pending", (await repository.GetAsync(operation.Id, operation.StoreId, node.Identity, token))!.State);
            handler.Unavailable = false;
            await wallet.ReconcileAsync(node, operation, token);
            var saved = (await repository.GetAsync(operation.Id, operation.StoreId, node.Identity, token))!;
            if (state == "CANCELED")
            {
                Assert.Equal("Expired", saved.State);
                continue;
            }
            Assert.Equal("Settled", saved.State);
            Assert.Equal(500_000, saved.AmountMsat);
            Assert.Equal(501_000, saved.SettledAmountMsat);
            Assert.Equal(501_000, saved.DisplayAmountMsat);
            Assert.False(saved.RequiresReconciliation);
            Assert.Empty(await repository.PendingAsync(token));

            // Existing settled receipts must backfill the received amount, even after restart.
            await using (var db = repositoryFactory.CreateContext())
                await db.Operations.Where(x => x.Id == saved.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettledAmountMsat, (long?)null), token);
            var legacy = Assert.Single(await repository.PendingAsync(token));
            Assert.Null(legacy.DisplayAmountMsat);
            // Neither a stale state nor a missing received amount undoes confirmed settlement.
            handler.State = "CANCELED";
            await wallet.ReconcileAsync(node, legacy, token);
            handler.State = "SETTLED";
            handler.ReceivedMsat = null;
            await wallet.ReconcileAsync(node, legacy, token);
            saved = (await repository.GetAsync(operation.Id, operation.StoreId, node.Identity, token))!;
            Assert.Equal("Settled", saved.State);
            Assert.Null(saved.DisplayAmountMsat);
            Assert.Single(await repository.PendingAsync(token));
            handler.ReceivedMsat = "501000";
            await wallet.ReconcileAsync(node, legacy, token);
            saved = (await repository.GetAsync(operation.Id, operation.StoreId, node.Identity, token))!;
            Assert.Equal(501_000, saved.DisplayAmountMsat);
            Assert.Equal(500_000, saved.AmountMsat);
            // A delayed reconciler cannot overwrite a known received amount.
            operation.SettledAmountMsat = 500_000;
            await repository.UpdateAsync(operation, token);
            Assert.Equal(501_000, (await repository.GetAsync(operation.Id, operation.StoreId, node.Identity, token))!.SettledAmountMsat);
        }
        Assert.Empty(await repository.PendingAsync(token));
    }

    private sealed class InvoiceHandler(LightningInvoice invoice, bool privateHints) : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public string State { get; set; } = "CANCELED";
        public string? ReceivedMsat { get; set; } = "501000";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/invoices", request.RequestUri!.AbsolutePath);
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(privateHints, body["private"]!.Value<bool>());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new
                    { payment_request = invoice.BOLT11, r_hash = Convert.ToBase64String(Encoders.Hex.DecodeData(invoice.PaymentHash)) }))
                };
            }
            Assert.StartsWith("/v1/invoice/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(JsonConvert.SerializeObject(new { state = State, settled = State == "SETTLED", amt_paid_msat = ReceivedMsat })) };
        }
    }

    private sealed class PaymentHandler : HttpMessageHandler
    {
        public string State { get; set; } = "SUCCEEDED";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("/v2/router/track/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(new
                { result = new { status = State, payment_hash = new string('a', 64), value_msat = "500000", fee_msat = "1234" } }) + "\n", Encoding.UTF8, "application/json")
            });
        }
    }
}
