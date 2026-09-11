using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRunApprovalPolicySnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approval_policy_captured_at",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_policy_inherited_from_run_id",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approval_policy_settings_updated_at",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_policy_snapshot_id",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_policy_source",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "launch_auto_approve_tools",
                table: "runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "launch_autopilot",
                table: "runs",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approval_policy_captured_at",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "approval_policy_inherited_from_run_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "approval_policy_settings_updated_at",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "approval_policy_snapshot_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "approval_policy_source",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "launch_auto_approve_tools",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "launch_autopilot",
                table: "runs");
        }
    }
}
