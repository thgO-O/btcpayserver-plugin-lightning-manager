using BTCPayServer.Plugins.LightningManager.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerOperationGuardTests
{
    private const string BackendIdentityFingerprint = "backend-a";
    private const string CryptoCode = "BTC";
    private const string PaymentHash = "AABBCC";
    private const string RemoteNodeId = "02AABBCC";

    [Fact]
    public void Payment_BlocksSameKeyUntilLeaseIsDisposed()
    {
        var guard = new LightningManagerOperationGuard();

        Assert.True(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var first));
        Assert.NotNull(first);
        Assert.False(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var blocked));
        Assert.Null(blocked);

        first!.Dispose();

        Assert.True(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var second));
        second!.Dispose();
    }

    [Fact]
    public void Keys_NormalizeCryptoCodeAndIdentifierCasing()
    {
        var guard = new LightningManagerOperationGuard();

        Assert.True(guard.TryBeginPayment(BackendIdentityFingerprint, " btc ", " aabbcc ", out var payment));
        Assert.False(guard.TryBeginPayment(BackendIdentityFingerprint, "BTC", "AABBCC", out _));

        Assert.True(guard.TryBeginChannel(BackendIdentityFingerprint, " btc ", " 02aabbcc ", out var channel));
        Assert.False(guard.TryBeginChannel(BackendIdentityFingerprint, "BTC", "02AABBCC", out _));

        payment!.Dispose();
        channel!.Dispose();
    }

    [Fact]
    public void DistinctKeysAndOperationTypes_DoNotBlockEachOther()
    {
        var guard = new LightningManagerOperationGuard();
        var leases = new List<IDisposable>();

        AssertAcquired(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var payment), payment, leases);
        AssertAcquired(guard.TryBeginPayment("backend-b", CryptoCode, PaymentHash, out var otherBackend), otherBackend, leases);
        AssertAcquired(guard.TryBeginPayment(BackendIdentityFingerprint, "LTC", PaymentHash, out var otherCrypto), otherCrypto, leases);
        AssertAcquired(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, "DDEEFF", out var otherHash), otherHash, leases);
        AssertAcquired(guard.TryBeginChannel(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var channel), channel, leases);

        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentAttempts_AllowOnlyOneLeaseForTheSamePayment()
    {
        var guard = new LightningManagerOperationGuard();
        var leases = await ConcurrentTestRunner.RunAsync(
            32,
            () =>
            {
                guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var lease);
                return lease;
            });

        var acquired = Assert.Single(leases, lease => lease is not null)!;
        Assert.False(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out _));

        acquired.Dispose();
        Assert.True(guard.TryBeginPayment(BackendIdentityFingerprint, CryptoCode, PaymentHash, out var afterRelease));
        afterRelease!.Dispose();
    }

    [Fact]
    public void DisposingOldLeaseTwice_DoesNotReleaseNewLease()
    {
        var guard = new LightningManagerOperationGuard();

        Assert.True(guard.TryBeginChannel(BackendIdentityFingerprint, CryptoCode, RemoteNodeId, out var first));
        first!.Dispose();
        Assert.True(guard.TryBeginChannel(BackendIdentityFingerprint, CryptoCode, RemoteNodeId, out var second));

        first.Dispose();

        Assert.False(guard.TryBeginChannel(BackendIdentityFingerprint, CryptoCode, RemoteNodeId, out _));
        second!.Dispose();
    }

    [Theory]
    [InlineData(null, "BTC", "hash")]
    [InlineData("backend", " ", "hash")]
    [InlineData("backend", "BTC", null)]
    public void InvalidKeyParts_AreRejected(
        string? backendIdentityFingerprint,
        string? cryptoCode,
        string? identifier)
    {
        var guard = new LightningManagerOperationGuard();

        Assert.ThrowsAny<ArgumentException>(() =>
            guard.TryBeginPayment(backendIdentityFingerprint!, cryptoCode!, identifier!, out _));
    }

    private static void AssertAcquired(bool acquired, IDisposable? lease, ICollection<IDisposable> leases)
    {
        Assert.True(acquired);
        Assert.NotNull(lease);
        leases.Add(lease!);
    }
}
