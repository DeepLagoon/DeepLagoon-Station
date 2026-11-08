using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20261108120000_SpeechBarkSettings")]
public sealed class SpeechBarkSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "speech_bark_settings_json", table: "profile", type: "TEXT", nullable: false, defaultValue: "{}");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "speech_bark_settings_json", table: "profile");
}
