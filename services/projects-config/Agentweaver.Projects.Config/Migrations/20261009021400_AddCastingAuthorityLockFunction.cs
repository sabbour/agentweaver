using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddCastingAuthorityLockFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.lock_casting_authority(
                    p_membership_id uuid,
                    p_issuer text,
                    p_subject text,
                    p_tenant_id text,
                    p_membership_revision bigint,
                    p_project_id text,
                    p_write boolean)
                RETURNS text
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF p_membership_id IS NULL
                        OR p_issuer IS NULL
                        OR p_subject IS NULL
                        OR p_tenant_id IS NULL
                        OR p_membership_revision IS NULL
                        OR p_membership_revision <= 0
                        OR p_project_id IS NULL
                        OR p_write IS NULL THEN
                        RAISE EXCEPTION 'casting authority lock arguments must be non-null and revisions positive'
                            USING ERRCODE = '22004';
                    END IF;

                    PERFORM 1
                    FROM projects_config.tenant_memberships AS membership
                    WHERE membership.membership_id = p_membership_id
                      AND membership.issuer = p_issuer
                      AND membership.subject = p_subject
                      AND membership.tenant_id = p_tenant_id
                      AND membership.revision = p_membership_revision
                      AND membership.state = 'Active'
                    FOR SHARE;
                    IF NOT FOUND THEN
                        RETURN 'membership_missing';
                    END IF;

                    PERFORM 1
                    FROM projects_config.project_role_assignments AS assignment
                    WHERE assignment.membership_id = p_membership_id
                      AND assignment.state = 'Active'
                      AND (
                          (assignment.resource_type = 'Project'
                              AND assignment.resource_id = p_project_id
                              AND (assignment.role = 'Owner'
                                  OR (NOT p_write AND assignment.role IN ('Contributor', 'Viewer'))))
                          OR
                          (assignment.resource_type = 'Tenant'
                              AND assignment.resource_id = p_tenant_id
                              AND assignment.role = 'TenantAdmin')
                      )
                    FOR SHARE;
                    IF NOT FOUND THEN
                        RETURN 'assignment_missing';
                    END IF;

                    RETURN 'authorized';
                END;
                $function$;

                REVOKE ALL ON FUNCTION projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean);
                """);
        }
    }
}
