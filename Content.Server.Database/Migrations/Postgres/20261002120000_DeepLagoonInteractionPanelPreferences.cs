using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres;

public partial class DeepLagoonInteractionPanelPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "friendly_consent", table: "profile", type: "integer", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "hugs_consent", table: "profile", type: "integer", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "playful_consent", table: "profile", type: "integer", nullable: false, defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "friendly_consent", table: "profile");
        migrationBuilder.DropColumn(name: "hugs_consent", table: "profile");
        migrationBuilder.DropColumn(name: "playful_consent", table: "profile");
    }
}
