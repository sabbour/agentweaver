using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Backfills the WorkPlan Phase-3 assembly columns onto the Postgres <c>WorkPlans</c> table. The
    /// hand-written <c>InitialPostgres</c> baseline created <c>WorkPlans</c> with only Id, CoordinatorRunId,
    /// OutcomeSpecId, Status, WorkflowId, CreatedAt and UpdatedAt, but the <see cref="WorkPlan"/> entity
    /// (and the SQLite model snapshot it was generated from) also has AssemblyStage, AssemblyStartedAt,
    /// IntegrationBranch, IsolationSummary and ProjectId. Without these columns every query that projects
    /// the full WorkPlan row — e.g. <c>CoordinatorStatusReader.GetCoordinatorStatusesAsync</c>, invoked
    /// unconditionally by the project run-list endpoint — failed with Postgres
    /// <c>42703: column w.AssemblyStage does not exist</c>, returning HTTP 500 for effectively every
    /// website board/run-list call. This migration adds the five missing columns so the table matches the
    /// model. ProjectId is non-nullable in the model; existing rows (if any) are backfilled with '' via the
    /// column default, matching EF's default behaviour for required string columns.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628090000_AddWorkPlanAssemblyFields")]
    public partial class AddWorkPlanAssemblyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssemblyStage",
                table: "WorkPlans",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssemblyStartedAt",
                table: "WorkPlans",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationBranch",
                table: "WorkPlans",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsolationSummary",
                table: "WorkPlans",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectId",
                table: "WorkPlans",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("ProjectId", "WorkPlans");
            migrationBuilder.DropColumn("IsolationSummary", "WorkPlans");
            migrationBuilder.DropColumn("IntegrationBranch", "WorkPlans");
            migrationBuilder.DropColumn("AssemblyStartedAt", "WorkPlans");
            migrationBuilder.DropColumn("AssemblyStage", "WorkPlans");
        }
    }
}
