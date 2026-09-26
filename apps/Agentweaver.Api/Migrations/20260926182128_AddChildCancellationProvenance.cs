using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChildCancellationProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedByRunId",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CoordinatorCancellationRequestedAt",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoordinatorCancellationRequestedByRunId",
                table: "WorkPlans",
                type: "TEXT",
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
