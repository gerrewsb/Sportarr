using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sportarr.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneMappingTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SceneMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RemoteId = table.Column<int>(type: "INTEGER", nullable: true),
                    SportType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LeagueId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LeagueName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SceneNames = table.Column<string>(type: "TEXT", nullable: false),
                    SessionPatternsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    QueryConfigJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneMappings_IsActive",
                table: "SceneMappings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMappings_Priority",
                table: "SceneMappings",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMappings_RemoteId",
                table: "SceneMappings",
                column: "RemoteId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMappings_Source",
                table: "SceneMappings",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMappings_SportType_LeagueId",
                table: "SceneMappings",
                columns: new[] { "SportType", "LeagueId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneMappings");
        }
    }
}
