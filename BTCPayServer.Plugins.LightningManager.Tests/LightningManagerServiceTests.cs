using System.Globalization;
using System.Net;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.Phoenixd;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerServiceTests
{
    private const string ExpiredBolt11 =
        "lnbcrt20u1psd66dppp5m4ughz9keyptj80qcn35cx9w52p7gc8eyx4m6y5456jlhm04wfvsdqqcqzpgxqyz5vqsp5pdsxhsnrs69n940373fnec2zxw5yzlksnev40ejcq39lnju5lt3s9qyyssqpq760qvf46y3cch948wau8e5ym0zungnqfvdx5wruy6f0hru2pp9txtc9up2lfc439a2xuz6nvgjw40vsddhywjpc5qmm0q3dj4m3dcqxzjjeg";
    private const string AmountlessBolt11 =
        "lnbcrt1p49g38fsp5pt3nfdequqzynepp4fnqkjky3vpnnjy7k4aw4qsrpjx8uauslj8spp5e0ecjegxuc8k760w6cluj2tvrgytwpp6ls9wwata4wx6qkj043lsdpyd35kw6r5de5kueedd4skuct8v4ez6ar9wd6qxqxfvcqcqcqp29qxpqysgqstrrcpnyws5g93th9wu7ngjvu29pa9tattwpj6q6nsq0ep2wv4ax964t0hgnjszwspy3udg88xft902rudt9mvc4trj8l2tv0uukf9gpgzqz2r";
    private const string FixedAmountBolt11 =
        "lnbcrt20n1p49g3gqsp5ne4g7vrg5my5m7ur9u9curv7hfvn0tfx9h4k65uwwwk2gezwt5gqpp5umaryhan5kynzu7xd56zaqc9g32ahul3ehdh9cn2uuz0yh8nd5gqdpdd35kw6r5de5kueedd4skuct8v4ez6enf0pjkgtt5v4ehgxqxfvcqcqcqp29qxpqysgqu7ku8yv7szhps4005y5lh6yulv8heln8ztfwj4urk8lhrws20d7nncm8965ctns7c920kn7k46egwcmmuuhtt6cyyyqzg7vf485klzqpa09hcc";

    private readonly LightningManagerService _service = new();

    [Fact]
    public void TryCreateSendPreview_WithInvalidBolt11_ReturnsFriendlyError()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, "not-a-bolt11", null, null, out _, out var error);

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
    public async Task ConnectPeerAsync_WhenRequestIsCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient
        {
            ConnectToHandler = (_, token) => throw new OperationCanceledException(token)
        };
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
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WithProviderErrorMentioningBalance_RemainsUnknownWithoutReconciliation()
    {
        var service = new BypassingValidationLightningManagerService();
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(
                new PayResponse(PayResult.Error, "insufficient balance: server=http://127.0.0.1"))
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, "21");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Equal(21, capturedParams!.MaxFeeFlat!.Satoshi);
    }

    [Fact]
    public async Task SendAsync_UsesNormalizedPreviewInvoice()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, " lnbcrt1test ", null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("lnbcrt1test", capturedBolt11);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    public void TryCreateSendPreview_WithInvalidMaxFee_ReturnsFriendlyError(string maxFeeSats)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(context, FixedAmountBolt11, null, maxFeeSats, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Maximum fee must be a positive whole number of sats.", error);
    }

    [Fact]
    public void TryCreateSendPreview_WithAmountlessInvoice_UsesUserAmount()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            AmountlessBolt11,
            "123",
            "7",
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.True(preview.IsAmountless);
        Assert.Equal(123, preview.UserAmountSats);
        Assert.Equal(LightMoney.Satoshis(123), preview.PaymentAmount);
    }

    [Fact]
    public async Task SendAsync_WithBlinkAmountlessInvoice_DoesNotDispatchPayment()
    {
        var payCalled = false;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = (_, _, _) =>
            {
                payCalled = true;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkBitcoin,
            client,
            "type=blink;server=https://api.blink.sv/;currency=BTC");

        var result = await _service.SendAsync(context, AmountlessBolt11, "123", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Amountless invoices are not supported by this backend.", result.Result.Message);
        Assert.False(payCalled);
    }

    [Fact]
    public async Task SendAsync_WithoutAmountlessCapability_DoesNotDispatchPayment()
    {
        var payCalled = false;
        var client = new FakeLightningClient
        {
            PayBolt11WithParamsHandler = (_, _, _) =>
            {
                payCalled = true;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.PayOnly(),
            client,
            "type=phoenixd;server=https://example.com");

        var result = await _service.SendAsync(context, AmountlessBolt11, "123", null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Amountless invoices are not supported by this backend.", result.Result.Message);
        Assert.False(payCalled);
    }

    [Fact]
    public void TryCreateSendPreview_WithBlinkFixedInvoice_RemainsSupported()
    {
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.BlinkBitcoin,
            connectionString: "type=blink;server=https://api.blink.sv/;currency=BTC");

        var ok = _service.TryCreateSendPreview(
            context,
            FixedAmountBolt11,
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
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public void TryCreateSendPreview_WithInvalidAmountlessAmount_ReturnsFriendlyError(string? amountSats)
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);

        var ok = _service.TryCreateSendPreview(
            context,
            AmountlessBolt11,
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
            FixedAmountBolt11,
            "999999",
            null,
            out var preview,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(preview);
        Assert.False(preview.IsAmountless);
        Assert.Null(preview.UserAmountSats);
        Assert.Equal(LightMoney.Satoshis(2), preview.PaymentAmount);
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

        var result = await _service.SendAsync(context, AmountlessBolt11, "321", "9");

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(capturedParams);
        Assert.Equal(LightMoney.Satoshis(321), capturedParams.Amount);
        Assert.Equal(9, capturedParams.MaxFeeFlat!.Satoshi);
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

        var result = await _service.SendAsync(context, FixedAmountBolt11, "999999", "-10");

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
        var service = new BypassingValidationLightningManagerService();
        Assert.NotEqual(
            firstStoreContext.BackendFingerprint,
            secondStoreContext.BackendFingerprint);
        Assert.Equal(
            firstStoreContext.BackendIdentityFingerprint,
            secondStoreContext.BackendIdentityFingerprint);

        var first = service.SendAsync(firstStoreContext, "lnbcrt1test", null, null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SendExecutionResult duplicate;
        try
        {
            duplicate = await service.SendAsync(secondStoreContext, "lnbcrt1test", null, null)
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
        var service = new BypassingValidationLightningManagerService(logger);
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new InvalidOperationException(
                "server=https://secret.example;macaroon=super-secret;preimage=private-preimage")
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Full,
            client,
            "type=lnd-rest;server=https://secret.example;macaroon=super-secret");

        await service.SendAsync(context, "lnbcrt1test", null, null);

        var log = Assert.Single(logger.Entries);
        Assert.Contains("pay", log, StringComparison.Ordinal);
        Assert.Contains("store-1", log, StringComparison.Ordinal);
        Assert.Contains("BTC", log, StringComparison.Ordinal);
        Assert.Contains("lnd-rest", log, StringComparison.Ordinal);
        Assert.Contains("unknown", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain("lnbcrt1test", log, StringComparison.Ordinal);
        Assert.DoesNotContain("test-hash", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", log, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private-preimage", log, StringComparison.Ordinal);
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment.Status);
    }

    [Fact]
    public async Task SendAsync_WhenCanceledBeforeDispatch_PropagatesCancellation()
    {
        var service = new BypassingValidationLightningManagerService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var payCalled = false;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, token) =>
            {
                payCalled = true;
                throw new OperationCanceledException(token);
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SendAsync(context, "lnbcrt1test", null, null, cts.Token));
        Assert.False(payCalled);
    }

    [Fact]
    public async Task SendAsync_WhenCanceledAfterDispatch_ReconcilesWithIndependentTimeout()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null, cts.Token);

        Assert.True(reconciliationCalled);
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenPaymentSucceedsAndDetailsLookupIsCanceled_PreservesSuccess()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null, cts.Token);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment.Status);
        Assert.Equal("test-hash", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessfulPaymentLookupStalls_CompletesAfterInternalTimeout()
    {
        var service = new BypassingValidationLightningManagerService();
        var neverCompletes = new TaskCompletionSource<LightningPayment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => neverCompletes.Task
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        var result = await service
            .SendAsync(context, "lnbcrt1test", null, null)
            .WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenPaySucceedsButLookupIsStale_ReportsComplete()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.NotNull(result.Payment);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseConfirmsFailureAndLookupIsEmpty_ReturnsFailed()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
        Assert.Equal(uint256.One.ToString(), result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenResponseIsUnknown_DoesNotTrustFailedDetails()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Failed, result.Payment!.Status);
        Assert.Equal("failed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenBlinkResponseClaimsFailureButLookupIsComplete_ReturnsSuccess()
    {
        var service = new BypassingValidationLightningManagerService();
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("Payment sent successfully.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Complete, result.Payment!.Status);
        Assert.Equal("completed-attempt", result.Payment.PaymentHash);
    }

    [Fact]
    public async Task SendAsync_WhenEclairReturnsOlderFailedAttempt_ReturnsUnknown()
    {
        var service = new BypassingValidationLightningManagerService();
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
            LightningCapabilities.Eclair,
            client,
            connectionString: "type=eclair;server=http://127.0.0.1:8080;password=test");

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. Check the Lightning node before retrying.", result.Result.Message);
        Assert.Equal(LightningPaymentStatus.Unknown, result.Payment!.Status);
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

        var result = await service.SendAsync(context, "lnbcrt1test", null, null);

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
    [InlineData("-1")]
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
    public void TryCreateOpenChannelPreview_ParsesWholeFeeRateWithInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full);
            const string nodeUri = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

            var ok = _service.TryCreateOpenChannelPreview(context, nodeUri, "25000", "2", out var preview, out var error);

            Assert.True(ok, error);
            Assert.NotNull(preview);
            Assert.Equal("2 sat/vB", preview.FeeRateDisplay);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void CreateTabs_WithPayOnlyCapabilities_HidesPeerAndChannelTabs()
    {
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.PayOnly());

        var tabs = _service.CreateTabs(context, ViewModels.LightningManagerNavPages.Send);

        Assert.True(tabs.ShowSend);
        Assert.False(tabs.ShowPeers);
        Assert.False(tabs.ShowChannels);
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
                new LightningChannel
                {
                    IsActive = true,
                    ChannelPoint = new OutPoint(uint256.One, 0)
                },
                new LightningChannel
                {
                    IsActive = false,
                    ChannelPoint = new OutPoint(uint256.One, 1)
                },
                new LightningChannel
                {
                    IsActive = true,
                    ChannelPoint = null
                }
            })
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanListChannels = true },
            client);
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(1, model.ActiveChannelsCount);
        Assert.Equal(1, model.InactiveChannelsCount);
        Assert.Equal(1, model.PendingChannelsCount);
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

        Assert.Equal(2, model.ActiveChannelsCount);
        Assert.Equal(3, model.InactiveChannelsCount);
        Assert.Equal(4, model.PendingChannelsCount);
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

        Assert.Equal(2, model.ActiveChannelsCount);
        Assert.Null(model.InactiveChannelsCount);
        Assert.Null(model.PendingChannelsCount);
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

        Assert.Equal(3, model.InactiveChannelsCount);
        Assert.Equal(3, model.PendingChannelsCount);
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
    public async Task PopulateOverviewAsync_WithNoPhoenixdChannels_PreservesZeroCounts()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                InactiveChannelsCount = 0,
                PendingChannelsCount = 0
            })
        };
        var context = TestContextFactory.CreateConfigured(
            LightningCapabilities.Phoenixd,
            client,
            connectionString: "type=phoenixd;server=https://example.test/;password=test");
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(0, model.InactiveChannelsCount);
        Assert.Equal(0, model.PendingChannelsCount);
    }

    [Fact]
    public async Task PopulateOverviewAsync_WhenChannelListingOmitsPending_PreservesGetInfoPendingCount()
    {
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => Task.FromResult(new LightningNodeInformation
            {
                ActiveChannelsCount = 5,
                InactiveChannelsCount = 6,
                PendingChannelsCount = 2
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
        var model = new ViewModels.OverviewViewModel();

        await _service.PopulateOverviewAsync(model, context);

        Assert.Equal(1, model.ActiveChannelsCount);
        Assert.Equal(1, model.InactiveChannelsCount);
        Assert.Equal(2, model.PendingChannelsCount);
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
        var service = new LightningManagerService(logger);
        var client = new FakeLightningClient
        {
            GetInfoHandler = _ => throw new InvalidOperationException("api-key=super-secret")
        };
        var context = TestContextFactory.CreateConfigured(
            new LightningCapabilities { CanGetInfo = true },
            client,
            "type=eclair;server=https://secret.example;password=super-secret");
        var model = new OverviewViewModel();

        await service.PopulateOverviewAsync(model, context);

        Assert.Contains("Could not load node information.", model.Notices);
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
        Assert.False(channel.IsPending);
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
        Assert.Equal(0m, channel.RemoteBalanceSats);
        Assert.Equal("20,000 sats", channel.LocalBalanceDisplay);
        Assert.Equal("0 sats", channel.RemoteBalanceDisplay);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithPendingChannelWithoutOutpoint_KeepsChannelList()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsActive = true,
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
        Assert.Equal("Pending", channel.ChannelPoint);
        Assert.True(channel.IsPending);
        Assert.Equal("Pending", channel.Status);
        Assert.Null(model.ChannelListMessage);
    }

    [Fact]
    public async Task PopulateChannelsAsync_WithInactiveChannelAndOutpoint_MarksInactiveNotPending()
    {
        var client = new FakeLightningClient
        {
            ListChannelsHandler = _ => Task.FromResult(new[]
            {
                new LightningChannel
                {
                    RemoteNode = new PubKey("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
                    IsActive = false,
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
        Assert.False(channel.IsPending);
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
    public async Task PopulateChannelsAsync_WhenSuccessful_LogsSanitizedMetadata()
    {
        var logger = new RecordingLogger();
        var service = new LightningManagerService(logger);
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
        Assert.DoesNotContain("30993", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenChannelAsync_WhenCanceledBeforeDispatch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var openCalled = false;
        var client = new FakeLightningClient
        {
            OpenChannelHandler = (_, token) =>
            {
                openCalled = true;
                throw new OperationCanceledException(token);
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.OpenChannelAsync(
                context,
                "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735",
                "100000",
                null,
                cts.Token));
        Assert.False(openCalled);
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
        var service = new LightningManagerService();
        Assert.NotEqual(
            firstStoreContext.BackendFingerprint,
            secondStoreContext.BackendFingerprint);
        Assert.Equal(
            firstStoreContext.BackendIdentityFingerprint,
            secondStoreContext.BackendIdentityFingerprint);
        const string nodeUri =
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

        var first = service.OpenChannelAsync(firstStoreContext, nodeUri, "100000", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ActionResultViewModel duplicate;
        try
        {
            duplicate = await service.OpenChannelAsync(secondStoreContext, nodeUri, "100000", null)
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

    private sealed class BypassingValidationLightningManagerService : LightningManagerService
    {
        public BypassingValidationLightningManagerService(
            ILogger<LightningManagerService>? logger = null,
            LightningManagerOperationGuard? operationGuard = null)
            : base(logger, operationGuard)
        {
        }

        public override bool TryCreateSendPreview(
            StoreLightningManagerContext context,
            string? bolt11,
            string? amountSats,
            string? maxFeeSats,
            out SendPreviewViewModel? preview,
            out string? error)
        {
            var maxFeeDisplay = string.IsNullOrWhiteSpace(maxFeeSats)
                ? LightningManagerDefaults.SendMaxFeeSats.ToString(CultureInfo.InvariantCulture)
                : maxFeeSats;
            preview = new SendPreviewViewModel
            {
                Bolt11 = (bolt11 ?? string.Empty).Trim(),
                PaymentAmount = LightMoney.Satoshis(1),
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
