using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260701133000_RemoveDbMetricsLayer")]
    public partial class RemoveDbMetricsLayer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "token_usage_records");
            migrationBuilder.DropColumn(name: "step_count", table: "runs");
            migrationBuilder.DropColumn(name: "review_wait_ms", table: "runs");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "step_count",
                table: "runs",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "review_wait_ms",
                table: "runs",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "token_usage_records",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    run_id = table.Column<string>(nullable: false),
                    workflow_run_id = table.Column<string>(nullable: true),
                    project_id = table.Column<string>(nullable: true),
                    model_id = table.Column<string>(nullable: false),
                    input_tokens = table.Column<long>(nullable: false, defaultValue: 0L),
                    output_tokens = table.Column<long>(nullable: false, defaultValue: 0L),
                    total_nano_aiu = table.Column<long>(nullable: false, defaultValue: 0L),
                    recorded_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_token_usage_records", x => x.id));

            migrationBuilder.CreateIndex("IX_token_usage_run", "token_usage_records", "run_id");
            migrationBuilder.CreateIndex("IX_token_usage_project_time", "token_usage_records", new[] { "project_id", "recorded_at" });
            migrationBuilder.CreateIndex("IX_token_usage_wfr", "token_usage_records", "workflow_run_id");
        }
    }
}
