using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;

namespace BTCPayServer.Plugins.LightningManager.ViewModels;

public static class LightningManagerNavPages
{
    public const string Overview = "LightningManagerOverview";
    public const string Send = "LightningManagerSend";
    public const string Peers = "LightningManagerPeers";
    public const string Channels = "LightningManagerChannels";
}

public class ActionResultViewModel
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class ValueRowViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public abstract class LightningManagerPageViewModel
{
    public string StoreId { get; init; } = string.Empty;
    public string CryptoCode { get; init; } = string.Empty;
    public LightningCapabilities Capabilities { get; init; } = LightningCapabilities.None;
    public bool IsConfigured { get; init; }
    public string? ConfigurationMessage { get; init; }
}

public class OverviewViewModel : LightningManagerPageViewModel
{
    public List<string> NodeUris { get; } = [];
    public List<ValueRowViewModel> SummaryRows { get; } = [];
    public List<ValueRowViewModel> OnchainBalanceRows { get; } = [];
    public List<ValueRowViewModel> OffchainBalanceRows { get; } = [];
    public List<string> Notices { get; } = [];
}

public class SendPreviewViewModel
{
    public string Bolt11 { get; init; } = string.Empty;
    public LightMoney PaymentAmount { get; init; } = LightMoney.Zero;
    public long? UserAmountSats { get; init; }
    public bool IsAmountless => UserAmountSats is not null;
    public string AmountDisplay { get; init; } = string.Empty;
    public long? MaxFeeSats { get; init; }
    public string? MaxFeeDisplay { get; init; }
    public bool UsesBackendFeePolicy => MaxFeeSats is null;
    public string Description { get; init; } = string.Empty;
    public string PaymentHash { get; init; } = string.Empty;
    public string Payee { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

public class SendResultDetailsViewModel
{
    public LightningPaymentStatus Status { get; init; }
    public string? TotalAmountDisplay { get; init; }
    public string? FeeAmountDisplay { get; init; }
    public string? PaymentHash { get; init; }
    public string? Preimage { get; init; }
}

public class SendViewModel : LightningManagerPageViewModel
{
    public string? Bolt11 { get; set; }
    public string? AmountSats { get; set; }
    public string? MaxFeeSats { get; set; }
    public SendPreviewViewModel? Preview { get; set; }
    public string? PaymentConfirmationToken { get; set; }
    public SendResultDetailsViewModel? Payment { get; set; }
    public ActionResultViewModel? Result { get; set; }
}

public class PeersViewModel : LightningManagerPageViewModel;

public class OpenChannelPreviewViewModel
{
    public string NodeUri { get; init; } = string.Empty;
    public string ChannelAmountDisplay { get; init; } = string.Empty;
    public string FeeRateDisplay { get; init; } = string.Empty;
}

public class LightningChannelItemViewModel
{
    public string RemoteNode { get; init; } = string.Empty;
    public string? ChannelPoint { get; init; }
    public decimal CapacitySats { get; init; }
    public decimal LocalBalanceSats { get; init; }
    public string CapacityDisplay { get; init; } = string.Empty;
    public string LocalBalanceDisplay { get; init; } = string.Empty;
    public string RemoteBalanceDisplay { get; init; } = string.Empty;
    public bool? IsPending { get; init; }
    public bool IsActive { get; init; }
    public bool IsPublic { get; init; }
    public string Status => IsPending is true
        ? "Pending"
        : IsActive
            ? "Active"
            : IsPending is false
                ? "Inactive"
                : "Unknown";
}

public class ChannelsViewModel : LightningManagerPageViewModel
{
    public string? NodeUri { get; set; }
    public string? ChannelAmountSats { get; set; }
    public string? FeeRateSatsPerByte { get; set; }
    public string? OpenChannelConfirmationToken { get; set; }
    public OpenChannelPreviewViewModel? Preview { get; set; }
    public List<LightningChannelItemViewModel> Channels { get; } = [];
    public string? ChannelListMessage { get; set; }
    public ActionResultViewModel? Result { get; set; }
}
