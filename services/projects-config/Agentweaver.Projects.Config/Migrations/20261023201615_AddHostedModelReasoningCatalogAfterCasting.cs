using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddHostedModelReasoningCatalogAfterCasting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosted_model_reasoning_catalog",
                schema: "projects_config",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bindings_revision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    supported_reasoning_efforts = table.Column<string[]>(type: "text[]", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_hosted_model_reasoning_catalog",
                        x => new
                        {
                            x.tenant_id,
                            x.project_id,
                            x.actor_id,
                            x.connection_id,
                            x.bindings_revision,
                            x.reference
                        });
                    table.CheckConstraint(
                        "ck_hosted_model_reasoning_supported_efforts",
                        "supported_reasoning_efforts <@ ARRAY['low', 'medium', 'high', 'xhigh']::text[]");
                    table.ForeignKey(
                        name: "FK_hosted_model_reasoning_catalog_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hosted_model_reasoning_catalog_project_id",
                schema: "projects_config",
                table: "hosted_model_reasoning_catalog",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hosted_model_reasoning_catalog",
                schema: "projects_config");
        }
    }
}
