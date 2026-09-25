using System.Net;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.Phoenixd;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerServiceTests
{
    private const string ExpiredBolt11 =
        "lnbcrt20u1psd66dppp5m4ughz9keyptj80qcn35cx9w52p7gc8eyx4m6y5456jlhm04wfvsdqqcqzpgxqyz5vqsp5pdsxhsnrs69n940373fnec2zxw5yzlksnev40ejcq39lnju5lt3s9qyyssqpq760qvf46y3cch948wau8e5ym0zungnqfvdx5wruy6f0hru2pp9txtc9up2lfc439a2xuz6nvgjw40vsddhywjpc5qmm0q3dj4m3dcqxzjjeg";
    private static readonly string FixedAmountPaymentHash =
        BOLT11PaymentRequest.Parse(TestInvoiceData.FixedAmountBolt11, Network.RegTest).PaymentHash!.ToString();
    private static readonly string FixedAmountPayee =
        BOLT11PaymentRequest.Parse(TestInvoiceData.FixedAmountBolt11, Network.RegTest).GetPayeePubKey().ToString();
    private static readonly string AmountlessPayee =
        BOLT11PaymentRequest.Parse(TestInvoiceData.AmountlessBolt11, Network.RegTest).GetPayeePubKey().ToString();

    private readonly LightningManagerService _service = TestLightningManagerServiceFactory.Create();

    [Fact]
    public void TryCreateSendPreview_WithInvalidBolt11_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, "not-a-bolt11", null, null, out _, out var error);

        Assert.False(ok);
        Assert.Equal("The BOLT11 invoice is invalid.", error);
    }

    [Fact]
    public async Task Send_WithUnrecoverablePayee_ReturnsFriendlyErrorWithoutDispatch()
    {
        const string alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        var original = TestInvoiceData.FixedAmountBolt11;
        var separator = original.LastIndexOf('1');
        var data = original[(separator + 1)..^6].Select(c => (byte)alphabet.IndexOf(c)).ToArray();
        // The final word contains the recovery id (valid range: 0–3). Keep the checksum valid.
        data[^1] = 4;
        var encoder = new Bech32Encoder(Encoding.ASCII.GetBytes(original[..separator])) { StrictLength = false };
        var bolt11 = encoder.EncodeRaw(data, Bech32EncodingType.BECH32);
        var payCalls = 0;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) =>
            {
                payCalls++;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var ok = _service.TryCreateSendPreview(context, bolt11, null, null, out var preview, out var error);
        var result = await _service.SendAsync(context, bolt11, null, null);

        Assert.False(ok);
        Assert.Null(preview);
        Assert.Equal("The BOLT11 invoice is invalid.", error);
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("The BOLT11 invoice is invalid.", result.Result.Message);
        Assert.Equal(0, payCalls);
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
    public async Task ConnectPeerAsync_WithValidNode_ConnectsOnceAndReturnsSuccess()
    {
        const string nodeId = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
        const string nodeUri = $"{nodeId}@127.0.0.1:9735";
        var callCount = 0;
        NodeInfo? connectedNode = null;
        var propagatedToken = CancellationToken.None;
        var client = new FakeLightningClient
        {
            ConnectToHandler = (node, token) =>
            {
                callCount++;
                connectedNode = node;
                propagatedToken = token;
                return Task.FromResult(ConnectionResult.Ok);
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        using var cancellation = new CancellationTokenSource();

        var result = await _service.ConnectPeerAsync(context, nodeUri, cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Connected to peer successfully.", result.Message);
        Assert.Equal(1, callCount);
        Assert.NotNull(connectedNode);
        Assert.Equal(nodeId, connectedNode.NodeId.ToString());
        Assert.Equal("127.0.0.1", connectedNode.Host);
        Assert.Equal(9735, connectedNode.Port);
        Assert.Equal(cancellation.Token, propagatedToken);
        Assert.True(propagatedToken.CanBeCanceled);
        Assert.False(propagatedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ConnectPeerAsync_WhenCanceledBeforeDispatch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.ConnectPeerAsync(
                context,
                "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735",
                cts.Token));
    }

    [Fact]
    public async Task SendAsync_WithUnknownPayResult_DoesNotMarkPaymentAsSuccessful()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_WithProviderError_DoesNotExposeRawErrorDetail()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, "server=http://127.0.0.1;macaroon=/Users/test/admin.macaroon"))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
        Assert.Equal(FixedAmountPaymentHash, result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WithProviderErrorMentioningBalance_RemainsUnknownWithoutReconciliation()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, "insufficient balance: server=http://127.0.0.1"))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_UsesExplicitMaximumFee()
    {
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

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, "21");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Equal(21, capturedParams!.MaxFeeFlat!.Satoshi);
    }

    [Fact]
    public async Task SendAsync_UsesNormalizedPreviewInvoice()
    {
        string? capturedBolt11 = null;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = (bolt11, _, _) =>
            {
                capturedBolt11 = bolt11;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, $" {TestInvoiceData.FixedAmountBolt11} ", null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal(TestInvoiceData.FixedAmountBolt11, capturedBolt11);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("9223372036854776")]
    public void TryCreateSendPreview_WithInvalidMaxFee_ReturnsFriendlyError(string maxFeeSats)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, TestInvoiceData.FixedAmountBolt11, null, maxFeeSats, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Maximum fee must be a positive whole number of sats.", error);
    }

    [Fact]
    public void TryCreateSendPreview_WithMaximumRepresentableMaxFee_Succeeds()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            TestInvoiceData.FixedAmountBolt11,
            null,
            "9223372036854775",
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(9_223_372_036_854_775, preview!.MaxFeeSats);
    }

    [Fact]
    public void TryCreateSendPreview_WithAmountlessInvoice_UsesUserAmount()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            TestInvoiceData.AmountlessBolt11,
            "123",
            "7",
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.True(preview.IsAmountless);
        Assert.Equal(123, preview.UserAmountSats);
        Assert.Equal(LightMoney.Satoshis(123), preview.PaymentAmount);
        Assert.Equal("0.00000123 BTC", preview.AmountBtcDisplay);
        Assert.Equal(AmountlessPayee, preview.Payee);
    }

    [Fact]
    public async Task SendAsync_WithBlinkAmountlessInvoice_DoesNotDispatchPayment()
    {
        var client = new FakeLightningClient();
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkBitcoin,
            client,
            "type=blink;server=https://api.blink.sv/;currency=BTC");

        var result = await _service.SendAsync(context, TestInvoiceData.AmountlessBolt11, "123", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Amountless invoices are not supported by this backend.", result.Result.Message);
    }

    [Fact]
    public void TryCreateSendPreview_WithBlinkFixedInvoice_RemainsSupported()
    {
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkBitcoin,
            connectionString: "type=blink;server=https://api.blink.sv/;currency=BTC");

        var ok = _service.TryCreateSendPreview(
            context,
            TestInvoiceData.FixedAmountBolt11,
            null,
            null,
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.False(preview.IsAmountless);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("9223372036854775807")]
    public void TryCreateSendPreview_WithInvalidAmountlessAmount_ReturnsFriendlyError(string? amountSats)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            TestInvoiceData.AmountlessBolt11,
            amountSats,
            null,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal("Amount must be a positive whole number of sats for an amountless invoice.", error);
    }

    [Fact]
    public void TryCreateSendPreview_WithFixedInvoice_IgnoresUserAmount()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            TestInvoiceData.FixedAmountBolt11,
            "999999",
            null,
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.False(preview.IsAmountless);
        Assert.Null(preview.UserAmountSats);
        Assert.Equal(LightMoney.Satoshis(2), preview.PaymentAmount);
        Assert.Equal("0.00000002 BTC", preview.AmountBtcDisplay);
        Assert.Equal(FixedAmountPayee, preview.Payee);
    }

    [Fact]
    public async Task SendAsync_WithAmountlessInvoice_PassesUserAmount()
    {
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

        var result = await _service.SendAsync(context, TestInvoiceData.AmountlessBolt11, "321", "9");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Equal(LightMoney.Satoshis(321), capturedParams.Amount);
        Assert.Equal(9, capturedParams.MaxFeeFlat!.Satoshi);
        Assert.Equal("321 sats", result.Payment!.PaymentAmountDisplay);
        Assert.Equal("321 sats", result.Payment.PrimaryAmountDisplay);
        Assert.Equal(AmountlessPayee, result.Payment.Payee);
    }

    [Fact]
    public async Task SendAsync_WithoutMaxFeeCapability_IgnoresTamperedAmountAndMaxFee()
    {
        PayInvoiceParams? capturedParams = null;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = (_, payParams, _) =>
            {
                capturedParams = payParams;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Phoenixd, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, "999999", "-10");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Null(capturedParams.Amount);
        Assert.Null(capturedParams.MaxFeeFlat);
    }

    [Fact]
    public async Task SendAsync_WithSharedBackendAcrossStores_DispatchesOnlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return new PayResponse(PayResult.Ok);
            }
        };
        var firstStoreContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=lnd-rest;server=https://shared-node.example/proxy;macaroon=AABB;allowinsecure=false",
            storeId: "store-1");
        var secondStoreContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=lnd-grpc;server=https://shared-node.example/proxy/;macaroon=CCDD",
            storeId: "store-2");
        Assert.NotEqual(
            firstStoreContext.BackendFingerprint,
            secondStoreContext.BackendFingerprint);
        Assert.Equal(
            firstStoreContext.BackendIdentityFingerprint,
            secondStoreContext.BackendIdentityFingerprint);

        var first = _service.SendAsync(firstStoreContext, TestInvoiceData.FixedAmountBolt11, null, null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SendExecutionResult duplicate;
        try
        {
            duplicate = await _service.SendAsync(secondStoreContext, TestInvoiceData.FixedAmountBolt11, null, null)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
        var original = await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(original.Result.IsSuccess);
        Assert.False(duplicate.Result.IsSuccess);
        Assert.Equal("Payment is already in progress.", duplicate.Result.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SendAsync_LogsOnlySanitizedOperationMetadata()
    {
        var logger = new RecordingLogger();
        var service = TestLightningManagerServiceFactory.Create(logger);
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new InvalidOperationException(
                "server=https://secret.example;macaroon=super-secret;preimage=private-preimage")
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=lnd-rest;server=https://secret.example;macaroon=super-secret");

        await service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        var log = Assert.Single(logger.Entries);
        Assert.Contains("pay", log, StringComparison.Ordinal);
        Assert.Contains("store-1", log, StringComparison.Ordinal);
        Assert.Contains("BTC", log, StringComparison.Ordinal);
        Assert.Contains("lnd-rest", log, StringComparison.Ordinal);
        Assert.Contains("unknown", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain(TestInvoiceData.FixedAmountBolt11, log, StringComparison.Ordinal);
        Assert.DoesNotContain(FixedAmountPaymentHash, log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", log, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private-preimage", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenPayThrowsAndPaymentIsUnknown_ReturnsUnknown()
    {
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

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_WhenCanceledBeforeDispatch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null, cts.Token));
    }

    [Fact]
    public async Task SendAsync_WhenCanceledAfterDispatch_ReconcilesWithIndependentTimeout()
    {
        using var cts = new CancellationTokenSource();
        var reconciliationCalled = false;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, token) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            },
            GetPaymentHandler = (_, token) =>
            {
                reconciliationCalled = true;
                Assert.False(token.IsCancellationRequested);
                return Task.FromResult(new LightningPayment
                {
                    Status = LightningPaymentStatus.Unknown,
                    PaymentHash = "test-hash"
                });
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null, cts.Token);

        Assert.True(reconciliationCalled);
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenPaymentSucceedsAndDetailsLookupIsCanceled_PreservesSuccess()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, token) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null, cts.Token);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal(FixedAmountPaymentHash, result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessfulPaymentLookupStalls_CompletesAfterInternalTimeout()
    {
        var neverCompletes = new TaskCompletionSource<LightningPayment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => neverCompletes.Task
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service
            .SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null)
            .WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessfulResponseTotalIncludesFee_DoesNotDoubleCountOrLookup()
    {
        var neverCompletes = new TaskCompletionSource<LightningPayment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var getPaymentCalls = 0;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Ok, new PayDetails
                {
                    TotalAmount = LightMoney.Satoshis(3),
                    FeeAmount = LightMoney.Satoshis(1),
                    PaymentHash = new uint256(FixedAmountPaymentHash),
                    Preimage = uint256.One,
                    Status = LightningPaymentStatus.Complete
                })),
            GetPaymentHandler = (_, _) =>
            {
                Interlocked.Increment(ref getPaymentCalls);
                return neverCompletes.Task;
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service
            .SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.Result.IsSuccess);
        Assert.Equal(0, getPaymentCalls);
        Assert.Equal("2 sats", result.Payment!.PaymentAmountDisplay);
        Assert.Equal("3 sats", result.Payment!.TotalAmountDisplay);
        Assert.Equal("3 sats", result.Payment.PrimaryAmountDisplay);
        Assert.Equal("1 sats", result.Payment.FeeAmountDisplay);
        Assert.Equal(FixedAmountPayee, result.Payment.Payee);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessfulResponseTotalExcludesFee_UsesCanonicalAmountPlusFee()
    {
        var getPaymentCalls = 0;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Ok, new PayDetails
                {
                    TotalAmount = LightMoney.Satoshis(2),
                    FeeAmount = LightMoney.Satoshis(1),
                    PaymentHash = new uint256(FixedAmountPaymentHash),
                    Preimage = uint256.One,
                    Status = LightningPaymentStatus.Complete
                })),
            GetPaymentHandler = (_, _) =>
            {
                Interlocked.Increment(ref getPaymentCalls);
                return Task.FromResult(new LightningPayment
                {
                    Status = LightningPaymentStatus.Failed,
                    AmountSent = LightMoney.Satoshis(100),
                    Fee = LightMoney.Satoshis(20),
                    PaymentHash = "stale-payment-hash",
                    Preimage = "stale-preimage"
                });
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal(0, getPaymentCalls);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
        Assert.Equal("3 sats", result.Payment.TotalAmountDisplay);
        Assert.Equal("1 sats", result.Payment.FeeAmountDisplay);
        Assert.Equal(FixedAmountPaymentHash, result.Payment.PaymentHash);
        Assert.Equal(uint256.One.ToString(), result.Payment.Preimage);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessfulResponseHasNoFee_UsesResponseTotal()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Ok, new PayDetails
                {
                    TotalAmount = LightMoney.Satoshis(4),
                    PaymentHash = new uint256(FixedAmountPaymentHash),
                    Preimage = uint256.One,
                    Status = LightningPaymentStatus.Complete
                }))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("4 sats", result.Payment!.TotalAmountDisplay);
        Assert.Null(result.Payment.FeeAmountDisplay);
    }

    [Fact]
    public async Task SendAsync_WhenPaySucceedsButLookupIsStale_ReportsComplete()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Pending,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenPayThrowsAndPaymentCompleted_ReturnsSuccess()
    {
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

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal("2 sats", result.Payment.PaymentAmountDisplay);
        Assert.Equal("2 sats", result.Payment.TotalAmountDisplay);
        Assert.Equal("0.1 sats", result.Payment.FeeAmountDisplay);
        Assert.Equal(FixedAmountPayee, result.Payment.Payee);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
        Assert.Equal("preimage", result.Payment.Preimage);
    }

    [Fact]
    public async Task SendAsync_WhenPayReturnsErrorButPaymentCompleted_ReturnsSuccess()
    {
        var getPaymentCalls = 0;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Error)),
            GetPaymentHandler = (_, _) => ++getPaymentCalls == 1
                ? Task.FromResult(new LightningPayment
                {
                    Status = LightningPaymentStatus.Complete,
                    PaymentHash = "test-hash",
                    AmountSent = LightMoney.Satoshis(2),
                    Fee = new LightMoney(100),
                    Preimage = "preimage"
                })
                : throw new TimeoutException("GetPayment was called more than once")
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
        Assert.Equal(1, getPaymentCalls);
    }

    [Fact]
    public async Task SendAsync_WhenLndReconciliationConfirmsFailure_ReturnsFailed()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Error)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=lnd-rest;server=https://example.com/");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseConfirmsFailureAndLookupIsEmpty_ReturnsFailed()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, new PayDetails
                {
                    Status = LightningPaymentStatus.Failed,
                    PaymentHash = uint256.One
                }))
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
        Assert.Equal(uint256.One.ToString(), result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenResponseIsUnknown_DoesNotTrustFailedDetails()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Unknown, new PayDetails
                {
                    Status = LightningPaymentStatus.Failed,
                    PaymentHash = uint256.One
                }))
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(
            "Payment status is unknown. Check the Lightning node before retrying.",
            result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
        Assert.Equal(uint256.One.ToString(), result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenLookupIsPending_DoesNotTrustConflictingFailedResponse()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, new PayDetails
                {
                    Status = LightningPaymentStatus.Failed,
                    PaymentHash = uint256.One
                })),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Pending,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(
            "Payment status is unknown. Check the Lightning node before retrying.",
            result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Pending, result.Payment!.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkLookupReturnsOlderFailureAndCurrentResponseIsUnknown_ReturnsUnknown()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "older-failed-attempt"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(
            "Payment status is unknown. Check the Lightning node before retrying.",
            result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
        Assert.Equal("older-failed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkCurrentResponseIsPendingAndLookupReturnsOlderFailure_PreservesPending()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Unknown, new PayDetails
                {
                    Status = LightningPaymentStatus.Pending,
                    PaymentHash = uint256.One
                })),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "older-failed-attempt"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(
            "Payment status is unknown. Check the Lightning node before retrying.",
            result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Pending, result.Payment!.Status);
        Assert.Equal("older-failed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseIsPendingAndLookupIsUnavailable_PreservesPendingDetails()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Unknown, new PayDetails
                {
                    Status = LightningPaymentStatus.Pending,
                    PaymentHash = uint256.One
                }))
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(
            "Payment status is unknown. Check the Lightning node before retrying.",
            result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Pending, result.Payment!.Status);
        Assert.Equal(uint256.One.ToString(), result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseAndLookupConfirmFailure_ReturnsFailed()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, new PayDetails
                {
                    Status = LightningPaymentStatus.Failed,
                    PaymentHash = uint256.One
                })),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "failed-attempt"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
        Assert.Equal("failed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseClaimsFailureButLookupIsComplete_ReturnsSuccess()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, new PayDetails
                {
                    Status = LightningPaymentStatus.Failed,
                    PaymentHash = uint256.One
                })),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "completed-attempt"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkPayOnly,
            client,
            connectionString: "type=blink;server=https://api.example.test/graphql;api-key=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
        Assert.Equal("completed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenEclairReturnsOlderFailedAttempt_ReturnsUnknown()
    {
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Error)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "test-hash"
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=eclair;server=http://127.0.0.1:8080;password=test");

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenPayReturnsRouteFailureButPaymentPending_ReturnsUnknown()
    {
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

        var result = await _service.SendAsync(context, TestInvoiceData.FixedAmountBolt11, null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Pending, result.Payment.Status);
    }

    [Fact]
    public async Task OpenChannelAsync_WithExplicitFeeRate_PreviewsAndDispatchesChosenFee()
    {
        const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";
        OpenChannelRequest? capturedRequest = null;
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "25000", "5", out var preview, out var error);
        var result = await _service.OpenChannelAsync(context, nodeUri, "25000", "5");

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.Equal("5 sat/vB", preview.FeeRateDisplay);
        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Equal(5m, capturedRequest.FeeRate.SatoshiPerByte);
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
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            connectionString: "type=lnd-rest;server=https://example.com/");
        const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "19999", "5", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Channel amount must be at least 20000 sats for LND backends.", error);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("2147483648")]
    public void TryCreateOpenChannelPreview_WithInvalidFeeRate_ReturnsFriendlyError(string feeRate)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
        const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "25000", feeRate, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Fee rate must be a whole number between 1 and 2147483647 sat/vB.", error);
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
        Assert.Empty(model.Notices);
        Assert.Empty(model.Warnings);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenBalanceFails_AddsWarning()
    {
        var client = new FakeLightningClient
        {
            GetBalanceHandler = _ => throw new InvalidOperationException("balance unavailable")
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetBalance = true },
            client);
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains("Could not load balances.", model.Warnings);
        Assert.DoesNotContain("Could not load balances.", model.Notices);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithChannelListingSupport_DerivesChannelCountsFromListChannels()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new ExplicitPendingLightningChannel
                {
                    IsActive = true,
                    IsPending = false,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                },
                new ExplicitPendingLightningChannel
                {
                    IsActive = false,
                    IsPending = false,
                    ChannelPoint = new OutPoint(uint256.One, 1)
                },
                new ExplicitPendingLightningChannel
                {
                    IsActive = true,
                    IsPending = true,
                    ChannelPoint = new OutPoint(uint256.One, 2)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client);
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "1");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenChannelListingFails_PreservesGetInfoChannelCounts()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                ActiveChannelsCount = 2,
                InactiveChannelsCount = 3,
                PendingChannelsCount = 4
            }),
            ListChannelsHandler = _ => throw new InvalidOperationException("list unavailable")
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanListChannels = true
            },
            client);
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "2");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "3");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "4");
        Assert.Contains("Could not load channels.", model.Warnings);
        Assert.DoesNotContain("Could not load channels.", model.Notices);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenPhoenixdInfoFails_DoesNotLoadBalance()
    {
        var balanceCalls = 0;
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => throw new InvalidOperationException("wrong network"),
            GetBalanceHandler = _ =>
            {
                balanceCalls++;
                return Task.FromResult(new LightningNodeBalance(
                    new OnchainBalance { Confirmed = Money.Satoshis(1000) },
                    null));
            }
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Phoenixd,
            client,
            connectionString: "type=phoenixd;server=https://example.test/;password=test");
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(0, balanceCalls);
        Assert.Empty(model.OnchainBalanceRows);
        Assert.Empty(model.OffchainBalanceRows);
        Assert.Contains("Could not load node information.", model.Warnings);
        Assert.DoesNotContain("Could not load node information.", model.Notices);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenNonPhoenixdInfoFails_StillLoadsBalance()
    {
        var balanceCalls = 0;
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => throw new InvalidOperationException("info unavailable"),
            GetBalanceHandler = _ =>
            {
                balanceCalls++;
                return Task.FromResult(new LightningNodeBalance(
                    new OnchainBalance { Confirmed = Money.Satoshis(1000) },
                    null));
            }
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetInfo = true, CanGetBalance = true },
            client,
            connectionString: "type=eclair;server=https://example.test/;password=test");
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(1, balanceCalls);
        Assert.Contains(model.OnchainBalanceRows, row => row.Label == "Confirmed");
        Assert.Contains("Could not load node information.", model.Warnings);
        Assert.DoesNotContain("Could not load node information.", model.Notices);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithPhoenixdOverlap_OmitsAmbiguousCounts()
    {
        using var httpClient = new HttpClient(new StaticJsonHttpMessageHandler(
            """
            {
              "chain": "regtest",
              "blockHeight": 100,
              "version": "v0.6.1",
              "channels": [
                { "state": "Normal" },
                { "state": "Normal" },
                { "state": "Offline" },
                { "state": "Closing" },
                { "state": "Syncing" }
              ]
            }
            """));
        var client = new PhoenixdLightningClient(
            new Uri("https://example.test/"),
            "test",
            Network.RegTest,
            httpClient);
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Phoenixd,
            client,
            connectionString: "type=phoenixd;server=https://example.test/;password=test");
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "2");
        Assert.DoesNotContain(model.SummaryRows, row => row.Label == "Inactive channels");
        Assert.DoesNotContain(model.SummaryRows, row => row.Label == "Pending channels");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithEqualCountsFromUnaffectedAdapter_PreservesBothCounts()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                InactiveChannelsCount = 3,
                PendingChannelsCount = 3
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Phoenixd,
            client,
            connectionString: "type=phoenixd;server=https://example.test/;password=test");
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "3");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "3");
    }

    [Theory]
    [InlineData("BTCPayServer.Lightning.Phoenixd", "1.7.1.0", true)]
    [InlineData("BTCPayServer.Lightning.Phoenixd", "1.7.0.0", false)]
    [InlineData("BTCPayServer.Lightning.Phoenixd", "1.7.2.0", false)]
    [InlineData("Another.Adapter", "1.7.1.0", false)]
    public void IsAffectedPhoenixdAdapter_MatchesOnlyAffectedAssemblyVersion(
        string assemblyName,
        string version,
        bool expected)
    {
        Assert.Equal(
            expected,
            LightningManagerService.IsAffectedPhoenixdAdapter(
                assemblyName,
                Version.Parse(version)));
    }

    [Fact]
    public void TryGetExplicitPendingState_WithUnknownNullableState_ReturnsUnsupported()
    {
        var supported = LightningManagerService.TryGetExplicitPendingState(
            new ExplicitPendingLightningChannel { IsPending = null },
            out var isPending);

        Assert.False(supported);
        Assert.Null(isPending);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithExplicitListing_UsesSingleListSnapshot()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                ActiveChannelsCount = 5,
                InactiveChannelsCount = 6,
                PendingChannelsCount = 2
            }),
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new ExplicitPendingLightningChannel
                {
                    IsActive = true,
                    IsPending = false,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                },
                new ExplicitPendingLightningChannel
                {
                    IsActive = false,
                    IsPending = false,
                    ChannelPoint = new OutPoint(uint256.One, 1)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanListChannels = true
            },
            client);
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "0");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithLndListing_PreservesCompleteGetInfoSnapshot()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                ActiveChannelsCount = 5,
                InactiveChannelsCount = 6,
                PendingChannelsCount = 2
            }),
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new ExplicitPendingLightningChannel
                {
                    IsActive = true,
                    IsPending = false,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanListChannels = true
            },
            client,
            "type=lnd-rest;server=https://example.test/");
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "5");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "6");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "2");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithUnknownEclairState_DoesNotCallItInactive()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    IsActive = false,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client,
            "type=eclair;server=https://example.test/;password=test");
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "0");
        Assert.DoesNotContain(model.SummaryRows, row => row.Label == "Inactive channels");
        Assert.DoesNotContain(model.SummaryRows, row => row.Label == "Pending channels");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WithLegacyAdapter_PreservesExclusiveGetInfoCounts()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                ActiveChannelsCount = 1,
                InactiveChannelsCount = 0,
                PendingChannelsCount = 1
            }),
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    IsActive = true,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                },
                new LightningChannel
                {
                    // A legacy CLN adapter loses CHANNELD_AWAITING_LOCKIN here even
                    // though the funding outpoint is already available.
                    IsActive = false,
                    ChannelPoint = new OutPoint(uint256.One, 1)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities
            {
                CanGetInfo = true,
                CanListChannels = true
            },
            client);
        var model = new OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Contains(model.SummaryRows, row => row.Label == "Active channels" && row.Value == "1");
        Assert.Contains(model.SummaryRows, row => row.Label == "Inactive channels" && row.Value == "0");
        Assert.Contains(model.SummaryRows, row => row.Label == "Pending channels" && row.Value == "1");
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenRequestIsCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient
        {
            GetInfoHandler = token => throw new OperationCanceledException(token)
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetInfo = true },
            client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.PopulateOverviewAsync(new ViewModels.OverviewViewModel(), context, cts.Token));
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenBackendFails_LogsOnlySanitizedMetadata()
    {
        var logger = new RecordingLogger();
        var service = TestLightningManagerServiceFactory.Create(logger);
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => throw new InvalidOperationException(
                "server=https://secret.example;api-key=super-secret")
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetInfo = true },
            client,
            "type=eclair;server=https://secret.example;password=super-secret");
        var model = new OverviewViewModel();

        await service.PopulateOverviewAsync(model, context);

        Assert.Contains("Could not load node information.", model.Warnings);
        Assert.DoesNotContain("Could not load node information.", model.Notices);
        var log = Assert.Single(logger.Entries);
        Assert.Contains("get-info", log, StringComparison.Ordinal);
        Assert.Contains("store-1", log, StringComparison.Ordinal);
        Assert.Contains("eclair", log, StringComparison.Ordinal);
        Assert.Contains("failed", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", log, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithChannel_MapsBalances()
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
        Assert.Null(channel.IsPending);
        Assert.Equal("Active", channel.Status);
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
        Assert.Equal("20,000 sats", channel.LocalBalanceDisplay);
        Assert.Equal("0 sats", channel.RemoteBalanceDisplay);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithExplicitPendingChannelAndOutpoint_MarksPending()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new ExplicitPendingLightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsActive = true,
                    IsPending = true,
                    Capacity = LightMoney.Satoshis(20_000),
                    LocalBalance = LightMoney.Satoshis(20_000),
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        var channel = Assert.Single(model.Channels);
        Assert.NotNull(channel.ChannelPoint);
        Assert.Equal(true, channel.IsPending);
        Assert.Equal("Pending", channel.Status);
        Assert.Null(model.ChannelListMessage);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithoutExplicitState_DoesNotInferPendingFromMissingOutpoint()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new LightningChannel
                {
                    IsActive = false,
                    Capacity = LightMoney.Satoshis(20_000),
                    LocalBalance = LightMoney.Satoshis(20_000),
                    ChannelPoint = null
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        var channel = Assert.Single(model.Channels);
        Assert.Null(channel.ChannelPoint);
        Assert.Null(channel.IsPending);
        Assert.Equal("Unknown", channel.Status);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithInactiveChannelAndOutpoint_MarksInactiveNotPending()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new LightningChannel[]
            {
                new ExplicitPendingLightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsActive = false,
                    IsPending = false,
                    Capacity = LightMoney.Satoshis(20_000),
                    LocalBalance = LightMoney.Satoshis(10_000),
                    ChannelPoint = new OutPoint(uint256.One, 0)
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var model = new ViewModels.ChannelsViewModel();

        await _service.PopulateChannelsAsync(model, context);

        var channel = Assert.Single(model.Channels);
        Assert.Equal(false, channel.IsPending);
        Assert.Equal("Inactive", channel.Status);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WhenRequestIsCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient
        {
            ListChannelsHandler = token => throw new OperationCanceledException(token)
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.PopulateChannelsAsync(new ViewModels.ChannelsViewModel(), context, cts.Token));
    }

    [Fact]
    public async Task PopulateChannelsAsync_WhenSuccessful_LogsOperationMetadata()
    {
        var logger = new RecordingLogger();
        var service = TestLightningManagerServiceFactory.Create(logger);
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(Array.Empty<LightningChannel>())
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client,
            "type=clightning;server=tcp://127.0.0.1:30993/");

        await service.PopulateChannelsAsync(new ChannelsViewModel(), context);

        var log = Assert.Single(logger.Entries);
        Assert.Contains("list-channels", log, StringComparison.Ordinal);
        Assert.Contains("store-1", log, StringComparison.Ordinal);
        Assert.Contains("clightning", log, StringComparison.Ordinal);
        Assert.Contains("success", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenChannelAsync_WhenCanceledBeforeDispatch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient();
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.OpenChannelAsync(
                context,
                "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735",
                "100000",
                null,
                cts.Token));
    }

    [Fact]
    public async Task OpenChannelAsync_WhenCanceledAfterDispatch_ReturnsUnknownStatus()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, token) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.OpenChannelAsync(
            context,
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735",
            "100000",
            null,
            cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("Channel opening status is unknown. Check the Lightning node before retrying.", result.Message);
    }

    [Fact]
    public async Task OpenChannelAsync_WhenBackendThrows_ReturnsUnknownStatus()
    {
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, _) => throw new TimeoutException("The backend response timed out")
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            connectionString: "type=lnd-rest;server=https://example.com/");

        var result = await _service.OpenChannelAsync(
            context,
            "038c0bcbad6a83cc11e8ec8c1cb0f2ffaa39e6ee5ded3d679b9a160ad18d2deda6@127.0.0.1:9735",
            "100000",
            null);

        Assert.False(result.IsSuccess);
        Assert.Equal("Channel opening status is unknown. Check the Lightning node before retrying.", result.Message);
    }

    [Fact]
    public async Task OpenChannelAsync_WhenBackendIgnoresCancellation_TimesOutButKeepsGuard()
    {
        var neverCompletes = new TaskCompletionSource<OpenChannelResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var firstCallToken = CancellationToken.None;
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, token) =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                {
                    return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
                }

                firstCallToken = token;
                return neverCompletes.Task;
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var service = new LightningManagerService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LightningManagerService>.Instance,
            new LightningManagerOperationGuard(),
            TimeSpan.FromMilliseconds(50));
        const string nodeUri =
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        ActionResultViewModel first;
        ActionResultViewModel second;
        try
        {
            first = await service
                .OpenChannelAsync(context, nodeUri, "100000", null)
                .WaitAsync(TimeSpan.FromSeconds(5));
            second = await service
                .OpenChannelAsync(context, nodeUri, "100000", null)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            neverCompletes.TrySetResult(new OpenChannelResponse(OpenChannelResult.Ok));
        }

        Assert.False(first.IsSuccess);
        Assert.Equal(
            "Channel opening status is unknown. Check the Lightning node before retrying.",
            first.Message);
        Assert.True(firstCallToken.IsCancellationRequested);
        Assert.False(second.IsSuccess);
        Assert.Equal("Channel opening is already in progress for this peer.", second.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OpenChannelAsync_WhenBackendNeedsMoreConfirmations_WarnsAboutPendingState()
    {
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, _) => Task.FromResult(
                new OpenChannelResponse(OpenChannelResult.NeedMoreConf))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await _service.OpenChannelAsync(
            context,
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735",
            "100000",
            null);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Channel opening may already be pending. Check the Lightning node before retrying.",
            result.Message);
    }

    [Fact]
    public async Task OpenChannelAsync_WithSharedBackendAcrossStores_DispatchesOnlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeLightningClient
        {
            OpenChannelHandler = async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return new OpenChannelResponse(OpenChannelResult.Ok);
            }
        };
        var firstStoreContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=eclair;server=https://shared-node.example/proxy/one?token=a;password=first",
            storeId: "store-1");
        var secondStoreContext = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=eclair;server=https://shared-node.example/proxy/two#fragment;password=second",
            storeId: "store-2");
        Assert.NotEqual(
            firstStoreContext.BackendFingerprint,
            secondStoreContext.BackendFingerprint);
        Assert.Equal(
            firstStoreContext.BackendIdentityFingerprint,
            secondStoreContext.BackendIdentityFingerprint);
        const string nodeUri =
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var first = _service.OpenChannelAsync(firstStoreContext, nodeUri, "100000", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ActionResultViewModel duplicate;
        try
        {
            duplicate = await _service.OpenChannelAsync(secondStoreContext, nodeUri, "100000", null)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
        var original = await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(original.IsSuccess);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("Channel opening is already in progress for this peer.", duplicate.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TryCreateSendPreview_WithExpiredInvoice_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, ExpiredBolt11, null, null, out var preview, out var error);

        Assert.False(ok);
        Assert.Null(preview);
        Assert.Equal("This invoice has already expired.", error);
    }

    private sealed class RecordingLogger : ILogger<LightningManagerService>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }

    private sealed class ExplicitPendingLightningChannel : LightningChannel
    {
#pragma warning disable CS0109 // The current package lacks this future upstream property.
        public new bool? IsPending { get; init; }
#pragma warning restore CS0109
    }

    private sealed class StaticJsonHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
