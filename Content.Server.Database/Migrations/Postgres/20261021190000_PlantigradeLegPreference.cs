using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Postgres;

public partial class PlantigradeLegPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "plantigrade_legs", table: "profile", type: "boolean", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "plantigrade_legs", table: "profile");
}
