using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssemblyTerminalStage",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssemblyTerminalStage",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "AssemblyStatusReason",
                table: "WorkPlans");
        }
    }
}
