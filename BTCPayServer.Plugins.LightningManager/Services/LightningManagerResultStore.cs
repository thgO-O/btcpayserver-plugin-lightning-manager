using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningManagerResultStore
{
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "LightningManager:Result:";
    private readonly IMemoryCache _memoryCache;
    private readonly object _pendingResultsLock = new();

    public LightningManagerResultStore(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public string StorePayment(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        SendExecutionResult result)
    {
        return Store(ResultScope.Payment, userId, storeId, cryptoCode, backendFingerprint, result);
    }

    public bool TryGetPayment(
        string? resultId,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out SendExecutionResult? result)
    {
        return TryGet(
            ResultScope.Payment,
            resultId,
            userId,
            storeId,
            cryptoCode,
            backendFingerprint,
            out result);
    }

    public bool TryGetPendingPayment(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out SendExecutionResult? result)
    {
        return TryGetPending(
            ResultScope.Payment,
            userId,
            storeId,
            cryptoCode,
            backendFingerprint,
            out result);
    }

    public string StoreChannel(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        ActionResultViewModel result)
    {
        return Store(ResultScope.Channel, userId, storeId, cryptoCode, backendFingerprint, result);
    }

    public bool TryGetChannel(
        string? resultId,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out ActionResultViewModel? result)
    {
        return TryGet(
            ResultScope.Channel,
            resultId,
            userId,
            storeId,
            cryptoCode,
            backendFingerprint,
            out result);
    }

    public bool TryGetPendingChannel(
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out ActionResultViewModel? result)
    {
        return TryGetPending(
            ResultScope.Channel,
            userId,
            storeId,
            cryptoCode,
            backendFingerprint,
            out result);
    }

    private string Store<T>(
        ResultScope scope,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        T result)
        where T : class
    {
        var resultId = Guid.NewGuid().ToString("N");
        var normalizedCryptoCode = NormalizeCryptoCode(cryptoCode);
        _memoryCache.Set(
            CacheKeyPrefix + resultId,
            new ScopedResult(
                scope,
                userId,
                storeId,
                normalizedCryptoCode,
                backendFingerprint,
                result),
            ResultLifetime);
        AddPendingResult(
            new PendingResultKey(
                scope,
                userId,
                storeId,
                normalizedCryptoCode,
                backendFingerprint),
            resultId);
        return resultId;
    }

    private bool TryGet<T>(
        ResultScope scope,
        string? resultId,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out T? result)
        where T : class
    {
        result = null;
        if (!Guid.TryParseExact(resultId, "N", out _))
        {
            return false;
        }

        var cacheKey = CacheKeyPrefix + resultId;
        if (!_memoryCache.TryGetValue(cacheKey, out ScopedResult? cached) ||
            cached is null ||
            cached.Scope != scope ||
            !string.Equals(cached.UserId, userId, StringComparison.Ordinal) ||
            !string.Equals(cached.StoreId, storeId, StringComparison.Ordinal) ||
            !string.Equals(cached.CryptoCode, cryptoCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cached.BackendFingerprint, backendFingerprint, StringComparison.Ordinal) ||
            cached.Result is not T typedResult)
        {
            return false;
        }

        RemovePendingResult(
            new PendingResultKey(
                scope,
                userId,
                storeId,
                NormalizeCryptoCode(cryptoCode),
                backendFingerprint),
            resultId);
        result = typedResult;
        return true;
    }

    private bool TryGetPending<T>(
        ResultScope scope,
        string userId,
        string storeId,
        string cryptoCode,
        string backendFingerprint,
        out T? result)
        where T : class
    {
        result = null;
        var pendingKey = new PendingResultKey(
            scope,
            userId,
            storeId,
            NormalizeCryptoCode(cryptoCode),
            backendFingerprint);
        while (TryTakePendingResult(pendingKey, out var resultId))
        {
            if (TryGet(
                    scope,
                    resultId,
                    userId,
                    storeId,
                    cryptoCode,
                    backendFingerprint,
                    out result))
            {
                return true;
            }
        }

        return false;
    }

    private void AddPendingResult(PendingResultKey key, string resultId)
    {
        lock (_pendingResultsLock)
        {
            var pending = _memoryCache.Get<Queue<string>>(key) ?? new Queue<string>();
            var active = new Queue<string>(pending.Count + 1);
            while (pending.TryDequeue(out var existingResultId))
            {
                if (_memoryCache.TryGetValue(CacheKeyPrefix + existingResultId, out _))
                {
                    active.Enqueue(existingResultId);
                }
            }

            active.Enqueue(resultId);
            _memoryCache.Set(key, active, ResultLifetime);
        }
    }

    private void RemovePendingResult(PendingResultKey key, string resultId)
    {
        lock (_pendingResultsLock)
        {
            if (!_memoryCache.TryGetValue(key, out Queue<string>? pending) || pending is null)
            {
                return;
            }

            var remaining = new Queue<string>(pending.Count);
            while (pending.TryDequeue(out var existingResultId))
            {
                if (!string.Equals(existingResultId, resultId, StringComparison.Ordinal))
                {
                    remaining.Enqueue(existingResultId);
                }
            }

            StorePendingResults(key, remaining);
        }
    }

    private bool TryTakePendingResult(PendingResultKey key, out string resultId)
    {
        lock (_pendingResultsLock)
        {
            resultId = string.Empty;
            if (!_memoryCache.TryGetValue(key, out Queue<string>? pending) ||
                pending is null ||
                !pending.TryDequeue(out var pendingResultId) ||
                pendingResultId is null)
            {
                return false;
            }

            resultId = pendingResultId;
            StorePendingResults(key, pending);
            return true;
        }
    }

    private void StorePendingResults(PendingResultKey key, Queue<string> pending)
    {
        if (pending.Count == 0)
        {
            _memoryCache.Remove(key);
        }
        else
        {
            _memoryCache.Set(key, pending, ResultLifetime);
        }
    }

    private static string NormalizeCryptoCode(string cryptoCode)
    {
        return cryptoCode.ToUpperInvariant();
    }

    private enum ResultScope
    {
        Payment,
        Channel
    }

    private sealed record PendingResultKey(
        ResultScope Scope,
        string UserId,
        string StoreId,
        string CryptoCode,
        string BackendFingerprint);
    private sealed record ScopedResult(
        ResultScope Scope,
        string UserId,
        string StoreId,
        string CryptoCode,
        string BackendFingerprint,
        object Result);
}
