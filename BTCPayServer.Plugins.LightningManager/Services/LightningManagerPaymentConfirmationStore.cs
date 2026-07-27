#nullable enable
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningManagerPaymentConfirmationStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "LightningManager:PaymentConfirmation:";
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _lifetime;

    public LightningManagerPaymentConfirmationStore(IMemoryCache memoryCache)
        : this(memoryCache, DefaultLifetime)
    {
    }

    internal LightningManagerPaymentConfirmationStore(IMemoryCache memoryCache, TimeSpan lifetime)
    {
        _memoryCache = memoryCache;
        _lifetime = lifetime;
    }

    public string Create(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string bolt11,
        long? amountSats,
        long? maxFeeSats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cryptoCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountSats ?? 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFeeSats ?? 1);

        var token = Guid.NewGuid().ToString("N");
        _memoryCache.Set(
            CacheKeyPrefix + token,
            new Confirmation(
                userId,
                storeId,
                cryptoCode.Trim().ToUpperInvariant(),
                backendFingerprint,
                NormalizeBolt11(bolt11),
                amountSats,
                maxFeeSats),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _lifetime
            });
        return token;
    }

    public bool TryConsume(
        string? token,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string? bolt11,
        string? amountSats,
        string? maxFeeSats)
    {
        if (string.IsNullOrWhiteSpace(bolt11) ||
            !Guid.TryParseExact(token, "N", out _) ||
            !_memoryCache.TryGetValue(CacheKeyPrefix + token, out Confirmation? confirmation) ||
            confirmation is null ||
            !string.Equals(confirmation.UserId, userId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.StoreId, storeId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.CryptoCode, cryptoCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(confirmation.BackendFingerprint, backendFingerprint, StringComparison.Ordinal) ||
            !string.Equals(confirmation.Bolt11, NormalizeBolt11(bolt11), StringComparison.Ordinal) ||
            !MatchesOptionalValue(confirmation.AmountSats, amountSats) ||
            !MatchesOptionalValue(confirmation.MaxFeeSats, maxFeeSats) ||
            !confirmation.TryConsume())
        {
            return false;
        }

        _memoryCache.Remove(CacheKeyPrefix + token);
        return true;
    }

    private static string NormalizeBolt11(string bolt11)
    {
        return bolt11.Trim().ToLowerInvariant();
    }

    private static bool MatchesOptionalValue(long? expected, string? actual)
    {
        if (expected is null)
        {
            return string.IsNullOrWhiteSpace(actual);
        }

        return long.TryParse(
                   actual?.Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var parsed) &&
               parsed > 0 &&
               parsed == expected;
    }

    private sealed class Confirmation(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string bolt11,
        long? amountSats,
        long? maxFeeSats)
    {
        private int _consumed;

        public string UserId { get; } = userId;
        public string StoreId { get; } = storeId;
        public string CryptoCode { get; } = cryptoCode;
        public string BackendFingerprint { get; } = backendFingerprint;
        public string Bolt11 { get; } = bolt11;
        public long? AmountSats { get; } = amountSats;
        public long? MaxFeeSats { get; } = maxFeeSats;

        public bool TryConsume()
        {
            return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
        }
    }
}
