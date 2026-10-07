using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeCredentialAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_grant_heads",
                schema: "identity_broker",
                columns: table => new
                {
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_grant_heads", x => x.grant_id);
                    table.CheckConstraint("ck_runtime_grant_heads_revision", "current_revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "runtime_grant_operations",
                schema: "identity_broker",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_grant_operations", x => x.operation_id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_grant_revisions",
                schema: "identity_broker",
                columns: table => new
                {
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    runtime_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registration_revision = table.Column<long>(type: "bigint", nullable: false),
                    registration_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    registration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verifier_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    audience = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivery_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_grant_revisions", x => new { x.grant_id, x.revision });
                    table.CheckConstraint("ck_runtime_grant_hashes", "registration_hash ~ '^[0-9a-f]{64}$' AND verifier_hash ~ '^[0-9a-f]{64}$' AND configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_runtime_grant_lifetime", "expires_at > recorded_at OR state = 2");
                    table.CheckConstraint("ck_runtime_grant_purpose", "purpose IN (0, 1)");
                    table.CheckConstraint("ck_runtime_grant_revision", "revision > 0 AND registration_revision > 0");
                    table.CheckConstraint("ck_runtime_grant_state", "state IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_runtime_grant_revisions_runtime_grant_heads_grant_id",
                        column: x => x.grant_id,
                        principalSchema: "identity_broker",
                        principalTable: "runtime_grant_heads",
                        principalColumn: "grant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runtime_grant_operation_receipts",
                schema: "identity_broker",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_grant_operation_receipts", x => x.operation_id);
                    table.ForeignKey(
                        name: "FK_runtime_grant_operation_receipts_runtime_grant_operations_o~",
                        column: x => x.operation_id,
                        principalSchema: "identity_broker",
                        principalTable: "runtime_grant_operations",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_grant_revisions_runtime_instance_id",
                schema: "identity_broker",
                table: "runtime_grant_revisions",
                column: "runtime_instance_id");

            migrationBuilder.Sql("""
                CREATE FUNCTION identity_broker.reject_runtime_grant_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Runtime grant audit records are append-only';
                END;
                $$;
                CREATE TRIGGER runtime_grant_revisions_append_only
                    BEFORE UPDATE OR DELETE ON identity_broker.runtime_grant_revisions
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_runtime_grant_mutation();
                CREATE TRIGGER runtime_grant_operations_append_only
                    BEFORE UPDATE OR DELETE ON identity_broker.runtime_grant_operations
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_runtime_grant_mutation();
                CREATE TRIGGER runtime_grant_receipts_append_only
                    BEFORE UPDATE OR DELETE ON identity_broker.runtime_grant_operation_receipts
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_runtime_grant_mutation();

                CREATE FUNCTION identity_broker.require_runtime_grant_revision_advance()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE'
                       OR NEW.grant_id IS DISTINCT FROM OLD.grant_id
                       OR NEW.current_revision <> OLD.current_revision + 1 THEN
                        RAISE EXCEPTION 'Runtime grant revision must advance by one';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER runtime_grant_heads_revision_advance
                    BEFORE UPDATE OR DELETE ON identity_broker.runtime_grant_heads
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.require_runtime_grant_revision_advance();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER runtime_grant_heads_revision_advance ON identity_broker.runtime_grant_heads;
                DROP FUNCTION identity_broker.require_runtime_grant_revision_advance();
                DROP TRIGGER runtime_grant_receipts_append_only ON identity_broker.runtime_grant_operation_receipts;
                DROP TRIGGER runtime_grant_operations_append_only ON identity_broker.runtime_grant_operations;
                DROP TRIGGER runtime_grant_revisions_append_only ON identity_broker.runtime_grant_revisions;
                DROP FUNCTION identity_broker.reject_runtime_grant_mutation();
                """);

            migrationBuilder.DropTable(
                name: "runtime_grant_operation_receipts",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "runtime_grant_revisions",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "runtime_grant_operations",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "runtime_grant_heads",
                schema: "identity_broker");
        }
    }
}
