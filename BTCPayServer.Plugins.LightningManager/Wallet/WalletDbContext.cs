using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed class WalletOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StoreId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string NodeIdentity { get; set; } = "";
    public string Direction { get; set; } = "";
    public string PaymentHash { get; set; } = "";
    public string InvoiceId { get; set; } = "";
    public string Bolt11 { get; set; } = "";
    public string Description { get; set; } = "";
    public long AmountMsat { get; set; }
    public long? SettledAmountMsat { get; set; }
    public long? FeeMsat { get; set; }
    public long? MaxFeeSats { get; set; }
    public string State { get; set; } = "Pending";
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsFinal => State is "Settled" or "Failed" or "Expired";
    // A failed hash can belong to an older attempt and later become pending/settled.
    public bool RequiresReconciliation => !IsFinal || (State == "Settled" && SettledAmountMsat is null) ||
        (Direction == "Outgoing" && State == "Failed");
    public long? DisplayAmountMsat => State == "Settled" ? SettledAmountMsat : AmountMsat;
    public string? FailureMessage => State == "Failed"
        ? FailureReason ?? "The node confirmed that this payment failed. A detailed failure reason is unavailable."
        : null;
}

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<WalletOperation> Operations => Set<WalletOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var op = modelBuilder.Entity<WalletOperation>();
        op.ToTable("LightningManagerWalletOperations");
        op.HasKey(x => x.Id);
        op.Ignore(x => x.IsFinal);
        op.Ignore(x => x.RequiresReconciliation);
        op.Ignore(x => x.DisplayAmountMsat);
        op.Ignore(x => x.FailureMessage);
        op.HasIndex(x => new { x.NodeIdentity, x.PaymentHash, x.Direction }).IsUnique();
        op.HasIndex(x => new { x.StoreId, x.NodeIdentity, x.CreatedAt });
        op.HasIndex(x => x.State);
    }
}

public sealed class WalletDbContextFactory(IOptions<DatabaseOptions> options)
    : BaseDbContextFactory<WalletDbContext>(options, "LightningManagerWalletMigrations")
{
    public override WalletDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<WalletDbContext>();
        ConfigureBuilder(builder, o =>
        {
            npgsqlOptionsAction?.Invoke(o);
            // Never replay a payment claim after an uncertain database commit.
            o.ExecutionStrategy(d => new NonRetryingExecutionStrategy(d));
        });
        return new WalletDbContext(builder.Options);
    }
}
