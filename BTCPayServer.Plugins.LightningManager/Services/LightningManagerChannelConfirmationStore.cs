#nullable enable
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningManagerChannelConfirmationStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "LightningManager:ChannelConfirmation:";
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _lifetime;

    public LightningManagerChannelConfirmationStore(IMemoryCache memoryCache)
        : this(memoryCache, DefaultLifetime)
    {
    }

    internal LightningManagerChannelConfirmationStore(IMemoryCache memoryCache, TimeSpan lifetime)
    {
        _memoryCache = memoryCache;
        _lifetime = lifetime;
    }

    public string Create(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string nodeUri,
        string channelAmountSats,
        string? feeRateSatsPerByte)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cryptoCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelAmountSats);

        var token = Guid.NewGuid().ToString("N");
        _memoryCache.Set(
            CacheKeyPrefix + token,
            new Confirmation(
                userId,
                storeId,
                cryptoCode.Trim().ToUpperInvariant(),
                backendFingerprint,
                nodeUri.Trim(),
                channelAmountSats.Trim(),
                feeRateSatsPerByte?.Trim() ?? string.Empty),
            _lifetime);
        return token;
    }

    public bool TryConsume(
        string? token,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string nodeUri,
        string channelAmountSats,
        string? feeRateSatsPerByte)
    {
        if (string.IsNullOrWhiteSpace(nodeUri) ||
            string.IsNullOrWhiteSpace(channelAmountSats) ||
            !Guid.TryParseExact(token, "N", out _) ||
            !_memoryCache.TryGetValue(CacheKeyPrefix + token, out Confirmation? confirmation) ||
            confirmation is null ||
            !string.Equals(confirmation.UserId, userId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.StoreId, storeId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.CryptoCode, cryptoCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(confirmation.BackendFingerprint, backendFingerprint, StringComparison.Ordinal) ||
            !string.Equals(confirmation.NodeUri, nodeUri.Trim(), StringComparison.Ordinal) ||
            !string.Equals(confirmation.ChannelAmountSats, channelAmountSats.Trim(), StringComparison.Ordinal) ||
            !string.Equals(
                confirmation.FeeRateSatsPerByte,
                feeRateSatsPerByte?.Trim() ?? string.Empty,
                StringComparison.Ordinal) ||
            !confirmation.TryConsume())
        {
            return false;
        }

        _memoryCache.Remove(CacheKeyPrefix + token);
        return true;
    }

    private sealed class Confirmation(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        string nodeUri,
        string channelAmountSats,
        string feeRateSatsPerByte)
    {
        private int _consumed;

        public string UserId { get; } = userId;
        public string StoreId { get; } = storeId;
        public string CryptoCode { get; } = cryptoCode;
        public string BackendFingerprint { get; } = backendFingerprint;
        public string NodeUri { get; } = nodeUri;
        public string ChannelAmountSats { get; } = channelAmountSats;
        public string FeeRateSatsPerByte { get; } = feeRateSatsPerByte;

        public bool TryConsume()
        {
            return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
        }
    }
}
