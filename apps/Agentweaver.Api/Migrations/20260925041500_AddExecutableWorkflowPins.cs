using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutableWorkflowPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "executable_workflow_pin_required",
                table: "runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "executable_workflow_manifest_schema_version",
                table: "runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_id",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_version",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_source",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_content_digest",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_workflow_definition_yaml",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "executable_workflow_pinned_at",
                table: "runs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "executable_workflow_pin_required", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_manifest_schema_version", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_definition_id", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_definition_version", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_source", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_content_digest", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_definition_yaml", table: "runs");
            migrationBuilder.DropColumn(name: "executable_workflow_pinned_at", table: "runs");
        }
    }
}
