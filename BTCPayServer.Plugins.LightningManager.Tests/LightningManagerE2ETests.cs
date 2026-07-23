using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerE2ETests
{
    private const string EnabledEnvironmentVariable = "LIGHTNING_MANAGER_E2E";
    private const long PaymentAmountSats = 500;

    [LightningManagerE2EFact]
    [Trait("Category", "LightningManagerE2E")]
    public async Task ClnAndLndManagementAndPaymentsWorkThroughProductionService()
    {
        var clnConnection = RequiredEnvironment("LIGHTNING_MANAGER_E2E_CLN");
        var lndConnection = RequiredEnvironment("LIGHTNING_MANAGER_E2E_LND");
        var factory = new LightningClientFactory(Network.RegTest);
        var cln = factory.Create(clnConnection);
        var lnd = factory.Create(lndConnection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var clnInfo = await cln.GetInfo(timeout.Token);
        var lndInfo = await lnd.GetInfo(timeout.Token);
        Assert.NotNull(clnInfo);
        Assert.NotNull(lndInfo);
        Assert.NotNull(await cln.GetBalance(timeout.Token));
        Assert.NotNull(await lnd.GetBalance(timeout.Token));

        var clnNode = RequiredNodeInfo("LIGHTNING_MANAGER_E2E_CLN_NODE_URI", clnInfo);
        var lndNode = RequiredNodeInfo("LIGHTNING_MANAGER_E2E_LND_NODE_URI", lndInfo);
        var service = new LightningManagerService();
        var clnContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            cln,
            clnConnection);
        var lndContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            lnd,
            lndConnection);

        var clnConnect = await service.ConnectPeerAsync(clnContext, lndNode.ToString(), timeout.Token);
        var lndConnect = await service.ConnectPeerAsync(lndContext, clnNode.ToString(), timeout.Token);

        Assert.True(clnConnect.IsSuccess, $"CLN peer connection failed: {clnConnect.Message}");
        Assert.True(lndConnect.IsSuccess, $"LND peer connection failed: {lndConnect.Message}");

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
                channel.LocalBalanceSats >= PaymentAmountSats),
            $"The E2E fixture requires an active CLN channel to LND with at least {PaymentAmountSats} sats of outbound liquidity.");
        Assert.True(
            lndChannels.Channels.Any(channel =>
                channel.IsActive &&
                string.Equals(channel.RemoteNode, clnNode.NodeId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                channel.LocalBalanceSats >= PaymentAmountSats),
            $"The E2E fixture requires an active LND channel to CLN with at least {PaymentAmountSats} sats of outbound liquidity.");

        AssertOpenChannelPreview(service, clnContext, lndNode);
        AssertOpenChannelPreview(service, lndContext, clnNode);

        var invoice = await lnd.CreateInvoice(
            LightMoney.Satoshis(PaymentAmountSats),
            $"Lightning Manager E2E {Guid.NewGuid():N}",
            TimeSpan.FromMinutes(5),
            timeout.Token);

        var result = await service.SendAsync(clnContext, invoice.BOLT11, null, "100", timeout.Token);

        Assert.True(result.Result.IsSuccess, result.Result.Message);
        var settled = await WaitForSettledInvoice(lnd, invoice.Id, timeout.Token);
        Assert.Equal(LightningInvoiceStatus.Paid, settled.Status);

        var returnInvoice = await cln.CreateInvoice(
            LightMoney.Satoshis(PaymentAmountSats),
            $"Lightning Manager E2E return {Guid.NewGuid():N}",
            TimeSpan.FromMinutes(5),
            timeout.Token);

        var returnResult = await service.SendAsync(lndContext, returnInvoice.BOLT11, null, "100", timeout.Token);

        Assert.True(returnResult.Result.IsSuccess, returnResult.Result.Message);
        var returnSettled = await WaitForSettledInvoice(cln, returnInvoice.Id, timeout.Token);
        Assert.Equal(LightningInvoiceStatus.Paid, returnSettled.Status);

        var clnOverview = new OverviewViewModel();
        await service.PopulateOverviewAsync(clnOverview, clnContext, timeout.Token);
        Assert.DoesNotContain(clnOverview.Notices, notice => notice.StartsWith("Could not", StringComparison.Ordinal));

        var lndOverview = new OverviewViewModel();
        await service.PopulateOverviewAsync(lndOverview, lndContext, timeout.Token);
        Assert.DoesNotContain(lndOverview.Notices, notice => notice.StartsWith("Could not", StringComparison.Ordinal));
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

    private static async Task<LightningInvoice> WaitForSettledInvoice(
        ILightningClient client,
        string invoiceId,
        CancellationToken cancellationToken)
    {
        LightningInvoice? settled = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            settled = await client.GetInvoice(invoiceId, cancellationToken);
            if (settled.Status == LightningInvoiceStatus.Paid)
            {
                return settled;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return settled ?? throw new InvalidOperationException("The destination invoice could not be loaded.");
    }

    private static string RequiredEnvironment(string name)
    {
        return Environment.GetEnvironmentVariable(name) ??
               throw new InvalidOperationException($"{name} must be set when {EnabledEnvironmentVariable}=1.");
    }

    private static NodeInfo RequiredNodeInfo(string environmentVariable, LightningNodeInformation info)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (NodeInfo.TryParse(configured, out var nodeInfo) && nodeInfo is not null)
            {
                return nodeInfo;
            }

            throw new InvalidOperationException($"{environmentVariable} must use pubkey@host[:port].");
        }

        return info.NodeInfoList.FirstOrDefault() ??
               throw new InvalidOperationException(
                   $"The node did not advertise a peer URI. Set {environmentVariable} explicitly.");
    }

    private sealed class LightningManagerE2EFactAttribute : FactAttribute
    {
        public LightningManagerE2EFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {EnabledEnvironmentVariable}=1 and run scripts/e2e.sh to execute this test.";
            }
        }
    }
}
