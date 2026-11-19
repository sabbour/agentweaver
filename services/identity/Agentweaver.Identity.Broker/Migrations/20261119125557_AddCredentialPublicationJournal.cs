using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialPublicationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(18)",
                maxLength: 18,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<long>(
                name: "expected_provider_revision",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuer",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_configuration_hash",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "published_provider_revision",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "credential_slot_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined', 'PublicationPending')");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "provider_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined', 'PublicationPending')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "((state = 'Claimed' AND quarantined_at IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Quarantined' AND quarantined_at IS NOT NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state IN ('Completed', 'PublicationPending', 'Published') AND quarantined_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END AND ((state = 'Completed' AND expected_provider_revision IS NULL) OR (state IN ('PublicationPending', 'Published') AND expected_provider_revision IS NOT NULL)))) AND ((state <> 'Published' AND published_provider_revision IS NULL AND published_configuration_hash IS NULL AND published_at IS NULL) OR (state = 'Published' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND CASE WHEN expected_provider_revision IS NOT NULL AND expected_provider_revision < 9223372036854775807 THEN published_provider_revision = expected_provider_revision + 1 ELSE FALSE END AND published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_publication_identity",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "(expected_provider_revision IS NULL AND purpose IS NULL AND issuer IS NULL AND tenant IS NULL) OR (expected_provider_revision IS NOT NULL AND expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807 AND purpose IS NOT NULL AND purpose = 'credential-publication' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND tenant IS NOT NULL AND octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "state IN ('Claimed', 'Quarantined', 'Completed', 'PublicationPending', 'Published')");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION identity_broker.guard_model_provider_credential_write_claim()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.state <> 'Claimed' OR NEW.quarantined_at IS NOT NULL OR
                           NEW.completed_revision IS NOT NULL OR NEW.completed_at IS NOT NULL OR
                           NEW.published_provider_revision IS NOT NULL OR
                           NEW.published_configuration_hash IS NOT NULL OR NEW.published_at IS NOT NULL THEN
                            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_initial_state_invalid';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_immutable';
                    END IF;
                    IF ROW(NEW.operation_id, NEW.credential_slot_id, NEW.scope, NEW.project_id,
                           NEW.provider_id, NEW.vault_source, NEW.vault_uri, NEW.secret_name,
                           NEW.expected_revision, NEW.request_fingerprint, NEW.actor_id, NEW.claimed_at,
                           NEW.expected_provider_revision, NEW.purpose, NEW.issuer, NEW.tenant)
                       IS DISTINCT FROM
                       ROW(OLD.operation_id, OLD.credential_slot_id, OLD.scope, OLD.project_id,
                           OLD.provider_id, OLD.vault_source, OLD.vault_uri, OLD.secret_name,
                           OLD.expected_revision, OLD.request_fingerprint, OLD.actor_id, OLD.claimed_at,
                           OLD.expected_provider_revision, OLD.purpose, OLD.issuer, OLD.tenant) THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_identity_immutable';
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Quarantined' AND
                       NEW.quarantined_at IS NOT NULL AND NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND
                       ((NEW.state = 'Completed' AND OLD.expected_provider_revision IS NULL AND OLD.purpose IS NULL AND OLD.issuer IS NULL AND OLD.tenant IS NULL) OR
                        (NEW.state = 'PublicationPending' AND OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL)) AND
                       NEW.quarantined_at IS NULL AND NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL AND
                       (CASE WHEN NEW.expected_revision < 9223372036854775807 THEN NEW.completed_revision = NEW.expected_revision + 1 ELSE FALSE END) AND
                       EXISTS (SELECT 1 FROM identity_broker.model_provider_credential_revisions r
                           WHERE r.operation_id = NEW.operation_id AND r.credential_slot_id = NEW.credential_slot_id AND
                                 r.revision = NEW.completed_revision AND r.scope = NEW.scope AND
                                 r.project_id IS NOT DISTINCT FROM NEW.project_id AND r.provider_id = NEW.provider_id AND
                                 r.vault_source = NEW.vault_source AND r.vault_uri = NEW.vault_uri AND
                                 r.secret_name = NEW.secret_name AND r.actor_id = NEW.actor_id AND
                                 r.mutation_kind = 'RecordReference' AND r.state = 'Enabled') THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'PublicationPending' AND NEW.state = 'Published' AND
                       OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND
                       OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.completed_revision IS NOT DISTINCT FROM OLD.completed_revision AND
                       NEW.completed_at IS NOT DISTINCT FROM OLD.completed_at AND
                       NEW.published_provider_revision IS NOT NULL AND NEW.published_configuration_hash IS NOT NULL AND NEW.published_at IS NOT NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_transition_invalid';
                END;
                $function$;
                DROP TRIGGER model_provider_credential_write_claim_guard ON identity_broker.model_provider_credential_write_claims;
                CREATE TRIGGER model_provider_credential_write_claim_guard
                BEFORE INSERT OR UPDATE OR DELETE ON identity_broker.model_provider_credential_write_claims
                FOR EACH ROW EXECUTE FUNCTION identity_broker.guard_model_provider_credential_write_claim();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE identity_broker.model_provider_credential_write_claims IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM identity_broker.model_provider_credential_write_claims WHERE expected_provider_revision IS NOT NULL) THEN
                        RAISE EXCEPTION 'credential_publication_journal_rollback_blocked';
                    END IF;
                END;
                $guard$;
                DROP TRIGGER model_provider_credential_write_claim_guard ON identity_broker.model_provider_credential_write_claims;
                CREATE OR REPLACE FUNCTION identity_broker.guard_model_provider_credential_write_claim()
                RETURNS trigger LANGUAGE plpgsql AS $function$
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
                       NEW.quarantined_at IS NOT NULL AND NEW.completed_revision IS NULL AND NEW.completed_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Completed' AND NEW.quarantined_at IS NULL AND
                       NEW.completed_revision = NEW.expected_revision + 1 AND NEW.completed_at IS NOT NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_transition_invalid';
                END;
                $function$;
                CREATE TRIGGER model_provider_credential_write_claim_guard
                BEFORE UPDATE OR DELETE ON identity_broker.model_provider_credential_write_claims
                FOR EACH ROW EXECUTE FUNCTION identity_broker.guard_model_provider_credential_write_claim();
                """);
            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_write_claims_publication_identity",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "expected_provider_revision",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "issuer",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "published_configuration_hash",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "published_provider_revision",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "purpose",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropColumn(
                name: "tenant",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(18)",
                oldMaxLength: 18);

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "credential_slot_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined')");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "provider_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "(state = 'Claimed' AND quarantined_at IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Quarantined' AND quarantined_at IS NOT NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Completed' AND quarantined_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "state IN ('Claimed', 'Quarantined', 'Completed')");
        }
    }
}
