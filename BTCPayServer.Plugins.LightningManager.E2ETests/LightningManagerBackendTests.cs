using System.Globalization;
using BTCPayServer;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

[Collection("Lightning Manager E2E")]
public class LightningManagerBackendTests(ITestOutputHelper output) : UnitTestBase(output)
{
    private const long PaymentAmountSats = 500;
    private const long MaxFeeSats = 100;
    private const long LiquidityBootstrapSats = 10_000;
    private const long RequiredOutboundLiquiditySats = 2 * (PaymentAmountSats + MaxFeeSats);

    [Fact(Timeout = 360_000)]
    [Trait("Integration", "Integration")]
    [Trait("Lightning", "Lightning")]
    public async Task ClnAndLndManagementAndPaymentsWorkThroughProductionService()
    {
        using var tester = CreateServerTester();
        tester.ActivateLightning();
        await tester.EnsureChannelsSetup();

        var cln = tester.CustomerLightningD;
        var lnd = tester.MerchantLnd.Client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var clnInfo = await cln.GetInfo(timeout.Token);
        var lndInfo = await lnd.GetInfo(timeout.Token);
        Assert.NotNull(clnInfo);
        Assert.NotNull(lndInfo);

        var clnNode = RequiredNodeInfo(clnInfo);
        var lndNode = RequiredNodeInfo(lndInfo);
        var service = new LightningManagerService(
            NullLogger<LightningManagerService>.Instance,
            new LightningManagerOperationGuard());
        var clnContext = CreateContext(tester, cln, "clightning");
        var lndContext = CreateContext(tester, lnd, "lnd-rest");

        var clnConnect = await service.ConnectPeerAsync(clnContext, lndNode.ToString(), timeout.Token);
        var lndConnect = await service.ConnectPeerAsync(lndContext, clnNode.ToString(), timeout.Token);
        Assert.True(clnConnect.IsSuccess, $"CLN peer connection failed: {clnConnect.Message}");
        Assert.True(lndConnect.IsSuccess, $"LND peer connection failed: {lndConnect.Message}");

        await EnsureLndOutboundLiquidityAsync(cln, lnd, clnNode.NodeId, timeout.Token);

        var clnChannels = new ChannelsViewModel();
        var lndChannels = new ChannelsViewModel();
        await service.PopulateChannelsAsync(clnChannels, clnContext, timeout.Token);
        await service.PopulateChannelsAsync(lndChannels, lndContext, timeout.Token);

        Assert.Null(clnChannels.ChannelListMessage);
        Assert.Null(lndChannels.ChannelListMessage);
        Assert.True(
            clnChannels.Channels.Any(channel =>
                channel.IsActive &&
                string.Equals(channel.RemoteNode, lndNode.NodeId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                channel.LocalBalanceSats >= RequiredOutboundLiquiditySats),
            $"The fixture requires an active CLN channel to LND with at least {RequiredOutboundLiquiditySats} sats of outbound liquidity.");
        Assert.True(
            lndChannels.Channels.Any(channel =>
                channel.IsActive &&
                string.Equals(channel.RemoteNode, clnNode.NodeId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                channel.LocalBalanceSats >= RequiredOutboundLiquiditySats),
            $"The fixture requires an active LND channel to CLN with at least {RequiredOutboundLiquiditySats} sats of outbound liquidity.");

        AssertOpenChannelPreview(service, clnContext, lndNode);
        AssertOpenChannelPreview(service, lndContext, clnNode);

        await AssertPaymentSettles(
            service, clnContext, lnd, "CLN to LND fixed", amountless: false, timeout.Token);
        await AssertPaymentSettles(
            service, lndContext, cln, "LND to CLN fixed", amountless: false, timeout.Token);
        await AssertPaymentSettles(
            service, clnContext, lnd, "CLN to LND amountless", amountless: true, timeout.Token);
        await AssertPaymentSettles(
            service, lndContext, cln, "LND to CLN amountless", amountless: true, timeout.Token);

        var clnOverview = new OverviewViewModel();
        await service.PopulateOverviewAsync(clnOverview, clnContext, timeout.Token);
        Assert.Empty(clnOverview.Notices);
        Assert.Empty(clnOverview.Warnings);

        var lndOverview = new OverviewViewModel();
        await service.PopulateOverviewAsync(lndOverview, lndContext, timeout.Token);
        Assert.Empty(lndOverview.Notices);
        Assert.Empty(lndOverview.Warnings);
        AssertPositivePeerCount(lndOverview);
    }

    private static StoreLightningManagerContext CreateContext(
        ServerTester tester,
        ILightningClient client,
        string backendType)
    {
        return new StoreLightningManagerContext
        {
            StoreId = $"e2e-{backendType}",
            CryptoCode = "BTC",
            Network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC"),
            Client = client,
            BackendType = backendType,
            BackendFingerprint = $"{backendType}-e2e",
            BackendIdentityFingerprint = $"{backendType}-e2e",
            Capabilities = LightningCapabilities.Full,
            DisplayName = backendType
        };
    }

    private static void AssertPositivePeerCount(OverviewViewModel overview)
    {
        var peersRow = Assert.Single(overview.SummaryRows, row => row.Label == "Peers");
        Assert.True(
            int.TryParse(peersRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var peersCount),
            $"The Overview peer count '{peersRow.Value}' is not a valid integer.");
        Assert.True(peersCount > 0, "The Overview did not report any connected peers.");
    }

    private static async Task EnsureLndOutboundLiquidityAsync(
        ILightningClient cln,
        ILightningClient lnd,
        PubKey clnNodeId,
        CancellationToken cancellationToken)
    {
        var channels = await lnd.ListChannels(cancellationToken);
        if (channels.Any(channel =>
                channel.IsActive &&
                channel.RemoteNode == clnNodeId &&
                channel.LocalBalance >= LightMoney.Satoshis(RequiredOutboundLiquiditySats)))
        {
            return;
        }

        var invoice = await lnd.CreateInvoice(
            LightMoney.Satoshis(LiquidityBootstrapSats),
            $"Lightning Manager E2E liquidity {Guid.NewGuid():N}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var payment = await cln.Pay(invoice.BOLT11, cancellationToken);
        Assert.Equal(PayResult.Ok, payment.Result);
        var settled = await WaitForSettledInvoice(lnd, invoice.Id, cancellationToken);
        Assert.Equal(LightningInvoiceStatus.Paid, settled.Status);
        Assert.Equal(LightMoney.Satoshis(LiquidityBootstrapSats), settled.AmountReceived);
    }

    private static void AssertOpenChannelPreview(
        LightningManagerService service,
        StoreLightningManagerContext context,
        NodeInfo remoteNode)
    {
        var created = service.TryCreateOpenChannelPreview(
            context,
            remoteNode.ToString(),
            "20000",
            "1",
            out var preview,
            out var error);

        Assert.True(created, error);
        Assert.NotNull(preview);
        Assert.Equal(remoteNode.ToString(), preview.NodeUri);
        Assert.Equal("1 sat/vB", preview.FeeRateDisplay);
        Assert.NotEmpty(preview.ChannelAmountDisplay);
    }

    private static async Task AssertPaymentSettles(
        LightningManagerService service,
        StoreLightningManagerContext payerContext,
        ILightningClient recipient,
        string description,
        bool amountless,
        CancellationToken cancellationToken)
    {
        var expectedAmount = LightMoney.Satoshis(PaymentAmountSats);
        var invoice = await recipient.CreateInvoice(
            amountless ? LightMoney.Zero : expectedAmount,
            $"Lightning Manager E2E {description} {Guid.NewGuid():N}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var parsedInvoice = BOLT11PaymentRequest.Parse(invoice.BOLT11, Network.RegTest);

        if (amountless)
        {
            Assert.True(parsedInvoice.MinimumAmount is null || parsedInvoice.MinimumAmount == LightMoney.Zero);
        }
        else
        {
            Assert.Equal(expectedAmount, parsedInvoice.MinimumAmount);
        }

        var result = await service.SendAsync(
            payerContext,
            invoice.BOLT11,
            amountless ? PaymentAmountSats.ToString(CultureInfo.InvariantCulture) : null,
            MaxFeeSats.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        Assert.True(result.Result.IsSuccess, $"{description} failed: {result.Result.Message}");
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.NotNull(parsedInvoice.PaymentHash);
        Assert.Equal(parsedInvoice.PaymentHash.ToString(), result.Payment.PaymentHash);
        Assert.False(string.IsNullOrWhiteSpace(result.Payment.Preimage));
        var totalAmountSats = ParseSats(result.Payment.TotalAmountDisplay);
        var feeAmountSats = ParseSats(result.Payment.FeeAmountDisplay);
        Assert.Equal(PaymentAmountSats + feeAmountSats, totalAmountSats);

        var settled = await WaitForSettledInvoice(recipient, invoice.Id, cancellationToken);
        Assert.Equal(LightningInvoiceStatus.Paid, settled.Status);
        Assert.Equal(expectedAmount, settled.AmountReceived);
    }

    private static async Task<LightningInvoice> WaitForSettledInvoice(
        ILightningClient client,
        string invoiceId,
        CancellationToken cancellationToken)
    {
        LightningInvoice? invoice = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            invoice = await client.GetInvoice(invoiceId, cancellationToken);
            if (invoice.Status == LightningInvoiceStatus.Paid)
            {
                return invoice;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return invoice ?? throw new InvalidOperationException("The destination invoice could not be loaded.");
    }

    private static decimal ParseSats(string? display)
    {
        const string suffix = " sats";
        Assert.NotNull(display);
        Assert.EndsWith(suffix, display, StringComparison.Ordinal);
        return decimal.Parse(display[..^suffix.Length], NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static NodeInfo RequiredNodeInfo(LightningNodeInformation info)
    {
        return info.NodeInfoList.FirstOrDefault() ??
               throw new InvalidOperationException("The Lightning node did not advertise a peer URI.");
    }
}
