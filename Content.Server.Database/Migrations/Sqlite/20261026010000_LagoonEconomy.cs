using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20261026010000_LagoonEconomy")]
public sealed class LagoonEconomy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("lagoon_economy_records", columns: table => new
        {
            lagoon_economy_records_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
            user_id = table.Column<Guid>(type: "TEXT", nullable: false),
            data = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_lagoon_economy_records", x => x.lagoon_economy_records_id));
        migrationBuilder.CreateIndex("IX_lagoon_economy_records_user_id", "lagoon_economy_records", "user_id");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("lagoon_economy_records");
}
