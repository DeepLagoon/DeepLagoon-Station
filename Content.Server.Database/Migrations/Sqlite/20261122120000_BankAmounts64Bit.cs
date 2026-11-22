using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20261122120000_BankAmounts64Bit")]
public sealed class BankAmounts64Bit : Migration
{
    // SQLite INTEGER already stores signed 64-bit values. Only the CLR model changes.
    protected override void Up(MigrationBuilder migrationBuilder) { }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refuse rollback before an old client/server could read balances it cannot represent.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE bank_amounts_rollback_guard (valid INTEGER NOT NULL CHECK (valid = 1));
            INSERT INTO bank_amounts_rollback_guard
            SELECT CASE WHEN
                EXISTS (SELECT 1 FROM profile WHERE bank_balance NOT BETWEEN -2147483648 AND 2147483647) OR
                EXISTS (SELECT 1 FROM bank_financial_operations WHERE
                    amount NOT BETWEEN -2147483648 AND 2147483647 OR
                    balance NOT BETWEEN -2147483648 AND 2147483647 OR
                    recipient_balance NOT BETWEEN -2147483648 AND 2147483647)
                THEN 0 ELSE 1 END;
            DROP TABLE bank_amounts_rollback_guard;
            """);
    }
}
