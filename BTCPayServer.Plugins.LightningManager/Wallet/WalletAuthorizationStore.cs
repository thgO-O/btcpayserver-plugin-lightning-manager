using System.Collections.Concurrent;
using System.Security.Cryptography;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Fido2NetLib;

namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed record WalletAuthorization(
    string UserId, string StoreId, string NodeIdentity, string ConfigurationFingerprint,
    SendPreviewViewModel Preview, AssertionOptions Options, DateTimeOffset ExpiresAt);

public sealed class WalletAuthorizationStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, WalletAuthorization> _entries = new();

    public string Create(WalletAuthorization authorization)
    {
        foreach (var entry in _entries)
            if (entry.Value.ExpiresAt <= clock.GetUtcNow()) _entries.TryRemove(entry.Key, out _);
        if (_entries.Count >= 1000) throw new WalletException("Too many payment confirmations. Try again later.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[id] = authorization;
        return id;
    }

    public WalletAuthorization? Consume(string? id, string userId, string storeId, string node, string fingerprint)
    {
        if (id is null || !_entries.TryGetValue(id, out var entry)) return null;
        if (entry.UserId != userId || entry.StoreId != storeId || entry.NodeIdentity != node || entry.ConfigurationFingerprint != fingerprint)
            return null;
        if (!_entries.TryRemove(id, out entry) || entry.ExpiresAt <= clock.GetUtcNow()) return null;
        return entry;
    }

    public WalletAuthorization? Get(string id, string userId, string storeId, string node, string fingerprint)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.ExpiresAt <= clock.GetUtcNow() ||
            entry.UserId != userId || entry.StoreId != storeId || entry.NodeIdentity != node || entry.ConfigurationFingerprint != fingerprint) return null;
        return entry;
    }
}
