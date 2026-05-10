#nullable enable
using BTCPayServer.Lightning;
using BTCPayServer.Models;
using BTCPayServer.Plugins.LightningManager.Services;

namespace BTCPayServer.Plugins.LightningManager.ViewModels;

public static class LightningManagerNavPages
{
    public const string Server = "LightningManagerServer";
    public const string ServerSend = "LightningManagerServerSend";
    public const string ServerPeers = "LightningManagerServerPeers";
    public const string ServerChannels = "LightningManagerServerChannels";
    public const string Overview = "LightningManagerOverview";
    public const string StoreBalance = "LightningManagerStoreBalance";
    public const string History = "LightningManagerHistory";
    public const string Send = "LightningManagerSend";
    public const string Peers = "LightningManagerPeers";
    public const string Channels = "LightningManagerChannels";
}

public class LightningManagerTabsViewModel
{
    public string StoreId { get; init; } = string.Empty;
    public string CryptoCode { get; init; } = string.Empty;
    public string ActivePage { get; init; } = string.Empty;
    public bool ShowOverview { get; init; } = true;
    public bool ShowStoreBalance { get; init; }
    public bool ShowHistory { get; init; }
    public bool ShowSend { get; init; }
    public bool ShowPeers { get; init; }
    public bool ShowChannels { get; init; }
}

public class ActionResultViewModel
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
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
    public string Title { get; init; } = string.Empty;
    public LightningCapabilities Capabilities { get; init; } = LightningCapabilities.None;
    public LightningManagerTabsViewModel Tabs { get; init; } = new();
    public bool IsConfigured { get; init; }
    public string? ConfigurationMessage { get; init; }
    public string? NodeDisplayName { get; set; }
    public string? NodeHost { get; init; }
    public List<string> Notices { get; } = [];
    public ActionResultViewModel? Result { get; set; }
}

public class OverviewViewModel : LightningManagerPageViewModel
{
    public string? Alias { get; set; }
    public string? Version { get; set; }
    public int? BlockHeight { get; set; }
    public long? PeersCount { get; set; }
    public long? ActiveChannelsCount { get; set; }
    public long? InactiveChannelsCount { get; set; }
    public long? PendingChannelsCount { get; set; }
    public List<string> NodeUris { get; } = [];
    public List<ValueRowViewModel> SummaryRows { get; } = [];
    public List<ValueRowViewModel> OnchainBalanceRows { get; } = [];
    public List<ValueRowViewModel> OffchainBalanceRows { get; } = [];
}

public class SendPreviewViewModel
{
    public string Bolt11 { get; init; } = string.Empty;
    public string AmountDisplay { get; init; } = string.Empty;
    public long MaxFeeSats { get; init; }
    public string MaxFeeDisplay { get; init; } = string.Empty;
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
    public string? MaxFeeSats { get; set; }
    public string DefaultMaxFeeSats { get; set; } = LightningManagerDefaults.SendMaxFeeSats.ToString();
    public SendPreviewViewModel? Preview { get; set; }
    public SendResultDetailsViewModel? Payment { get; set; }
}

public class ManagedSendPreviewViewModel
{
    public string Bolt11 { get; init; } = string.Empty;
    public string AmountDisplay { get; init; } = string.Empty;
    public string MaxFeeDisplay { get; init; } = string.Empty;
    public string ReservedDisplay { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PaymentHash { get; init; } = string.Empty;
    public string Payee { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

public class HistoryEntryViewModel
{
    public DateTimeOffset CreatedAt { get; init; }
    public string Event { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string AmountDisplay { get; init; } = string.Empty;
    public string PendingDisplay { get; init; } = string.Empty;
    public string? InvoiceId { get; init; }
    public string? PaymentHash { get; init; }
    public string? Description { get; init; }
}

public class HistoryFilterOptionViewModel
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public class StoreBalanceViewModel : LightningManagerPageViewModel
{
    public bool IsInternalNode { get; set; }
    public bool IsServerAdmin { get; set; }
    public bool AccountEnabled { get; set; }
    public bool CanSendFromLedger { get; set; }
    public string? BalanceMessage { get; set; }
    public string TotalBalanceDisplay { get; set; } = "0 sats";
    public string AvailableBalanceDisplay { get; set; } = "0 sats";
    public string ReservedBalanceDisplay { get; set; } = "0 sats";
    public string? Bolt11 { get; set; }
    public string? MaxFeeSats { get; set; }
    public string DefaultMaxFeeSats { get; set; } = LightningManagerDefaults.SendMaxFeeSats.ToString();
    public ManagedSendPreviewViewModel? Preview { get; set; }
    public SendResultDetailsViewModel? Payment { get; set; }
}

public class StoreHistoryViewModel : LightningManagerPageViewModel
{
    public bool IsInternalNode { get; set; }
    public bool IsServerAdmin { get; set; }
    public bool AccountEnabled { get; set; }
    public bool CanViewHistory { get; set; }
    public string? HistoryMessage { get; set; }
    public string? EventType { get; set; }
    public string? Status { get; set; }
    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Pager.SearchTerm) ||
        !string.IsNullOrWhiteSpace(EventType) ||
        !string.IsNullOrWhiteSpace(Status);
    public StoreHistoryPagerViewModel Pager { get; } = new();
    public List<HistoryFilterOptionViewModel> EventOptions { get; } = [];
    public List<HistoryFilterOptionViewModel> StatusOptions { get; } = [];
    public List<HistoryEntryViewModel> Entries { get; } = [];
}

public class StoreHistoryPagerViewModel : BasePagingViewModel
{
    public int EntryCount { get; set; }
    public override int CurrentPageCount => EntryCount;
}

public class ServerLightningManagerViewModel
{
    public ActionResultViewModel? Result { get; set; }
    public string? SelectedCryptoCode { get; set; }
    public string? AdjustmentAccount { get; set; }
    public string? AdjustmentAmountSats { get; set; }
    public string? AdjustmentMemo { get; set; }
    public string AdjustmentOperationId { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> InternalCryptoCodes { get; } = [];
    public List<ServerLightningNodeViewModel> Nodes { get; } = [];
    public List<ServerLightningAccountViewModel> Accounts { get; } = [];
    public OverviewViewModel NodeOverview { get; set; } = new();
    public SendViewModel Send { get; set; } = new();
    public PeersViewModel Peers { get; set; } = new();
    public ChannelsViewModel Channels { get; set; } = new();
}

public class ServerLightningNodeViewModel
{
    public string CryptoCode { get; init; } = string.Empty;
    public string LedgerTotalDisplay { get; init; } = "0 sats";
}

public class ServerLightningAccountViewModel
{
    public string StoreId { get; init; } = string.Empty;
    public string StoreName { get; init; } = string.Empty;
    public string CryptoCode { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool CanManageAccess { get; init; }
    public string AvailableDisplay { get; init; } = "0 sats";
    public string PendingDisplay { get; init; } = "0 sats";
    public string TotalDisplay { get; init; } = "0 sats";
    public string AccountValue => $"{StoreId}|{CryptoCode}";
}

public class PeersViewModel : LightningManagerPageViewModel
{
    public string? NodeUri { get; set; }
    public string? PeerListMessage { get; set; }
}

public class OpenChannelPreviewViewModel
{
    public string NodeUri { get; init; } = string.Empty;
    public string ChannelAmountDisplay { get; init; } = string.Empty;
    public string FeeRateDisplay { get; init; } = string.Empty;
}

public class LightningChannelItemViewModel
{
    public string RemoteNode { get; init; } = string.Empty;
    public string ChannelPoint { get; init; } = string.Empty;
    public decimal CapacitySats { get; init; }
    public decimal LocalBalanceSats { get; init; }
    public decimal RemoteBalanceSats { get; init; }
    public string CapacityDisplay { get; init; } = string.Empty;
    public string LocalBalanceDisplay { get; init; } = string.Empty;
    public string RemoteBalanceDisplay { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool IsPublic { get; init; }
}

public class ChannelsViewModel : LightningManagerPageViewModel
{
    public string? NodeUri { get; set; }
    public string? ChannelAmountSats { get; set; }
    public string? FeeRateSatsPerByte { get; set; }
    public OpenChannelPreviewViewModel? Preview { get; set; }
    public List<LightningChannelItemViewModel> Channels { get; } = [];
    public string? ChannelListMessage { get; set; }
}
