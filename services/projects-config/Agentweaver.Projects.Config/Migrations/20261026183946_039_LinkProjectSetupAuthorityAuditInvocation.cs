using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class _039_LinkProjectSetupAuthorityAuditInvocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "invocation_id",
                schema: "projects_config",
                table: "authority_audit",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        WITH audit_links AS (
                            SELECT invocation_id, authority_audit_event_id AS event_id
                            FROM projects_config.project_setup_operations
                            WHERE authority_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT invocation_id, owner_authority_audit_event_id
                            FROM projects_config.project_setup_operations
                            WHERE owner_authority_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT invocation_id, owner_revocation_audit_event_id
                            FROM projects_config.project_setup_operations
                            WHERE owner_revocation_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT substring(project.name FROM
                                '^p1-([0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$')::uuid,
                                audit.event_id
                            FROM projects_config.projects AS project
                            JOIN projects_config.authority_audit AS audit
                                ON audit.resource_type = 'Project' AND audit.resource_id = project.project_id
                            WHERE project.name ~*
                                '^p1-[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                        )
                        SELECT event_id
                        FROM audit_links
                        GROUP BY event_id
                        HAVING COUNT(DISTINCT invocation_id) > 1
                    ) THEN
                        RAISE EXCEPTION 'authority audit event is linked to multiple setup invocations';
                    END IF;

                    IF EXISTS (
                        WITH audit_links AS (
                            SELECT invocation_id, tenant_id, project_id, assignment_id AS expected_assignment_id,
                                'role_assigned' AS expected_event_type, authority_audit_event_id AS event_id
                            FROM projects_config.project_setup_operations
                            WHERE operation_kind = 'Setup' AND authority_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT invocation_id, tenant_id, project_id, assignment_id,
                                'role_revoked', authority_audit_event_id
                            FROM projects_config.project_setup_operations
                            WHERE operation_kind = 'Cleanup' AND authority_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT invocation_id, tenant_id, project_id, owner_assignment_id,
                                'role_assigned', owner_authority_audit_event_id
                            FROM projects_config.project_setup_operations
                            WHERE owner_authority_audit_event_id IS NOT NULL
                            UNION ALL
                            SELECT invocation_id, tenant_id, project_id, owner_assignment_id,
                                'role_revoked', owner_revocation_audit_event_id
                            FROM projects_config.project_setup_operations
                            WHERE owner_revocation_audit_event_id IS NOT NULL
                        )
                        SELECT 1
                        FROM audit_links AS link
                        LEFT JOIN projects_config.authority_audit AS audit ON audit.event_id = link.event_id
                        WHERE audit.event_id IS NULL
                            OR audit.tenant_id IS DISTINCT FROM link.tenant_id
                            OR audit.resource_type IS DISTINCT FROM 'Project'
                            OR audit.resource_id IS DISTINCT FROM link.project_id
                            OR audit.assignment_id IS DISTINCT FROM link.expected_assignment_id
                            OR audit.event_type IS DISTINCT FROM link.expected_event_type
                    ) THEN
                        RAISE EXCEPTION 'setup operation references an authority audit event outside its tenant, project, assignment, or event type';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM projects_config.projects AS project
                        JOIN projects_config.authority_audit AS audit
                            ON audit.resource_id = project.project_id
                        WHERE project.name ~*
                            '^p1-[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                            AND (audit.tenant_id IS DISTINCT FROM project.tenant_id
                                OR audit.resource_type IS DISTINCT FROM 'Project')
                    ) THEN
                        RAISE EXCEPTION 'P1 authority audit event does not match its project tenant and resource';
                    END IF;
                END
                $$;

                DROP TRIGGER authority_audit_immutable ON projects_config.authority_audit;

                WITH audit_links AS (
                    SELECT invocation_id, authority_audit_event_id AS event_id
                    FROM projects_config.project_setup_operations
                    WHERE authority_audit_event_id IS NOT NULL
                    UNION
                    SELECT invocation_id, owner_authority_audit_event_id
                    FROM projects_config.project_setup_operations
                    WHERE owner_authority_audit_event_id IS NOT NULL
                    UNION
                    SELECT invocation_id, owner_revocation_audit_event_id
                    FROM projects_config.project_setup_operations
                    WHERE owner_revocation_audit_event_id IS NOT NULL
                    UNION
                    SELECT substring(project.name FROM
                        '^p1-([0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$')::uuid,
                        audit.event_id
                    FROM projects_config.projects AS project
                    JOIN projects_config.authority_audit AS audit
                        ON audit.resource_type = 'Project' AND audit.resource_id = project.project_id
                    WHERE project.name ~*
                        '^p1-[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                )
                UPDATE projects_config.authority_audit AS audit
                SET invocation_id = audit_links.invocation_id
                FROM audit_links
                WHERE audit.event_id = audit_links.event_id;

                CREATE TRIGGER authority_audit_immutable
                BEFORE UPDATE OR DELETE ON projects_config.authority_audit
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_authority_audit_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_authority_audit_tenant_invocation_created_at",
                schema: "projects_config",
                table: "authority_audit",
                columns: new[] { "tenant_id", "invocation_id", "created_at" },
                filter: "\"invocation_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_authority_audit_tenant_invocation_created_at",
                schema: "projects_config",
                table: "authority_audit");

            migrationBuilder.DropColumn(
                name: "invocation_id",
                schema: "projects_config",
                table: "authority_audit");
        }
    }
}
