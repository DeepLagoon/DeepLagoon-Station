using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Postgres;

[DbContext(typeof(PostgresServerDbContext))]
[Migration("20261029190000_BlueMoonPhysiology")]
public sealed class BlueMoonPhysiology : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "arousal_preferences_json", table: "profile", type: "text", nullable: false, defaultValue: "{}");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "arousal_preferences_json", table: "profile");
}
