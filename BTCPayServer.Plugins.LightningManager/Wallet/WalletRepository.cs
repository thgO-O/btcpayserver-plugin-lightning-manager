using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed class WalletRepository(WalletDbContextFactory factory)
{
    public bool Ready { get; private set; }

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = factory.CreateContext();
        await db.Database.MigrateAsync(token);
        Ready = true;
    }

    public void RequireReady()
    {
        if (!Ready) throw new WalletException("Wallet storage is unavailable. No payment was submitted.");
    }

    public async Task<bool> InsertAsync(WalletOperation operation, CancellationToken token)
    {
        RequireReady();
        await using var db = factory.CreateContext();
        db.Operations.Add(operation);
        try
        {
            await db.SaveChangesAsync(token);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public async Task<WalletOperation?> GetAsync(Guid id, string storeId, string node, CancellationToken token)
    {
        RequireReady();
        await using var db = factory.CreateContext();
        return await db.Operations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.StoreId == storeId && x.NodeIdentity == node, token);
    }

    public async Task<List<WalletOperation>> ListAsync(string storeId, string node, int count, CancellationToken token)
    {
        RequireReady();
        await using var db = factory.CreateContext();
        return await db.Operations.AsNoTracking().Where(x => x.StoreId == storeId && x.NodeIdentity == node)
            .OrderByDescending(x => x.CreatedAt).Take(count).ToListAsync(token);
    }

    public async Task<List<WalletOperation>> PendingAsync(CancellationToken token)
    {
        RequireReady();
        await using var db = factory.CreateContext();
        return await db.Operations.AsNoTracking().Where(x => x.State == "Pending" || x.State == "Unknown" || x.State == "Submitting" ||
                (x.Direction == "Outgoing" && (x.State == "Failed" || (x.State == "Settled" && x.SettledAmountMsat == null))))
            .OrderBy(x => x.UpdatedAt).Take(100).ToListAsync(token);
    }

    public async Task UpdateAsync(WalletOperation operation, CancellationToken token)
    {
        await using var db = factory.CreateContext();
        // Confirmed settlement wins even if a reconciler saved an older failure first.
        // Settlement is immutable, except filling a previously unavailable actual amount.
        await db.Operations.Where(x => x.Id == operation.Id &&
            (x.State != "Settled" || (operation.State == "Settled" && x.SettledAmountMsat == null && operation.SettledAmountMsat != null)) &&
            (operation.State == "Settled" || x.State != "Expired"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, operation.State)
                .SetProperty(x => x.SettledAmountMsat, x => x.SettledAmountMsat ?? operation.SettledAmountMsat)
                .SetProperty(x => x.FeeMsat, x => x.State == "Settled" ? x.FeeMsat ?? operation.FeeMsat : operation.FeeMsat)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
    }

    public async Task DeferAsync(Guid id, CancellationToken token)
    {
        await using var db = factory.CreateContext();
        await db.Operations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
    }
}
