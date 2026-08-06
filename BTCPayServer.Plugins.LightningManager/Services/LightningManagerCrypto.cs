namespace BTCPayServer.Plugins.LightningManager.Services;

internal static class LightningManagerCrypto
{
    public const string Bitcoin = "BTC";
    public const string UnsupportedMessage = "Lightning Manager only supports BTC Lightning.";

    public static bool IsSupported(string? cryptoCode)
    {
        return string.Equals(cryptoCode?.Trim(), Bitcoin, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class LightningManagerDefaults
{
    public const long SendMaxFeeSats = 10;
}
