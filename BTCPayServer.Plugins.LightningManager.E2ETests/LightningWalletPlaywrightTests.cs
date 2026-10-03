using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Wallet;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Npgsql;
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
            // Start the host with its real CSP before the core browser helper sets NoCSP.
            tester.Server.PayTester.NoCSP = false;
            await tester.Server.StartAsync();
            tester.ServerUri = new Uri(tester.Server.PayTester.ServerUriWithIP.AbsoluteUri.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
            await tester.StartAsync();
            await tester.Page.AddInitScriptAsync("document.addEventListener('securitypolicyviolation', e => (window.walletCspViolations ??= []).push(e.violatedDirective));");
            var adminEmail = await tester.RegisterNewUser(true);
            var (_, storeId) = await tester.CreateNewStore();
            await tester.AddLightningNode(LightningTestImplementation.CoreLightning);
            var root = $"/stores/{storeId}/lightning/BTC/wallet/";
            await tester.GoToUrl(root, ignoreResponse: true);
            await Expect(tester.Page.GetByText("Wallet Mode is disabled for this store.", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(tester.Page.Locator(".ln-wallet__nav [aria-disabled=true]")).ToHaveCountAsync(4);
            await Expect(tester.Page.Locator(".ln-wallet__nav a[href]")).ToHaveCountAsync(0);
            await tester.GoToUrl(root + "settings");
            await tester.Page.Locator("#Enabled").CheckAsync();
            await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            Assert.Contains(root.TrimEnd('/'), tester.Page.Url, StringComparison.Ordinal);
            await Expect(tester.Page.Locator(".ln-wallet__balance strong")).ToBeVisibleAsync();
            await AssertResponsiveWalletAsync(tester, root);
            await tester.Page.GetByRole(AriaRole.Link, new() { Name = "Pay", Exact = true }).First.ClickAsync();
            await Expect(tester.Page.GetByText("Register a passkey", new() { Exact = false })).ToBeVisibleAsync();
            var sendResponse = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "send").ToString());
            Assert.True(sendResponse.Headers.ContainsKey("content-security-policy"));
            await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Scan QR", Exact = true }).ClickAsync();
            await Expect(tester.Page.Locator("#wallet-scan-modal")).ToBeVisibleAsync();
            await tester.Page.Locator("#wallet-scan-modal .btn-close").ClickAsync();
            await Expect(tester.Page.Locator("#wallet-scan-modal")).ToBeHiddenAsync();
            await tester.Page.EvaluateAsync("() => navigator.serviceWorker.ready");
            Assert.Empty(await tester.Page.EvaluateAsync<string[]>("() => window.walletCspViolations ?? []"));
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
            var registration = tester.Page.Locator(".alert-success, .alert-danger").First;
            await Expect(registration).ToBeVisibleAsync(new() { Timeout = 30_000 });
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
            await tester.Page.GetByRole(AriaRole.Link, new() { Name = "Try again", Exact = true }).ClickAsync();
            await Expect(tester.Page.Locator(".ln-wallet__balance strong")).ToBeVisibleAsync();
            Assert.Empty(await tester.Page.EvaluateAsync<string[]>("() => window.walletCspViolations ?? []"));

            await using var anonymous = await tester.Browser.NewContextAsync();
            var manifest = await anonymous.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "manifest.webmanifest").ToString());
            Assert.Equal(200, manifest.Status);
            var status = await anonymous.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "operations/" + Guid.NewGuid() + "/status").ToString());
            Assert.Contains("/login", status.Url, StringComparison.OrdinalIgnoreCase);
            await tester.GoToUrl("/account");
            await tester.Logout();
            await tester.GoToRegister();
            var operatorEmail = await tester.RegisterNewUser();
            await tester.SkipWizard();
            var denied = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root).ToString());
            Assert.Equal(403, denied.Status);

            // Keep only the other account's real resident credential on the authenticator.
            await tester.GoToUrl("/account/passkeys");
            await tester.Page.Locator("#passkey-form input[name=Name]").FillAsync("Other account passkey");
            await tester.Page.Locator("#btn-add-passkey").ClickAsync();
            await Expect(tester.Page.GetByText("Other account passkey", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
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

            // An operator can recover from invalid input without access to store settings.
            var stores = tester.Server.PayTester.GetService<BTCPayServer.Services.Stores.StoreRepository>();
            await using var users = tester.Server.PayTester.GetService<BTCPayServer.Data.ApplicationDbContextFactory>().CreateContext();
            var operatorId = await users.Users.Where(x => x.Email == operatorEmail).Select(x => x.Id).SingleAsync(TestContext.Current.CancellationToken);
            var role = new BTCPayServer.Services.Stores.StoreRoleId(storeId, "Wallet operator");
            await stores.AddOrUpdateStoreRole(role, new[] { BTCPayServer.Client.Policies.CanViewStoreSettings,
                BTCPayServer.Client.Policies.CanUseLightningNodeInStore });
            await stores.AddOrUpdateStoreUser(storeId, operatorId, role);
            await tester.GoToUrl("/account");
            await tester.Logout();
            await tester.GoToLogin();
            await tester.LogIn(operatorEmail);
            var settings = await tester.Page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri, root + "settings").ToString());
            Assert.Equal(403, settings.Status);
            foreach (var invalid in new[]
            {
                (Path: "send", Field: "bolt11", Value: "invalid-invoice", Button: "Review payment", Error: "The BOLT11 invoice is invalid."),
                // Valid for the HTML number input, but outside the server's Int64 range.
                (Path: "receive", Field: "amountSats", Value: "10000000000000000000", Button: "Create invoice", Error: "Enter a positive whole number of sats.")
            })
            {
                await tester.GoToUrl(root + invalid.Path);
                await tester.Page.Locator("#" + invalid.Field).FillAsync(invalid.Value);
                await tester.Page.GetByRole(AriaRole.Button, new() { Name = invalid.Button, Exact = true }).ClickAsync();
                await Expect(tester.Page.GetByRole(AriaRole.Alert)).ToHaveTextAsync(invalid.Error);
                await Expect(tester.Page.Locator(".ln-wallet__nav a[href]")).ToHaveCountAsync(4);
                await Expect(tester.Page.Locator(".ln-wallet__nav [aria-disabled=true]")).ToHaveCountAsync(0);
                await Expect(tester.Page.Locator(".ln-wallet__brand")).ToHaveAttributeAsync("href", root);
                await Expect(tester.Page.Locator(".ln-wallet__icon-button")).ToHaveCountAsync(0);
                await tester.Page.GetByRole(AriaRole.Link, new() { Name = "Pay", Exact = true }).ClickAsync();
                await Expect(tester.Page.GetByRole(AriaRole.Heading, new() { Name = "Pay Lightning", Exact = true })).ToBeVisibleAsync();
                await tester.Page.Locator(".ln-wallet__brand").ClickAsync();
                await Expect(tester.Page.Locator(".ln-wallet__balance")).ToBeVisibleAsync();
            }
            await tester.GoToUrl(root + "history");
            await Expect(tester.Page.Locator(".ln-wallet__operation")).ToHaveCountAsync(0);
            await stores.UpdateSetting(storeId, WalletSettings.Key, new WalletSettings { Enabled = false });
            await tester.GoToUrl(root, ignoreResponse: true);
            await Expect(tester.Page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Wallet Mode is disabled for this store.");
            await Expect(tester.Page.Locator(".ln-wallet__nav [aria-disabled=true]")).ToHaveCountAsync(4);
            await Expect(tester.Page.Locator(".ln-wallet__nav a[href]")).ToHaveCountAsync(0);

        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUG_PLUGINS", previousPlugins);
            Environment.SetEnvironmentVariable("BTCPAY_PLUGINDIR", previousDirectory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertResponsiveWalletAsync(PlaywrightTester tester, string root)
    {
        var page = tester.Page;
        var artifacts = Environment.GetEnvironmentVariable("TESTS_ARTIFACTS_DIR");
        var screens = new[] { (Path: "", Active: "Home"), (Path: "send", Active: "Pay"),
            (Path: "receive", Active: "Receive"), (Path: "history", Active: "History"), (Path: "settings", Active: "") };
        foreach (var theme in new[] { "light", "dark" })
        {
            await tester.GoToUrl(root);
            // GoToUrl waits for navigation commit, before the theme script necessarily runs.
            await page.WaitForFunctionAsync("() => typeof window.setColorMode === 'function'");
            await page.EvaluateAsync("mode => window.setColorMode(mode)", theme);
            foreach (var width in new[] { 320, 390, 1280 })
            {
                await page.SetViewportSizeAsync(width, 844);
                foreach (var screen in screens)
                {
                    await tester.GoToUrl(root + screen.Path);
                    await Expect(page.Locator("html")).ToHaveAttributeAsync("data-btcpay-theme", theme);
                    var overflows = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
                    Assert.False(overflows, $"{screen.Path} overflows at {width}px in {theme} mode.");
                    var current = page.Locator(".ln-wallet__nav [aria-current=page]");
                    await Expect(current).ToHaveCountAsync(screen.Active == "" ? 0 : 1);
                    if (screen.Active != "") await Expect(current).ToHaveTextAsync(screen.Active);
                    foreach (var link in await page.Locator(".ln-wallet__nav a").AllAsync())
                        Assert.True((await link.BoundingBoxAsync())!.Height >= 44);
                    if (width < 768)
                    {
                        var nav = (await page.Locator(".ln-wallet__nav").BoundingBoxAsync())!;
                        Assert.InRange(nav.Y + nav.Height, 843, 845);
                    }
                    if (screen.Path == "settings")
                    {
                        // A shared text-input rule previously stretched this checkbox to 44px.
                        var toggle = (await page.Locator("#Enabled").BoundingBoxAsync())!;
                        Assert.InRange(toggle.Height, 20, 32);
                        await Expect(page.Locator("#Enabled")).ToBeCheckedAsync();
                    }
                    if (!string.IsNullOrEmpty(artifacts))
                        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts,
                            $"wallet-ui-{theme}-{width}-{(screen.Path == "" ? "home" : screen.Path)}.png"), FullPage = true });
                }
            }
        }
        await page.SetViewportSizeAsync(1280, 844);
        await tester.GoToUrl(root);
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
        var statusUrl = new Uri(page.Url).AbsolutePath + "/status";
        var observedDeferredSettlement = false;
        // A paid state can arrive before its received amount is ready. The UI must
        // refresh the receipt on settlement even while reconciliation continues.
        await page.RouteAsync("**" + statusUrl, async route =>
        {
            var response = await route.FetchAsync();
            var status = JObject.Parse(await response.TextAsync());
            if (status["state"]!.Value<string>() == "Settled") observedDeferredSettlement = true;
            status["final"] = false;
            await route.FulfillAsync(new() { Response = response, Body = status.ToString(Formatting.None) });
        });
        var paid = await scenario.Recipient.Pay(received, new PayInvoiceParams { MaxFeeFlat = NBitcoin.Money.Satoshis(100) }, token);
        Assert.Equal(PayResult.Ok, paid.Result);
        await Expect(page.Locator("[data-status-url]")).ToHaveTextAsync("Settled", new() { Timeout = 30_000 });
        await Expect(page.Locator("[data-status-url]")).ToHaveAttributeAsync("data-final", "true");
        Assert.True(observedDeferredSettlement, "The receipt must refresh after a settled status with final=false.");
        await page.UnrouteAsync("**" + statusUrl);
        await Expect(page.Locator("[data-operation-amount]")).ToHaveTextAsync("500 sats");
        var receivedId = Guid.Parse(new Uri(page.Url).Segments.Last());
        await using (var db = tester.Server.PayTester.GetService<WalletDbContextFactory>().CreateContext())
        {
            var receipt = await db.Operations.SingleAsync(x => x.Id == receivedId, token);
            Assert.Equal(500_000, receipt.AmountMsat);
            Assert.Equal(500_000, receipt.SettledAmountMsat);
            // Simulate a settled invoice recorded before received amounts were persisted.
            await db.Operations.Where(x => x.Id == receivedId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettledAmountMsat, (long?)null), token);
        }
        await tester.GoToUrl(root + "operations/" + receivedId);
        await Expect(page.Locator("[data-operation-amount]")).ToHaveTextAsync("500 sats");
        await using (var db = tester.Server.PayTester.GetService<WalletDbContextFactory>().CreateContext())
            Assert.Equal(500_000, (await db.Operations.SingleAsync(x => x.Id == receivedId, token)).SettledAmountMsat);
        await tester.GoToUrl(root + "history");
        await Expect(page.Locator($".ln-wallet__operation[href$='/operations/{receivedId}'] .ln-wallet__operation-value strong")).ToHaveTextAsync("500 sats");

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
            var registration = page.Locator(".alert-success, .alert-danger").First;
            await Expect(registration).ToBeVisibleAsync(new() { Timeout = 30_000 });
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

                if (!amountless)
                {
                    // Generate a genuinely signed assertion of this account's resident key,
                    // with presence but UV=false. Relax only the browser request; the server
                    // must still enforce its original Required option through native Fido2.
                    await session.SendAsync("WebAuthn.setUserVerified", new Dictionary<string, object>
                        { ["authenticatorId"] = authenticatorId!, ["isUserVerified"] = false });
                    await page.EvaluateAsync("""
                        () => {
                            const get = navigator.credentials.get.bind(navigator.credentials);
                            Object.defineProperty(navigator.credentials, 'get', { configurable: true, value: async options => {
                                window.walletServerVerification = options.publicKey.userVerification;
                                const credential = await get({ ...options, publicKey: { ...options.publicKey, userVerification: 'discouraged' } });
                                window.walletAssertionUV = (new Uint8Array(credential.response.authenticatorData)[32] & 4) !== 0;
                                return credential;
                            }});
                        }
                        """);
                    try
                    {
                        var rejectionTask = page.WaitForResponseAsync(r => r.Url.EndsWith("/send/execute", StringComparison.Ordinal) && r.Request.Method == "POST");
                        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
                        var uvRejected = await rejectionTask;
                        Assert.Equal(400, uvRejected.Status);
                        await Expect(page.Locator(".ln-wallet__payment-message")).ToContainTextAsync("Passkey verification failed");
                        Assert.Equal("required", await page.EvaluateAsync<string>("() => window.walletServerVerification"));
                        Assert.False(await page.EvaluateAsync<bool>("() => window.walletAssertionUV"));
                        Assert.NotEqual(LightningInvoiceStatus.Paid, (await scenario.Recipient.GetInvoice(invoice.Id, token)).Status);
                        var hash = BOLT11PaymentRequest.Parse(invoice.BOLT11, NBitcoin.Network.RegTest).PaymentHash!.ToString();
                        await using var db = tester.Server.PayTester.GetService<WalletDbContextFactory>().CreateContext();
                        Assert.False(await db.Operations.AnyAsync(x => x.PaymentHash == hash && x.Direction == "Outgoing", token));
                    }
                    finally
                    {
                        await page.EvaluateAsync("() => delete navigator.credentials.get");
                        await session.SendAsync("WebAuthn.setUserVerified", new Dictionary<string, object>
                            { ["authenticatorId"] = authenticatorId!, ["isUserVerified"] = true });
                    }
                    // The rejected assertion consumes its confirmation. Review afresh.
                    await tester.GoToUrl(root + "send");
                    await page.Locator("#bolt11").FillAsync(invoice.BOLT11);
                    await page.Locator("#maxFeeSats").FillAsync("100");
                    await page.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
                }

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

                if (amountless)
                {
                    var factory = tester.Server.PayTester.GetService<WalletDbContextFactory>();
                    var disconnect = tester.Server.PayTester.GetService<WalletRequestDisconnectProbe>();
                    disconnect.Reset();
                    await using var db = factory.CreateContext();
                    await using var gate = new NpgsqlConnection(db.Database.GetConnectionString());
                    await gate.OpenAsync(token);
                    var lockId = Random.Shared.Next(1, int.MaxValue);
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@id)", gate);
                    command.Parameters.AddWithValue("id", lockId);
                    await command.ExecuteNonQueryAsync(token);
                    command.CommandText = $"""
                        CREATE FUNCTION "LMWalletTestPause"() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN PERFORM pg_advisory_xact_lock({lockId}); RETURN NEW; END $$;
                        CREATE TRIGGER "LMWalletTestPause" BEFORE INSERT ON "LightningManagerWalletOperations"
                        FOR EACH ROW EXECUTE FUNCTION "LMWalletTestPause"();
                        """;
                    await command.ExecuteNonQueryAsync(token);
                    try
                    {
                        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromSeconds(30));
                        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND objid = CAST(@id AS oid) AND NOT granted)";
                        while (!(bool)(await command.ExecuteScalarAsync(wait.Token))!)
                            await Task.Delay(100, wait.Token);
                        // Destroy the paying document while its server-side insert is paused.
                        await tester.GoToUrl(root + "history");
                        await disconnect.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    }
                    finally
                    {
                        command.CommandText = "SELECT pg_advisory_unlock(@id)";
                        await command.ExecuteNonQueryAsync(CancellationToken.None);
                        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION \"LMWalletTestPause\"() CASCADE", CancellationToken.None);
                    }
                    // The browser request is gone. Backend settlement and the journal must still complete.
                    var hash = BOLT11PaymentRequest.Parse(invoice.BOLT11, NBitcoin.Network.RegTest).PaymentHash!.ToString();
                    Guid id;
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                    recovery.CancelAfter(TimeSpan.FromSeconds(30));
                    while (true)
                    {
                        var completed = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.StoreId == storeId && x.PaymentHash == hash && x.Direction == "Outgoing", recovery.Token);
                        if (completed?.State == "Settled") { id = completed.Id; break; }
                        await Task.Delay(100, recovery.Token);
                    }
                    await tester.GoToUrl(root + "operations/" + id);
                }
                else await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
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
                    await db.Operations.Where(x => x.Id == operationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Submitting").SetProperty(x => x.FeeMsat, (long?)null).SetProperty(x => x.SettledAmountMsat, (long?)null).SetProperty(x => x.AmountMsat, 1_000_000L), token);
                var restarted = new WalletRepository(database);
                await restarted.InitializeAsync(token);
                await stores.UpdateSetting(storeId, WalletSettings.Key, new WalletSettings { Enabled = false });
                await walletService.ReconcilePendingAsync(token);
                var node = await walletService.GetNodeAsync((await stores.FindStore(storeId))!, token);
                var recovered = await restarted.GetAsync(operationId, storeId, node.Identity, token);
                Assert.Equal("Settled", recovered!.State);
                Assert.Equal(500_000, recovered.SettledAmountMsat);
                Assert.Equal(1_000_000, recovered.AmountMsat);
                var duplicate = walletService.Preview(node, invoice.BOLT11, amountless ? "500" : null, "100");
                var rejection = await Assert.ThrowsAsync<WalletException>(() => walletService.PayAsync(node, recovered.UserId, duplicate));
                Assert.Contains("already recorded", rejection.Message, StringComparison.Ordinal);
                await stores.UpdateSetting(storeId, WalletSettings.Key, new WalletSettings { Enabled = true });
                await tester.GoToUrl(root + "operations/" + operationId);
                await Expect(page.Locator("[data-operation-amount]")).ToHaveTextAsync("500 sats");
                await Expect(page.GetByText("The reviewed amount was 1,000 sats.", new() { Exact = false })).ToBeVisibleAsync();
                var replay = await page.Context.APIRequest.PostAsync(new Uri(tester.ServerUri, root + "send/execute").ToString(), new()
                {
                    Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
                    Form = page.Context.APIRequest.CreateFormData().Append("confirmationId", confirmationId)
                        .Append("assertion", "{}").Append("__RequestVerificationToken", csrf)
                });
                Assert.Equal(400, replay.Status);
            }

            // A previously paid amountless invoice must not become a new wallet payment.
            var prior = await scenario.Recipient.CreateInvoice(LightMoney.Zero, "Previously paid through Manager", TimeSpan.FromMinutes(5), token);
            var priorWallet = tester.Server.PayTester.GetService<WalletService>();
            var priorStores = tester.Server.PayTester.GetService<BTCPayServer.Services.Stores.StoreRepository>();
            var priorNode = await priorWallet.GetNodeAsync((await priorStores.FindStore(storeId))!, token);
            var priorManager = tester.Server.PayTester.GetService<BTCPayServer.Plugins.LightningManager.Services.LightningManagerService>();
            Assert.True((await priorManager.SendAsync(priorNode.Context, prior.BOLT11, "500", "100", token)).Result.IsSuccess);
            Assert.Equal(LightMoney.Satoshis(500), (await scenario.Recipient.GetInvoice(prior.Id, token)).AmountReceived);
            await tester.GoToUrl(root + "send");
            await page.Locator("#bolt11").FillAsync(prior.BOLT11);
            await page.Locator("#amountSats").FillAsync("1000");
            await page.Locator("#maxFeeSats").FillAsync("100");
            await page.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm with passkey", Exact = true }).ClickAsync();
            await Expect(page.Locator(".ln-wallet__payment-message")).ToContainTextAsync("already recorded or known to the node");
            Assert.Equal(LightMoney.Satoshis(500), (await scenario.Recipient.GetInvoice(prior.Id, token)).AmountReceived);
            var priorHash = BOLT11PaymentRequest.Parse(prior.BOLT11, NBitcoin.Network.RegTest).PaymentHash!.ToString();
            await using (var db = tester.Server.PayTester.GetService<WalletDbContextFactory>().CreateContext())
                Assert.False(await db.Operations.AnyAsync(x => x.PaymentHash == priorHash && x.Direction == "Outgoing", token));

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
