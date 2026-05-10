#nullable enable
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BTCPayServer.Plugins.LightningManager.Services;

public interface IStoreLightningAccessService
{
    Task<bool> SetStoreAccessAsync(string storeId, string cryptoCode, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> EnforceStoreAccessAsync(string storeId, CancellationToken cancellationToken = default);
}

public class StoreLightningAccessService(
    ApplicationDbContextFactory dbContextFactory,
    PaymentMethodHandlerDictionary handlers,
    ILightningLedgerRepository ledgerRepository) : IStoreLightningAccessService
{
    public async Task<bool> SetStoreAccessAsync(
        string storeId,
        string cryptoCode,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeId) ||
            !LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return false;
        }

        await using var ctx = dbContextFactory.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var dbTx = tx.GetDbTransaction();
        var conn = ctx.Database.GetDbConnection();
        var store = await ctx.Stores.SingleOrDefaultAsync(s => s.Id == storeId, cancellationToken);
        if (store is null)
        {
            return false;
        }

        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO "LightningManagerLedgerAccounts" ("StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt")
                VALUES (@storeId, @cryptoCode, @enabled, @now, @now)
                ON CONFLICT ("StoreId", "CryptoCode") DO NOTHING
                """,
                new { storeId, cryptoCode, enabled, now = DateTimeOffset.UtcNow },
                transaction: dbTx,
                cancellationToken: cancellationToken));

        var updated = await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE "LightningManagerLedgerAccounts"
                SET "Enabled" = @enabled, "UpdatedAt" = @now
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                """,
                new { storeId, cryptoCode, enabled, now = DateTimeOffset.UtcNow },
                transaction: dbTx,
                cancellationToken: cancellationToken));
        if (updated != 1)
        {
            return false;
        }

        if (!enabled && IsInternalLightningNode(store, cryptoCode, handlers))
        {
            if (DisableNativeLightningCheckout(store, cryptoCode))
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> EnforceStoreAccessAsync(string storeId, CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var store = await ctx.Stores.SingleOrDefaultAsync(s => s.Id == storeId, cancellationToken);
        if (store is null)
        {
            return false;
        }

        var changed = false;
        foreach (var cryptoCode in GetInternalLightningCryptoCodes(store, handlers))
        {
            var account = await ledgerRepository.GetAccountAsync(store.Id, cryptoCode, cancellationToken);
            if (account is not { Enabled: true })
            {
                changed |= DisableNativeLightningCheckout(store, cryptoCode);
            }
        }

        if (changed)
        {
            await ctx.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    internal static bool DisableNativeLightningCheckout(StoreData store, string cryptoCode)
    {
        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return false;
        }

        var lnId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var lnurlId = PaymentTypes.LNURL.GetPaymentMethodId(cryptoCode);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(lnId, true);
        blob.SetExcluded(lnurlId, true);
        return store.SetStoreBlob(blob);
    }

    internal static IReadOnlyList<string> GetInternalLightningCryptoCodes(
        StoreData store,
        PaymentMethodHandlerDictionary handlers)
    {
        return store.GetPaymentMethodConfigs()
            .Keys
            .Select(TryGetLightningCryptoCode)
            .Where(cryptoCode => cryptoCode is not null)
            .Select(cryptoCode => cryptoCode!)
            .Where(LightningManagerCrypto.IsSupported)
            .Where(cryptoCode => IsInternalLightningNode(store, cryptoCode, handlers))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsInternalLightningNode(
        StoreData store,
        string cryptoCode,
        PaymentMethodHandlerDictionary handlers)
    {
        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return false;
        }

        var lnId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnId, handlers);
        return config is { IsInternalNode: true } && config.GetExternalLightningUrl() is null;
    }

    private static string? TryGetLightningCryptoCode(PaymentMethodId paymentMethodId)
    {
        const string lightningSuffix = "-LN";
        var value = paymentMethodId.ToString();
        return value.EndsWith(lightningSuffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^lightningSuffix.Length].ToUpperInvariant()
            : null;
    }
}

public class LightningManagerStoreAccessGuard(
    EventAggregator eventAggregator,
    Logs logs,
    IStoreLightningAccessService accessService)
    : EventHostedServiceBase(eventAggregator, logs)
{
    protected override void SubscribeToEvents()
    {
        Subscribe<StoreEvent.Updated>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is StoreEvent.Updated storeEvent)
        {
            await accessService.EnforceStoreAccessAsync(storeEvent.StoreId, cancellationToken);
            return;
        }

        await base.ProcessEvent(evt, cancellationToken);
    }
}
