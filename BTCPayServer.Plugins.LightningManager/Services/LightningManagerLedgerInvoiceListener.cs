#nullable enable
using BTCPayServer.Data;
using BTCPayServer.Configuration;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LightningManager.Services;

public class LightningManagerLedgerInvoiceListener(
    EventAggregator eventAggregator,
    Logs logs,
    IOptions<LightningNetworkOptions> lightningNetworkOptions,
    PaymentMethodHandlerDictionary handlers,
    InvoiceRepository invoiceRepository,
    ILightningLedgerRepository ledgerRepository,
    IStoreLightningLedgerService ledgerService)
    : EventHostedServiceBase(eventAggregator, logs)
{
    private enum InternalPromptVerification
    {
        Internal,
        External,
        Unknown
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<InvoiceNewPaymentDetailsEvent>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceNewPaymentDetailsEvent paymentDetailsEvent)
        {
            var invoice = await invoiceRepository.GetInvoice(paymentDetailsEvent.InvoiceId);
            if (invoice is not null)
            {
                await RecordInvoicePaymentMethodAsync(
                    invoice,
                    paymentDetailsEvent.PaymentMethodId,
                    paymentDetailsEvent.Details,
                    cancellationToken);
            }
            return;
        }

        if (evt is InvoiceEvent { Name: InvoiceEvent.Created } createdEvent)
        {
            await RecordInvoicePaymentMethodsAsync(createdEvent.Invoice, cancellationToken);
            return;
        }

        if (evt is not InvoiceEvent { Name: InvoiceEvent.ReceivedPayment, Payment: not null } invoiceEvent)
        {
            await base.ProcessEvent(evt, cancellationToken);
            return;
        }

        var payment = invoiceEvent.Payment;
        var paymentHash = ResolvePaymentHash(payment);
        if (string.IsNullOrWhiteSpace(paymentHash))
        {
            return;
        }

        if (!TryGetLedgerCryptoCode(payment.PaymentMethodId, out var paymentCryptoCode))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payment.Currency) &&
            !string.Equals(payment.Currency, paymentCryptoCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var recordedMethod = await ledgerRepository.GetInvoicePaymentMethodAsync(
            invoiceEvent.Invoice.Id,
            payment.PaymentMethodId.ToString(),
            paymentHash,
            cancellationToken);
        var creditMethod = await ResolveCreditMethodAsync(
            invoiceEvent.Invoice,
            payment.PaymentMethodId,
            recordedMethod,
            paymentCryptoCode,
            paymentHash,
            cancellationToken);
        if (creditMethod is null)
        {
            return;
        }

        await ledgerService.CreditInvoicePaymentAsync(
            creditMethod.StoreId,
            creditMethod.CryptoCode,
            invoiceEvent.Invoice.Id,
            payment.Id,
            paymentHash,
            StoreLightningLedgerService.ToMSat(new LightMoney(payment.Value, LightMoneyUnit.BTC)),
            cancellationToken);
    }

    internal static bool IsLedgerPaymentMethod(string cryptoCode, PaymentMethodId paymentMethodId)
    {
        return TryGetLedgerCryptoCode(paymentMethodId, out var methodCryptoCode) &&
               string.Equals(cryptoCode, methodCryptoCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RecordInvoicePaymentMethodsAsync(
        InvoiceEntity invoice,
        CancellationToken cancellationToken)
    {
        foreach (var prompt in invoice.GetPaymentPrompts())
        {
            await RecordInvoicePaymentMethodAsync(
                invoice,
                prompt.PaymentMethodId,
                prompt.Details,
                cancellationToken);
        }
    }

    private async Task RecordInvoicePaymentMethodAsync(
        InvoiceEntity invoice,
        PaymentMethodId paymentMethodId,
        object? details,
        CancellationToken cancellationToken)
    {
        if (!TryGetLedgerCryptoCode(paymentMethodId, out var cryptoCode))
        {
            return;
        }

        var promptDetails = ResolvePromptDetails(paymentMethodId, details);
        if (promptDetails is null ||
            string.IsNullOrWhiteSpace(promptDetails.PaymentHash?.ToString()))
        {
            return;
        }

        var paymentHash = promptDetails.PaymentHash.ToString();
        var verification = await VerifyInternalLightningPromptAsync(cryptoCode, promptDetails, cancellationToken);
        await ledgerRepository.RecordInvoicePaymentMethodAsync(
            invoice.Id,
            paymentMethodId.ToString(),
            paymentHash,
            invoice.StoreId,
            cryptoCode,
            ToVerificationStatus(verification),
            cancellationToken);
    }

    internal static bool TryGetLedgerCryptoCode(PaymentMethodId paymentMethodId, out string cryptoCode)
    {
        var value = paymentMethodId.ToString();
        foreach (var suffix in new[] { "-LNURL", "-LN" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return LightningManagerCrypto.TryNormalizeSupported(
                    value[..^suffix.Length],
                    out cryptoCode);
            }
        }

        cryptoCode = string.Empty;
        return false;
    }

    internal string? ResolvePromptPaymentHash(PaymentMethodId paymentMethodId, object? details)
    {
        return ResolvePromptDetails(paymentMethodId, details)?.PaymentHash?.ToString();
    }

    private LigthningPaymentPromptDetails? ResolvePromptDetails(PaymentMethodId paymentMethodId, object? details)
    {
        if (details is LigthningPaymentPromptDetails lightningDetails)
        {
            return lightningDetails;
        }

        if (details is not JToken token || !handlers.TryGetValue(paymentMethodId, out var handler))
        {
            return null;
        }

        try
        {
            return handler.ParsePaymentPromptDetails(token) as LigthningPaymentPromptDetails;
        }
        catch (Exception ex)
        {
            Logs.PayServer.LogWarning(
                ex,
                "Could not parse Lightning Manager payment prompt details for {PaymentMethodId}.",
                paymentMethodId);
            return null;
        }
    }

    internal string? ResolvePaymentHash(PaymentEntity payment)
    {
        if (handlers.TryGetValue(payment.PaymentMethodId, out var handler))
        {
            try
            {
                var paymentHash = payment.GetDetails<LightningLikePaymentData>(handler)?.PaymentHash?.ToString();
                if (!string.IsNullOrWhiteSpace(paymentHash))
                {
                    return paymentHash;
                }
            }
            catch
            {
            }
        }

        return string.IsNullOrWhiteSpace(payment.Id) ? null : payment.Id;
    }

    private async Task<InternalPromptVerification> VerifyInternalLightningPromptAsync(
        string cryptoCode,
        LigthningPaymentPromptDetails details,
        CancellationToken cancellationToken)
    {
        if (!TryGetInternalLightningClient(cryptoCode, out var client))
        {
            return InternalPromptVerification.External;
        }

        var paymentHash = details.PaymentHash?.ToString();
        var hadUnknown = false;
        if (!string.IsNullOrWhiteSpace(details.InvoiceId))
        {
            var invoiceMatch = await InternalInvoiceMatchesAsync(client, details.InvoiceId, paymentHash, cancellationToken);
            if (invoiceMatch is true)
            {
                return InternalPromptVerification.Internal;
            }
            hadUnknown |= invoiceMatch is null;
        }

        if (!string.IsNullOrWhiteSpace(paymentHash))
        {
            var hashMatch = await IsInternalPaymentHashAsync(cryptoCode, paymentHash, cancellationToken);
            if (hashMatch is true)
            {
                return InternalPromptVerification.Internal;
            }
            hadUnknown |= hashMatch is null;
        }

        if (hadUnknown)
        {
            Logs.PayServer.LogWarning(
                "Could not verify Lightning Manager invoice prompt ownership for {CryptoCode} payment hash {PaymentHash}.",
                cryptoCode,
                paymentHash);
            return InternalPromptVerification.Unknown;
        }

        return InternalPromptVerification.External;
    }

    private async Task<LightningManagerInvoicePaymentMethod?> ResolveCreditMethodAsync(
        InvoiceEntity invoice,
        PaymentMethodId paymentMethodId,
        LightningManagerInvoicePaymentMethod? recordedMethod,
        string paymentCryptoCode,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        if (recordedMethod is not null)
        {
            return await ShouldCreditRecordedMethodAsync(recordedMethod, paymentHash, cancellationToken)
                ? recordedMethod
                : null;
        }

        var prompt = invoice.GetPaymentPrompt(paymentMethodId);
        if (prompt is null)
        {
            return null;
        }

        var promptDetails = ResolvePromptDetails(paymentMethodId, prompt.Details);
        if (promptDetails is null ||
            !string.Equals(promptDetails.PaymentHash?.ToString(), paymentHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var verification = await VerifyInternalLightningPromptAsync(paymentCryptoCode, promptDetails, cancellationToken);
        await ledgerRepository.RecordInvoicePaymentMethodAsync(
            invoice.Id,
            paymentMethodId.ToString(),
            paymentHash,
            invoice.StoreId,
            paymentCryptoCode,
            ToVerificationStatus(verification),
            cancellationToken);

        return verification == InternalPromptVerification.Internal
            ? new LightningManagerInvoicePaymentMethod
            {
                InvoiceId = invoice.Id,
                PaymentMethodId = paymentMethodId.ToString(),
                PaymentHash = paymentHash,
                StoreId = invoice.StoreId,
                CryptoCode = paymentCryptoCode,
                IsInternalNode = true,
                VerificationStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal
            }
            : null;
    }

    private async Task<bool> ShouldCreditRecordedMethodAsync(
        LightningManagerInvoicePaymentMethod recordedMethod,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        var verificationStatus = ResolveVerificationStatus(recordedMethod);
        if (verificationStatus == LightningManagerInvoicePaymentVerificationStatuses.Internal &&
            !string.IsNullOrEmpty(recordedMethod.PaymentHash))
        {
            return true;
        }

        if (verificationStatus == LightningManagerInvoicePaymentVerificationStatuses.External)
        {
            return false;
        }

        if (recordedMethod.IsInternalNode &&
            !string.IsNullOrEmpty(recordedMethod.PaymentHash))
        {
            return true;
        }

        if (!recordedMethod.IsInternalNode &&
            verificationStatus != LightningManagerInvoicePaymentVerificationStatuses.Unknown)
        {
            return false;
        }

        return await IsInternalPaymentHashAsync(recordedMethod.CryptoCode, paymentHash, cancellationToken) is true;
    }

    private static string ResolveVerificationStatus(LightningManagerInvoicePaymentMethod recordedMethod)
    {
        if (recordedMethod.IsInternalNode &&
            recordedMethod.VerificationStatus == LightningManagerInvoicePaymentVerificationStatuses.External)
        {
            return LightningManagerInvoicePaymentVerificationStatuses.Internal;
        }

        return string.IsNullOrWhiteSpace(recordedMethod.VerificationStatus)
            ? LightningManagerInvoicePaymentVerificationStatuses.External
            : recordedMethod.VerificationStatus;
    }

    private async Task<bool?> IsInternalPaymentHashAsync(
        string cryptoCode,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        if (!TryGetInternalLightningClient(cryptoCode, out var client) ||
            !uint256.TryParse(paymentHash, out var hash))
        {
            return false;
        }

        try
        {
            var invoice = await client.GetInvoice(hash, cancellationToken);
            return InvoiceMatchesPaymentHash(invoice, paymentHash);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool?> InternalInvoiceMatchesAsync(
        ILightningClient client,
        string invoiceId,
        string? paymentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await client.GetInvoice(invoiceId, cancellationToken);
            return InvoiceMatchesPaymentHash(invoice, paymentHash);
        }
        catch
        {
            return null;
        }
    }

    private static string ToVerificationStatus(InternalPromptVerification verification)
    {
        return verification switch
        {
            InternalPromptVerification.Internal => LightningManagerInvoicePaymentVerificationStatuses.Internal,
            InternalPromptVerification.Unknown => LightningManagerInvoicePaymentVerificationStatuses.Unknown,
            _ => LightningManagerInvoicePaymentVerificationStatuses.External
        };
    }

    private static bool InvoiceMatchesPaymentHash(LightningInvoice? invoice, string? paymentHash)
    {
        if (invoice is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(paymentHash) &&
               !string.IsNullOrWhiteSpace(invoice.PaymentHash) &&
               string.Equals(invoice.PaymentHash, paymentHash, StringComparison.OrdinalIgnoreCase);
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

    internal Task ProcessEventForTestingAsync(object evt, CancellationToken cancellationToken = default)
    {
        return ProcessEvent(evt, cancellationToken);
    }
}
