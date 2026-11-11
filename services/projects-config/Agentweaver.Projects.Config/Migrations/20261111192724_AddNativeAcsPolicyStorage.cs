using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeAcsPolicyStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "native_acs_policy_change_audit",
                schema: "projects_config",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    previous_revision = table.Column<long>(type: "bigint", nullable: true),
                    change_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    previous_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_change_audit", x => x.audit_id);
                    table.CheckConstraint("ck_native_acs_policy_change_audit_hash", "content_sha256 ~ '^[0-9a-f]{64}$' AND (previous_content_sha256 IS NULL OR previous_content_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_native_acs_policy_change_audit_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                    table.CheckConstraint("ck_native_acs_policy_change_audit_type", "change_type IN ('created', 'updated')");
                });

            migrationBuilder.CreateTable(
                name: "native_acs_policy_heads",
                schema: "projects_config",
                columns: table => new
                {
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_heads", x => new { x.scope_type, x.scope_id, x.policy_id });
                    table.CheckConstraint("ck_native_acs_policy_heads_revision", "current_revision > 0");
                    table.CheckConstraint("ck_native_acs_policy_heads_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                });

            migrationBuilder.CreateTable(
                name: "native_acs_policy_idempotency",
                schema: "projects_config",
                columns: table => new
                {
                    scope_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    receipt = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_idempotency", x => x.scope_digest);
                    table.CheckConstraint("ck_native_acs_policy_idempotency_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                });

            migrationBuilder.CreateTable(
                name: "native_acs_policy_outbox",
                schema: "projects_config",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_outbox", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "native_acs_policy_revisions",
                schema: "projects_config",
                columns: table => new
                {
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    write_transaction_id = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValueSql: "pg_current_xact_id()::text"),
                    created_by_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_revisions", x => new { x.scope_type, x.scope_id, x.policy_id, x.revision });
                    table.CheckConstraint("ck_native_acs_policy_revisions_hash", "content_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_native_acs_policy_revisions_revision", "revision > 0");
                    table.CheckConstraint("ck_native_acs_policy_revisions_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                });

            migrationBuilder.CreateTable(
                name: "native_acs_policy_artifact_files",
                schema: "projects_config",
                columns: table => new
                {
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    relative_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    write_transaction_id = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValueSql: "pg_current_xact_id()::text")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_artifact_files", x => new { x.scope_type, x.scope_id, x.policy_id, x.revision, x.relative_path });
                    table.CheckConstraint("ck_native_acs_policy_artifact_files_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                    table.ForeignKey(
                        name: "FK_native_acs_policy_artifact_files_native_acs_policy_revision~",
                        columns: x => new { x.scope_type, x.scope_id, x.policy_id, x.revision },
                        principalSchema: "projects_config",
                        principalTable: "native_acs_policy_revisions",
                        principalColumns: new[] { "scope_type", "scope_id", "policy_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_native_acs_policy_change_audit_scope_type_scope_id_policy_i~",
                schema: "projects_config",
                table: "native_acs_policy_change_audit",
                columns: new[] { "scope_type", "scope_id", "policy_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_native_acs_policy_outbox_created_at",
                schema: "projects_config",
                table: "native_acs_policy_outbox",
                column: "created_at");

            migrationBuilder.Sql(
                """
                CREATE TRIGGER native_acs_policy_revisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_revisions_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER native_acs_policy_artifact_files_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_artifact_files
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_artifact_files_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_artifact_files
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE FUNCTION projects_config.require_native_acs_policy_file_insert_in_revision_transaction()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF NEW.write_transaction_id <> pg_catalog.pg_current_xact_id()::text
                        OR NOT EXISTS (
                            SELECT 1
                            FROM projects_config.native_acs_policy_revisions AS revision
                            WHERE revision.scope_type = NEW.scope_type
                              AND revision.scope_id = NEW.scope_id
                              AND revision.policy_id = NEW.policy_id
                              AND revision.revision = NEW.revision
                              AND revision.write_transaction_id = NEW.write_transaction_id) THEN
                        RAISE EXCEPTION 'native ACS policy files must be inserted with their revision'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION
                    projects_config.require_native_acs_policy_file_insert_in_revision_transaction()
                    FROM PUBLIC;
                CREATE TRIGGER native_acs_policy_artifact_files_insert_with_revision
                BEFORE INSERT ON projects_config.native_acs_policy_artifact_files
                FOR EACH ROW EXECUTE FUNCTION
                    projects_config.require_native_acs_policy_file_insert_in_revision_transaction();

                CREATE TRIGGER native_acs_policy_idempotency_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_idempotency
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_idempotency_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_idempotency
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER native_acs_policy_change_audit_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_change_audit
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_change_audit_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_change_audit
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER native_acs_policy_outbox_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_outbox
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_outbox_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_outbox
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE FUNCTION projects_config.require_native_acs_policy_head_advance()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.scope_type IS DISTINCT FROM OLD.scope_type
                        OR NEW.scope_id IS DISTINCT FROM OLD.scope_id
                        OR NEW.policy_id IS DISTINCT FROM OLD.policy_id
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at
                        OR NEW.current_revision <> OLD.current_revision + 1 THEN
                        RAISE EXCEPTION 'native ACS policy heads must advance by one revision'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER native_acs_policy_heads_advance
                BEFORE UPDATE ON projects_config.native_acs_policy_heads
                FOR EACH ROW EXECUTE FUNCTION projects_config.require_native_acs_policy_head_advance();

                CREATE FUNCTION projects_config.lock_native_acs_policy_authority(
                    p_membership_id uuid,
                    p_issuer text,
                    p_actor_id text,
                    p_tenant_id text,
                    p_membership_revision bigint,
                    p_scope_type text,
                    p_scope_id text,
                    p_grant_ids uuid[],
                    p_grant_resource_types text[],
                    p_grant_resource_ids text[],
                    p_grant_roles text[],
                    p_grant_revisions bigint[])
                RETURNS TABLE(assignment_id uuid, revision bigint, authorization_result text)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog
                AS $function$
                DECLARE
                    grant_index integer;
                BEGIN
                    IF p_membership_id IS NULL OR p_issuer IS NULL OR p_actor_id IS NULL
                        OR p_tenant_id IS NULL OR p_membership_revision IS NULL
                        OR p_membership_revision <= 0 OR p_scope_type IS NULL OR p_scope_id IS NULL
                        OR p_grant_ids IS NULL OR cardinality(p_grant_ids) = 0
                        OR p_grant_resource_types IS NULL OR p_grant_resource_ids IS NULL
                        OR p_grant_roles IS NULL OR p_grant_revisions IS NULL
                        OR cardinality(p_grant_ids) <> cardinality(p_grant_resource_types)
                        OR cardinality(p_grant_ids) <> cardinality(p_grant_resource_ids)
                        OR cardinality(p_grant_ids) <> cardinality(p_grant_roles)
                        OR cardinality(p_grant_ids) <> cardinality(p_grant_revisions)
                        OR array_position(p_grant_ids, NULL) IS NOT NULL
                        OR array_position(p_grant_resource_types, NULL) IS NOT NULL
                        OR array_position(p_grant_resource_ids, NULL) IS NOT NULL
                        OR array_position(p_grant_roles, NULL) IS NOT NULL
                        OR array_position(p_grant_revisions, NULL) IS NOT NULL THEN
                        RAISE EXCEPTION 'native ACS authority lock arguments are invalid'
                            USING ERRCODE = '22023';
                    END IF;

                    PERFORM 1
                    FROM projects_config.tenant_memberships AS membership
                    WHERE membership.membership_id = p_membership_id
                      AND membership.issuer = p_issuer
                      AND membership.subject = p_actor_id
                      AND membership.tenant_id = p_tenant_id
                      AND membership.revision = p_membership_revision
                      AND membership.state = 'Active'
                    FOR SHARE;
                    IF NOT FOUND THEN
                        RETURN QUERY SELECT NULL::uuid, NULL::bigint, 'forbidden'::text;
                        RETURN;
                    END IF;

                    IF p_scope_type = 'Project' THEN
                        PERFORM 1
                        FROM projects_config.projects AS project
                        WHERE project.project_id = p_scope_id
                          AND project.tenant_id = p_tenant_id
                          AND project.state = 'Active'
                        FOR SHARE;
                        IF NOT FOUND THEN
                            RETURN QUERY SELECT NULL::uuid, NULL::bigint, 'not_found'::text;
                            RETURN;
                        END IF;
                    ELSIF p_scope_type <> 'Platform' OR p_scope_id <> 'default' THEN
                        RETURN QUERY SELECT NULL::uuid, NULL::bigint, 'forbidden'::text;
                        RETURN;
                    END IF;

                    FOR grant_index IN 1 .. cardinality(p_grant_ids) LOOP
                        RETURN QUERY
                        SELECT assignment.assignment_id, assignment.revision, 'authorized'::text
                        FROM projects_config.project_role_assignments AS assignment
                        WHERE assignment.assignment_id = p_grant_ids[grant_index]
                          AND assignment.membership_id = p_membership_id
                          AND assignment.resource_type = p_grant_resource_types[grant_index]
                          AND assignment.resource_id = p_grant_resource_ids[grant_index]
                          AND assignment.role = p_grant_roles[grant_index]
                          AND assignment.revision = p_grant_revisions[grant_index]
                          AND assignment.state = 'Active'
                          AND (
                              (p_scope_type = 'Platform'
                                  AND assignment.resource_type = 'Platform'
                                  AND assignment.resource_id = 'default'
                                  AND assignment.role = 'PlatformAdmin')
                              OR
                              (p_scope_type = 'Project'
                                  AND ((assignment.resource_type = 'Project'
                                          AND assignment.resource_id = p_scope_id
                                          AND assignment.role = 'Owner')
                                      OR (assignment.resource_type = 'Tenant'
                                          AND assignment.resource_id = p_tenant_id
                                          AND assignment.role = 'TenantAdmin')))
                          )
                        FOR SHARE;
                        IF FOUND THEN
                            RETURN;
                        END IF;
                    END LOOP;

                    RETURN QUERY SELECT NULL::uuid, NULL::bigint, 'forbidden'::text;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.lock_native_acs_policy_authority(
                    uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[])
                    FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "native_acs_policy_artifact_files",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "native_acs_policy_change_audit",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "native_acs_policy_heads",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "native_acs_policy_idempotency",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "native_acs_policy_outbox",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "native_acs_policy_revisions",
                schema: "projects_config");

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS projects_config.lock_native_acs_policy_authority(
                    uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[]);
                DROP FUNCTION IF EXISTS projects_config.require_native_acs_policy_head_advance();
                DROP FUNCTION IF EXISTS projects_config.require_native_acs_policy_file_insert_in_revision_transaction();
                """);
        }
    }
}
