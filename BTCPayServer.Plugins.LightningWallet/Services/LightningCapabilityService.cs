#nullable enable
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LightningWallet.Services;

public interface ILightningCapabilityService
{
    LightningCapabilities GetCapabilities(ILightningClient? client, string? connectionString, bool isInternalNode);
}

public class LightningCapabilityService : ILightningCapabilityService
{
    private static readonly HashSet<string> FullNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        LightningConnectionType.CLightning,
        LightningConnectionType.LndREST,
        LightningConnectionType.LndGRPC,
        LightningConnectionType.Eclair,
        "ldk-rest"
    };

    private static readonly HashSet<string> FullNodeTypesWithoutClose = new(StringComparer.OrdinalIgnoreCase)
    {
        LightningConnectionType.LNbank
    };

    private static readonly HashSet<string> PayFocusedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "phoenixd",
        "breez",
        "micro",
        "nwc",
        LightningConnectionType.Charge,
        LightningConnectionType.LNDhub,
        "ldk-rest"
    };

    public virtual LightningCapabilities GetCapabilities(ILightningClient? client, string? connectionString, bool isInternalNode)
    {
        if (client is null)
        {
            return LightningCapabilities.None;
        }

        if (isInternalNode)
        {
            return LightningCapabilities.Full;
        }

        var type = TryGetConnectionType(connectionString) ?? InferConnectionType(client);
        if (string.IsNullOrEmpty(type))
        {
            return LightningCapabilities.Generic;
        }

        if (FullNodeTypes.Contains(type))
        {
            return LightningCapabilities.Full;
        }

        if (FullNodeTypesWithoutClose.Contains(type))
        {
            return LightningCapabilities.FullWithoutClose;
        }

        if (type.Equals("blink", StringComparison.OrdinalIgnoreCase))
        {
            return HasBlinkUsdCurrency(connectionString)
                ? LightningCapabilities.PayOnly(canGetInfo: false, canGetBalance: false)
                : LightningCapabilities.PayOnly(canGetInfo: false);
        }

        if (PayFocusedTypes.Contains(type))
        {
            return LightningCapabilities.InfoBalancePay;
        }

        return LightningCapabilities.Generic;
    }

    private static bool HasBlinkUsdCurrency(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString, out _);
            return values.TryGetValue("currency", out var currency) &&
                   currency.Equals("USD", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetConnectionType(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (connectionString.StartsWith("nostr+walletconnect:", StringComparison.OrdinalIgnoreCase))
        {
            return "nwc";
        }

        try
        {
            LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            return type;
        }
        catch
        {
            return null;
        }
    }

    private static string? InferConnectionType(ILightningClient client)
    {
        var typeName = client.GetType().Name;
        if (typeName.Contains("Phoenixd", StringComparison.OrdinalIgnoreCase))
        {
            return "phoenixd";
        }

        if (typeName.Contains("Blink", StringComparison.OrdinalIgnoreCase))
        {
            return "blink";
        }

        if (typeName.Contains("Breez", StringComparison.OrdinalIgnoreCase))
        {
            return "breez";
        }

        if (typeName.Contains("Micro", StringComparison.OrdinalIgnoreCase))
        {
            return "micro";
        }

        if (typeName.Contains("NostrWalletConnect", StringComparison.OrdinalIgnoreCase))
        {
            return "nwc";
        }

        if (typeName.Contains("CLightning", StringComparison.OrdinalIgnoreCase))
        {
            return LightningConnectionType.CLightning;
        }

        if (typeName.Contains("Lnd", StringComparison.OrdinalIgnoreCase))
        {
            return LightningConnectionType.LndREST;
        }

        if (typeName.Contains("Eclair", StringComparison.OrdinalIgnoreCase))
        {
            return LightningConnectionType.Eclair;
        }

        if (typeName.Contains("LndHub", StringComparison.OrdinalIgnoreCase))
        {
            return LightningConnectionType.LNDhub;
        }

        if (typeName.Contains("Ldk", StringComparison.OrdinalIgnoreCase))
        {
            return "ldk-rest";
        }

        if (typeName.Contains("LNbank", StringComparison.OrdinalIgnoreCase))
        {
            return LightningConnectionType.LNbank;
        }

        return null;
    }
}
