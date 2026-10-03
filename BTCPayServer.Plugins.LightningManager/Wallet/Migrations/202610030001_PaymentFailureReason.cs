using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.LightningManager.Wallet.Migrations;

[DbContext(typeof(WalletDbContext))]
[Migration("20261003000100_PaymentFailureReason")]
public sealed class PaymentFailureReason : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "FailureReason", table: "LightningManagerWalletOperations", type: "text", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "FailureReason", table: "LightningManagerWalletOperations");
}
