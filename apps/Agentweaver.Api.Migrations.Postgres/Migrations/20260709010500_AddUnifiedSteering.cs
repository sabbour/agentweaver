using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8) — additive schema for the coordinator-owned steering path:
    /// the source-agnostic envelope fields + durable action-intent state machine on SteeringDirectives,
    /// the per-subtask/per-plan loop-bound + retention markers, and the new SteeringRevisionExecutions
    /// two-phase revision-effect table (unique on (SteeringDirectiveId, ActionAttempt, RunId)) that makes
    /// the in-place (direction A) steer exactly-once PER TARGET CHILD under crash recovery. All columns
    /// are nullable or have a default so the migration is safe to apply to a populated database.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260709010500_AddUnifiedSteering")]
    public partial class AddUnifiedSteering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── SteeringDirectives: envelope + durable action-intent state machine ──────────────────
            migrationBuilder.AddColumn<string>(
                name: "Source", table: "SteeringDirectives", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "Severity", table: "SteeringDirectives", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "TargetScopeJson", table: "SteeringDirectives", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "TreeHash", table: "SteeringDirectives", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DecidedAction", table: "SteeringDirectives", type: "text", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "ActionAttempt", table: "SteeringDirectives", type: "integer", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecStartedAt", table: "SteeringDirectives",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "ExecutionAttempts", table: "SteeringDirectives", type: "integer",
                nullable: false, defaultValue: 0);

            // ── Subtasks: fresh-dispatch (direction B) idempotency + steering retention ─────────────
            migrationBuilder.AddColumn<int>(
                name: "LastResetDirectiveId", table: "Subtasks", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "LastResetAttempt", table: "Subtasks", type: "integer", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SteeringRetentionUntil", table: "Subtasks",
                type: "timestamp with time zone", nullable: true);

            // ── WorkPlans: per-plan steering loop bound (§6) ────────────────────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "SteeringIterations", table: "WorkPlans", type: "integer",
                nullable: false, defaultValue: 0);

            // ── SteeringRevisionExecutions: two-phase attempt-specific revision-effect marker (§3d) ──
            migrationBuilder.CreateTable(
                name: "SteeringRevisionExecutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(type: "text", nullable: false),
                    SteeringDirectiveId = table.Column<int>(type: "integer", nullable: false),
                    ActionAttempt = table.Column<int>(type: "integer", nullable: false),
                    EffectState = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CheckpointWatermark = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteeringRevisionExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SteeringRevisionExecutions_SteeringDirectiveId_ActionAttempt_RunId",
                table: "SteeringRevisionExecutions",
                columns: new[] { "SteeringDirectiveId", "ActionAttempt", "RunId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SteeringRevisionExecutions");

            migrationBuilder.DropColumn("SteeringIterations", "WorkPlans");

            migrationBuilder.DropColumn("SteeringRetentionUntil", "Subtasks");
            migrationBuilder.DropColumn("LastResetAttempt", "Subtasks");
            migrationBuilder.DropColumn("LastResetDirectiveId", "Subtasks");

            migrationBuilder.DropColumn("ExecStartedAt", "SteeringDirectives");
            migrationBuilder.DropColumn("ExecutionAttempts", "SteeringDirectives");
            migrationBuilder.DropColumn("ActionAttempt", "SteeringDirectives");
            migrationBuilder.DropColumn("DecidedAction", "SteeringDirectives");
            migrationBuilder.DropColumn("TreeHash", "SteeringDirectives");
            migrationBuilder.DropColumn("TargetScopeJson", "SteeringDirectives");
            migrationBuilder.DropColumn("Severity", "SteeringDirectives");
            migrationBuilder.DropColumn("Source", "SteeringDirectives");
        }
    }
}
