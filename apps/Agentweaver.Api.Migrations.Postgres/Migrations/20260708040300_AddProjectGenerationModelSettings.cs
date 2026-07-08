using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds nullable per-project model overrides for server-authored generation flows. Null keeps using
    /// the global Generation fallback and does not affect normal run/agent model selection.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260708040300_AddProjectGenerationModelSettings")]
    public partial class AddProjectGenerationModelSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blueprint_generation_model",
                table: "projects",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "workflow_generation_model",
                table: "projects",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome_spec_generation_model",
                table: "projects",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("outcome_spec_generation_model", "projects");
            migrationBuilder.DropColumn("workflow_generation_model", "projects");
            migrationBuilder.DropColumn("blueprint_generation_model", "projects");
        }
    }
}
