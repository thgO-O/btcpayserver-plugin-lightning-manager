#nullable enable
using System.Security.Cryptography;
using System.Text;
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
            var apiKey = LightningBackendTypes.TryGetValue(connectionString, "api-key");
            if (string.IsNullOrEmpty(apiKey))
            {
                return LightningCapabilities.None;
            }

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
    private static readonly byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);
    private static readonly HashSet<string> EndpointIdentityTypes = new(StringComparer.Ordinal)
    {
        CLightning,
        LndRest,
        Eclair,
        Phoenixd
    };

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

    public static string GetFingerprint(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var values = LightningConnectionStringHelper.ExtractValues(connectionString, out _);
        return CreateFingerprint(
            "configuration",
            values.Where(pair => !IsImplicitDefault(pair)),
            stripServerCredentials: false);
    }

    public static string GetIdentityFingerprint(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        var normalizedType = NormalizeFingerprintValue("type", type, stripServerCredentials: false);
        if (!EndpointIdentityTypes.Contains(normalizedType))
        {
            // Hosted and unknown backends may use credentials to select a tenant.
            return GetFingerprint(connectionString);
        }

        if (values.TryGetValue("server", out var server))
        {
            values["server"] = NormalizeIdentityServer(normalizedType, server);
        }

        return CreateFingerprint(
            "identity",
            values.Where(pair => pair.Key is "type" or "server"),
            stripServerCredentials: true);
    }

    private static string NormalizeIdentityServer(string backendType, string value)
    {
        var normalized = NormalizeFingerprintValue("server", value, stripServerCredentials: true);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
        {
            return normalized;
        }

        return backendType switch
        {
            LndRest => server.AbsoluteUri.TrimEnd('/'),
            Eclair or Phoenixd => new Uri(server, ".").AbsoluteUri,
            _ => normalized
        };
    }

    private static string CreateFingerprint(
        string purpose,
        IEnumerable<KeyValuePair<string, string>> values,
        bool stripServerCredentials)
    {
        var canonical = string.Concat(
            values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var value = NormalizeFingerprintValue(
                        pair.Key,
                        pair.Value,
                        stripServerCredentials);
                    return $"{pair.Key.Length}:{pair.Key}{value.Length}:{value}";
                }));
        return Convert.ToHexString(
            HMACSHA256.HashData(
                FingerprintKey,
                Encoding.UTF8.GetBytes($"{purpose}:{canonical}")));
    }

    private static bool IsImplicitDefault(KeyValuePair<string, string> pair)
    {
        return pair.Key.Equals("allowinsecure", StringComparison.Ordinal) &&
               bool.TryParse(pair.Value, out var allowInsecure) &&
               !allowInsecure;
    }

    private static string NormalizeFingerprintValue(
        string key,
        string value,
        bool stripServerCredentials)
    {
        if (key.Equals("type", StringComparison.Ordinal))
        {
            var type = value.ToLowerInvariant();
            return type == LndGrpc ? LndRest : type;
        }

        if (key.Equals("currency", StringComparison.Ordinal))
        {
            return value.ToUpperInvariant();
        }

        if (key.Equals("allowinsecure", StringComparison.Ordinal) &&
            bool.TryParse(value, out var allowInsecure))
        {
            return allowInsecure ? "true" : "false";
        }

        if (key.Equals("macaroon", StringComparison.Ordinal) &&
            value.Length > 0 &&
            value.All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        if (key.Equals("certthumbprint", StringComparison.Ordinal))
        {
            var thumbprint = value.Replace(":", string.Empty, StringComparison.Ordinal);
            if (thumbprint.Length > 0 && thumbprint.All(Uri.IsHexDigit))
            {
                return thumbprint.ToUpperInvariant();
            }
        }

        if (key.Equals("server", StringComparison.Ordinal))
        {
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "unix:" + value;
            }
            else if (value.StartsWith("/", StringComparison.Ordinal))
            {
                value = "unix:/" + value;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var server))
            {
                return value;
            }

            if (server.Scheme == "unix")
            {
                var path = server.AbsoluteUri["unix:".Length..].TrimStart('/');
                return "unix:/" + path;
            }

            if (stripServerCredentials && server.Scheme == "tcp")
            {
                return new UriBuilder(server.Scheme, server.DnsSafeHost, server.Port)
                    .Uri
                    .AbsoluteUri;
            }

            if (stripServerCredentials &&
                server.Scheme is "http" or "https" &&
                !string.IsNullOrEmpty(server.UserInfo))
            {
                server = new UriBuilder(server)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                }.Uri;
            }

            return server.AbsoluteUri;
        }

        return value;
    }
}
