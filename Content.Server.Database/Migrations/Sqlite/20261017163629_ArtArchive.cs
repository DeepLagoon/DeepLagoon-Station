using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ArtArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archived_artworks",
                columns: table => new
                {
                    archived_artworks_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    work_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    author = table.Column<Guid>(type: "TEXT", nullable: false),
                    author_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    document = table.Column<string>(type: "TEXT", nullable: false),
                    preview = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    hidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archived_artworks", x => x.archived_artworks_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_archived_artworks_work_id_revision_hash",
                table: "archived_artworks",
                columns: new[] { "work_id", "revision_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archived_artworks");
        }
    }
}
