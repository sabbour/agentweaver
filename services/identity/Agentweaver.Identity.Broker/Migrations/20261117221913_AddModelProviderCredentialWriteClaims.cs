using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderCredentialWriteClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_credential_write_claims",
                schema: "identity_broker",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    vault_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    vault_uri = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    secret_name = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    expected_revision = table.Column<long>(type: "bigint", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quarantined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_revision = table.Column<long>(type: "bigint", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_provider_credential_write_claims", x => x.operation_id);
                    table.CheckConstraint("ck_model_provider_credential_write_claims_byok_name", "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_outcome", "(state = 'Claimed' AND quarantined_at IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Quarantined' AND quarantined_at IS NOT NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Completed' AND quarantined_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_revision", "expected_revision >= 0");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_secret_name", "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_state", "state IN ('Claimed', 'Quarantined', 'Completed')");
                    table.CheckConstraint("ck_model_provider_credential_write_claims_vault_source", "vault_source IN ('Existing', 'Byok')");
                    table.ForeignKey(
                        name: "FK_model_provider_credential_write_claims_model_provider_crede~",
                        columns: x => new { x.credential_slot_id, x.completed_revision },
                        principalSchema: "identity_broker",
                        principalTable: "model_provider_credential_revisions",
                        principalColumns: new[] { "credential_slot_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "credential_slot_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined')");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id_c~",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                columns: new[] { "credential_slot_id", "completed_revision" });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "provider_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined')");

            migrationBuilder.Sql("""
                CREATE FUNCTION identity_broker.guard_model_provider_credential_write_claim()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_immutable';
                    END IF;
                    IF ROW(NEW.operation_id, NEW.credential_slot_id, NEW.scope, NEW.project_id,
                           NEW.provider_id, NEW.vault_source, NEW.vault_uri, NEW.secret_name,
                           NEW.expected_revision, NEW.request_fingerprint, NEW.actor_id, NEW.claimed_at)
                       IS DISTINCT FROM
                       ROW(OLD.operation_id, OLD.credential_slot_id, OLD.scope, OLD.project_id,
                           OLD.provider_id, OLD.vault_source, OLD.vault_uri, OLD.secret_name,
                           OLD.expected_revision, OLD.request_fingerprint, OLD.actor_id, OLD.claimed_at) THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_identity_immutable';
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Quarantined' AND
                       NEW.quarantined_at IS NOT NULL AND NEW.completed_revision IS NULL AND
                       NEW.completed_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Completed' AND
                       NEW.quarantined_at IS NULL AND
                       NEW.completed_revision = NEW.expected_revision + 1 AND
                       NEW.completed_at IS NOT NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_transition_invalid';
                END;
                $function$;

                CREATE TRIGGER model_provider_credential_write_claim_guard
                BEFORE UPDATE OR DELETE ON identity_broker.model_provider_credential_write_claims
                FOR EACH ROW EXECUTE FUNCTION identity_broker.guard_model_provider_credential_write_claim();

                CREATE FUNCTION identity_broker.reject_model_provider_credential_write_claim_truncate()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_immutable';
                    RETURN NULL;
                END;
                $function$;

                CREATE TRIGGER model_provider_credential_write_claim_truncate_guard
                BEFORE TRUNCATE ON identity_broker.model_provider_credential_write_claims
                FOR EACH STATEMENT EXECUTE FUNCTION identity_broker.reject_model_provider_credential_write_claim_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER model_provider_credential_write_claim_truncate_guard
                    ON identity_broker.model_provider_credential_write_claims;
                DROP TRIGGER model_provider_credential_write_claim_guard
                    ON identity_broker.model_provider_credential_write_claims;
                DROP FUNCTION identity_broker.reject_model_provider_credential_write_claim_truncate();
                DROP FUNCTION identity_broker.guard_model_provider_credential_write_claim();
                """);
            migrationBuilder.DropTable(
                name: "model_provider_credential_write_claims",
                schema: "identity_broker");
        }
    }
}
