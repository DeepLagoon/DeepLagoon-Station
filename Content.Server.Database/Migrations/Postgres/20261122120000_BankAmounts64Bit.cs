using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Postgres;

[DbContext(typeof(PostgresServerDbContext))]
[Migration("20261122120000_BankAmounts64Bit")]
public sealed class BankAmounts64Bit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(name: "bank_balance", table: "profile", type: "bigint",
            nullable: false, oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AlterColumn<long>(name: "amount", table: "bank_financial_operations", type: "bigint",
            nullable: false, oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AlterColumn<long>(name: "balance", table: "bank_financial_operations", type: "bigint",
            nullable: false, oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AlterColumn<long>(name: "recipient_balance", table: "bank_financial_operations", type: "bigint",
            nullable: true, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // PostgreSQL refuses narrowing values outside Int32; it must never truncate savings.
        migrationBuilder.AlterColumn<int>(name: "bank_balance", table: "profile", type: "integer",
            nullable: false, oldClrType: typeof(long), oldType: "bigint");
        migrationBuilder.AlterColumn<int>(name: "amount", table: "bank_financial_operations", type: "integer",
            nullable: false, oldClrType: typeof(long), oldType: "bigint");
        migrationBuilder.AlterColumn<int>(name: "balance", table: "bank_financial_operations", type: "integer",
            nullable: false, oldClrType: typeof(long), oldType: "bigint");
        migrationBuilder.AlterColumn<int>(name: "recipient_balance", table: "bank_financial_operations", type: "integer",
            nullable: true, oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
    }
}
