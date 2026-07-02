using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260702113000_AddRunSandboxInfo")]
    public partial class AddRunSandboxInfo : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sandbox_backend",
                table: "runs",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sandbox_claim_name",
                table: "runs",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sandbox_pod_name",
                table: "runs",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sandbox_namespace",
                table: "runs",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sandbox_backend",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "sandbox_claim_name",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "sandbox_pod_name",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "sandbox_namespace",
                table: "runs");
        }
    }
}
