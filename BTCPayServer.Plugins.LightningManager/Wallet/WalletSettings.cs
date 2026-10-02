namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed class WalletException(string message) : Exception(message);

public sealed class WalletSettings
{
    public const string Key = "LightningManager.Wallet";
    public bool Enabled { get; set; }
}
