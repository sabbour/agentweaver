using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_sources",
                schema: "projects_config",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    repository = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: false),
                    requested_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    subpath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_sources", x => x.source_id);
                    table.CheckConstraint("ck_marketplace_sources_revision", "revision > 0");
                    table.CheckConstraint("ck_marketplace_sources_state", "state IN ('Active', 'Removed')");
                    table.ForeignKey(
                        name: "FK_marketplace_sources_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_sources_project_id_state",
                schema: "projects_config",
                table: "marketplace_sources",
                columns: new[] { "project_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_sources_project_name_active",
                schema: "projects_config",
                table: "marketplace_sources",
                columns: new[] { "project_id", "normalized_name" },
                unique: true,
                filter: "\"state\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_sources",
                schema: "projects_config");
        }
    }
}
