#nullable enable
using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningManagerOperationGuard
{
    private readonly ConcurrentDictionary<OperationKey, byte> _operations = new();

    public bool TryBeginPayment(
        string backendFingerprint,
        string cryptoCode,
        string paymentHash,
        out IDisposable? lease)
    {
        return TryBegin(OperationType.Payment, backendFingerprint, cryptoCode, paymentHash, out lease);
    }

    public bool TryBeginChannel(
        string backendFingerprint,
        string cryptoCode,
        string remoteNodeId,
        out IDisposable? lease)
    {
        return TryBegin(OperationType.Channel, backendFingerprint, cryptoCode, remoteNodeId, out lease);
    }

    private bool TryBegin(
        OperationType type,
        string backendFingerprint,
        string cryptoCode,
        string identifier,
        out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(cryptoCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        var key = new OperationKey(
            type,
            backendFingerprint,
            cryptoCode.Trim().ToUpperInvariant(),
            identifier.Trim().ToUpperInvariant());

        if (!_operations.TryAdd(key, 0))
        {
            lease = null;
            return false;
        }

        lease = new OperationLease(_operations, key);
        return true;
    }

    private enum OperationType
    {
        Payment,
        Channel
    }

    private sealed record OperationKey(
        OperationType Type,
        string BackendFingerprint,
        string CryptoCode,
        string Identifier);

    private sealed class OperationLease : IDisposable
    {
        private readonly ConcurrentDictionary<OperationKey, byte> _operations;
        private OperationKey? _key;

        public OperationLease(
            ConcurrentDictionary<OperationKey, byte> operations,
            OperationKey key)
        {
            _operations = operations;
            _key = key;
        }

        public void Dispose()
        {
            var key = Interlocked.Exchange(ref _key, null);
            if (key is not null)
            {
                _operations.TryRemove(key, out _);
            }
        }
    }
}
