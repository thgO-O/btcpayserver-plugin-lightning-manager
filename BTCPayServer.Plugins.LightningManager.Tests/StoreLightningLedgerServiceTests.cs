using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.Options;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class StoreLightningLedgerServiceTests
{
    private const string ValidBolt11 =
        "lnbcrt20u1psd66dppp5m4ughz9keyptj80qcn35cx9w52p7gc8eyx4m6y5456jlhm04wfvsdqqcqzpgxqyz5vqsp5pdsxhsnrs69n940373fnec2zxw5yzlksnev40ejcq39lnju5lt3s9qyyssqpq760qvf46y3cch948wau8e5ym0zungnqfvdx5wruy6f0hru2pp9txtc9up2lfc439a2xuz6nvgjw40vsddhywjpc5qmm0q3dj4m3dcqxzjjeg";

    [Fact]
    public async Task EnsureAccountAsync_CreatesAccountDisabledByDefault()
    {
        var repository = new InMemoryLightningLedgerRepository();

        var account = await repository.EnsureAccountAsync("store-1", "BTC");

        Assert.False(account.Enabled);
    }

    [Fact]
    public async Task EnsureAccountAsync_WhenAccountDisabled_DoesNotReenable()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.SetAccountEnabledAsync("store-1", "BTC", false);

        var account = await repository.EnsureAccountAsync("store-1", "BTC");

        Assert.False(account.Enabled);
    }

    [Fact]
    public async Task CreditInvoicePaymentAsync_WhenDuplicated_CreditsOnce()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);

        var first = await service.CreditInvoicePaymentAsync("store-1", "BTC", "invoice-1", "payment-1", "hash-1", 50_000);
        var second = await service.CreditInvoicePaymentAsync("store-1", "BTC", "invoice-1", "payment-1", "hash-1", 50_000);

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(50_000, snapshot.Balance.TotalMSat);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task CreditInvoicePaymentAsync_WithNonBtcLightning_DoesNotCreditLedger()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);

        var credited = await service.CreditInvoicePaymentAsync("store-1", "LTC", "invoice-1", "payment-1", "hash-1", 50_000);

        var snapshot = await repository.GetSnapshotAsync("store-1", "LTC");
        Assert.False(credited);
        Assert.Null(snapshot.Account);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WithInsufficientBalance_DoesNotCallLightningClient()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = CreateService(repository, CreateRequest());
        var payCalled = false;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) =>
            {
                payCalled = true;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Insufficient Pay balance.", result.Result.Message);
        Assert.False(payCalled);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPaymentSucceeds_DebitsAmountAndReleasesReserve()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok))
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.True(result.Result.IsSuccess);
        Assert.Equal(100_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPaymentSucceeds_UsesActualPaymentAmount()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 250_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "payment-hash",
                Amount = new LightMoney(95_000),
                AmountSent = new LightMoney(97_000),
                Fee = new LightMoney(2_000),
                Preimage = "preimage"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.True(result.Result.IsSuccess);
        Assert.Equal(153_000, snapshot.Balance.TotalMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount && e.AmountMSat == -95_000);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee && e.AmountMSat == -2_000);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenOnlyAmountSentIsKnown_UsesAmountSent()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 250_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "payment-hash",
                AmountSent = new LightMoney(97_000),
                Preimage = "preimage"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.True(result.Result.IsSuccess);
        Assert.Equal(153_000, snapshot.Balance.TotalMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount && e.AmountMSat == -97_000);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenSettledAmountExceedsReservation_KeepsReservationPending()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Ok)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "payment-hash",
                Amount = new LightMoney(100_000),
                AmountSent = new LightMoney(120_000),
                Fee = new LightMoney(20_000),
                Preimage = "preimage"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. The pending Pay balance will remain locked until reconciliation.", result.Result.Message);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReserveSend &&
            e.PaymentHash == "payment-hash" &&
            e.Status == LightningLedgerEntryStatuses.Pending);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee);
    }

    [Fact]
    public async Task SettleSendReservationAsync_WhenFeeExceedsLimit_FailsWithoutSettlementEntries()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var reservation = await repository.ReserveSendAsync(
            "store-1",
            "BTC",
            "payment-hash",
            "lnbcrt1test",
            paymentAmountMSat: 100_000,
            feeLimitMSat: 10_000);
        Assert.True(reservation.IsSuccess);

        var settled = await repository.SettleSendReservationAsync(
            reservation.Value!.EntryId,
            paymentAmountMSat: 80_000,
            feeMSat: 20_000,
            paymentHash: "payment-hash",
            preimage: "preimage");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 10);
        Assert.False(settled);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend && e.Status == LightningLedgerEntryStatuses.Pending);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPaymentStatusIsUnknown_KeepsBalanceReserved()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown))
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
        Assert.Equal(90_000, snapshot.Balance.AvailableMSat);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPayReturnsUnknownButPaymentFailed_ReleasesReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Unknown)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "payment-hash"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Lightning payment failed.", result.Result.Message);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPayReturnsErrorButPaymentIsUnknown_KeepsBalanceReserved()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.Error)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Unknown,
                PaymentHash = "payment-hash"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. The pending Pay balance will remain locked until reconciliation.", result.Result.Message);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPayReturnsRouteFailureButPaymentCompleted_SettlesReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 250_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => Task.FromResult(new PayResponse(PayResult.CouldNotFindRoute)),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "payment-hash",
                Amount = new LightMoney(95_000),
                AmountSent = new LightMoney(97_000),
                Fee = new LightMoney(2_000),
                Preimage = "preimage"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.True(result.Result.IsSuccess);
        Assert.Equal(153_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount && e.AmountMSat == -95_000);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee && e.AmountMSat == -2_000);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPayThrowsAndPaymentIsUnknown_KeepsBalanceReserved()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new TimeoutException("timed out"),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Unknown,
                PaymentHash = "payment-hash"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Payment status is unknown. The pending Pay balance will remain locked until reconciliation.", result.Result.Message);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenPayThrowsAndPaymentFailed_ReleasesReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) => throw new TimeoutException("timed out"),
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "payment-hash"
            })
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
    }

    [Fact]
    public async Task SendFromLedgerAsync_AfterReleasedFailure_AllowsRetryingSameInvoice()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 300_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        var attempts = 0;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, _) =>
            {
                attempts++;
                return Task.FromResult(new PayResponse(attempts == 1 ? PayResult.CouldNotFindRoute : PayResult.Ok));
            }
        };
        var context = CreateInternalContext(client);

        var first = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");
        var second = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 20);
        Assert.False(first.Result.IsSuccess);
        Assert.True(second.Result.IsSuccess);
        Assert.Equal(2, snapshot.Entries.Count(e => e.Type == LightningLedgerEntryTypes.ReserveSend && e.PaymentHash == "payment-hash"));
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
    }

    [Fact]
    public async Task ReserveSendAsync_WithSamePaymentHashInDifferentStore_BlocksSecondReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository, "store-1");
        await EnableAccountAsync(repository, "store-2");
        foreach (var storeId in new[] { "store-1", "store-2" })
        {
            await repository.InsertEntryAsync(new LightningLedgerEntryInput
            {
                StoreId = storeId,
                CryptoCode = "BTC",
                Type = LightningLedgerEntryTypes.CreditInvoicePayment,
                AmountMSat = 200_000,
                IdempotencyKey = $"credit:{storeId}"
            });
        }

        var first = await repository.ReserveSendAsync(
            "store-1",
            "BTC",
            "payment-hash",
            "lnbcrt1test",
            100_000,
            10_000);
        var second = await repository.ReserveSendAsync(
            "store-2",
            "BTC",
            "payment-hash",
            "lnbcrt1test",
            100_000,
            10_000);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("This invoice already has a pending or settled ledger payment attempt.", second.ErrorMessage);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WhenRequestIsCanceledAfterReservation_StillReleasesReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository, CreateRequest());
        using var cts = new CancellationTokenSource();
        var payTokenWasCanceled = true;
        var client = new FakeLightningClient
        {
            PayBolt11Handler = (_, token) =>
            {
                cts.Cancel();
                payTokenWasCanceled = token.IsCancellationRequested;
                return Task.FromResult(new PayResponse(PayResult.Error));
            }
        };
        var context = CreateInternalContext(client);

        var result = await service.SendFromLedgerAsync(context, "lnbcrt1test", "10", cts.Token);

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.False(result.Result.IsSuccess);
        Assert.False(payTokenWasCanceled);
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
    }

    [Fact]
    public async Task ReconcilePendingSendsAsync_WhenPaymentFailed_ReleasesReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        await repository.ReserveSendAsync("store-1", "BTC", "payment-hash", "lnbcrt1test", 100_000, 10_000);
        var client = new FakeLightningClient
        {
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Failed,
                PaymentHash = "payment-hash"
            })
        };
        var options = new LightningNetworkOptions();
        options.InternalLightningByCryptoCode.Add("BTC", client);
        var service = CreateService(repository, options: options);

        await service.ReconcilePendingSendsAsync();

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.Equal(200_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
    }

    [Fact]
    public async Task ReconcilePendingSendsAsync_WhenPaymentComplete_UsesActualPaymentAmount()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 250_000,
            IdempotencyKey = "credit"
        });
        await repository.ReserveSendAsync("store-1", "BTC", "payment-hash", "lnbcrt1test", 100_000, 10_000);
        var client = new FakeLightningClient
        {
            GetPaymentHandler = (_, _) => Task.FromResult(new LightningPayment
            {
                Status = LightningPaymentStatus.Complete,
                PaymentHash = "payment-hash",
                Amount = new LightMoney(95_000),
                AmountSent = new LightMoney(97_000),
                Fee = new LightMoney(2_000),
                Preimage = "preimage"
            })
        };
        var options = new LightningNetworkOptions();
        options.InternalLightningByCryptoCode.Add("BTC", client);
        var service = CreateService(repository, options: options);

        await service.ReconcilePendingSendsAsync();

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        Assert.Equal(153_000, snapshot.Balance.TotalMSat);
        Assert.Equal(0, snapshot.Balance.ReservedMSat);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount && e.AmountMSat == -95_000);
        Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee && e.AmountMSat == -2_000);
    }

    [Fact]
    public async Task ReconcilePendingSendsAsync_WhenOldestBatchStaysUnknown_StillReconcilesLaterReservations()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });
        for (var i = 0; i < 100; i++)
        {
            await repository.ReserveSendAsync("store-1", "BTC", $"unknown-{i}", $"lnbcrt1unknown{i}", 1_000, 0);
        }
        await repository.ReserveSendAsync("store-1", "BTC", "later-failed", "lnbcrt1failed", 1_000, 0);
        var client = new FakeLightningClient
        {
            GetPaymentHandler = (paymentHash, _) => Task.FromResult(new LightningPayment
            {
                Status = paymentHash == "later-failed" ? LightningPaymentStatus.Failed : LightningPaymentStatus.Unknown,
                PaymentHash = paymentHash
            })
        };
        var options = new LightningNetworkOptions();
        options.InternalLightningByCryptoCode.Add("BTC", client);
        var service = CreateService(repository, options: options);

        await service.ReconcilePendingSendsAsync();

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 200);
        Assert.Contains(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReleaseReserve &&
            e.PaymentHash == "later-failed");
        Assert.DoesNotContain(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReserveSend &&
            e.PaymentHash == "later-failed" &&
            e.Status == LightningLedgerEntryStatuses.Pending);
    }

    [Fact]
    public async Task ReconcilePendingSendsAsync_WhenFirstFiveBatchesStayUnknown_StillReconcilesLaterReservations()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 600_000,
            IdempotencyKey = "credit"
        });
        for (var i = 0; i < 500; i++)
        {
            await repository.ReserveSendAsync("store-1", "BTC", $"unknown-{i}", $"lnbcrt1unknown{i}", 1_000, 0);
        }
        await repository.ReserveSendAsync("store-1", "BTC", "later-failed", "lnbcrt1failed", 1_000, 0);
        var client = new FakeLightningClient
        {
            GetPaymentHandler = (paymentHash, _) => Task.FromResult(new LightningPayment
            {
                Status = paymentHash == "later-failed" ? LightningPaymentStatus.Failed : LightningPaymentStatus.Unknown,
                PaymentHash = paymentHash
            })
        };
        var options = new LightningNetworkOptions();
        options.InternalLightningByCryptoCode.Add("BTC", client);
        var service = CreateService(repository, options: options);

        await service.ReconcilePendingSendsAsync();

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 700);
        Assert.Contains(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReleaseReserve &&
            e.PaymentHash == "later-failed");
        Assert.DoesNotContain(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReserveSend &&
            e.PaymentHash == "later-failed" &&
            e.Status == LightningLedgerEntryStatuses.Pending);
    }

    [Fact]
    public async Task ReconcilePendingSendsAsync_RevisitsOldUnknownReservationsOnNextRun()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 600_000,
            IdempotencyKey = "credit"
        });
        for (var i = 0; i < 501; i++)
        {
            await repository.ReserveSendAsync("store-1", "BTC", $"unknown-{i}", $"lnbcrt1unknown{i}", 1_000, 0);
        }

        var completeOldest = false;
        var client = new FakeLightningClient
        {
            GetPaymentHandler = (paymentHash, _) => Task.FromResult(new LightningPayment
            {
                Status = completeOldest && paymentHash == "unknown-0"
                    ? LightningPaymentStatus.Complete
                    : LightningPaymentStatus.Unknown,
                PaymentHash = paymentHash,
                Amount = new LightMoney(1_000),
                AmountSent = new LightMoney(1_000)
            })
        };
        var options = new LightningNetworkOptions();
        options.InternalLightningByCryptoCode.Add("BTC", client);
        var service = CreateService(repository, options: options);

        await service.ReconcilePendingSendsAsync();
        completeOldest = true;
        await service.ReconcilePendingSendsAsync();

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 700);
        Assert.Contains(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.DebitSendAmount &&
            e.PaymentHash == "unknown-0");
        Assert.DoesNotContain(snapshot.Entries, e =>
            e.Type == LightningLedgerEntryTypes.ReserveSend &&
            e.PaymentHash == "unknown-0" &&
            e.Status == LightningLedgerEntryStatuses.Pending);
    }

    [Fact]
    public async Task CreateManagedSendPreviewAsync_WithOverflowingFee_ReturnsValidationFailure()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = new StoreLightningLedgerService(repository, Options.Create(new LightningNetworkOptions()));
        var context = CreateInternalContext(new FakeLightningClient());

        var result = await service.CreateManagedSendPreviewAsync(context, ValidBolt11, long.MaxValue.ToString());

        Assert.False(result.IsSuccess);
        Assert.Equal("Maximum fee must be a non-negative whole number of sats.", result.ErrorMessage);
    }

    [Fact]
    public async Task CreateManagedSendPreviewAsync_WithNegativeFee_ReturnsValidationFailure()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = new StoreLightningLedgerService(repository, Options.Create(new LightningNetworkOptions()));
        var context = CreateInternalContext(new FakeLightningClient());

        var result = await service.CreateManagedSendPreviewAsync(context, ValidBolt11, "-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("Maximum fee must be a non-negative whole number of sats.", result.ErrorMessage);
    }

    [Fact]
    public async Task SendFromLedgerAsync_WithNegativeFee_ReturnsValidationFailureWithoutReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = new StoreLightningLedgerService(repository, Options.Create(new LightningNetworkOptions()));
        var payCalled = false;
        var context = CreateInternalContext(new FakeLightningClient
        {
            PayBolt11Handler = (_, _) =>
            {
                payCalled = true;
                return Task.FromResult(new PayResponse(PayResult.Ok));
            }
        });

        var result = await service.SendFromLedgerAsync(context, ValidBolt11, "-1");

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 10);
        Assert.False(result.Result.IsSuccess);
        Assert.Equal("Maximum fee must be a non-negative whole number of sats.", result.Result.Message);
        Assert.False(payCalled);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend);
    }

    [Fact]
    public async Task ReserveSendAsync_WithNegativeInputs_ReturnsValidationFailureWithoutReservation()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 200_000,
            IdempotencyKey = "credit"
        });

        var negativeFee = await repository.ReserveSendAsync(
            "store-1",
            "BTC",
            "payment-hash-fee",
            "lnbcrt1test",
            paymentAmountMSat: 100_000,
            feeLimitMSat: -1);
        var negativeAmount = await repository.ReserveSendAsync(
            "store-1",
            "BTC",
            "payment-hash-amount",
            "lnbcrt1test",
            paymentAmountMSat: -1,
            feeLimitMSat: 10_000);

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 10);
        Assert.False(negativeFee.IsSuccess);
        Assert.Equal("Fee limit must be non-negative.", negativeFee.ErrorMessage);
        Assert.False(negativeAmount.IsSuccess);
        Assert.Equal("Payment amount must be non-negative.", negativeAmount.ErrorMessage);
        Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend);
    }

    [Fact]
    public async Task AddServerAdminAdjustmentAsync_WithOverflowingSats_ReturnsValidationFailure()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);

        var result = await service.AddServerAdminAdjustmentAsync(
            "store-2",
            "BTC",
            long.MaxValue.ToString(),
            "too large",
            Guid.NewGuid().ToString("N"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Adjustment amount must be a non-zero whole number of sats.", result.Message);
    }

    [Fact]
    public async Task AddServerAdminAdjustmentAsync_WithStoreAccount_DoesNotRequireConfiguredStoreContext()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);

        var result = await service.AddServerAdminAdjustmentAsync(
            "store-2",
            "btc",
            "1234",
            "opening balance",
            Guid.NewGuid().ToString("N"));

        var snapshot = await repository.GetSnapshotAsync("store-2", "BTC");
        Assert.True(result.IsSuccess);
        Assert.Equal(1_234_000, snapshot.Balance.TotalMSat);
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(LightningLedgerEntryTypes.AdminAdjustment, entry.Type);
        Assert.Equal("opening balance", entry.Description);
    }

    [Fact]
    public async Task AddServerAdminAdjustmentAsync_WithNonBtcLightning_ReturnsValidationFailure()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);

        var result = await service.AddServerAdminAdjustmentAsync(
            "store-2",
            "LTC",
            "1234",
            "opening balance",
            Guid.NewGuid().ToString("N"));

        var snapshot = await repository.GetSnapshotAsync("store-2", "LTC");
        Assert.False(result.IsSuccess);
        Assert.Equal(LightningManagerCrypto.UnsupportedMessage, result.Message);
        Assert.Null(snapshot.Account);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task AddServerAdminAdjustmentAsync_WithSameOperationId_CreditsOnce()
    {
        var repository = new InMemoryLightningLedgerRepository();
        var service = CreateService(repository);
        var operationId = Guid.NewGuid().ToString("N");

        var first = await service.AddServerAdminAdjustmentAsync("store-2", "BTC", "1234", "opening balance", operationId);
        var second = await service.AddServerAdminAdjustmentAsync("store-2", "BTC", "1234", "opening balance", operationId);

        var snapshot = await repository.GetSnapshotAsync("store-2", "BTC");
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("Store balance was already adjusted.", second.Message);
        Assert.Equal(1_234_000, snapshot.Balance.TotalMSat);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task PopulateStoreBalanceAsync_WhenAdmin_DoesNotLoadNodeWideBalance()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = CreateService(repository);
        var getBalanceCalled = false;
        var client = new FakeLightningClient
        {
            GetBalanceHandler = _ =>
            {
                getBalanceCalled = true;
                return Task.FromResult(new LightningNodeBalance(null, null));
            }
        };
        var context = CreateInternalContext(client);
        var model = new ViewModels.StoreBalanceViewModel();

        await service.PopulateStoreBalanceAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: true);

        Assert.True(model.IsServerAdmin);
        Assert.True(model.AccountEnabled);
        Assert.False(getBalanceCalled);
    }

    [Fact]
    public async Task PopulateStoreBalanceAsync_WhenStoreOwner_CanSendButIsNotAdmin()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreBalanceViewModel();

        await service.PopulateStoreBalanceAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        Assert.False(model.IsServerAdmin);
        Assert.True(model.AccountEnabled);
        Assert.True(model.CanSendFromLedger);
    }

    [Fact]
    public async Task PopulateStoreBalanceAsync_WhenAccountDisabled_BlocksStorePay()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.SetAccountEnabledAsync("store-1", "BTC", false);
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreBalanceViewModel();

        await service.PopulateStoreBalanceAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        var account = await repository.EnsureAccountAsync("store-1", "BTC");
        Assert.False(account.Enabled);
        Assert.False(model.AccountEnabled);
        Assert.False(model.CanSendFromLedger);
        Assert.Equal("Lightning access is disabled for this store.", model.BalanceMessage);
    }

    [Fact]
    public async Task PopulateStoreHistoryAsync_WhenAccountDisabled_DoesNotReenable()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.SetAccountEnabledAsync("store-1", "BTC", false);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 10_000,
            IdempotencyKey = "credit",
            InvoiceId = "invoice-1"
        });
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreHistoryViewModel();

        await service.PopulateStoreHistoryAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        var account = await repository.EnsureAccountAsync("store-1", "BTC");
        Assert.False(account.Enabled);
        Assert.False(model.AccountEnabled);
        Assert.True(model.CanViewHistory);
        Assert.Equal("Lightning access is disabled for this store. History is read-only.", model.HistoryMessage);
        Assert.Contains(model.Entries, e => e.InvoiceId == "invoice-1");
    }

    [Fact]
    public async Task CreditInvoicePaymentAsync_WhenAccountDisabled_StillCreditsWithoutReenabling()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.SetAccountEnabledAsync("store-1", "BTC", false);
        var service = CreateService(repository);

        var credited = await service.CreditInvoicePaymentAsync(
            "store-1",
            "BTC",
            "invoice-1",
            "payment-1",
            "hash-1",
            10_000_000);

        var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");
        var account = await repository.EnsureAccountAsync("store-1", "BTC");
        Assert.True(credited);
        Assert.False(account.Enabled);
        Assert.Equal(10_000_000, snapshot.Balance.TotalMSat);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task PopulateStoreBalanceAsync_FormatsBalancesWithEnglishThousandsSeparators()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 1_678_331_496,
            IdempotencyKey = "credit"
        });
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreBalanceViewModel();

        await service.PopulateStoreBalanceAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        Assert.Equal("1,678,331.496 sats", model.AvailableBalanceDisplay);
        Assert.Equal("1,678,331.496 sats", model.TotalBalanceDisplay);
    }

    [Fact]
    public void StoreBalanceViewModel_DoesNotExposeHistoryEntries()
    {
        Assert.Null(typeof(ViewModels.StoreBalanceViewModel).GetProperty("Entries"));
    }

    [Fact]
    public async Task PopulateStoreHistoryAsync_HumanizesEntriesAndFormatsAmounts()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 10_000_000,
            IdempotencyKey = "credit",
            InvoiceId = "invoice-1",
            PaymentHash = "hash-1",
            Description = "Lightning invoice payment"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.ReserveSend,
            Status = LightningLedgerEntryStatuses.Pending,
            ReservedMSat = 11_000_000,
            IdempotencyKey = "reserve",
            PaymentHash = "hash-2",
            Description = "Managed Lightning send reservation"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.ReleaseReserve,
            ReservedMSat = -11_000_000,
            IdempotencyKey = "release",
            PaymentHash = "hash-2"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.DebitSendAmount,
            AmountMSat = -10_000_000,
            IdempotencyKey = "amount",
            PaymentHash = "hash-2"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.DebitSendFee,
            AmountMSat = -1_000,
            IdempotencyKey = "fee",
            PaymentHash = "hash-2"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.AdminAdjustment,
            AmountMSat = 5_000_000,
            IdempotencyKey = "adjustment",
            Description = "manual correction"
        });
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreHistoryViewModel();

        await service.PopulateStoreHistoryAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        Assert.True(model.AccountEnabled);
        Assert.True(model.CanViewHistory);
        Assert.Contains(model.Entries, e => e.Event == "Received" && e.AmountDisplay == "+10,000 sats" && e.InvoiceId == "invoice-1");
        Assert.Contains(model.Entries, e => e.Event == "Payment pending" && e.PendingDisplay == "+11,000 sats" && e.Status == LightningLedgerEntryStatuses.Pending);
        Assert.Contains(model.Entries, e => e.Event == "Pending released" && e.PendingDisplay == "-11,000 sats");
        Assert.Contains(model.Entries, e => e.Event == "Paid" && e.AmountDisplay == "-10,000 sats");
        Assert.Contains(model.Entries, e => e.Event == "Fee" && e.AmountDisplay == "-1 sats");
        Assert.Contains(model.Entries, e => e.Event == "Balance adjusted" && e.AmountDisplay == "+5,000 sats" && e.Description == "manual correction");
    }

    [Fact]
    public async Task PopulateStoreHistoryAsync_AppliesSearchEventStatusAndPaging()
    {
        var repository = new InMemoryLightningLedgerRepository();
        await EnableAccountAsync(repository);
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 10_000_000,
            IdempotencyKey = "credit-apple",
            InvoiceId = "invoice-apple",
            Description = "Apple order"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = 20_000_000,
            IdempotencyKey = "credit-orange",
            InvoiceId = "invoice-orange",
            Description = "Orange order"
        });
        await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Type = LightningLedgerEntryTypes.ReserveSend,
            Status = LightningLedgerEntryStatuses.Pending,
            ReservedMSat = 5_000_000,
            IdempotencyKey = "reserve-apple",
            PaymentHash = "hash-apple",
            Description = "Apple payout"
        });
        var service = CreateService(repository);
        var context = CreateInternalContext(new FakeLightningClient());
        var model = new ViewModels.StoreHistoryViewModel
        {
            EventType = LightningLedgerEntryTypes.ReserveSend,
            Status = LightningLedgerEntryStatuses.Pending
        };
        model.Pager.SearchTerm = " apple ";
        model.Pager.Count = 1;

        await service.PopulateStoreHistoryAsync(
            model,
            context,
            canModifyStoreSettings: true,
            canModifyServerSettings: false);

        var entry = Assert.Single(model.Entries);
        Assert.Equal("apple", model.Pager.SearchTerm);
        Assert.Equal(1, model.Pager.Total);
        Assert.Equal(1, model.Pager.EntryCount);
        Assert.Equal("Payment pending", entry.Event);
        Assert.Equal("hash-apple", entry.PaymentHash);
        Assert.Equal("+5,000 sats", entry.PendingDisplay);
        Assert.Contains(model.EventOptions, option => option.Value == LightningLedgerEntryTypes.ReserveSend && option.Label == "Payment pending");
        Assert.Contains(model.StatusOptions, option => option.Value == LightningLedgerEntryStatuses.Pending);
    }

    private static TestStoreLightningLedgerService CreateService(
        InMemoryLightningLedgerRepository repository,
        ManagedSendRequest? request = null,
        LightningNetworkOptions? options = null)
    {
        return new TestStoreLightningLedgerService(
            repository,
            Options.Create(options ?? new LightningNetworkOptions()),
            request);
    }

    private static async Task<LightningLedgerAccount> EnableAccountAsync(
        InMemoryLightningLedgerRepository repository,
        string storeId = "store-1",
        string cryptoCode = "BTC")
    {
        var account = await repository.EnsureAccountAsync(storeId, cryptoCode);
        await repository.SetAccountEnabledAsync(storeId, cryptoCode, true);
        return account;
    }

    private static ManagedSendRequest CreateRequest()
    {
        return new ManagedSendRequest
        {
            Bolt11 = "lnbcrt1test",
            Amount = new LightMoney(100_000),
            AmountMSat = 100_000,
            FeeLimitMSat = 10_000,
            PaymentHash = "payment-hash",
            Payee = "payee",
            Description = "test",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
    }

    private static StoreLightningManagerContext CreateInternalContext(ILightningClient client)
    {
        return TestContextFactory.CreateConfigured(
            LightningCapabilities.None,
            client,
            isInternalNode: true,
            isSharedBackend: true,
            isReadOnly: true,
            backendCapabilities: LightningCapabilities.Full);
    }

    private sealed class TestStoreLightningLedgerService(
        ILightningLedgerRepository repository,
        IOptions<LightningNetworkOptions> options,
        ManagedSendRequest? request)
        : StoreLightningLedgerService(repository, options)
    {
        protected override bool TryBuildManagedSendRequest(
            StoreLightningManagerContext context,
            string? bolt11,
            string? maxFeeSats,
            out ManagedSendRequest? managedSendRequest,
            out string? error)
        {
            managedSendRequest = request;
            error = request is null ? "invalid" : null;
            return request is not null;
        }
    }

    private sealed class InMemoryLightningLedgerRepository : ILightningLedgerRepository
    {
        private readonly Dictionary<(string StoreId, string CryptoCode), LightningLedgerAccount> _accounts = [];
        private readonly Dictionary<(string InvoiceId, string PaymentMethodId, string PaymentHash), LightningManagerInvoicePaymentMethod> _invoicePaymentMethods = [];
        private readonly List<LightningLedgerEntry> _entries = [];
        private long _createdAtTicks;

        public Task<LightningLedgerAccount?> GetAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default)
        {
            _accounts.TryGetValue((storeId, cryptoCode), out var account);
            return Task.FromResult(account);
        }

        public Task<LightningLedgerAccount> EnsureAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default)
        {
            var key = (storeId, cryptoCode);
            if (!_accounts.TryGetValue(key, out var account))
            {
                account = new LightningLedgerAccount
                {
                    StoreId = storeId,
                    CryptoCode = cryptoCode,
                    Enabled = false,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _accounts.Add(key, account);
            }

            account.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(account);
        }

        public Task<bool> SetAccountEnabledAsync(
            string storeId,
            string cryptoCode,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            if (!_accounts.TryGetValue((storeId, cryptoCode), out var account))
            {
                return Task.FromResult(false);
            }

            account.Enabled = enabled;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }

        public Task<LightningLedgerSnapshot> GetSnapshotAsync(string storeId, string cryptoCode, int limit = 50, CancellationToken cancellationToken = default)
        {
            _accounts.TryGetValue((storeId, cryptoCode), out var account);
            var matchingRows = _entries
                .Where(e => e.StoreId == storeId && e.CryptoCode == cryptoCode)
                .ToList();
            var rows = matchingRows
                .OrderByDescending(e => e.CreatedAt)
                .Take(limit <= 0 ? 0 : limit)
                .ToList();
            return Task.FromResult(new LightningLedgerSnapshot
            {
                Account = account,
                Balance = Balance(matchingRows),
                Entries = rows
            });
        }

        public Task<LightningLedgerEntryPage> GetEntriesAsync(
            string storeId,
            string cryptoCode,
            LightningLedgerEntryQuery query,
            CancellationToken cancellationToken = default)
        {
            var rows = _entries
                .Where(e => e.StoreId == storeId && e.CryptoCode == cryptoCode);

            if (!string.IsNullOrWhiteSpace(query.Type))
            {
                rows = rows.Where(e => e.Type == query.Type);
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                rows = rows.Where(e => e.Status == query.Status);
            }

            if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            {
                rows = rows.Where(e =>
                    Contains(e.InvoiceId, query.SearchTerm) ||
                    Contains(e.PaymentHash, query.SearchTerm) ||
                    Contains(e.Description, query.SearchTerm) ||
                    Contains(e.Metadata, query.SearchTerm) ||
                    Contains(e.IdempotencyKey, query.SearchTerm));
            }

            var filtered = rows.ToList();
            return Task.FromResult(new LightningLedgerEntryPage
            {
                Total = filtered.Count,
                Entries = filtered
                .OrderByDescending(e => e.CreatedAt)
                .Skip(Math.Max(0, query.Skip))
                .Take(Math.Max(1, query.Count))
                .ToList()
            });
        }

        public Task<long> GetBitcoinLedgerTotalMSatAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entries
                .Where(e => e.CryptoCode == LightningManagerCrypto.Bitcoin && e.Status != LightningLedgerEntryStatuses.Void)
                .Sum(e => e.AmountMSat));
        }

        public Task<IReadOnlyList<LightningLedgerAccountSnapshot>> GetStoreAccountSnapshotsAsync(int? limit = null, CancellationToken cancellationToken = default)
        {
            var rows = _accounts
                .Values
                .Where(account => account.CryptoCode == LightningManagerCrypto.Bitcoin)
                .OrderBy(account => account.CryptoCode)
                .ThenBy(account => account.StoreId)
                .Select(account =>
                {
                    var balance = Balance(_entries.Where(entry => entry.StoreId == account.StoreId && entry.CryptoCode == account.CryptoCode));
                    return new LightningLedgerAccountSnapshot
                    {
                        StoreId = account.StoreId,
                        StoreName = account.StoreId,
                        CryptoCode = account.CryptoCode,
                        Enabled = account.Enabled,
                        TotalMSat = balance.TotalMSat,
                        ReservedMSat = balance.ReservedMSat
                    };
                })
                .ToList();

            if (limit is not null)
            {
                rows = rows.Take(limit.Value).ToList();
            }

            return Task.FromResult<IReadOnlyList<LightningLedgerAccountSnapshot>>(rows);
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
            var now = DateTimeOffset.UtcNow;
            _invoicePaymentMethods[(invoiceId, paymentMethodId, paymentHash)] = new LightningManagerInvoicePaymentMethod
            {
                InvoiceId = invoiceId,
                PaymentMethodId = paymentMethodId,
                PaymentHash = paymentHash,
                StoreId = storeId,
                CryptoCode = cryptoCode,
                VerificationStatus = verificationStatus,
                IsInternalNode = verificationStatus == LightningManagerInvoicePaymentVerificationStatuses.Internal,
                CreatedAt = now,
                UpdatedAt = now
            };
            return Task.CompletedTask;
        }

        public Task<LightningManagerInvoicePaymentMethod?> GetInvoicePaymentMethodAsync(
            string invoiceId,
            string paymentMethodId,
            string paymentHash,
            CancellationToken cancellationToken = default)
        {
            _invoicePaymentMethods.TryGetValue((invoiceId, paymentMethodId, paymentHash), out var method);
            return Task.FromResult(method);
        }

        public Task<bool> InsertEntryAsync(LightningLedgerEntryInput input, CancellationToken cancellationToken = default)
        {
            if (_entries.Any(e => e.StoreId == input.StoreId && e.CryptoCode == input.CryptoCode && e.IdempotencyKey == input.IdempotencyKey))
            {
                return Task.FromResult(false);
            }

            _entries.Add(ToEntry(input));
            return Task.FromResult(true);
        }

        public Task<LightningLedgerOperationResult<LightningSendReservation>> ReserveSendAsync(
            string storeId,
            string cryptoCode,
            string paymentHash,
            string bolt11,
            long paymentAmountMSat,
            long feeLimitMSat,
            CancellationToken cancellationToken = default)
        {
            var account = EnsureAccountAsync(storeId, cryptoCode, cancellationToken).GetAwaiter().GetResult();
            if (!account.Enabled)
            {
                return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Failure("Lightning access is disabled for this store."));
            }

            if (paymentAmountMSat < 0)
            {
                return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Failure("Payment amount must be non-negative."));
            }

            if (feeLimitMSat < 0)
            {
                return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Failure("Fee limit must be non-negative."));
            }

            var reservedMSat = paymentAmountMSat + feeLimitMSat;
            var balance = Balance(_entries.Where(e => e.StoreId == storeId && e.CryptoCode == cryptoCode));
            if (balance.AvailableMSat < reservedMSat)
            {
                return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Failure("Insufficient Pay balance."));
            }

            if (_entries.Any(e =>
                    e.CryptoCode == cryptoCode &&
                    e.PaymentHash == paymentHash &&
                    ((e.Type == LightningLedgerEntryTypes.ReserveSend && e.Status == LightningLedgerEntryStatuses.Pending) ||
                     e.Type == LightningLedgerEntryTypes.DebitSendAmount)))
            {
                return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Failure("This invoice already has a pending or settled ledger payment attempt."));
            }

            var entry = ToEntry(new LightningLedgerEntryInput
            {
                StoreId = storeId,
                CryptoCode = cryptoCode,
                Type = LightningLedgerEntryTypes.ReserveSend,
                Status = LightningLedgerEntryStatuses.Pending,
                ReservedMSat = reservedMSat,
                PaymentAmountMSat = paymentAmountMSat,
                FeeLimitMSat = feeLimitMSat,
                IdempotencyKey = $"send:{paymentHash}:{Guid.NewGuid():N}:reserve",
                PaymentHash = paymentHash,
                Metadata = bolt11
            });
            _entries.Add(entry);
            return Task.FromResult(LightningLedgerOperationResult<LightningSendReservation>.Success(new LightningSendReservation
            {
                EntryId = entry.Id,
                StoreId = storeId,
                CryptoCode = cryptoCode,
                PaymentHash = paymentHash,
                PaymentAmountMSat = paymentAmountMSat,
                FeeLimitMSat = feeLimitMSat,
                ReservedMSat = reservedMSat
            }));
        }

        public Task<bool> SettleSendReservationAsync(
            string reservationEntryId,
            long paymentAmountMSat,
            long feeMSat,
            string paymentHash,
            string? preimage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reserve = _entries.SingleOrDefault(e => e.Id == reservationEntryId && e.Status == LightningLedgerEntryStatuses.Pending);
            if (reserve is null)
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(reserve.PaymentHash, paymentHash, StringComparison.OrdinalIgnoreCase) ||
                paymentAmountMSat < 0 ||
                feeMSat < 0 ||
                paymentAmountMSat > long.MaxValue - feeMSat ||
                paymentAmountMSat + feeMSat > reserve.ReservedMSat ||
                reserve.FeeLimitMSat is null ||
                feeMSat > reserve.FeeLimitMSat.Value)
            {
                return Task.FromResult(false);
            }

            _entries.Add(ToEntry(new LightningLedgerEntryInput
            {
                StoreId = reserve.StoreId,
                CryptoCode = reserve.CryptoCode,
                Type = LightningLedgerEntryTypes.ReleaseReserve,
                ReservedMSat = -reserve.ReservedMSat,
                IdempotencyKey = $"send:{paymentHash}:{reservationEntryId}:release",
                PaymentHash = paymentHash
            }));
            _entries.Add(ToEntry(new LightningLedgerEntryInput
            {
                StoreId = reserve.StoreId,
                CryptoCode = reserve.CryptoCode,
                Type = LightningLedgerEntryTypes.DebitSendAmount,
                AmountMSat = -paymentAmountMSat,
                IdempotencyKey = $"send:{paymentHash}:{reservationEntryId}:amount",
                PaymentHash = paymentHash,
                Preimage = preimage
            }));
            if (feeMSat > 0)
            {
                _entries.Add(ToEntry(new LightningLedgerEntryInput
                {
                    StoreId = reserve.StoreId,
                    CryptoCode = reserve.CryptoCode,
                    Type = LightningLedgerEntryTypes.DebitSendFee,
                    AmountMSat = -feeMSat,
                    IdempotencyKey = $"send:{paymentHash}:{reservationEntryId}:fee",
                    PaymentHash = paymentHash
                }));
            }

            reserve.Status = LightningLedgerEntryStatuses.Settled;
            reserve.SettledAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }

        public Task ReleaseSendReservationAsync(string reservationEntryId, string reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reserve = _entries.SingleOrDefault(e => e.Id == reservationEntryId && e.Status == LightningLedgerEntryStatuses.Pending);
            if (reserve is null)
            {
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(reserve.PaymentHash))
            {
                return Task.CompletedTask;
            }

            _entries.Add(ToEntry(new LightningLedgerEntryInput
            {
                StoreId = reserve.StoreId,
                CryptoCode = reserve.CryptoCode,
                Type = LightningLedgerEntryTypes.ReleaseReserve,
                ReservedMSat = -reserve.ReservedMSat,
                IdempotencyKey = $"send:{reserve.PaymentHash}:{reservationEntryId}:release",
                PaymentHash = reserve.PaymentHash,
                Description = reason
            }));
            reserve.Status = LightningLedgerEntryStatuses.Settled;
            reserve.SettledAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LightningLedgerEntry>> GetPendingSendReservationsAsync(
            int limit = 100,
            DateTimeOffset? createdAfter = null,
            string? idAfter = null,
            CancellationToken cancellationToken = default)
        {
            var rows = _entries
                .Where(e => e.Type == LightningLedgerEntryTypes.ReserveSend && e.Status == LightningLedgerEntryStatuses.Pending)
                .Where(e =>
                    createdAfter is null ||
                    e.CreatedAt > createdAfter ||
                    (e.CreatedAt == createdAfter && string.CompareOrdinal(e.Id, idAfter) > 0))
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<LightningLedgerEntry>>(rows);
        }

        private static LightningLedgerBalance Balance(IEnumerable<LightningLedgerEntry> rows)
        {
            var included = rows.Where(e => e.Status != LightningLedgerEntryStatuses.Void).ToArray();
            return new LightningLedgerBalance
            {
                TotalMSat = included.Sum(e => e.AmountMSat),
                ReservedMSat = Math.Max(0, included.Sum(e => e.ReservedMSat))
            };
        }

        private LightningLedgerEntry ToEntry(LightningLedgerEntryInput input)
        {
            var now = DateTimeOffset.UnixEpoch.AddTicks(++_createdAtTicks);
            return new LightningLedgerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                StoreId = input.StoreId,
                CryptoCode = input.CryptoCode,
                Type = input.Type,
                Status = input.Status,
                AmountMSat = input.AmountMSat,
                ReservedMSat = input.ReservedMSat,
                PaymentAmountMSat = input.PaymentAmountMSat,
                FeeLimitMSat = input.FeeLimitMSat,
                IdempotencyKey = input.IdempotencyKey,
                InvoiceId = input.InvoiceId,
                PaymentHash = input.PaymentHash,
                Preimage = input.Preimage,
                Description = input.Description,
                Metadata = input.Metadata,
                CreatedAt = now,
                SettledAt = input.Status == LightningLedgerEntryStatuses.Pending ? null : now
            };
        }

        private static bool Contains(string? value, string search)
        {
            return value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
        }
    }
}
