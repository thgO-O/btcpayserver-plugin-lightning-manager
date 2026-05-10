using BTCPayServer.Plugins.LightningManager.Controllers;
using BTCPayServer.Plugins.LightningManager.Services;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class ServerLightningManagerControllerTests
{
    [Fact]
    public void ShouldShowServerAccount_WithCurrentInternalStore_ReturnsTrue()
    {
        var account = new LightningLedgerAccountSnapshot
        {
            StoreId = "store-1",
            CryptoCode = "BTC"
        };

        var show = ServerLightningManagerController.ShouldShowServerAccount(
            account,
            new HashSet<string>(StringComparer.Ordinal) { "store-1" });

        Assert.True(show);
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 0, 1)]
    public void ShouldShowServerAccount_WithDetachedLedgerState_ReturnsTrue(
        bool enabled,
        long totalMSat,
        long reservedMSat)
    {
        var account = new LightningLedgerAccountSnapshot
        {
            StoreId = "store-1",
            CryptoCode = "BTC",
            Enabled = enabled,
            TotalMSat = totalMSat,
            ReservedMSat = reservedMSat
        };

        var show = ServerLightningManagerController.ShouldShowServerAccount(
            account,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.True(show);
    }

    [Fact]
    public void ShouldShowServerAccount_WithDetachedEmptyDisabledAccount_ReturnsFalse()
    {
        var account = new LightningLedgerAccountSnapshot
        {
            StoreId = "store-1",
            CryptoCode = "BTC"
        };

        var show = ServerLightningManagerController.ShouldShowServerAccount(
            account,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(show);
    }
}
