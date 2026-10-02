using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.LightningManager.Wallet.Migrations;

[DbContext(typeof(WalletDbContext))]
[Migration("20260930000200_SettledAmount")]
public sealed class SettledAmount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<long>(
        name: "SettledAmountMsat", table: "LightningManagerWalletOperations", type: "bigint", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "SettledAmountMsat", table: "LightningManagerWalletOperations");
}
