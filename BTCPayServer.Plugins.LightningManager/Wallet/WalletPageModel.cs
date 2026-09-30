using BTCPayServer.Plugins.LightningManager.ViewModels;

namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed class WalletPageModel
{
    public string StoreId { get; set; } = "";
    public string? StoreName { get; set; }
    public bool Enabled { get; set; }
    public bool IsInternal { get; set; }
    public bool HasPasskey { get; set; }
    public decimal? BalanceSats { get; set; }
    public string? BalanceError { get; set; }
    public List<WalletOperation> Operations { get; set; } = [];
    public SendPreviewViewModel? Preview { get; set; }
    public string? ConfirmationId { get; set; }
    public WalletOperation? Operation { get; set; }
    public string? Error { get; set; }
}
