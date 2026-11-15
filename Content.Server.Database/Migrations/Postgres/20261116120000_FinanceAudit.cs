using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Postgres;

[DbContext(typeof(PostgresServerDbContext))]
[Migration("20261116120000_FinanceAudit")]
public sealed class FinanceAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "finance_delivered", table: "lagoon_coin_operations", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.Sql("UPDATE lagoon_coin_operations SET finance_delivered = TRUE WHERE amount <> 0");
        migrationBuilder.CreateIndex(name: "IX_lagoon_coin_operations_finance_delivered_created_at",
            table: "lagoon_coin_operations", columns: new[] { "finance_delivered", "created_at" }, filter: "amount <> 0");

        migrationBuilder.CreateTable("bank_financial_operations", columns: table => new
        {
            transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
            created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            kind = table.Column<string>(type: "text", nullable: false),
            amount = table.Column<int>(type: "integer", nullable: false),
            user_id = table.Column<Guid>(type: "uuid", nullable: false),
            character = table.Column<string>(type: "text", nullable: false),
            balance = table.Column<int>(type: "integer", nullable: false),
            recipient_id = table.Column<Guid>(type: "uuid", nullable: true),
            recipient_character = table.Column<string>(type: "text", nullable: true),
            recipient_balance = table.Column<int>(type: "integer", nullable: true),
            origin_json = table.Column<string>(type: "text", nullable: true),
            delivered = table.Column<bool>(type: "boolean", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_bank_financial_operations", row => row.transaction_id));
        migrationBuilder.CreateIndex(name: "IX_bank_financial_operations_delivered_created_at",
            table: "bank_financial_operations", columns: new[] { "delivered", "created_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("bank_financial_operations");
        migrationBuilder.DropIndex("IX_lagoon_coin_operations_finance_delivered_created_at", "lagoon_coin_operations");
        migrationBuilder.DropColumn("finance_delivered", "lagoon_coin_operations");
    }
}
