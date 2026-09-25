using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutableWorkflowPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_content_digest",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_id",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_version",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_yaml",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "executable_workflow_manifest_schema_version",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "executable_workflow_pin_required",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "executable_workflow_pinned_at",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_source",
                table: "runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "executable_workflow_content_digest",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_definition_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_definition_version",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_definition_yaml",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_manifest_schema_version",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_pin_required",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_pinned_at",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "executable_workflow_source",
                table: "runs");
        }
    }
}
