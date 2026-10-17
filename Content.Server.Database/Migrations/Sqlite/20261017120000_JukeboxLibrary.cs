using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

public partial class JukeboxLibrary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "jukebox_library_json", table: "preference", type: "TEXT", nullable: false, defaultValue: "{}");
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "jukebox_library_json", table: "preference");
}
