using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorMergeEffectsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergeAppliedAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEffectId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEffectState",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEvidenceJson",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeIntentJson",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergeLifecycleGeneration",
                table: "WorkPlans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergeObservedAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergePreparedAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeRecoveryAction",
                table: "WorkPlans",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeAppliedAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeEffectId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeEffectState",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeEvidenceJson",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeIntentJson",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeLifecycleGeneration",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeObservedAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergePreparedAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "MergeRecoveryAction",
                table: "WorkPlans");
        }
    }
}
