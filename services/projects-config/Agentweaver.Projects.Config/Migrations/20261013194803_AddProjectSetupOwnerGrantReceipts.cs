using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSetupOwnerGrantReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_project_setup_operations_revision",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.AddColumn<Guid>(
                name: "owner_assignment_id",
                schema: "projects_config",
                table: "project_setup_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "owner_assignment_revision",
                schema: "projects_config",
                table: "project_setup_operations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_authority_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_revocation_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_owner_assignment_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_owner_authority_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_authority_audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_owner_revocation_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_revocation_audit_event_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_setup_operations_owner_binding",
                schema: "projects_config",
                table: "project_setup_operations",
                sql: "(owner_assignment_id IS NULL AND owner_assignment_revision IS NULL AND owner_authority_audit_event_id IS NULL) OR (owner_assignment_id IS NOT NULL AND owner_assignment_id <> assignment_id AND owner_assignment_revision IS NOT NULL AND owner_authority_audit_event_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_setup_operations_owner_revocation_audit",
                schema: "projects_config",
                table: "project_setup_operations",
                sql: "owner_revocation_audit_event_id IS NULL OR operation_kind = 'Cleanup'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_setup_operations_revision",
                schema: "projects_config",
                table: "project_setup_operations",
                sql: "membership_revision > 0 AND project_revision > 0 AND project_configuration_revision > 0 AND assignment_revision > 0 AND (owner_assignment_revision IS NULL OR owner_assignment_revision > 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_project_setup_operations_authority_audit_owner_authority_au~",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_authority_audit_event_id",
                principalSchema: "projects_config",
                principalTable: "authority_audit",
                principalColumn: "event_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_project_setup_operations_authority_audit_owner_revocation_a~",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_revocation_audit_event_id",
                principalSchema: "projects_config",
                principalTable: "authority_audit",
                principalColumn: "event_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_project_setup_operations_project_role_assignments_owner_ass~",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "owner_assignment_id",
                principalSchema: "projects_config",
                principalTable: "project_role_assignments",
                principalColumn: "assignment_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_project_setup_operations_authority_audit_owner_authority_au~",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropForeignKey(
                name: "FK_project_setup_operations_authority_audit_owner_revocation_a~",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropForeignKey(
                name: "FK_project_setup_operations_project_role_assignments_owner_ass~",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropIndex(
                name: "IX_project_setup_operations_owner_assignment_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropIndex(
                name: "IX_project_setup_operations_owner_authority_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropIndex(
                name: "IX_project_setup_operations_owner_revocation_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_setup_operations_owner_binding",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_setup_operations_owner_revocation_audit",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_setup_operations_revision",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropColumn(
                name: "owner_assignment_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropColumn(
                name: "owner_assignment_revision",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropColumn(
                name: "owner_authority_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.DropColumn(
                name: "owner_revocation_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_setup_operations_revision",
                schema: "projects_config",
                table: "project_setup_operations",
                sql: "membership_revision > 0 AND project_revision > 0 AND project_configuration_revision > 0 AND assignment_revision > 0");
        }
    }
}
