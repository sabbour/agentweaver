using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowChildWorkCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParentJoinNodeId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentResumeClaimOwner",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ParentResumeClaimedAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ParentResumeDeliveredAt",
                table: "WorkPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentResumeRequestId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentResumeResultJson",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentResumeState",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentRunId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentWorkflowId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentWorkflowNodeId",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowBranchNodeId",
                table: "Subtasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowBranchOrdinal",
                table: "Subtasks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkPlans_ParentRunId_ParentWorkflowNodeId",
                table: "WorkPlans",
                columns: new[] { "ParentRunId", "ParentWorkflowNodeId" },
                unique: true,
                filter: "\"ParentRunId\" IS NOT NULL AND \"ParentWorkflowNodeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Subtasks_WorkPlanId_WorkflowBranchNodeId",
                table: "Subtasks",
                columns: new[] { "WorkPlanId", "WorkflowBranchNodeId" },
                unique: true,
                filter: "\"WorkflowBranchNodeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Subtasks_WorkPlanId_WorkflowBranchOrdinal",
                table: "Subtasks",
                columns: new[] { "WorkPlanId", "WorkflowBranchOrdinal" },
                unique: true,
                filter: "\"WorkflowBranchOrdinal\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkPlans_ParentRunId_ParentWorkflowNodeId",
                table: "WorkPlans");

            migrationBuilder.DropIndex(
                name: "IX_Subtasks_WorkPlanId_WorkflowBranchNodeId",
                table: "Subtasks");

            migrationBuilder.DropIndex(
                name: "IX_Subtasks_WorkPlanId_WorkflowBranchOrdinal",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "ParentJoinNodeId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeClaimOwner",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeClaimedAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeDeliveredAt",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeRequestId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeResultJson",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentResumeState",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentWorkflowId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentWorkflowNodeId",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "WorkflowBranchNodeId",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "WorkflowBranchOrdinal",
                table: "Subtasks");
        }
    }
}
