using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

public partial class DeepLagoonInteractionPanelPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "friendly_consent", table: "profile", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "hugs_consent", table: "profile", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "playful_consent", table: "profile", type: "INTEGER", nullable: false, defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The preceding data-only migration has no target schema for EF to rebuild.
        migrationBuilder.Sql("ALTER TABLE profile DROP COLUMN friendly_consent;");
        migrationBuilder.Sql("ALTER TABLE profile DROP COLUMN hugs_consent;");
        migrationBuilder.Sql("ALTER TABLE profile DROP COLUMN playful_consent;");
    }
}
