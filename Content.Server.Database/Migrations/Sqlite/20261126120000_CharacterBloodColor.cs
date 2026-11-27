using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20261126120000_CharacterBloodColor")]
public sealed class CharacterBloodColor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "blood_color", table: "profile", type: "TEXT", nullable: false, defaultValue: "");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Native DROP COLUMN is supported by the bundled SQLite. EF's rebuild
        // path needs a previous TargetModel, absent in the preceding hand-authored migration.
        migrationBuilder.Sql("ALTER TABLE profile DROP COLUMN blood_color;");
}
