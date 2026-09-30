using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

public partial class LightningManagerPlaywrightTests
{
    [Fact(Timeout = 120_000)]
    public async Task WalletModeRequiresStoreAccessAndExposesOnlyPublicPwaAssets()
    {
        var previousPlugins = Environment.GetEnvironmentVariable("DEBUG_PLUGINS");
        var previousDirectory = Environment.GetEnvironmentVariable("BTCPAY_PLUGINDIR");
        var directory = Path.Combine(Path.GetTempPath(), "wallet-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("DEBUG_PLUGINS", GetPluginAssemblyPath() + ";" + typeof(WalletWebAuthnTestPlugin).Assembly.Location);
        Environment.SetEnvironmentVariable("BTCPAY_PLUGINDIR", directory);
        try
        {
            await using var tester = CreatePlaywrightTester(newDb: true);
            tester.Server.ActivateLightning(LightningTestImplementation.CoreLightning);
            tester.Server.PayTester.NoCSP = true;
            await tester.Server.StartAsync();
            tester.ServerUri = new Uri(tester.Server.PayTester.ServerUriWithIP.AbsoluteUri.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
            await tester.StartAsync();
            var adminEmail = await tester.RegisterNewUser(true);
            var (_, storeId) = await tester.CreateNewStore();
            await tester.AddLightningNode(LightningTestImplementation.CoreLightning);
            var root = $"/stores/{storeId}/lightning/BTC/wallet/";
            await tester.GoToUrl(root, ignoreResponse: true);
            await Expect(tester.Page.GetByText("Wallet Mode is disabled for this store.", new() { Exact = true })).ToBeVisibleAsync();
            await tester.GoToUrl(root + "settings");
            await tester.Page.Locator("#Enabled").CheckAsync();
            await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            Assert.Contains(root.TrimEnd('/'), tester.Page.Url, StringComparison.Ordinal);
            await Expect(tester.Page.Locator(".ln-wallet__balance strong")).ToBeVisibleAsync();
            await tester.Page.GetByRole(AriaRole.Link, new() { Name = "Pay", Exact = true }).First.ClickAsync();
            await Expect(tester.Page.GetByText("Register a passkey", new() { Exact = false })).ToBeVisibleAsync();
            var authenticator = await tester.Page.Context.NewCDPSessionAsync(tester.Page);
            await authenticator.SendAsync("WebAuthn.enable");
            var added = await authenticator.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
            {
                ["options"] = new { protocol = "ctap2", transport = "internal", hasResidentKey = true,
                    hasUserVerification = true, isUserVerified = true, automaticPresenceSimulation = true }
            });
            var authenticatorId = added!.Value.GetProperty("authenticatorId").GetString();
            await tester.GoToUrl("/account/passkeys");
            await tester.Page.Locator("#passkey-form input[name=Name]").FillAsync("Wallet test passkey");
            await tester.Page.Locator("#btn-add-passkey").ClickAsync();
            var registration = await tester.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success, BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Error);
            Assert.Contains("registered successfully", await registration.InnerTextAsync(), StringComparison.Ordinal);
            await Expect(tester.Page.GetByText("Wallet test passkey", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var credentials = await authenticator.SendAsync("WebAuthn.getCredentials", new Dictionary<string, object> { ["authenticatorId"] = authenticatorId! });
            var adminCredentialId = credentials!.Value.GetProperty("credentials")[0].GetProperty("credentialId").GetString();

            await tester.GoToUrl(root);
            await tester.Page.EvaluateAsync("() => navigator.serviceWorker.ready.then(() => new Promise(resolve => navigator.serviceWorker.controller ? resolve() : navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })))");
            var cached = await tester.Page.EvaluateAsync<string[]>("async () => (await Promise.all((await caches.keys()).map(async key => (await (await caches.open(key)).keys()).map(r => new URL(r.url).pathname)))).flat()");
            Assert.NotEmpty(cached);
            Assert.All(cached, path => Assert.Contains(path, new[] { root + "offline", root + "assets/wallet.js", root + "assets/wallet.css", root + "assets/icon-192.png", root + "assets/icon-512.png" }));
            var financial = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "history").ToString());
            Assert.Contains("no-store", financial.Headers["cache-control"], StringComparison.Ordinal);
            foreach (var endpoint in new[] { "balance", "history/data" })
            {
                var data = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root + endpoint).ToString());
                Assert.Equal(200, data.Status);
                Assert.Contains("no-store", data.Headers["cache-control"], StringComparison.Ordinal);
                Assert.Contains("application/json", data.Headers["content-type"], StringComparison.Ordinal);
            }
            await tester.Page.Context.SetOfflineAsync(true);
            await tester.Page.GotoAsync(new Uri(tester.ServerUri, root).ToString());
            await Expect(tester.Page.GetByRole(AriaRole.Heading, new() { Name = "You are offline", Exact = true })).ToBeVisibleAsync();
            await tester.Page.Context.SetOfflineAsync(false);

            await using var anonymous = await tester.Browser.NewContextAsync();
            var manifest = await anonymous.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "manifest.webmanifest").ToString());
            Assert.Equal(200, manifest.Status);
            var status = await anonymous.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "operations/" + Guid.NewGuid() + "/status").ToString());
            Assert.Contains("/login", status.Url, StringComparison.OrdinalIgnoreCase);
            await tester.GoToUrl("/account");
            await tester.Logout();
            await tester.GoToRegister();
            await tester.RegisterNewUser();
            await tester.SkipWizard();
            var denied = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root).ToString());
            Assert.Equal(403, denied.Status);

            // Keep only the other account's real resident credential on the authenticator.
            await tester.GoToUrl("/account/passkeys");
            await tester.Page.Locator("#passkey-form input[name=Name]").FillAsync("Other account passkey");
            await tester.Page.Locator("#btn-add-passkey").ClickAsync();
            Assert.Contains("registered successfully", await (await tester.FindAlertMessage()).InnerTextAsync(), StringComparison.Ordinal);
            await authenticator.SendAsync("WebAuthn.removeCredential", new Dictionary<string, object>
                { ["authenticatorId"] = authenticatorId!, ["credentialId"] = adminCredentialId! });
            await tester.Logout();
            await tester.GoToLogin();
            await tester.LogIn(adminEmail);
            var invoice = await tester.Server.CustomerLightningD.CreateInvoice(LightMoney.Satoshis(500), "Wrong account passkey", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            await tester.GoToUrl(root + "send");
            await tester.Page.Locator("#bolt11").FillAsync(invoice.BOLT11);
            await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
            await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
            await Expect(tester.Page.Locator(".ln-wallet__payment-message")).ToContainTextAsync("Passkey verification failed");
            Assert.NotEqual(LightningInvoiceStatus.Paid, (await tester.Server.CustomerLightningD.GetInvoice(invoice.Id, TestContext.Current.CancellationToken)).Status);
            await tester.GoToUrl(root + "history");
            await Expect(tester.Page.Locator(".ln-wallet__operation")).ToHaveCountAsync(0);
            await authenticator.DetachAsync();
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUG_PLUGINS", previousPlugins);
            Environment.SetEnvironmentVariable("BTCPAY_PLUGINDIR", previousDirectory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertWalletModeAsync(PlaywrightTester tester, string storeId, BackendScenario scenario, CancellationToken token)
    {
        var page = tester.Page;
        await page.SetViewportSizeAsync(390, 844);
        var root = $"/stores/{storeId}/lightning/BTC/wallet/";
        await using (var anonymous = await tester.Browser.NewContextAsync())
        {
            var response = await anonymous.APIRequest.GetAsync(new Uri(tester.ServerUri, root).ToString());
            Assert.Contains("/login", response.Url, StringComparison.OrdinalIgnoreCase);
        }
        await tester.GoToUrl(root + "settings");
        await page.Locator("#Enabled").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.Locator(".ln-wallet__balance strong")).ToBeVisibleAsync();
        var noCsrf = await page.Context.APIRequest.PostAsync(new Uri(tester.ServerUri, root + "receive").ToString(),
            new() { Form = page.Context.APIRequest.CreateFormData().Append("amountSats", "500") });
        Assert.Equal(400, noCsrf.Status);
        await tester.GoToUrl(root + "send");
        await Expect(page.GetByText("Register a passkey", new() { Exact = false })).ToBeVisibleAsync();

        // Give the peer spendable liquidity above the channel reserve and commitment fees.
        var bootstrap = await scenario.Recipient.CreateInvoice(LightMoney.Satoshis(30_000),
            "Wallet receive liquidity", TimeSpan.FromMinutes(5), token);
        Assert.Equal(PayResult.Ok, (await scenario.Managed.Pay(bootstrap.BOLT11, token)).Result);
        await LightningRouteReadiness.WaitAsync(scenario.Recipient, RequiredNodeInfo(await scenario.Managed.GetInfo(token)).NodeId, 500, token);

        // Receiving is available without a passkey; settle through the actual peer.
        await tester.GoToUrl(root + "receive");
        await page.Locator("#amountSats").FillAsync("500");
        await page.Locator("#description").FillAsync("Wallet receive " + scenario.Name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create invoice", Exact = true }).ClickAsync();
        await Expect(page.Locator("#wallet-invoice")).ToBeVisibleAsync();
        var received = await page.Locator("#wallet-invoice").InnerTextAsync();
        var paid = await scenario.Recipient.Pay(received, new PayInvoiceParams { MaxFeeFlat = NBitcoin.Money.Satoshis(100) }, token);
        Assert.Equal(PayResult.Ok, paid.Result);
        await Expect(page.Locator("[data-status-url]")).ToHaveTextAsync("Settled", new() { Timeout = 30_000 });

        var session = await page.Context.NewCDPSessionAsync(page);
        await session.SendAsync("WebAuthn.enable");
        var added = await session.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new { protocol = "ctap2", transport = "internal", hasResidentKey = true,
                hasUserVerification = true, isUserVerified = true, automaticPresenceSimulation = true }
        });
        var authenticatorId = added!.Value.GetProperty("authenticatorId").GetString();
        try
        {
            await tester.GoToUrl("/account/passkeys");
            await page.Locator("#passkey-form input[name=Name]").FillAsync("Wallet test passkey");
            await page.Locator("#btn-add-passkey").ClickAsync();
            var registration = await tester.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success, BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Error);
            Assert.Contains("registered successfully", await registration.InnerTextAsync(), StringComparison.Ordinal);
            await Expect(page.GetByText("Wallet test passkey", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });

            foreach (var amountless in new[] { false, true })
            {
                var invoice = await scenario.Recipient.CreateInvoice(amountless ? LightMoney.Zero : LightMoney.Satoshis(500),
                    "Wallet pay " + scenario.Name, TimeSpan.FromMinutes(5), token);
                await tester.GoToUrl(root + "send");
                await page.Locator("#bolt11").FillAsync("lightning:" + invoice.BOLT11);
                if (amountless) await page.Locator("#amountSats").FillAsync("500");
                await page.Locator("#maxFeeSats").FillAsync("100");
                await page.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
                await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Review payment", Exact = true })).ToBeVisibleAsync();

                var confirmationId = await page.Locator("[name=confirmationId]").InputValueAsync();
                var csrf = await page.Locator(".ln-wallet__confirm [name=__RequestVerificationToken]").InputValueAsync();
                foreach (var field in new[] { "bolt11", "amountSats", "maxFeeSats" })
                {
                    var tampered = await page.Context.APIRequest.PostAsync(new Uri(tester.ServerUri, root + "send/execute").ToString(), new()
                    {
                        Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
                        Form = page.Context.APIRequest.CreateFormData().Append("confirmationId", confirmationId)
                            .Append("assertion", "{}").Append("__RequestVerificationToken", csrf).Append(field, "999999")
                    });
                    Assert.Equal(400, tampered.Status);
                    Assert.Contains("new payment review", await tampered.TextAsync(), StringComparison.Ordinal);
                    Assert.NotEqual(LightningInvoiceStatus.Paid, (await scenario.Recipient.GetInvoice(invoice.Id, token)).Status);
                }

                // A browser cancellation never reaches execution and keeps the invoice unpaid.
                await page.EvaluateAsync("() => Object.defineProperty(navigator.credentials, 'get', { configurable: true, value: () => Promise.reject(new DOMException('Cancelled', 'NotAllowedError')) })");
                await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
                await Expect(page.Locator(".ln-wallet__payment-message")).ToContainTextAsync("No payment was submitted");
                Assert.NotEqual(LightningInvoiceStatus.Paid, (await scenario.Recipient.GetInvoice(invoice.Id, token)).Status);
                await page.EvaluateAsync("() => delete navigator.credentials.get");

                if (!amountless)
                {
                    // A real PostgreSQL write failure must prevent contacting the node.
                    var factory = tester.Server.PayTester.GetService<WalletDbContextFactory>();
                    var service = tester.Server.PayTester.GetService<WalletService>();
                    var storeRepository = tester.Server.PayTester.GetService<BTCPayServer.Services.Stores.StoreRepository>();
                    var currentNode = await service.GetNodeAsync((await storeRepository.FindStore(storeId))!, token);
                    var validated = service.Preview(currentNode, invoice.BOLT11, null, "100");
                    await using var db = factory.CreateContext();
                    await db.Database.ExecuteSqlRawAsync("""
                        CREATE FUNCTION "LMWalletTestReject"() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN RAISE EXCEPTION 'Simulated wallet persistence failure'; END $$;
                        CREATE TRIGGER "LMWalletTestReject" BEFORE INSERT ON "LightningManagerWalletOperations"
                        FOR EACH ROW EXECUTE FUNCTION "LMWalletTestReject"();
                        """, token);
                    try
                    {
                        await Assert.ThrowsAsync<DbUpdateException>(() => service.PayAsync(currentNode, "storage-failure-test", validated));
                        Assert.NotEqual(LightningInvoiceStatus.Paid, (await scenario.Recipient.GetInvoice(invoice.Id, token)).Status);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION \"LMWalletTestReject\"() CASCADE", token);
                    }
                }

                await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
                await Expect(page.Locator("[data-status-url]")).ToHaveTextAsync("Settled", new() { Timeout = 30_000 });
                var recipientInvoice = await scenario.Recipient.GetInvoice(invoice.Id, token);
                Assert.Equal(LightningInvoiceStatus.Paid, recipientInvoice.Status);
                Assert.Equal(LightMoney.Satoshis(500), recipientInvoice.AmountReceived);
                var operationId = Guid.Parse(new Uri(page.Url).Segments.Last());
                var database = tester.Server.PayTester.GetService<WalletDbContextFactory>();
                var walletService = tester.Server.PayTester.GetService<WalletService>();
                var stores = tester.Server.PayTester.GetService<BTCPayServer.Services.Stores.StoreRepository>();
                // Simulate losing the local result after the backend settled, then restarting storage.
                await using (var db = database.CreateContext())
                    await db.Operations.Where(x => x.Id == operationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Submitting").SetProperty(x => x.FeeMsat, (long?)null), token);
                var restarted = new WalletRepository(database);
                await restarted.InitializeAsync(token);
                await stores.UpdateSetting(storeId, WalletSettings.Key, new WalletSettings { Enabled = false });
                await walletService.ReconcilePendingAsync(token);
                var node = await walletService.GetNodeAsync((await stores.FindStore(storeId))!, token);
                var recovered = await restarted.GetAsync(operationId, storeId, node.Identity, token);
                Assert.Equal("Settled", recovered!.State);
                var duplicate = walletService.Preview(node, invoice.BOLT11, amountless ? "500" : null, "100");
                var rejection = await Assert.ThrowsAsync<WalletException>(() => walletService.PayAsync(node, recovered.UserId, duplicate));
                Assert.Contains("already recorded", rejection.Message, StringComparison.Ordinal);
                await stores.UpdateSetting(storeId, WalletSettings.Key, new WalletSettings { Enabled = true });
                var replay = await page.Context.APIRequest.PostAsync(new Uri(tester.ServerUri, root + "send/execute").ToString(), new()
                {
                    Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
                    Form = page.Context.APIRequest.CreateFormData().Append("confirmationId", confirmationId)
                        .Append("assertion", "{}").Append("__RequestVerificationToken", csrf)
                });
                Assert.Equal(400, replay.Status);
            }

            await tester.GoToUrl(root + "history");
            await Expect(page.Locator(".ln-wallet__operation")).ToHaveCountAsync(3);
            var manifestResponse = await page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "manifest.webmanifest").ToString());
            dynamic manifest = JsonConvert.DeserializeObject((await manifestResponse.TextAsync()))!;
            Assert.Equal(root, (string)manifest.scope);
            Assert.Equal(root, (string)manifest.start_url);
            Assert.Equal("standalone", (string)manifest.display);
            var artifacts = Environment.GetEnvironmentVariable("TESTS_ARTIFACTS_DIR");
            if (!string.IsNullOrEmpty(artifacts))
                await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"wallet-{scenario.Name.Replace(' ', '-')}.png"), FullPage = true });
        }
        finally
        {
            await session.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object> { ["authenticatorId"] = authenticatorId! });
            await session.DetachAsync();
        }
    }
}
