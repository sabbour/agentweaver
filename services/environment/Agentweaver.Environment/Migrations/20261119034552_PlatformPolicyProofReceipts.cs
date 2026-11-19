using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class PlatformPolicyProofReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_policy_proof_holds",
                schema: "environment",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    physical_target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "character varying(63)", maxLength: 63, nullable: false),
                    namespace_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    seal_epoch = table.Column<long>(type: "bigint", nullable: false),
                    guard_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    profile_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    registry_revision = table.Column<long>(type: "bigint", nullable: false),
                    preparation_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_policy_proof_holds", x => x.hold_id);
                    table.CheckConstraint("ck_environment_platform_policy_proof_holds_hashes", "guard_digest ~ '^[0-9a-f]{64}$' AND profile_digest ~ '^[0-9a-f]{64}$' AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_environment_platform_policy_proof_holds_versions", "seal_epoch > 0 AND registry_revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "platform_policy_proof_receipts",
                schema: "environment",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_epoch = table.Column<long>(type: "bigint", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    intent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    compiler_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    evaluator_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ceiling_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    profile_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    guard_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    spec_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    post_write_resource_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    object_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    policy_generation = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_policy_proof_receipts", x => x.receipt_id);
                    table.CheckConstraint("ck_environment_platform_policy_proof_receipts_hashes", "source_hash ~ '^[0-9a-f]{64}$' AND intent_hash ~ '^[0-9a-f]{64}$' AND profile_digest ~ '^[0-9a-f]{64}$' AND guard_digest ~ '^[0-9a-f]{64}$' AND snapshot_id ~ '^[0-9a-f]{64}$' AND spec_digest ~ '^[0-9a-f]{64}$' AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_environment_platform_policy_proof_receipts_snapshot", "jsonb_typeof(snapshot_json) = 'object'");
                    table.CheckConstraint("ck_environment_platform_policy_proof_receipts_versions", "source_epoch > 0 AND source_revision > 0 AND policy_generation > 0");
                    table.ForeignKey(
                        name: "FK_platform_policy_proof_receipts_platform_policy_proof_holds_~",
                        column: x => x.hold_id,
                        principalSchema: "environment",
                        principalTable: "platform_policy_proof_holds",
                        principalColumn: "hold_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_environment_platform_policy_proof_holds_physical_namespace",
                schema: "environment",
                table: "platform_policy_proof_holds",
                columns: new[] { "physical_target", "namespace_uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_environment_platform_policy_proof_holds_reservation",
                schema: "environment",
                table: "platform_policy_proof_holds",
                column: "reservation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_environment_platform_policy_proof_holds_target",
                schema: "environment",
                table: "platform_policy_proof_holds",
                column: "target_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_environment_platform_policy_proof_receipts_hold",
                schema: "environment",
                table: "platform_policy_proof_receipts",
                columns: new[] { "hold_id", "target_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION environment.reject_platform_policy_proof_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'platform policy proof records are append-only'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE FUNCTION environment.enforce_platform_policy_proof_receipt_hold()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM environment.platform_policy_proof_holds hold
                        WHERE hold.hold_id = NEW.hold_id
                          AND hold.target_id = NEW.target_id
                          AND hold.reservation_id = NEW.reservation_id
                          AND hold.profile_digest = NEW.profile_digest
                          AND hold.guard_digest = NEW.guard_digest
                    ) THEN
                        RAISE EXCEPTION 'platform policy proof receipt does not match its hold'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER platform_policy_proof_receipts_hold_identity
                    BEFORE INSERT ON environment.platform_policy_proof_receipts
                    FOR EACH ROW EXECUTE FUNCTION environment.enforce_platform_policy_proof_receipt_hold();
                CREATE TRIGGER platform_policy_proof_holds_immutable
                    BEFORE UPDATE OR DELETE ON environment.platform_policy_proof_holds
                    FOR EACH ROW EXECUTE FUNCTION environment.reject_platform_policy_proof_mutation();
                CREATE TRIGGER platform_policy_proof_holds_no_truncate
                    BEFORE TRUNCATE ON environment.platform_policy_proof_holds
                    FOR EACH STATEMENT EXECUTE FUNCTION environment.reject_platform_policy_proof_mutation();
                CREATE TRIGGER platform_policy_proof_receipts_immutable
                    BEFORE UPDATE OR DELETE ON environment.platform_policy_proof_receipts
                    FOR EACH ROW EXECUTE FUNCTION environment.reject_platform_policy_proof_mutation();
                CREATE TRIGGER platform_policy_proof_receipts_no_truncate
                    BEFORE TRUNCATE ON environment.platform_policy_proof_receipts
                    FOR EACH STATEMENT EXECUTE FUNCTION environment.reject_platform_policy_proof_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER platform_policy_proof_receipts_no_truncate
                    ON environment.platform_policy_proof_receipts;
                DROP TRIGGER platform_policy_proof_receipts_immutable
                    ON environment.platform_policy_proof_receipts;
                DROP TRIGGER platform_policy_proof_receipts_hold_identity
                    ON environment.platform_policy_proof_receipts;
                DROP TRIGGER platform_policy_proof_holds_no_truncate
                    ON environment.platform_policy_proof_holds;
                DROP TRIGGER platform_policy_proof_holds_immutable
                    ON environment.platform_policy_proof_holds;
                DROP FUNCTION environment.enforce_platform_policy_proof_receipt_hold();
                DROP FUNCTION environment.reject_platform_policy_proof_mutation();
                """);

            migrationBuilder.DropTable(
                name: "platform_policy_proof_receipts",
                schema: "environment");

            migrationBuilder.DropTable(
                name: "platform_policy_proof_holds",
                schema: "environment");
        }
    }
}
