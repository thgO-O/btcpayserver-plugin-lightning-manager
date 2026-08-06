using System.Globalization;
using System.Text.RegularExpressions;
using BTCPayServer.Lightning;
using BTCPayServer.Tests;
using BTCPayServer.Tests.Lnd;
using Microsoft.Playwright;
using NBitcoin;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

[CollectionDefinition("Lightning Manager E2E", DisableParallelization = true)]
public sealed class LightningManagerE2ECollection;

[Trait("Playwright", "Playwright")]
[Trait("Lightning", "Lightning")]
[Collection("Lightning Manager E2E")]
public class LightningManagerPlaywrightTests(ITestOutputHelper output) : UnitTestBase(output)
{
    private const long ChannelAmountSats = 100_000;
    private const long PaymentAmountSats = 500;
    private const long MaxFeeSats = 100;
    private const float NavigationTimeoutMilliseconds = 60_000;
    private const string EclairConnection =
        "type=eclair;server=http://127.0.0.1:4570/;password=lightning-manager-e2e";
    private const string CustomerLndConnection =
        "http://lnd:lnd@127.0.0.1:35532/";
    private const string PendingChannelMessage =
        "Channel opening may already be pending. Check the Lightning node before retrying.";

    [Theory(Timeout = 360_000)]
    [InlineData(ManagedBackend.Cln)]
    [InlineData(ManagedBackend.Lnd)]
    [InlineData(ManagedBackend.Eclair)]
    public async Task CanManageBackendThroughPluginUi(ManagedBackend backend)
    {
        var previousDebugPlugins = Environment.GetEnvironmentVariable("DEBUG_PLUGINS");
        var previousPluginDirectory = Environment.GetEnvironmentVariable("plugindir");
        var previousBtcpayPluginDirectory = Environment.GetEnvironmentVariable("BTCPAY_PLUGINDIR");
        var isolatedPluginDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lightning-manager-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedPluginDirectory);

        Environment.SetEnvironmentVariable("DEBUG_PLUGINS", GetPluginAssemblyPath());
        Environment.SetEnvironmentVariable("plugindir", isolatedPluginDirectory);
        Environment.SetEnvironmentVariable("BTCPAY_PLUGINDIR", isolatedPluginDirectory);

        try
        {
            await using var tester = CreatePlaywrightTester($"LightningManager{backend}E2E", newDb: true);
            tester.Server.ActivateLightning();

            var eclair = new LightningClientFactory(Network.RegTest).Create(EclairConnection);
            var customerLnd = new LndMockTester(
                tester.Server,
                "TEST_CUSTOMERLND",
                CustomerLndConnection,
                "customer_lnd",
                Network.RegTest).Client;
            var scenario = CreateScenario(tester, backend, eclair, customerLnd);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            await EnsureSpendableCoinbaseAsync(tester, timeout.Token);
            var managedInfo = await scenario.Managed.GetInfo(timeout.Token);
            var recipientInfo = await scenario.Recipient.GetInfo(timeout.Token);
            Assert.NotNull(managedInfo);
            Assert.NotNull(recipientInfo);
            if (scenario.ExpectedVersionPrefix is not null)
            {
                Assert.StartsWith(
                    scenario.ExpectedVersionPrefix,
                    managedInfo.Version,
                    StringComparison.Ordinal);
            }

            var managedNode = RequiredNodeInfo(managedInfo);
            var recipientNode = RequiredNodeInfo(recipientInfo);
            var existingChannel = (await scenario.Managed.ListChannels(timeout.Token))
                .Any(channel => channel.RemoteNode == recipientNode.NodeId);
            Assert.False(
                existingChannel,
                $"The disposable fixture already has a {scenario.Name} channel to the test recipient. " +
                "Recreate its volumes before rerunning this channel-opening test.");

            await FundNodeAsync(tester, scenario, timeout.Token);
            await tester.StartAsync();
            tester.Page.SetDefaultNavigationTimeout(NavigationTimeoutMilliseconds);
            await tester.RegisterNewUser(true);
            var (_, storeId) = await tester.CreateNewStore();

            await ConfigureBackendAsync(tester, storeId, scenario);
            await AssertOverviewAsync(tester, storeId, scenario.ExpectedVersionPrefix);
            await ConnectPeerAsync(tester, storeId, recipientNode);
            await OpenChannelAsync(tester, storeId, recipientNode);

            await tester.Server.ExplorerNode.GenerateAsync(6, timeout.Token);
            await WaitForChainSyncAsync(tester, scenario, timeout.Token);
            await WaitForActiveChannelAsync(
                scenario,
                recipientNode.NodeId,
                managedNode.NodeId,
                timeout.Token);
            await WaitForActiveChannelInUiAsync(
                tester,
                storeId,
                recipientNode.NodeId,
                scenario.Name,
                timeout.Token);

            await PayThroughUiAsync(
                tester,
                storeId,
                scenario.Recipient,
                $"{scenario.Name} fixed",
                amountless: false,
                timeout.Token);
            await PayThroughUiAsync(
                tester,
                storeId,
                scenario.Recipient,
                $"{scenario.Name} amountless",
                amountless: true,
                timeout.Token);

            await tester.Page.AssertNoError();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUG_PLUGINS", previousDebugPlugins);
            Environment.SetEnvironmentVariable("plugindir", previousPluginDirectory);
            Environment.SetEnvironmentVariable("BTCPAY_PLUGINDIR", previousBtcpayPluginDirectory);
            if (Directory.Exists(isolatedPluginDirectory))
            {
                Directory.Delete(isolatedPluginDirectory, recursive: true);
            }
        }
    }

    private static BackendScenario CreateScenario(
        PlaywrightTester tester,
        ManagedBackend backend,
        ILightningClient eclair,
        ILightningClient customerLnd)
    {
        return backend switch
        {
            ManagedBackend.Cln => new BackendScenario(
                "CLN",
                tester.Server.MerchantLightningD,
                customerLnd,
                LightningTestImplementation.CoreLightning,
                null),
            ManagedBackend.Lnd => new BackendScenario(
                "LND",
                tester.Server.MerchantLnd.Client,
                customerLnd,
                LightningTestImplementation.LND,
                null),
            ManagedBackend.Eclair => new BackendScenario(
                "Eclair",
                eclair,
                tester.Server.CustomerLightningD,
                null,
                "0.8"),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };
    }

    private static async Task EnsureSpendableCoinbaseAsync(
        PlaywrightTester tester,
        CancellationToken cancellationToken)
    {
        if (await tester.Server.ExplorerNode.GetBlockCountAsync() <=
            tester.Server.ExplorerNode.Network.Consensus.CoinbaseMaturity)
        {
            await tester.Server.ExplorerNode.GenerateAsync(
                tester.Server.ExplorerNode.Network.Consensus.CoinbaseMaturity + 1,
                cancellationToken);
        }
    }

    private static async Task FundNodeAsync(
        PlaywrightTester tester,
        BackendScenario scenario,
        CancellationToken cancellationToken)
    {
        var balanceBefore = (await scenario.Managed.GetBalance(cancellationToken)).OnchainBalance.Confirmed;
        var address = await scenario.Managed.GetDepositAddress(cancellationToken);
        await tester.Server.ExplorerNode.SendToAddressAsync(address, Money.Coins(0.01m), cancellationToken);
        await tester.Server.ExplorerNode.GenerateAsync(6, cancellationToken);
        await WaitForChainSyncAsync(tester, scenario, cancellationToken);

        await WaitUntilAsync(
            async () => (await scenario.Managed.GetBalance(cancellationToken)).OnchainBalance.Confirmed >=
                        balanceBefore + Money.Coins(0.01m),
            $"{scenario.Name} did not observe its confirmed funding transaction.",
            cancellationToken);
    }

    private static async Task ConfigureBackendAsync(
        PlaywrightTester tester,
        string storeId,
        BackendScenario scenario)
    {
        if (scenario.ConnectionType is { } connectionType)
        {
            await tester.AddLightningNode(connectionType);
            return;
        }

        await tester.GoToUrl($"/stores/{storeId}/lightning/BTC/setup");
        await tester.Page.ClickAsync("label[for=\"LightningNodeType-Custom\"]");
        await tester.Page.Locator("#ConnectionString").FillAsync(EclairConnection);
        await tester.ClickPagePrimary();
        await tester.FindAlertMessage(partialText: "BTC Lightning node updated.");
    }

    private static async Task AssertOverviewAsync(
        PlaywrightTester tester,
        string storeId,
        string? expectedVersionPrefix)
    {
        await tester.GoToUrl(ManagerUrl(storeId, "overview"));
        await Expect(tester.Page.GetByRole(AriaRole.Heading, new() { Name = "BTC Lightning Manager" }))
            .ToBeVisibleAsync();

        foreach (var capability in new[]
                 {
                     "Info", "Balance", "Pay BOLT11", "Amountless BOLT11", "Set Max Fee",
                     "Connect Peer", "Open Channel", "List Channels"
                 })
        {
            await Expect(tester.Page.Locator(".badge.bg-success").Filter(new() { HasText = capability }))
                .ToHaveCountAsync(1);
        }

        var version = tester.Page.Locator("dt:has-text(\"Version\") + dd");
        await Expect(version).ToHaveTextAsync(new Regex("\\S+"));
        if (expectedVersionPrefix is not null)
        {
            await Expect(version).ToContainTextAsync(expectedVersionPrefix);
        }

        await Expect(tester.Page.Locator(".alert-warning"))
            .ToHaveCountAsync(0);

        foreach (var page in new[] { "send", "peers", "channels" })
        {
            await Expect(tester.Page.Locator($"a[href$=\"/manager/{page}\"]"))
                .ToHaveCountAsync(1);
        }

        await tester.Page.AssertNoError();
    }

    private static async Task ConnectPeerAsync(PlaywrightTester tester, string storeId, NodeInfo remoteNode)
    {
        await tester.GoToUrl(ManagerUrl(storeId, "peers"));
        await tester.Page.Locator("#nodeUri").FillAsync(remoteNode.ToString());
        await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Connect peer" }).ClickAsync();
        await tester.FindAlertMessage(partialText: "Connected to peer successfully.");
    }

    private static async Task OpenChannelAsync(PlaywrightTester tester, string storeId, NodeInfo remoteNode)
    {
        await tester.GoToUrl(ManagerUrl(storeId, "channels"));
        await tester.Page.Locator("#channelNodeUri").FillAsync(remoteNode.ToString());
        await tester.Page.Locator("#channelAmountSats")
            .FillAsync(ChannelAmountSats.ToString(CultureInfo.InvariantCulture));
        await tester.Page.Locator("#feeRateSatsPerByte").FillAsync("1");
        await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Preview channel open" }).ClickAsync();

        await Expect(tester.Page.GetByRole(AriaRole.Heading, new() { Name = "Confirm Channel Open" }))
            .ToBeVisibleAsync();
        await Expect(tester.Page.Locator("#open-channel-form input[name=confirmationToken]"))
            .ToHaveValueAsync(new Regex("^[0-9a-f]{32}$"));
        await Expect(tester.Page.Locator("#open-channel-form input[name=nodeUri]"))
            .ToHaveValueAsync(remoteNode.ToString());
        await Expect(tester.Page.Locator("#open-channel-form input[name=channelAmountSats]"))
            .ToHaveValueAsync(ChannelAmountSats.ToString(CultureInfo.InvariantCulture));

        await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Open channel", Exact = true }).ClickAsync();
        Assert.Matches("[?&]resultId=[0-9a-f]{32}(?:&|$)", tester.Page.Url);
        var result = await VisibleTextAsync(tester.Page.Locator(".alert-success, .alert-danger").First);
        Assert.True(
            result is "Channel opening request submitted." or PendingChannelMessage,
            $"Unexpected channel result: {result}");
    }

    private static async Task PayThroughUiAsync(
        PlaywrightTester tester,
        string storeId,
        ILightningClient recipient,
        string description,
        bool amountless,
        CancellationToken cancellationToken)
    {
        var expectedAmount = LightMoney.Satoshis(PaymentAmountSats);
        var invoice = await recipient.CreateInvoice(
            amountless ? LightMoney.Zero : expectedAmount,
            $"Lightning Manager UI E2E {description} {Guid.NewGuid():N}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var parsedInvoice = BOLT11PaymentRequest.Parse(invoice.BOLT11, Network.RegTest);

        await tester.GoToUrl(ManagerUrl(storeId, "send"));
        await tester.Page.Locator("#bolt11").FillAsync(invoice.BOLT11);
        var amountInput = tester.Page.Locator("#amountSats");
        await Expect(amountInput).ToHaveCountAsync(1);
        if (amountless)
        {
            await amountInput.FillAsync(PaymentAmountSats.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            await Expect(amountInput).ToHaveValueAsync(string.Empty);
        }

        await tester.Page.Locator("#maxFeeSats").FillAsync(MaxFeeSats.ToString(CultureInfo.InvariantCulture));
        await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Preview payment" }).ClickAsync();
        await Expect(tester.Page.GetByRole(AriaRole.Heading, new() { Name = "Confirm Payment" }))
            .ToBeVisibleAsync();
        await Expect(tester.Page.Locator("#execute-payment-form input[name=confirmationToken]"))
            .ToHaveValueAsync(new Regex("^[0-9a-f]{32}$"));
        await Expect(tester.Page.Locator(".payment-box"))
            .ToContainTextAsync($"{PaymentAmountSats.ToString("N0", CultureInfo.InvariantCulture)} sats");

        await tester.Page.GetByRole(AriaRole.Button, new() { Name = "Pay invoice" }).ClickAsync();
        Assert.Matches("[?&]resultId=[0-9a-f]{32}(?:&|$)", tester.Page.Url);
        await tester.FindAlertMessage(partialText: "Payment sent successfully.");

        var result = tester.Page.Locator(".payment-box");
        await Expect(result.GetByRole(AriaRole.Heading, new() { Name = "Payment Result" })).ToBeVisibleAsync();
        await Expect(result.Locator("dt:has-text(\"Status\") + dd")).ToHaveTextAsync("Complete");
        await Expect(result.Locator("dt:has-text(\"Payment hash\") + dd code"))
            .ToHaveTextAsync(parsedInvoice.PaymentHash!.ToString());
        await Expect(result.Locator("dt:has-text(\"Preimage\") + dd code"))
            .ToHaveTextAsync(new Regex("^[0-9a-f]{64}$"));

        var totalAmountSats = ParseSats(await VisibleTextAsync(result.Locator("p.h2")));
        var feeAmountSats = ParseSats(await VisibleTextAsync(result.Locator("dt:has-text(\"Fee\") + dd")));
        Assert.Equal(PaymentAmountSats + feeAmountSats, totalAmountSats);

        await WaitUntilAsync(
            async () =>
            {
                var settled = await recipient.GetInvoice(invoice.Id, cancellationToken);
                return settled.Status == LightningInvoiceStatus.Paid && settled.AmountReceived == expectedAmount;
            },
            "The destination did not receive the expected payment amount.",
            cancellationToken);
    }

    private static async Task WaitForChainSyncAsync(
        PlaywrightTester tester,
        BackendScenario scenario,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            async () =>
            {
                var blockCount = await tester.Server.ExplorerNode.GetBlockCountAsync();
                var managedInfo = await scenario.Managed.GetInfo(cancellationToken);
                var recipientInfo = await scenario.Recipient.GetInfo(cancellationToken);
                return managedInfo.BlockHeight == blockCount && recipientInfo.BlockHeight == blockCount;
            },
            $"{scenario.Name} or its test recipient did not synchronize to the Bitcoin tip.",
            cancellationToken);
    }

    private static async Task WaitForActiveChannelAsync(
        BackendScenario scenario,
        PubKey managedRemoteNode,
        PubKey recipientRemoteNode,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    var managedChannels = await scenario.Managed.ListChannels(cancellationToken);
                    var recipientChannels = await scenario.Recipient.ListChannels(cancellationToken);
                    var expectedCapacity = LightMoney.Satoshis(ChannelAmountSats);
                    return managedChannels.Count(channel =>
                               channel.IsActive &&
                               channel.RemoteNode == managedRemoteNode &&
                               channel.Capacity == expectedCapacity) == 1 &&
                           recipientChannels.Count(channel =>
                               channel.IsActive &&
                               channel.RemoteNode == recipientRemoteNode &&
                               channel.Capacity == expectedCapacity) == 1;
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
            },
            $"The {scenario.Name} channel did not become active on both nodes.",
            cancellationToken);
    }

    private static async Task WaitForActiveChannelInUiAsync(
        PlaywrightTester tester,
        string storeId,
        PubKey remoteNode,
        string backendName,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            async () =>
            {
                await tester.GoToUrl(ManagerUrl(storeId, "channels"));
                var card = tester.Page.Locator("div.border.rounded.p-3")
                    .Filter(new() { HasText = remoteNode.ToString() });
                return await card.CountAsync() == 1 &&
                       await card.GetByText("Active", new() { Exact = true }).CountAsync() == 1;
            },
            $"The active {backendName} channel did not appear in Lightning Manager.",
            cancellationToken);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        Assert.Fail(failureMessage);
    }

    private static async Task<string> VisibleTextAsync(ILocator locator)
    {
        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        return (await locator.InnerTextAsync()).Trim();
    }

    private static decimal ParseSats(string display)
    {
        const string suffix = " sats";
        Assert.EndsWith(suffix, display, StringComparison.Ordinal);
        return decimal.Parse(display[..^suffix.Length], NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static NodeInfo RequiredNodeInfo(LightningNodeInformation info)
    {
        return info.NodeInfoList.FirstOrDefault() ??
               throw new InvalidOperationException("The Lightning node did not advertise a peer URI.");
    }

    private static string ManagerUrl(string storeId, string page)
    {
        return $"/stores/{storeId}/lightning/BTC/manager/{page}";
    }

    private static string GetPluginAssemblyPath()
    {
        var path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../BTCPayServer.Plugins.LightningManager/bin/Debug/net10.0/BTCPayServer.Plugins.LightningManager.dll"));
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Build the E2E project in Debug so the plugin can be loaded.", path);
    }

    public enum ManagedBackend
    {
        Cln,
        Lnd,
        Eclair
    }

    private sealed record BackendScenario(
        string Name,
        ILightningClient Managed,
        ILightningClient Recipient,
        LightningTestImplementation? ConnectionType,
        string? ExpectedVersionPrefix);
}
