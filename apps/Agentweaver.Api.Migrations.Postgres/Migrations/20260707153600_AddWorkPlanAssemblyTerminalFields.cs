using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Persists the assembly terminal stage and reason on WorkPlans so coordinator graph/API readers
    /// can distinguish gates that never ran from the gate/action that actually parked or failed.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260707153600_AddWorkPlanAssemblyTerminalFields")]
    public partial class AddWorkPlanAssemblyTerminalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssemblyStatusReason",
                table: "WorkPlans",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssemblyTerminalStage",
                table: "WorkPlans",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("AssemblyTerminalStage", "WorkPlans");
            migrationBuilder.DropColumn("AssemblyStatusReason", "WorkPlans");
        }
    }
}
