using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LightningManager.Services;

public static class LightningCapabilityService
{
    public static LightningCapabilities GetCapabilities(string? connectionString)
    {
        var type = LightningBackendTypes.TryGet(connectionString);
        if (string.IsNullOrEmpty(type))
        {
            return LightningCapabilities.None;
        }

        if (type is
            LightningBackendTypes.CLightning or
            LightningBackendTypes.LndRest or
            LightningBackendTypes.LndGrpc or
            LightningBackendTypes.Eclair)
        {
            return LightningCapabilities.Full;
        }

        if (type == LightningBackendTypes.Phoenixd)
        {
            return LightningCapabilities.Phoenixd;
        }

        if (type == LightningBackendTypes.Blink)
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString!, out _);
            if (!values.TryGetValue("api-key", out var apiKey) ||
                string.IsNullOrEmpty(apiKey))
            {
                return LightningCapabilities.None;
            }

            values.TryGetValue("currency", out var currency);
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
            return type.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    public static string GetFingerprint(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return CreateFingerprint("configuration", connectionString);
    }

    public static string GetIdentityFingerprint(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        var normalizedType = NormalizeFingerprintValue("type", type, stripServerCredentials: false);
        if (normalizedType == Blink)
        {
            // Blink credentials select the hosted wallet, so they are part of its identity.
            return CreateFingerprint(
                "configuration",
                values.Where(pair => !IsImplicitDefault(pair)),
                stripServerCredentials: false);
        }

        if (normalizedType is not (CLightning or LndRest or Eclair or Phoenixd))
        {
            throw new ArgumentException(
                $"Unsupported Lightning backend type '{type}'.",
                nameof(connectionString));
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
        return CreateFingerprint(purpose, canonical);
    }

    private static string CreateFingerprint(string purpose, string value)
    {
        return Convert.ToHexString(
            HMACSHA256.HashData(
                FingerprintKey,
                Encoding.UTF8.GetBytes($"{purpose}:{value}")));
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
