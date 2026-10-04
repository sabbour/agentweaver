using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddChildCancellationProvenancePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                table: "Subtasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedByRunId",
                table: "Subtasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CoordinatorCancellationRequestedAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoordinatorCancellationRequestedByRunId",
                table: "WorkPlans",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedByRunId",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "CoordinatorCancellationRequestedAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "CoordinatorCancellationRequestedByRunId",
                table: "WorkPlans");
        }
    }
}
