using BTCPayServer;
using BTCPayServer.Events;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Options;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerLedgerInvoiceListenerTests
{
    [Fact]
    public void IsLedgerPaymentMethod_AcceptsLightningAndLnurl()
    {
        Assert.True(LightningManagerLedgerInvoiceListener.IsLedgerPaymentMethod(
            "BTC",
            PaymentTypes.LN.GetPaymentMethodId("BTC")));
        Assert.True(LightningManagerLedgerInvoiceListener.IsLedgerPaymentMethod(
            "BTC",
            PaymentTypes.LNURL.GetPaymentMethodId("BTC")));
        Assert.False(LightningManagerLedgerInvoiceListener.IsLedgerPaymentMethod(
            "BTC",
            PaymentTypes.CHAIN.GetPaymentMethodId("BTC")));
        Assert.False(LightningManagerLedgerInvoiceListener.IsLedgerPaymentMethod(
            "LTC",
            PaymentTypes.LN.GetPaymentMethodId("LTC")));
    }

    [Theory]
    [InlineData("BTC-LN", "BTC")]
    [InlineData("btc-lnurl", "BTC")]
    public void TryGetLedgerCryptoCode_ExtractsLightningCryptoCode(string paymentMethodId, string expectedCryptoCode)
    {
        var ok = LightningManagerLedgerInvoiceListener.TryGetLedgerCryptoCode(
            PaymentMethodId.Parse(paymentMethodId),
            out var cryptoCode);

        Assert.True(ok);
        Assert.Equal(expectedCryptoCode, cryptoCode);
    }

    [Theory]
    [InlineData("LTC-LN")]
    [InlineData("ltc-lnurl")]
    public void TryGetLedgerCryptoCode_RejectsNonBtcLightning(string paymentMethodId)
    {
        var ok = LightningManagerLedgerInvoiceListener.TryGetLedgerCryptoCode(
            PaymentMethodId.Parse(paymentMethodId),
            out var cryptoCode);

        Assert.False(ok);
        Assert.Empty(cryptoCode);
    }

    [Fact]
    public async Task ReceivedPayment_WithNonBtcLightning_DoesNotCreditLedger()
    {
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("LTC").ToString(),
            PaymentHash = "payment-hash-1",
            StoreId = "store-1",
            CryptoCode = "LTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(repository, ledger);

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", "payment-hash-1", "LTC"));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithoutRecordedPaymentHash_DoesNotCreditLedger()
    {
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(new RecordingLightningLedgerRepository(), ledger);

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", "payment-hash-1"));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithExternalRecordedPaymentHash_DoesNotCreditLedger()
    {
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = "payment-hash-1",
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = false
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(repository, ledger);

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", "payment-hash-1"));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithoutRecordedPaymentHashOwnedByInternalNode_DoesNotCreditLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            new RecordingLightningLedgerRepository(),
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (paymentHash, _) => Task.FromResult(new LightningInvoice
                {
                    PaymentHash = paymentHash.ToString()
                })
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithExternalRecordedPaymentHashOwnedByInternalNode_DoesNotCreditLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = paymentHash,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = false,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.External
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (paymentHash, _) => Task.FromResult(new LightningInvoice
                {
                    PaymentHash = paymentHash.ToString()
                })
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithExternalStatusAndLegacyInternalFlag_DoesNotCreditLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = paymentHash,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.External
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (paymentHash, _) => Task.FromResult(new LightningInvoice
                {
                    PaymentHash = paymentHash.ToString()
                })
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithUnknownRecordedPaymentHashOwnedByInternalNode_CreditsLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = paymentHash,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = false,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Unknown
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (paymentHash, _) => Task.FromResult(new LightningInvoice
                {
                    PaymentHash = paymentHash.ToString()
                })
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        var credit = Assert.Single(ledger.Credits);
        Assert.Equal("store-1", credit.StoreId);
        Assert.Equal("BTC", credit.CryptoCode);
        Assert.Equal(paymentHash, credit.PaymentHash);
    }

    [Fact]
    public async Task ReceivedPayment_WithUnknownRecordedPaymentHashButInternalInvoiceHasNoHash_DoesNotCreditLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = paymentHash,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = false,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Unknown
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (_, _) => Task.FromResult(new LightningInvoice())
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithInternalRecordedPaymentHash_CreditsLedger()
    {
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = "payment-hash-1",
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(repository, ledger);

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", "payment-hash-1"));

        var credit = Assert.Single(ledger.Credits);
        Assert.Equal("store-1", credit.StoreId);
        Assert.Equal("BTC", credit.CryptoCode);
        Assert.Equal("invoice-1", credit.InvoiceId);
        Assert.Equal("payment-hash-1", credit.PaymentId);
        Assert.Equal("payment-hash-1", credit.PaymentHash);
        Assert.Equal(100_000, credit.AmountMSat);
    }

    [Fact]
    public async Task RecordInvoicePaymentMethod_WhenExistingMarkerIsInternal_KeepsInternalMarker()
    {
        var repository = new RecordingLightningLedgerRepository();
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = paymentHash,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
        });

        await repository.RecordInvoicePaymentMethodAsync(
            "invoice-1",
            PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            paymentHash,
            "store-1",
            "BTC",
            LightningManagerInvoicePaymentVerificationStatuses.Unknown);

        var marker = await repository.GetInvoicePaymentMethodAsync(
            "invoice-1",
            PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            paymentHash);
        Assert.NotNull(marker);
        Assert.True(marker!.IsInternalNode);
        Assert.Equal(LightningManagerInvoicePaymentVerificationStatuses.Internal, marker.VerificationStatus);
    }


    [Fact]
    public async Task ReceivedPayment_WithLegacyInternalMarkerNotOwnedByInternalNode_DoesNotCreditLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = string.Empty,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (_, _) => throw new NotSupportedException()
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Empty(ledger.Credits);
    }

    [Fact]
    public async Task ReceivedPayment_WithLegacyInternalMarkerOwnedByInternalNode_CreditsLedger()
    {
        const string paymentHash = "0000000000000000000000000000000000000000000000000000000000000001";
        var repository = new RecordingLightningLedgerRepository();
        repository.Mark(new LightningManagerInvoicePaymentMethod
        {
            InvoiceId = "invoice-1",
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString(),
            PaymentHash = string.Empty,
            StoreId = "store-1",
            CryptoCode = "BTC",
            IsInternalNode = true,
            VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
        });
        var ledger = new RecordingStoreLightningLedgerService();
        var listener = CreateListener(
            repository,
            ledger,
            new FakeLightningClient
            {
                GetInvoiceByPaymentHashHandler = (paymentHash, _) => Task.FromResult(new LightningInvoice
                {
                    PaymentHash = paymentHash.ToString()
                })
            });

        await listener.ProcessEventForTestingAsync(CreateReceivedPayment("invoice-1", "store-1", paymentHash));

        Assert.Single(ledger.Credits);
    }

    [Fact]
    public void ResolvePromptPaymentHash_ExtractsHashFromLightningDetails()
    {
        var listener = CreateListener(new RecordingLightningLedgerRepository(), new RecordingStoreLightningLedgerService());
        var paymentHash = uint256.Parse("0000000000000000000000000000000000000000000000000000000000000001");

        var resolved = listener.ResolvePromptPaymentHash(
            PaymentTypes.LN.GetPaymentMethodId("BTC"),
            new LigthningPaymentPromptDetails { PaymentHash = paymentHash });

        Assert.Equal(paymentHash.ToString(), resolved);
    }

    private static LightningManagerLedgerInvoiceListener CreateListener(
        ILightningLedgerRepository repository,
        IStoreLightningLedgerService ledgerService,
        ILightningClient? internalClient = null)
    {
        var logs = new Logs();
        var options = new LightningNetworkOptions();
        if (internalClient is not null)
        {
            options.InternalLightningByCryptoCode.Add("BTC", internalClient);
        }

        return new LightningManagerLedgerInvoiceListener(
            new EventAggregator(logs),
            logs,
            Options.Create(options),
            new PaymentMethodHandlerDictionary([]),
            null!,
            repository,
            ledgerService);
    }

    private static InvoiceEvent CreateReceivedPayment(
        string invoiceId,
        string storeId,
        string paymentHash,
        string cryptoCode = "BTC")
    {
        return new InvoiceEvent(
            new InvoiceEntity { Id = invoiceId, StoreId = storeId },
            InvoiceEvent.ReceivedPayment)
        {
            Payment = new PaymentEntity
            {
                Id = paymentHash,
                Currency = cryptoCode,
                PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode),
                Value = 0.000001m
            }
        };
    }

    private sealed class RecordingLightningLedgerRepository : ILightningLedgerRepository
    {
        private readonly Dictionary<(string InvoiceId, string PaymentMethodId, string PaymentHash), LightningManagerInvoicePaymentMethod> _markers = [];

        public void Mark(LightningManagerInvoicePaymentMethod marker)
        {
            _markers[(marker.InvoiceId, marker.PaymentMethodId, marker.PaymentHash)] = marker;
        }

        public Task RecordInvoicePaymentMethodAsync(
            string invoiceId,
            string paymentMethodId,
            string paymentHash,
            string storeId,
            string cryptoCode,
            string verificationStatus,
            CancellationToken cancellationToken = default)
        {
            if (_markers.TryGetValue((invoiceId, paymentMethodId, paymentHash), out var existing) &&
                existing.VerificationStatus == LightningManagerInvoicePaymentVerificationStatuses.Internal)
            {
                verificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal;
            }

            Mark(new LightningManagerInvoicePaymentMethod
            {
                InvoiceId = invoiceId,
                PaymentMethodId = paymentMethodId,
                PaymentHash = paymentHash,
                StoreId = storeId,
                CryptoCode = cryptoCode,
                VerificationStatus = verificationStatus,
                IsInternalNode = verificationStatus == LightningManagerInvoicePaymentVerificationStatuses.Internal
            });
            return Task.CompletedTask;
        }

        public Task<LightningManagerInvoicePaymentMethod?> GetInvoicePaymentMethodAsync(
            string invoiceId,
            string paymentMethodId,
            string paymentHash,
            CancellationToken cancellationToken = default)
        {
            if (!_markers.TryGetValue((invoiceId, paymentMethodId, paymentHash), out var marker))
            {
                _markers.TryGetValue((invoiceId, paymentMethodId, string.Empty), out marker);
            }
            return Task.FromResult(marker);
        }

        public Task<LightningLedgerAccount?> GetAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerAccount> EnsureAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerSnapshot> GetSnapshotAsync(string storeId, string cryptoCode, int limit = 50, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerEntryPage> GetEntriesAsync(string storeId, string cryptoCode, LightningLedgerEntryQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LightningLedgerAccountSnapshot>> GetStoreAccountSnapshotsAsync(int? limit = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<long> GetBitcoinLedgerTotalMSatAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> InsertEntryAsync(LightningLedgerEntryInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerOperationResult<LightningSendReservation>> ReserveSendAsync(string storeId, string cryptoCode, string paymentHash, string bolt11, long paymentAmountMSat, long feeLimitMSat, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> SettleSendReservationAsync(string reservationEntryId, long paymentAmountMSat, long feeMSat, string paymentHash, string? preimage, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseSendReservationAsync(string reservationEntryId, string reason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LightningLedgerEntry>> GetPendingSendReservationsAsync(int limit = 100, DateTimeOffset? createdAfter = null, string? idAfter = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class RecordingStoreLightningLedgerService : IStoreLightningLedgerService
    {
        public List<Credit> Credits { get; } = [];

        public Task<bool> CreditInvoicePaymentAsync(
            string storeId,
            string cryptoCode,
            string invoiceId,
            string paymentId,
            string? paymentHash,
            long amountMSat,
            CancellationToken cancellationToken = default)
        {
            Credits.Add(new Credit(storeId, cryptoCode, invoiceId, paymentId, paymentHash, amountMSat));
            return Task.FromResult(true);
        }

        public Task PopulateStoreBalanceAsync(StoreBalanceViewModel model, StoreLightningManagerContext context, bool canModifyStoreSettings, bool canModifyServerSettings, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task PopulateStoreHistoryAsync(StoreHistoryViewModel model, StoreLightningManagerContext context, bool canModifyStoreSettings, bool canModifyServerSettings, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResultViewModel> AddServerAdminAdjustmentAsync(string storeId, string cryptoCode, string? amountSats, string? memo, string? operationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ManagedSendPreviewResult> CreateManagedSendPreviewAsync(StoreLightningManagerContext context, string? bolt11, string? maxFeeSats, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SendExecutionResult> SendFromLedgerAsync(StoreLightningManagerContext context, string bolt11, string? maxFeeSats, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReconcilePendingSendsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed record Credit(
        string StoreId,
        string CryptoCode,
        string InvoiceId,
        string PaymentId,
        string? PaymentHash,
        long AmountMSat);
}
