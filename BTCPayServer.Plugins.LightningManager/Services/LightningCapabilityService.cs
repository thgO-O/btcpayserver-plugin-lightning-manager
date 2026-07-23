#nullable enable
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LightningManager.Services;

public interface ILightningCapabilityService
{
    LightningCapabilities GetCapabilities(string? connectionString);
}

public class LightningCapabilityService : ILightningCapabilityService
{
    private static readonly HashSet<string> FullNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        LightningBackendTypes.CLightning,
        LightningBackendTypes.LndRest,
        LightningBackendTypes.LndGrpc,
        LightningBackendTypes.Eclair
    };

    public virtual LightningCapabilities GetCapabilities(string? connectionString)
    {
        var type = LightningBackendTypes.TryGet(connectionString);
        if (string.IsNullOrEmpty(type))
        {
            return LightningCapabilities.None;
        }

        if (FullNodeTypes.Contains(type))
        {
            return LightningCapabilities.Full;
        }

        if (type.Equals(LightningBackendTypes.Phoenixd, StringComparison.OrdinalIgnoreCase))
        {
            return LightningCapabilities.Phoenixd;
        }

        if (type.Equals(LightningBackendTypes.Blink, StringComparison.OrdinalIgnoreCase))
        {
            var currency = LightningBackendTypes.TryGetValue(connectionString, "currency");
            if (currency is null || currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
            {
                return LightningCapabilities.BlinkPayOnly;
            }

            if (currency.Equals("BTC", StringComparison.OrdinalIgnoreCase))
            {
                return LightningCapabilities.BlinkBitcoin;
            }
        }

        return LightningCapabilities.None;
    }
}

internal static class LightningBackendTypes
{
    public const string CLightning = "clightning";
    public const string LndRest = "lnd-rest";
    public const string LndGrpc = "lnd-grpc";
    public const string Eclair = "eclair";
    public const string Phoenixd = "phoenixd";
    public const string Blink = "blink";

    public static string? TryGet(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part.IndexOf('=') <= 0) ||
                !parts.Any(part => part[..part.IndexOf('=')].Trim().Equals("type", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            return type;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetValue(string? connectionString, string key)
    {
        if (TryGet(connectionString) is null)
        {
            return null;
        }

        try
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString!, out _);
            return values.TryGetValue(key, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Is(string? connectionString, string type)
    {
        return string.Equals(TryGet(connectionString), type, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAny(string? connectionString, params string[] types)
    {
        var actual = TryGet(connectionString);
        return !string.IsNullOrEmpty(actual) &&
               types.Any(type => string.Equals(actual, type, StringComparison.OrdinalIgnoreCase));
    }
}
