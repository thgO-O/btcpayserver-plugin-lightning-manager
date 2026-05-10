#nullable enable

namespace BTCPayServer.Plugins.LightningManager.Services;

internal static class LightningManagerCrypto
{
    public const string Bitcoin = "BTC";
    public const string UnsupportedMessage = "Lightning Manager only supports BTC Lightning.";

    public static bool IsSupported(string? cryptoCode)
    {
        return string.Equals(cryptoCode?.Trim(), Bitcoin, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeSupported(string? cryptoCode, out string normalized)
    {
        normalized = string.Empty;
        if (!IsSupported(cryptoCode))
        {
            return false;
        }

        normalized = Bitcoin;
        return true;
    }
}

internal static class LightningManagerDefaults
{
    public const long SendMaxFeeSats = 10;
}
