using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the <c>token_usage_records</c> table for Feature 019 (AI Credit and token monitoring).
    /// Stores one row per <c>agent.turn.usage</c> run event, keyed by <c>id</c> (idempotent on
    /// re-delivery). Three indexes support the four-level usage hierarchy (run, workflow_run,
    /// project+time, app+time).
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260629000000_AddTokenUsageRecords")]
    public partial class AddTokenUsageRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "token_usage_records",
                columns: table => new
                {
                    id              = table.Column<string>(nullable: false),
                    run_id          = table.Column<string>(nullable: false),
                    workflow_run_id = table.Column<string>(nullable: true),
                    project_id      = table.Column<string>(nullable: true),
                    model_id        = table.Column<string>(nullable: false),
                    input_tokens    = table.Column<long>(nullable: false, defaultValue: 0L),
                    output_tokens   = table.Column<long>(nullable: false, defaultValue: 0L),
                    total_nano_aiu  = table.Column<long>(nullable: false, defaultValue: 0L),
                    recorded_at     = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_token_usage_records", x => x.id));

            migrationBuilder.CreateIndex(
                name: "IX_token_usage_run",
                table: "token_usage_records",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_token_usage_project_time",
                table: "token_usage_records",
                columns: new[] { "project_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_token_usage_wfr",
                table: "token_usage_records",
                column: "workflow_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("token_usage_records");
        }
    }
}
