#nullable enable
namespace BTCPayServer.Plugins.LightningManager.Services;

public static class LightningLedgerEntryTypes
{
    public const string CreditInvoicePayment = nameof(CreditInvoicePayment);
    public const string ReserveSend = nameof(ReserveSend);
    public const string DebitSendAmount = nameof(DebitSendAmount);
    public const string DebitSendFee = nameof(DebitSendFee);
    public const string ReleaseReserve = nameof(ReleaseReserve);
    public const string AdminAdjustment = nameof(AdminAdjustment);
}

public static class LightningLedgerEntryStatuses
{
    public const string Pending = nameof(Pending);
    public const string Settled = nameof(Settled);
    public const string Void = nameof(Void);
}

public class LightningLedgerAccount
{
    public string StoreId { get; set; } = string.Empty;
    public string CryptoCode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class LightningLedgerEntry
{
    public string Id { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string CryptoCode { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountMSat { get; set; }
    public long ReservedMSat { get; set; }
    public long? PaymentAmountMSat { get; set; }
    public long? FeeLimitMSat { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? InvoiceId { get; set; }
    public string? PaymentHash { get; set; }
    public string? Preimage { get; set; }
    public string? Description { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
}

public class LightningLedgerBalance
{
    public long TotalMSat { get; set; }
    public long ReservedMSat { get; set; }
    public long AvailableMSat => Math.Max(0, TotalMSat - ReservedMSat);
}

public class LightningLedgerSnapshot
{
    public LightningLedgerAccount? Account { get; init; }
    public LightningLedgerBalance Balance { get; init; } = new();
    public IReadOnlyList<LightningLedgerEntry> Entries { get; init; } = [];
}

public class LightningLedgerEntryQuery
{
    public string? SearchTerm { get; init; }
    public string? Type { get; init; }
    public string? Status { get; init; }
    public int Skip { get; init; }
    public int Count { get; init; } = 50;
}

public class LightningLedgerEntryPage
{
    public IReadOnlyList<LightningLedgerEntry> Entries { get; init; } = [];
    public int Total { get; init; }
}

public class LightningLedgerAccountSnapshot
{
    public string StoreId { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public string CryptoCode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public long TotalMSat { get; set; }
    public long ReservedMSat { get; set; }
    public long AvailableMSat => Math.Max(0, TotalMSat - ReservedMSat);
}

public static class LightningManagerInvoicePaymentVerificationStatuses
{
    public const string Internal = nameof(Internal);
    public const string External = nameof(External);
    public const string Unknown = nameof(Unknown);
}

public class LightningManagerInvoicePaymentMethod
{
    public string InvoiceId { get; set; } = string.Empty;
    public string PaymentMethodId { get; set; } = string.Empty;
    public string PaymentHash { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string CryptoCode { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = LightningManagerInvoicePaymentVerificationStatuses.External;
    public bool IsInternalNode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class LightningLedgerEntryInput
{
    public required string StoreId { get; init; }
    public required string CryptoCode { get; init; }
    public required string Type { get; init; }
    public string Status { get; init; } = LightningLedgerEntryStatuses.Settled;
    public long AmountMSat { get; init; }
    public long ReservedMSat { get; init; }
    public long? PaymentAmountMSat { get; init; }
    public long? FeeLimitMSat { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? InvoiceId { get; init; }
    public string? PaymentHash { get; init; }
    public string? Preimage { get; init; }
    public string? Description { get; init; }
    public string? Metadata { get; init; }
}

public class LightningSendReservation
{
    public string EntryId { get; init; } = string.Empty;
    public string StoreId { get; init; } = string.Empty;
    public string CryptoCode { get; init; } = string.Empty;
    public string PaymentHash { get; init; } = string.Empty;
    public long PaymentAmountMSat { get; init; }
    public long FeeLimitMSat { get; init; }
    public long ReservedMSat { get; init; }
}

public class LightningLedgerOperationResult<T>
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Value { get; init; }

    public static LightningLedgerOperationResult<T> Success(T value)
    {
        return new LightningLedgerOperationResult<T> { IsSuccess = true, Value = value };
    }

    public static LightningLedgerOperationResult<T> Failure(string errorMessage)
    {
        return new LightningLedgerOperationResult<T> { IsSuccess = false, ErrorMessage = errorMessage };
    }
}
