using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class PersistPlatformPolicyPreparations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "native_acs_policy_preparations",
                schema: "projects_config",
                columns: table => new
                {
                    preparation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expected_head_policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    expected_head_revision = table.Column<long>(type: "bigint", nullable: true),
                    expected_head_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    proposed_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_acs_policy_preparations", x => x.preparation_id);
                    table.CheckConstraint("ck_native_acs_policy_preparations_expected_head", "(expected_head_policy_id IS NULL AND expected_head_revision IS NULL AND expected_head_content_sha256 IS NULL) OR (expected_head_policy_id IS NOT NULL AND expected_head_revision > 0 AND expected_head_content_sha256 IS NOT NULL)");
                    table.CheckConstraint("ck_native_acs_policy_preparations_hashes", "scope_digest ~ '^[0-9a-f]{64}$' AND request_digest ~ '^[0-9a-f]{64}$' AND proposed_content_sha256 ~ '^[0-9a-f]{64}$' AND (expected_head_content_sha256 IS NULL OR expected_head_content_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_native_acs_policy_preparations_scope", "scope_type = 'Platform' AND scope_id = 'default'");
                });

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_policy_preparations_scope_digest",
                schema: "projects_config",
                table: "native_acs_policy_preparations",
                column: "scope_digest",
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER native_acs_policy_preparations_immutable
                BEFORE UPDATE OR DELETE ON projects_config.native_acs_policy_preparations
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER native_acs_policy_preparations_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_policy_preparations
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "native_acs_policy_preparations",
                schema: "projects_config");
        }
    }
}
