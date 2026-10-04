using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorMergeEffects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergeAppliedAt",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEffectId",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEffectState",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeEvidenceJson",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeIntentJson",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergeLifecycleGeneration",
                table: "WorkPlans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergeObservedAt",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergePreparedAt",
                table: "WorkPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeRecoveryAction",
                table: "WorkPlans",
                type: "TEXT",
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
