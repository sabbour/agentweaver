using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedSteering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SteeringIterations",
                table: "WorkPlans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastResetAttempt",
                table: "Subtasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastResetDirectiveId",
                table: "Subtasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SteeringRetentionUntil",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActionAttempt",
                table: "SteeringDirectives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedAction",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecStartedAt",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionAttempts",
                table: "SteeringDirectives",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetScopeJson",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreeHash",
                table: "SteeringDirectives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SteeringRevisionExecutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SteeringDirectiveId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionAttempt = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectState = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CheckpointWatermark = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
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
            migrationBuilder.DropTable(
                name: "SteeringRevisionExecutions");

            migrationBuilder.DropColumn(
                name: "SteeringIterations",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "LastResetAttempt",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "LastResetDirectiveId",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "SteeringRetentionUntil",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "ActionAttempt",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "DecidedAction",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "ExecStartedAt",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "ExecutionAttempts",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "TargetScopeJson",
                table: "SteeringDirectives");

            migrationBuilder.DropColumn(
                name: "TreeHash",
                table: "SteeringDirectives");
        }
    }
}
