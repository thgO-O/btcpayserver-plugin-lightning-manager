using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerServiceTests
{
    private const string ValidBolt11 =
        "lnbcrt20u1psd66dppp5m4ughz9keyptj80qcn35cx9w52p7gc8eyx4m6y5456jlhm04wfvsdqqcqzpgxqyz5vqsp5pdsxhsnrs69n940373fnec2zxw5yzlksnev40ejcq39lnju5lt3s9qyyssqpq760qvf46y3cch948wau8e5ym0zungnqfvdx5wruy6f0hru2pp9txtc9up2lfc439a2xuz6nvgjw40vsddhywjpc5qmm0q3dj4m3dcqxzjjeg";
    private const string SharedInternalNodeReadOnlyMessage =
        "Lightning actions are disabled for stores using the server's shared internal Lightning node.";

    private readonly LightningManagerService _service = new();

    [Fact]
    public void TryCreateSendPreview_WithInvalidBolt11_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, "not-a-bolt11", null, out _, out var error);

        Assert.False(ok);
        Assert.Equal("The BOLT11 invoice is invalid.", error);
    }

    [Fact]
    public async Task ConnectPeerAsync_WithInvalidNodeUri_ReturnsFailure()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var result = await _service.ConnectPeerAsync(context, "invalid-node-uri");

        Assert.False(result.IsSuccess);
        Assert.Equal("The node URI is invalid. Use pubkey@host[:port].", result.Message);
    }

    [Fact]
    public async Task SendAsync_WithUnknownPayResult_DoesNotMarkPaymentAsSuccessful()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_WithProviderError_DoesNotExposeRawErrorDetail()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, "server=http://127.0.0.1;macaroon=/Users/test/admin.macaroon"))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Null(result.Result.Detail);
    }

    [Fact]
    public async Task SendAsync_UsesExplicitMaximumFee()
    {
        var service = new BypassingValidationLightningManagerService();
        PayInvoiceParams? capturedParams = null;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = (_, payParams, _) =>
            {
                capturedParams = payParams;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", "21");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Equal(21, capturedParams!.MaxFeeFlat!.Satoshi);
    }

    [Fact]
    public void TryCreateSendPreview_WithInvalidMaxFee_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, "lnbc1test", "-1", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Maximum fee must be a non-negative whole number of sats.", error);
    }

    [Fact]
    public async Task SendAsync_WhenPayThrowsAndPaymentIsUnknown_ReturnsUnknown()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new TimeoutException("timed out"),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Unknown,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_WhenPayThrowsAndPaymentCompleted_ReturnsSuccess()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new TimeoutException("timed out"),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "test-hash",
                AmountSent = LightMoney.Satoshis(2),
                Fee = new LightMoney(100),
                Preimage = "preimage"
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
        Assert.Equal("preimage", result.Payment.Preimage);
    }

    [Fact]
    public async Task SendAsync_WhenPayReturnsErrorButPaymentCompleted_ReturnsSuccess()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Error)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "test-hash",
                AmountSent = LightMoney.Satoshis(2),
                Fee = new LightMoney(100),
                Preimage = "preimage"
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenPayReturnsRouteFailureButPaymentPending_ReturnsUnknown()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.CouldNotFindRoute)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Pending,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Pending, result.Payment.Status);
    }

    [Fact]
    public async Task PopulatePeersAsync_ShowsUnavailableMessage()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        var model = new ViewModels.PeersViewModel();

        await _service.PopulatePeersAsync(model, context);

        Assert.Equal("Peer listing is not available for this backend.", model.PeerListMessage);
    }

    [Fact]
    public async Task PopulatePeersAsync_WithReadOnlySharedInternalNode_DoesNotListNodePeers()
    {
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.None,
            new FakeLightningClient(),
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: true);
        var model = new ViewModels.PeersViewModel();

        await _service.PopulatePeersAsync(model, context);

        Assert.Equal(SharedInternalNodeReadOnlyMessage, model.PeerListMessage);
    }

    [Fact]
    public void TryCreateOpenChannelPreview_WithInvalidAmount_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "0", "5", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Channel amount must be a positive whole number of sats.", error);
    }

    [Fact]
    public void TryCreateOpenChannelPreview_WithLndBackendBelowMinimum_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, new LndLikeLightningClient());
        const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "19999", "5", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Channel amount must be at least 20000 sats for LND backends.", error);
    }

    [Fact]
    public void TryCreateOpenChannelPreview_ParsesFeeRateWithInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            CultureInfo.CurrentUICulture = new CultureInfo("pt-BR");
            var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
            const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

            var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "25000", "1.5", out var preview, out var error);

            Assert.True(ok, error);
            Assert.NotNull(preview);
            Assert.Equal("1.5 sat/vB", preview.FeeRateDisplay);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    [Fact]
    public void CreateTabs_WithPayOnlyCapabilities_HidesPeerAndChannelTabs()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly());

        var tabs = _service.CreateTabs(context, ViewModels.LightningManagerNavPages.Send);

        Assert.True(tabs.ShowSend);
        Assert.False(tabs.ShowHistory);
        Assert.False(tabs.ShowPeers);
        Assert.False(tabs.ShowChannels);
    }

    [Fact]
    public void CreateTabs_WithReadOnlySharedInternalNode_HidesMutatingTabs()
    {
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanGetBalance = true
            },
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: true);

        var tabs = _service.CreateTabs(context, ViewModels.LightningManagerNavPages.Overview);

        Assert.False(tabs.ShowOverview);
        Assert.True(tabs.ShowStoreBalance);
        Assert.True(tabs.ShowHistory);
        Assert.False(tabs.ShowSend);
        Assert.False(tabs.ShowPeers);
        Assert.False(tabs.ShowChannels);
    }

    [Fact]
    public void CreateTabs_WithInternalNode_HidesGlobalNodeActions()
    {
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: false);

        var tabs = _service.CreateTabs(context, ViewModels.LightningManagerNavPages.StoreBalance);

        Assert.False(tabs.ShowOverview);
        Assert.True(tabs.ShowStoreBalance);
        Assert.True(tabs.ShowHistory);
        Assert.False(tabs.ShowSend);
        Assert.False(tabs.ShowPeers);
        Assert.False(tabs.ShowChannels);
    }

    [Fact]
    public void TryCreateSendPreview_WithReadOnlySharedInternalNode_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: true);

        var ok = _service.TryCreateSendPreview(context, "lnbc1test", null, out _, out var error);

        Assert.False(ok);
        Assert.Equal(SharedInternalNodeReadOnlyMessage, error);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithNullBalanceFields_SkipsMissingRows()
    {
        var client = new FakeLightningClient
        {
            GetBalanceHandler = _ => Task.FromResult(
                new LightningNodeBalance(
                    new OnchainBalance
                    {
                        Confirmed = Money.Satoshis(1000),
                        Unconfirmed = Money.Satoshis(250),
                        Reserved = null
                    },
                    new OffchainBalance
                    {
                        Opening = LightMoney.Satoshis(100),
                        Local = LightMoney.Satoshis(200),
                        Remote = LightMoney.Satoshis(300),
                        Closing = LightMoney.Satoshis(400)
                    }))
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetBalance = true },
            client,
            connectionString: "type=eclair;server=http://127.0.0.1:8285/;password=eclairpw");
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.OnchainBalanceRows, row => row.Label == "Confirmed");
        Assert.Contains(model.OffchainBalanceRows, row => row.Label == "Remote");
        Assert.DoesNotContain(model.OnchainBalanceRows, row => row.Label == "Reserved");
        Assert.DoesNotContain(model.Notices, notice => notice.StartsWith("Could not load balances:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithChannelListingSupport_DerivesChannelCountsFromListChannels()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel { IsActive = true },
                new LightningChannel { IsActive = false }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client);
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(1, model.ActiveChannelsCount);
        Assert.Equal(1, model.InactiveChannelsCount);
        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "1");
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithReadOnlySharedInternalNode_ShowsReadOnlyMessage()
    {
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanGetBalance = true
            },
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: true);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        Assert.Equal(SharedInternalNodeReadOnlyMessage, model.ChannelListMessage);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithClosableChannel_SetsManageState()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsPublic = false,
                    IsActive = true,
                    Capacity = LightMoney.Satoshis(20_000),
                    LocalBalance = LightMoney.Satoshis(12_000),
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        var channel = Assert.Single(model.Channels);
        Assert.Equal("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", channel.RemoteNode);
        Assert.Equal("20,000 sats", channel.CapacityDisplay);
        Assert.Equal("12,000 sats", channel.LocalBalanceDisplay);
        Assert.Equal("8,000 sats", channel.RemoteBalanceDisplay);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WhenLocalBalanceExceedsCapacity_ClampsDisplay()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsPublic = false,
                    IsActive = true,
                    Capacity = LightMoney.Satoshis(20_000),
                    LocalBalance = LightMoney.Satoshis(25_000),
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        var channel = Assert.Single(model.Channels);
        Assert.Equal(20_000m, channel.CapacitySats);
        Assert.Equal(20_000m, channel.LocalBalanceSats);
        Assert.Equal(0m, channel.RemoteBalanceSats);
        Assert.Equal("20,000 sats", channel.LocalBalanceDisplay);
        Assert.Equal("0 sats", channel.RemoteBalanceDisplay);
    }

    [Fact]
    public async Task OpenChannelAsync_WithEclairFollowUpChannelIdParseError_ReturnsSuccess()
    {
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, _) => throw new Exception(
                "The form field 'channelId' was malformed: Invalid hexadecimal character 'w' at index 65")
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=eclair;server=http://127.0.0.1:8285/;password=eclairpw");

        var result = await _service.OpenChannelAsync(
            context,
            "038c0bcbad6a83cc11e8ec8c1cb0f2ffaa39e6ee5ded3d679b9a160ad18d2deda6@127.0.0.1:9735",
            "100000",
            null);

        Assert.True(result.IsSuccess);
        Assert.Equal("Channel opening request submitted.", result.Message);
    }

    [Fact]
    public void TryCreateSendPreview_WithExpiredInvoice_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, ValidBolt11, null, out var preview, out var error);

        Assert.False(ok);
        Assert.Null(preview);
        Assert.Equal("This invoice has already expired.", error);
    }

    private sealed class BypassingValidationLightningManagerService : LightningManagerService
    {
        public override bool TryCreateSendPreview(
            StoreLightningManagerContext context,
            string? bolt11,
            string? maxFeeSats,
            out SendPreviewViewModel? preview,
            out string? error)
        {
            var maxFeeDisplay = string.IsNullOrWhiteSpace(maxFeeSats)
                ? LightningManagerDefaults.SendMaxFeeSats.ToString(CultureInfo.InvariantCulture)
                : maxFeeSats;
            preview = new SendPreviewViewModel
            {
                Bolt11 = bolt11 ?? string.Empty,
                AmountDisplay = "1 sat",
                MaxFeeSats = string.IsNullOrWhiteSpace(maxFeeSats)
                    ? LightningManagerDefaults.SendMaxFeeSats
                    : long.Parse(maxFeeSats, CultureInfo.InvariantCulture),
                MaxFeeDisplay = $"{maxFeeDisplay} sats",
                Description = "Test invoice",
                PaymentHash = "test-hash",
                Payee = "test-payee",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            error = null;
            return true;
        }
    }
}
