using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260723200000_AddSkillMarketplaceSources")]
    public partial class AddSkillMarketplaceSources : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_marketplace_sources",
                columns: table => new
                {
                    source_id = table.Column<string>(nullable: false),
                    project_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    repository = table.Column<string>(nullable: false),
                    branch = table.Column<string>(nullable: true),
                    subpath = table.Column<string>(nullable: true),
                    parse_strategy = table.Column<string>(nullable: true),
                    enabled = table.Column<bool>(nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_marketplace_sources", x => x.source_id);
                    table.ForeignKey(
                        name: "FK_skill_marketplace_sources_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Case-insensitive uniqueness parity with SQLite (name COLLATE NOCASE).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_skill_marketplace_sources_project_name\" ON skill_marketplace_sources (project_id, lower(name));");
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "skill_marketplace_sources");
    }
}
