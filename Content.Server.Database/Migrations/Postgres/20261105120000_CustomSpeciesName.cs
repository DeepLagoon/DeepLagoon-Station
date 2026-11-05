using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Postgres;

[DbContext(typeof(PostgresServerDbContext))]
[Migration("20261105120000_CustomSpeciesName")]
public sealed class CustomSpeciesName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "custom_species_name", table: "profile", type: "text", nullable: false, defaultValue: "");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "custom_species_name", table: "profile");
}
