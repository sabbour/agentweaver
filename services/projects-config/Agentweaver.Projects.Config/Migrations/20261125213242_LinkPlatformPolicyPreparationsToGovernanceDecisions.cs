using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class LinkPlatformPolicyPreparationsToGovernanceDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "governance_decision_hash",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "governance_decision_id",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_policy_preparations_governance_decision_id",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                column: "governance_decision_id",
                unique: true,
                filter: "governance_decision_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_native_acs_policy_preparations_governance_decision_hash",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                sql: "governance_decision_hash IS NULL OR governance_decision_hash ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_native_acs_policy_preparations_governance_decision_pair",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                sql: "(governance_decision_id IS NULL AND governance_decision_hash IS NULL) OR (governance_decision_id IS NOT NULL AND governance_decision_hash IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_native_acs_policy_preparations_project_governance_decisions~",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                column: "governance_decision_id",
                principalSchema: "projects_config",
                principalTable: "project_governance_decisions",
                principalColumn: "decision_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_native_acs_policy_preparations_project_governance_decisions~",
                schema: "projects_config",
                table: "native_acs_policy_preparations");

            migrationBuilder.DropIndex(
                name: "ux_native_acs_policy_preparations_governance_decision_id",
                schema: "projects_config",
                table: "native_acs_policy_preparations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_native_acs_policy_preparations_governance_decision_hash",
                schema: "projects_config",
                table: "native_acs_policy_preparations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_native_acs_policy_preparations_governance_decision_pair",
                schema: "projects_config",
                table: "native_acs_policy_preparations");

            migrationBuilder.DropColumn(
                name: "governance_decision_hash",
                schema: "projects_config",
                table: "native_acs_policy_preparations");

            migrationBuilder.DropColumn(
                name: "governance_decision_id",
                schema: "projects_config",
                table: "native_acs_policy_preparations");
        }
    }
}
