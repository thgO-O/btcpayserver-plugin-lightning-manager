using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.LightningManager.Wallet.Migrations;

[DbContext(typeof(WalletDbContext))]
[Migration("20260930000100_WalletOperations")]
public sealed class WalletOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "LightningManagerWalletOperations" (
                "Id" uuid PRIMARY KEY,
                "StoreId" text NOT NULL,
                "UserId" text NOT NULL,
                "NodeIdentity" text NOT NULL,
                "Direction" text NOT NULL,
                "PaymentHash" text NOT NULL,
                "InvoiceId" text NOT NULL,
                "Bolt11" text NOT NULL,
                "Description" text NOT NULL,
                "AmountMsat" bigint NOT NULL,
                "FeeMsat" bigint NULL,
                "MaxFeeSats" bigint NULL,
                "State" text NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                "ExpiresAt" timestamptz NULL
            );
            CREATE UNIQUE INDEX "IX_LMWallet_NodeHashDirection"
                ON "LightningManagerWalletOperations" ("NodeIdentity", "PaymentHash", "Direction");
            CREATE INDEX "IX_LMWallet_StoreNodeCreated"
                ON "LightningManagerWalletOperations" ("StoreId", "NodeIdentity", "CreatedAt");
            CREATE INDEX "IX_LMWallet_State" ON "LightningManagerWalletOperations" ("State");
            """);
    }
}
