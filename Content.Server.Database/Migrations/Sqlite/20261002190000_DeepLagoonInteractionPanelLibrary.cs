using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

public partial class DeepLagoonInteractionPanelLibrary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "social_actions_json", table: "preference", type: "TEXT", nullable: false, defaultValue: "[]");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "social_actions_json", table: "preference");
}
