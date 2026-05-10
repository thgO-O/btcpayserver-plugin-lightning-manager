#nullable enable
using System.Globalization;
using BTCPayServer.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.LightningManager.Services;

public interface IStoreLightningLedgerService
{
    Task PopulateStoreBalanceAsync(
        StoreBalanceViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default);
    Task PopulateStoreHistoryAsync(
        StoreHistoryViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default);
    Task<ActionResultViewModel> AddServerAdminAdjustmentAsync(
        string storeId,
        string cryptoCode,
        string? amountSats,
        string? memo,
        string? operationId,
        CancellationToken cancellationToken = default);
    Task<ManagedSendPreviewResult> CreateManagedSendPreviewAsync(
        StoreLightningManagerContext context,
        string? bolt11,
        string? maxFeeSats,
        CancellationToken cancellationToken = default);
    Task<SendExecutionResult> SendFromLedgerAsync(
        StoreLightningManagerContext context,
        string bolt11,
        string? maxFeeSats,
        CancellationToken cancellationToken = default);
    Task<bool> CreditInvoicePaymentAsync(
        string storeId,
        string cryptoCode,
        string invoiceId,
        string paymentId,
        string? paymentHash,
        long amountMSat,
        CancellationToken cancellationToken = default);
    Task ReconcilePendingSendsAsync(CancellationToken cancellationToken = default);
}

public class ManagedSendPreviewResult
{
    public bool IsSuccess { get; init; }
    public ManagedSendPreviewViewModel? Preview { get; init; }
    public string? ErrorMessage { get; init; }

    public static ManagedSendPreviewResult Success(ManagedSendPreviewViewModel preview)
    {
        return new ManagedSendPreviewResult { IsSuccess = true, Preview = preview };
    }

    public static ManagedSendPreviewResult Failure(string errorMessage)
    {
        return new ManagedSendPreviewResult { IsSuccess = false, ErrorMessage = errorMessage };
    }
}

public class StoreLightningLedgerService(
    ILightningLedgerRepository repository,
    IOptions<LightningNetworkOptions> lightningNetworkOptions) : IStoreLightningLedgerService
{
    private const int PendingSendReconciliationBatchSize = 100;
    private const int MaxHistoryLimit = 500;
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);
    private static readonly IReadOnlyDictionary<string, string> HistoryEventLabels = new Dictionary<string, string>
    {
        [LightningLedgerEntryTypes.CreditInvoicePayment] = "Received",
        [LightningLedgerEntryTypes.ReserveSend] = "Payment pending",
        [LightningLedgerEntryTypes.ReleaseReserve] = "Pending released",
        [LightningLedgerEntryTypes.DebitSendAmount] = "Paid",
        [LightningLedgerEntryTypes.DebitSendFee] = "Fee",
        [LightningLedgerEntryTypes.AdminAdjustment] = "Balance adjusted"
    };
    private static readonly IReadOnlyDictionary<string, string> HistoryStatusLabels = new Dictionary<string, string>
    {
        [LightningLedgerEntryStatuses.Pending] = LightningLedgerEntryStatuses.Pending,
        [LightningLedgerEntryStatuses.Settled] = LightningLedgerEntryStatuses.Settled,
        [LightningLedgerEntryStatuses.Void] = LightningLedgerEntryStatuses.Void
    };

    public async Task PopulateStoreBalanceAsync(
        StoreBalanceViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default)
    {
        model.IsInternalNode = context.IsInternalNode;
        model.IsServerAdmin = canModifyServerSettings;
        model.DefaultMaxFeeSats = LightningManagerDefaults.SendMaxFeeSats.ToString(CultureInfo.InvariantCulture);

        if (!context.IsConfigured)
        {
            model.BalanceMessage = context.ConfigurationError ?? "Lightning is not available for this store.";
            return;
        }

        if (!context.IsInternalNode)
        {
            model.BalanceMessage = "Pay is only available for the internal Lightning node.";
            return;
        }

        var account = await repository.GetAccountAsync(context.StoreId, context.CryptoCode, cancellationToken);
        if (account is not { Enabled: true })
        {
            model.BalanceMessage = "Lightning access is disabled for this store.";
            return;
        }

        var snapshot = await repository.GetSnapshotAsync(context.StoreId, context.CryptoCode, limit: 0, cancellationToken: cancellationToken);
        model.AccountEnabled = true;
        model.CanSendFromLedger = canModifyStoreSettings &&
                                  context.Client is not null &&
                                  context.Network is not null &&
                                  context.BackendCapabilities.CanPayBolt11;
        model.TotalBalanceDisplay = FormatMSat(snapshot.Balance.TotalMSat);
        model.AvailableBalanceDisplay = FormatMSat(snapshot.Balance.AvailableMSat);
        model.ReservedBalanceDisplay = FormatMSat(snapshot.Balance.ReservedMSat);
    }

    public async Task PopulateStoreHistoryAsync(
        StoreHistoryViewModel model,
        StoreLightningManagerContext context,
        bool canModifyStoreSettings,
        bool canModifyServerSettings,
        CancellationToken cancellationToken = default)
    {
        model.IsInternalNode = context.IsInternalNode;
        model.IsServerAdmin = canModifyServerSettings;
        PopulateHistoryFilters(model);

        if (!context.IsConfigured)
        {
            model.HistoryMessage = context.ConfigurationError ?? "Lightning is not available for this store.";
            return;
        }

        if (!context.IsInternalNode)
        {
            model.HistoryMessage = "History is only available for the internal Lightning node.";
            return;
        }

        var account = await repository.GetAccountAsync(context.StoreId, context.CryptoCode, cancellationToken);
        if (account is not { Enabled: true })
        {
            model.AccountEnabled = false;
            model.HistoryMessage = "Lightning access is disabled for this store. History is read-only.";
        }
        else
        {
            model.AccountEnabled = true;
        }

        var entries = await repository.GetEntriesAsync(
            context.StoreId,
            context.CryptoCode,
            new LightningLedgerEntryQuery
            {
                SearchTerm = model.Pager.SearchTerm,
                Type = model.EventType,
                Status = model.Status,
                Skip = model.Pager.Skip,
                Count = model.Pager.Count
            },
            cancellationToken);
        model.CanViewHistory = true;
        model.Pager.Total = entries.Total;
        model.Pager.EntryCount = entries.Entries.Count;
        foreach (var entry in entries.Entries)
        {
            model.Entries.Add(new HistoryEntryViewModel
            {
                CreatedAt = entry.CreatedAt,
                Event = ToHistoryEvent(entry.Type),
                Status = entry.Status,
                AmountDisplay = FormatSignedMSat(entry.AmountMSat),
                PendingDisplay = FormatSignedMSat(entry.ReservedMSat),
                InvoiceId = entry.InvoiceId,
                PaymentHash = entry.PaymentHash,
                Description = entry.Description
            });
        }
    }

    public Task<ActionResultViewModel> AddServerAdminAdjustmentAsync(
        string storeId,
        string cryptoCode,
        string? amountSats,
        string? memo,
        string? operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(cryptoCode))
        {
            return Task.FromResult(Failure("Select a store account."));
        }

        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return Task.FromResult(Failure(LightningManagerCrypto.UnsupportedMessage));
        }

        return AddAdminAdjustmentCoreAsync(
            storeId.Trim(),
            cryptoCode,
            amountSats,
            memo,
            operationId,
            cancellationToken);
    }

    private async Task<ActionResultViewModel> AddAdminAdjustmentCoreAsync(
        string storeId,
        string cryptoCode,
        string? amountSats,
        string? memo,
        string? operationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseSignedSats(amountSats, out var msats) || msats == 0)
        {
            return Failure("Adjustment amount must be a non-zero whole number of sats.");
        }

        if (!Guid.TryParse(operationId, out var adjustmentId))
        {
            return Failure("Adjustment operation is invalid.");
        }

        await repository.EnsureAccountAsync(storeId, cryptoCode, cancellationToken);

        var inserted = await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = storeId,
            CryptoCode = cryptoCode,
            Type = LightningLedgerEntryTypes.AdminAdjustment,
            AmountMSat = msats,
            IdempotencyKey = $"adjustment:{adjustmentId:N}",
            Description = string.IsNullOrWhiteSpace(memo) ? "Store balance adjustment" : memo.Trim()
        }, cancellationToken);

        return inserted ? Success("Store balance adjusted.") : Success("Store balance was already adjusted.");
    }

    public async Task<ManagedSendPreviewResult> CreateManagedSendPreviewAsync(
        StoreLightningManagerContext context,
        string? bolt11,
        string? maxFeeSats,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildManagedSendRequest(context, bolt11, maxFeeSats, out var request, out var error))
        {
            return ManagedSendPreviewResult.Failure(error ?? "The invoice is invalid.");
        }

        var account = await repository.GetAccountAsync(context.StoreId, context.CryptoCode, cancellationToken);
        if (account is not { Enabled: true })
        {
            return ManagedSendPreviewResult.Failure("Lightning access is disabled for this store.");
        }

        var snapshot = await repository.GetSnapshotAsync(context.StoreId, context.CryptoCode, limit: 0, cancellationToken: cancellationToken);

        if (snapshot.Balance.AvailableMSat < request!.ReservedMSat)
        {
            return ManagedSendPreviewResult.Failure("Insufficient Pay balance.");
        }

        return ManagedSendPreviewResult.Success(CreatePreview(request));
    }

    public async Task<SendExecutionResult> SendFromLedgerAsync(
        StoreLightningManagerContext context,
        string bolt11,
        string? maxFeeSats,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildManagedSendRequest(context, bolt11, maxFeeSats, out var request, out var error))
        {
            return SendFailure(error ?? "The invoice is invalid.");
        }

        var reservation = await repository.ReserveSendAsync(
            context.StoreId,
            context.CryptoCode,
            request!.PaymentHash,
            request.Bolt11,
            request.AmountMSat,
            request.FeeLimitMSat,
            cancellationToken);

        if (!reservation.IsSuccess || reservation.Value is null)
        {
            return SendFailure(reservation.ErrorMessage ?? "Could not reserve Pay balance.");
        }

        var paymentOperationToken = CancellationToken.None;
        try
        {
            var payResponse = await context.Client!.Pay(
                request.Bolt11,
                new PayInvoiceParams
                {
                    Amount = request.Amount,
                    MaxFeeFlat = Money.Satoshis(FromMSat(request.FeeLimitMSat).ToUnit(LightMoneyUnit.Satoshi))
                },
                paymentOperationToken);

            var payment = await TryLoadPaymentAsync(context.Client, request.PaymentHash, paymentOperationToken);
            var details = CreatePaymentDetails(request.PaymentHash, payment, payResponse);

            if (payResponse.Result != PayResult.Ok)
            {
                var knownResult = await ResolveKnownPaymentResultAsync(
                    reservation.Value.EntryId,
                    request.PaymentHash,
                    request,
                    payment,
                    paymentOperationToken);
                if (knownResult is not null)
                {
                    return knownResult;
                }
            }

            switch (payResponse.Result)
            {
                case PayResult.Ok:
                    if (!await repository.SettleSendReservationAsync(
                            reservation.Value.EntryId,
                            ResolvePaymentAmountMSat(request, payment),
                            ResolvePaymentFeeMSat(payment, payResponse),
                            request.PaymentHash,
                            payment?.Preimage ?? payResponse.Details?.Preimage?.ToString(),
                            paymentOperationToken))
                    {
                        return UnknownSettlementResult(request.PaymentHash, payment, payResponse);
                    }
                    return new SendExecutionResult
                    {
                        Result = Success("Payment sent."),
                        Payment = details
                    };
                case PayResult.Unknown:
                    return new SendExecutionResult
                    {
                        Result = Failure("Payment status is unknown. The pending Pay balance will remain locked until reconciliation."),
                        Payment = details
                    };
                case PayResult.CouldNotFindRoute:
                    await repository.ReleaseSendReservationAsync(
                        reservation.Value.EntryId,
                        "Release managed send reservation after route failure",
                        paymentOperationToken);
                    return SendFailure("No route to the invoice destination was found.");
                case PayResult.Error:
                    await repository.ReleaseSendReservationAsync(
                        reservation.Value.EntryId,
                        "Release managed send reservation after payment error",
                        paymentOperationToken);
                    return SendFailure(LightningPaymentErrorMessages.NormalizePayError(payResponse.ErrorDetail));
                default:
                    await repository.ReleaseSendReservationAsync(
                        reservation.Value.EntryId,
                        "Release managed send reservation after payment failure",
                        paymentOperationToken);
                    return SendFailure("The payment failed.");
            }
        }
        catch (NotSupportedException)
        {
            await repository.ReleaseSendReservationAsync(
                reservation.Value.EntryId,
                "Release managed send reservation after unsupported payment",
                paymentOperationToken);
            return SendFailure("This backend does not support sending payments.");
        }
        catch (Exception)
        {
            var payment = await TryLoadPaymentAsync(context.Client!, request.PaymentHash, paymentOperationToken);
            switch (payment?.Status)
            {
                case LightningPaymentStatus.Complete:
                    if (!await repository.SettleSendReservationAsync(
                            reservation.Value.EntryId,
                            ResolvePaymentAmountMSat(request, payment),
                            ResolvePaymentFeeMSat(payment),
                            request.PaymentHash,
                            payment.Preimage,
                            paymentOperationToken))
                    {
                        return UnknownSettlementResult(request.PaymentHash, payment, null);
                    }
                    return new SendExecutionResult
                    {
                        Result = Success("Payment sent."),
                        Payment = CreatePaymentDetails(request.PaymentHash, payment, null)
                    };
                case LightningPaymentStatus.Failed:
                    await repository.ReleaseSendReservationAsync(
                        reservation.Value.EntryId,
                        "Release managed send reservation after failed payment lookup",
                        paymentOperationToken);
                    return SendFailure("Lightning payment failed.");
                default:
                    return new SendExecutionResult
                    {
                        Result = Failure("Payment status is unknown. The pending Pay balance will remain locked until reconciliation."),
                        Payment = CreatePaymentDetails(request.PaymentHash, payment, null)
                    };
            }
        }
    }

    private async Task<SendExecutionResult?> ResolveKnownPaymentResultAsync(
        string reservationEntryId,
        string paymentHash,
        ManagedSendRequest request,
        LightningPayment? payment,
        CancellationToken cancellationToken)
    {
        switch (payment?.Status)
        {
            case LightningPaymentStatus.Complete:
                if (!await repository.SettleSendReservationAsync(
                        reservationEntryId,
                        ResolvePaymentAmountMSat(request, payment),
                        ResolvePaymentFeeMSat(payment),
                        paymentHash,
                        payment.Preimage,
                        cancellationToken))
                {
                    return UnknownSettlementResult(paymentHash, payment, null);
                }
                return new SendExecutionResult
                {
                    Result = Success("Payment sent."),
                    Payment = CreatePaymentDetails(paymentHash, payment, null)
                };
            case LightningPaymentStatus.Pending:
            case LightningPaymentStatus.Unknown:
                return new SendExecutionResult
                {
                    Result = Failure("Payment status is unknown. The pending Pay balance will remain locked until reconciliation."),
                    Payment = CreatePaymentDetails(paymentHash, payment, null)
                };
            case LightningPaymentStatus.Failed:
                await repository.ReleaseSendReservationAsync(
                    reservationEntryId,
                    "Release managed send reservation after failed payment lookup",
                    cancellationToken);
                return SendFailure("Lightning payment failed.");
            default:
                return null;
        }
    }

    public async Task<bool> CreditInvoicePaymentAsync(
        string storeId,
        string cryptoCode,
        string invoiceId,
        string paymentId,
        string? paymentHash,
        long amountMSat,
        CancellationToken cancellationToken = default)
    {
        if (amountMSat <= 0 ||
            !LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return false;
        }

        await repository.EnsureAccountAsync(storeId, cryptoCode, cancellationToken);

        return await repository.InsertEntryAsync(new LightningLedgerEntryInput
        {
            StoreId = storeId,
            CryptoCode = cryptoCode,
            Type = LightningLedgerEntryTypes.CreditInvoicePayment,
            AmountMSat = amountMSat,
            IdempotencyKey = $"invoice:{invoiceId}:payment:{paymentId}",
            InvoiceId = invoiceId,
            PaymentHash = paymentHash,
            Description = "Lightning invoice payment"
        }, cancellationToken);
    }

    public async Task ReconcilePendingSendsAsync(CancellationToken cancellationToken = default)
    {
        if (!await _reconciliationLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        try
        {
            DateTimeOffset? createdAfter = null;
            string? idAfter = null;

            while (true)
            {
                var pending = await repository.GetPendingSendReservationsAsync(
                    PendingSendReconciliationBatchSize,
                    createdAfter,
                    idAfter,
                    cancellationToken);
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var reserve in pending)
                {
                    if (string.IsNullOrWhiteSpace(reserve.PaymentHash) ||
                        !TryGetInternalLightningClient(reserve.CryptoCode, out var client))
                    {
                        continue;
                    }

                    LightningPayment? payment;
                    try
                    {
                        payment = await client.GetPayment(reserve.PaymentHash, cancellationToken);
                    }
                    catch
                    {
                        continue;
                    }

                    switch (payment?.Status)
                    {
                        case LightningPaymentStatus.Complete:
                            await repository.SettleSendReservationAsync(
                                reserve.Id,
                                ResolvePaymentAmountMSat(reserve, payment),
                                ResolvePaymentFeeMSat(payment),
                                reserve.PaymentHash,
                                payment.Preimage,
                                cancellationToken);
                            break;
                        case LightningPaymentStatus.Failed:
                            await repository.ReleaseSendReservationAsync(
                                reserve.Id,
                                "Release managed send reservation after failed reconciliation",
                                cancellationToken);
                            break;
                    }
                }

                var last = pending[^1];
                createdAfter = last.CreatedAt;
                idAfter = last.Id;

                if (pending.Count < PendingSendReconciliationBatchSize)
                {
                    break;
                }
            }
        }
        finally
        {
            _reconciliationLock.Release();
        }
    }

    protected virtual bool TryBuildManagedSendRequest(
        StoreLightningManagerContext context,
        string? bolt11,
        string? maxFeeSats,
        out ManagedSendRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!LightningManagerCrypto.IsSupported(context.CryptoCode))
        {
            error = LightningManagerCrypto.UnsupportedMessage;
            return false;
        }

        if (!context.IsConfigured || context.Client is null || context.Network is null)
        {
            error = "Lightning is not available for this store.";
            return false;
        }

        if (!context.IsInternalNode)
        {
            error = "Pay is only available for the internal Lightning node.";
            return false;
        }

        if (!context.BackendCapabilities.CanPayBolt11)
        {
            error = "BOLT11 payments are not supported by this backend.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bolt11))
        {
            error = "A BOLT11 invoice is required.";
            return false;
        }

        if (!TryParseFeeLimit(maxFeeSats, out var feeLimitMSat))
        {
            error = "Maximum fee must be a non-negative whole number of sats.";
            return false;
        }

        if (!BOLT11PaymentRequest.TryParse(bolt11.Trim(), out var paymentRequest, context.Network.NBitcoinNetwork) || paymentRequest is null)
        {
            error = "The BOLT11 invoice is invalid.";
            return false;
        }

        if (paymentRequest.MinimumAmount is null || paymentRequest.MinimumAmount == LightMoney.Zero)
        {
            error = "Amountless invoices are not supported by this interface.";
            return false;
        }

        if (paymentRequest.ExpiryDate <= DateTimeOffset.UtcNow)
        {
            error = "This invoice has already expired.";
            return false;
        }

        if (paymentRequest.PaymentHash is null)
        {
            error = "The BOLT11 invoice is missing a payment hash.";
            return false;
        }

        var amountMSat = ToMSat(paymentRequest.MinimumAmount);
        if (!TryAddMSat(amountMSat, feeLimitMSat, out _))
        {
            error = "Total reserved amount is too large.";
            return false;
        }

        request = new ManagedSendRequest
        {
            Bolt11 = bolt11.Trim(),
            Amount = paymentRequest.MinimumAmount,
            AmountMSat = amountMSat,
            FeeLimitMSat = feeLimitMSat,
            PaymentHash = paymentRequest.PaymentHash.ToString(),
            Payee = paymentRequest.GetPayeePubKey().ToString(),
            Description = paymentRequest.ShortDescription ?? "No description",
            ExpiresAt = paymentRequest.ExpiryDate
        };
        return true;
    }

    private static ManagedSendPreviewViewModel CreatePreview(ManagedSendRequest request)
    {
        return new ManagedSendPreviewViewModel
        {
            Bolt11 = request.Bolt11,
            AmountDisplay = FormatMSat(request.AmountMSat),
            MaxFeeDisplay = FormatMSat(request.FeeLimitMSat),
            ReservedDisplay = FormatMSat(request.ReservedMSat),
            Description = request.Description,
            PaymentHash = request.PaymentHash,
            Payee = request.Payee,
            ExpiresAt = request.ExpiresAt
        };
    }

    private static SendResultDetailsViewModel CreatePaymentDetails(
        string paymentHash,
        LightningPayment? payment,
        PayResponse? response)
    {
        var status = response?.Result == PayResult.Ok ? LightningPaymentStatus.Complete : LightningPaymentStatus.Unknown;
        return new SendResultDetailsViewModel
        {
            Status = payment?.Status ?? status,
            TotalAmountDisplay = FormatNullable(payment?.AmountSent ?? response?.Details?.TotalAmount),
            FeeAmountDisplay = FormatNullable(payment?.Fee ?? response?.Details?.FeeAmount),
            PaymentHash = payment?.PaymentHash ?? response?.Details?.PaymentHash?.ToString() ?? paymentHash,
            Preimage = payment?.Preimage ?? response?.Details?.Preimage?.ToString()
        };
    }

    private static SendExecutionResult UnknownSettlementResult(
        string paymentHash,
        LightningPayment? payment,
        PayResponse? response)
    {
        return new SendExecutionResult
        {
            Result = Failure("Payment status is unknown. The pending Pay balance will remain locked until reconciliation."),
            Payment = CreatePaymentDetails(paymentHash, payment, response)
        };
    }

    private static async Task<LightningPayment?> TryLoadPaymentAsync(
        ILightningClient client,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetPayment(paymentHash, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseFeeLimit(string? maxFeeSats, out long feeLimitMSat)
    {
        if (string.IsNullOrWhiteSpace(maxFeeSats))
        {
            feeLimitMSat = LightningManagerDefaults.SendMaxFeeSats * 1000;
            return true;
        }

        if (long.TryParse(
                maxFeeSats.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sats) &&
            sats >= 0 &&
            TrySatsToMSat(sats, out feeLimitMSat))
        {
            return true;
        }

        feeLimitMSat = 0;
        return false;
    }

    private static bool TryParseSignedSats(string? amountSats, out long msats)
    {
        if (long.TryParse(
                amountSats?.Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var sats) &&
            TrySatsToMSat(sats, out msats))
        {
            return true;
        }

        msats = 0;
        return false;
    }

    internal static long ToMSat(LightMoney amount)
    {
        return checked((long)amount.MilliSatoshi);
    }

    private static LightMoney FromMSat(long msats)
    {
        return new LightMoney(msats);
    }

    private static long ResolvePaymentAmountMSat(ManagedSendRequest request, LightningPayment? payment)
    {
        if (payment is not null && TryGetPaymentAmountMSat(payment, out var amountMSat))
        {
            return amountMSat;
        }

        if (payment?.AmountSent is { } amountSent)
        {
            return ToMSat(amountSent);
        }

        return request.AmountMSat;
    }

    private static long ResolvePaymentAmountMSat(LightningLedgerEntry reserve, LightningPayment payment)
    {
        if (TryGetPaymentAmountMSat(payment, out var amountMSat))
        {
            return amountMSat;
        }

        if (payment.AmountSent is { } amountSent)
        {
            return ToMSat(amountSent);
        }

        return reserve.PaymentAmountMSat ?? 0;
    }

    private static bool TryGetPaymentAmountMSat(LightningPayment payment, out long amountMSat)
    {
        if (payment.Amount is { } amount)
        {
            amountMSat = ToMSat(amount);
            return true;
        }

        if (payment.AmountSent is { } amountSent && payment.Fee is { } fee)
        {
            amountMSat = Math.Max(0, ToMSat(amountSent) - ToMSat(fee));
            return true;
        }

        amountMSat = 0;
        return false;
    }

    private static long ResolvePaymentFeeMSat(LightningPayment? payment, PayResponse? response = null)
    {
        if (payment?.Fee is { } fee)
        {
            return ToMSat(fee);
        }

        if (payment?.AmountSent is { } amountSent && payment.Amount is { } amount)
        {
            return Math.Max(0, ToMSat(amountSent) - ToMSat(amount));
        }

        return ToMSat(response?.Details?.FeeAmount ?? LightMoney.Zero);
    }

    private static bool TrySatsToMSat(long sats, out long msats)
    {
        if (sats > long.MaxValue / 1000 || sats < long.MinValue / 1000)
        {
            msats = 0;
            return false;
        }

        msats = sats * 1000;
        return true;
    }

    private static bool TryAddMSat(long left, long right, out long result)
    {
        try
        {
            result = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    internal static string FormatMSat(long msats)
    {
        return $"{FromMSat(msats).ToUnit(LightMoneyUnit.Satoshi).ToString("#,0.########", CultureInfo.InvariantCulture)} sats";
    }

    private static string FormatSignedMSat(long msats)
    {
        return msats > 0 ? $"+{FormatMSat(msats)}" : FormatMSat(msats);
    }

    private static string ToHistoryEvent(string type)
    {
        return HistoryEventLabels.TryGetValue(type, out var label) ? label : type;
    }

    private static void PopulateHistoryFilters(StoreHistoryViewModel model)
    {
        model.Pager.SearchTerm = string.IsNullOrWhiteSpace(model.Pager.SearchTerm) ? null : model.Pager.SearchTerm.Trim();
        model.Pager.Skip = Math.Max(0, model.Pager.Skip);
        model.Pager.Count = model.Pager.Count <= 0
            ? BasePagingViewModel.CountDefault
            : Math.Min(model.Pager.Count, MaxHistoryLimit);
        model.EventType = model.EventType is not null && HistoryEventLabels.ContainsKey(model.EventType)
            ? model.EventType
            : null;
        model.Status = model.Status is not null && HistoryStatusLabels.ContainsKey(model.Status)
            ? model.Status
            : null;
        var paginationQuery = new Dictionary<string, object>();
        if (model.EventType is not null)
        {
            paginationQuery["eventType"] = model.EventType;
        }
        if (model.Status is not null)
        {
            paginationQuery["status"] = model.Status;
        }
        model.Pager.PaginationQuery = paginationQuery;

        model.EventOptions.Clear();
        foreach (var option in HistoryEventLabels)
        {
            model.EventOptions.Add(new HistoryFilterOptionViewModel
            {
                Value = option.Key,
                Label = option.Value
            });
        }

        model.StatusOptions.Clear();
        foreach (var option in HistoryStatusLabels)
        {
            model.StatusOptions.Add(new HistoryFilterOptionViewModel
            {
                Value = option.Key,
                Label = option.Value
            });
        }
    }

    private static string? FormatNullable(LightMoney? amount)
    {
        return amount is null ? null : FormatMSat(ToMSat(amount));
    }

    private bool TryGetInternalLightningClient(string cryptoCode, out ILightningClient client)
    {
        if (!LightningManagerCrypto.IsSupported(cryptoCode))
        {
            client = null!;
            return false;
        }

        foreach (var pair in lightningNetworkOptions.Value.InternalLightningByCryptoCode)
        {
            if (pair.Key.Equals(cryptoCode, StringComparison.OrdinalIgnoreCase))
            {
                client = pair.Value;
                return true;
            }
        }

        client = null!;
        return false;
    }

    private static ActionResultViewModel Success(string message)
    {
        return new ActionResultViewModel { IsSuccess = true, Message = message };
    }

    private static ActionResultViewModel Failure(string message)
    {
        return new ActionResultViewModel { IsSuccess = false, Message = message };
    }

    private static SendExecutionResult SendFailure(string message)
    {
        return new SendExecutionResult { Result = Failure(message) };
    }

}

public class ManagedSendRequest
{
    public string Bolt11 { get; init; } = string.Empty;
    public LightMoney Amount { get; init; } = LightMoney.Zero;
    public long AmountMSat { get; init; }
    public long FeeLimitMSat { get; init; }
    public long ReservedMSat => checked(AmountMSat + FeeLimitMSat);
    public string PaymentHash { get; init; } = string.Empty;
    public string Payee { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}
