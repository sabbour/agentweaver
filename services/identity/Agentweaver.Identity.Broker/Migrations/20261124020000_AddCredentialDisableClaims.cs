using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialDisableClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '30s';
                LOCK TABLE identity_broker.model_provider_credential_write_claims IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE identity_broker.model_provider_credential_revisions IN SHARE MODE;
                """);

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

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "credential_slot_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined', 'PublicationPending', 'DisablePending')");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                column: "provider_id",
                unique: true,
                filter: "state IN ('Claimed', 'Quarantined', 'PublicationPending', 'DisablePending')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "state IN ('Claimed', 'Quarantined', 'Completed', 'PublicationPending', 'Published', 'Cancelled', 'DisablePending', 'Disabled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_publication_identity",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "(expected_provider_revision IS NULL AND purpose IS NULL AND issuer IS NULL AND tenant IS NULL) OR (expected_provider_revision IS NOT NULL AND expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807 AND purpose IS NOT NULL AND purpose = 'credential-publication' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND tenant IS NOT NULL AND octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$') OR ((expected_provider_revision IS NULL OR (expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807)) AND purpose IS NOT NULL AND purpose = 'credential-disable' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND (tenant IS NULL OR (octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$')))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "((state = 'Claimed' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Quarantined' AND quarantined_at IS NOT NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Cancelled' AND cancelled_at IS NOT NULL) OR (state = 'PublicationPending' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NULL AND completed_at IS NULL AND expected_provider_revision IS NOT NULL) OR (state IN ('Completed', 'Published') AND quarantined_at IS NULL AND cancelled_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END AND ((state = 'Completed' AND pending_secret_version IS NULL AND expected_provider_revision IS NULL) OR (state = 'Published' AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND expected_provider_revision IS NOT NULL))) OR (state = 'DisablePending' AND purpose = 'credential-disable' AND tenant IS NULL AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Disabled' AND purpose = 'credential-disable' AND tenant IS NOT NULL AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END)) AND ((state NOT IN ('Published', 'Disabled') AND published_provider_revision IS NULL AND published_configuration_hash IS NULL AND published_at IS NULL) OR (state = 'Published' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND CASE WHEN expected_provider_revision IS NOT NULL AND expected_provider_revision < 9223372036854775807 THEN published_provider_revision = expected_provider_revision + 1 ELSE FALSE END AND published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$') OR (state = 'Disabled' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND (expected_provider_revision IS NULL OR (expected_provider_revision < 9223372036854775807 AND published_provider_revision = expected_provider_revision + 1)) AND published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$'))");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION identity_broker.guard_model_provider_credential_write_claim()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.dispatched_at IS NOT NULL OR
                           NEW.quarantined_at IS NOT NULL OR NEW.recovered_at IS NOT NULL OR NEW.cancelled_at IS NOT NULL OR
                           NEW.completed_revision IS NOT NULL OR NEW.completed_at IS NOT NULL OR
                           NEW.published_provider_revision IS NOT NULL OR
                           NEW.published_configuration_hash IS NOT NULL OR NEW.published_at IS NOT NULL THEN
                            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_initial_state_invalid';
                        END IF;
                        IF NEW.state = 'Claimed' AND NEW.pending_secret_version IS NULL THEN
                            RETURN NEW;
                        END IF;
                        IF NEW.state = 'DisablePending' AND NEW.purpose = 'credential-disable' AND
                           NEW.issuer IS NOT NULL AND NEW.tenant IS NULL AND NEW.pending_secret_version IS NOT NULL THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_initial_state_invalid';
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_immutable';
                    END IF;
                    IF ROW(NEW.operation_id, NEW.credential_slot_id, NEW.scope, NEW.project_id,
                           NEW.provider_id, NEW.vault_source, NEW.vault_uri, NEW.secret_name,
                           NEW.expected_revision, NEW.request_fingerprint, NEW.actor_id, NEW.claimed_at,
                           NEW.expected_provider_revision, NEW.purpose, NEW.issuer)
                       IS DISTINCT FROM
                       ROW(OLD.operation_id, OLD.credential_slot_id, OLD.scope, OLD.project_id,
                           OLD.provider_id, OLD.vault_source, OLD.vault_uri, OLD.secret_name,
                           OLD.expected_revision, OLD.request_fingerprint, OLD.actor_id, OLD.claimed_at,
                           OLD.expected_provider_revision, OLD.purpose, OLD.issuer) THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_identity_immutable';
                    END IF;
                    IF OLD.tenant IS NOT NULL AND NEW.tenant IS DISTINCT FROM OLD.tenant THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_identity_immutable';
                    END IF;
                    IF OLD.dispatched_at IS NOT NULL AND NEW.dispatched_at IS DISTINCT FROM OLD.dispatched_at THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_dispatch_immutable';
                    END IF;
                    IF OLD.pending_secret_version IS NOT NULL AND NEW.pending_secret_version IS DISTINCT FROM OLD.pending_secret_version THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_pending_version_immutable';
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Claimed' AND
                       OLD.dispatched_at IS NULL AND NEW.dispatched_at IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Quarantined' AND
                       NEW.quarantined_at IS NOT NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Completed' AND
                       NEW.recovered_at IS NULL AND
                       OLD.expected_provider_revision IS NULL AND OLD.purpose IS NULL AND OLD.issuer IS NULL AND OLD.tenant IS NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
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
                    IF ((OLD.state = 'Claimed' AND NEW.recovered_at IS NULL) OR
                        (OLD.state = 'Quarantined' AND OLD.dispatched_at IS NOT NULL AND NEW.recovered_at IS NOT NULL)) AND
                       NEW.state = 'PublicationPending' AND
                       OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND
                       NEW.pending_secret_version IS NOT NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'PublicationPending' AND NEW.state = 'Published' AND
                       OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND
                       OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND
                       NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
                       (CASE WHEN NEW.expected_revision < 9223372036854775807 THEN NEW.completed_revision = NEW.expected_revision + 1 ELSE FALSE END) AND
                       NEW.published_provider_revision IS NOT NULL AND NEW.published_configuration_hash IS NOT NULL AND NEW.published_at IS NOT NULL AND
                       EXISTS (SELECT 1 FROM identity_broker.model_provider_credential_revisions r
                           WHERE r.operation_id = NEW.operation_id AND r.credential_slot_id = NEW.credential_slot_id AND
                                 r.revision = NEW.completed_revision AND r.scope = NEW.scope AND
                                 r.project_id IS NOT DISTINCT FROM NEW.project_id AND r.provider_id = NEW.provider_id AND
                                 r.vault_source = NEW.vault_source AND r.vault_uri = NEW.vault_uri AND
                                 r.secret_name = NEW.secret_name AND r.secret_version = NEW.pending_secret_version AND
                                 r.actor_id = NEW.actor_id AND
                                 r.mutation_kind = 'RecordReference' AND r.state = 'Enabled') THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state IN ('Claimed', 'Quarantined', 'PublicationPending') AND NEW.state = 'Cancelled' AND
                       NEW.cancelled_at IS NOT NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'DisablePending' AND NEW.state = 'Disabled' AND
                       OLD.purpose = 'credential-disable' AND OLD.tenant IS NULL AND NEW.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND
                       NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
                       (CASE WHEN NEW.expected_revision < 9223372036854775807 THEN NEW.completed_revision = NEW.expected_revision + 1 ELSE FALSE END) AND
                       NEW.published_provider_revision IS NOT NULL AND NEW.published_configuration_hash IS NOT NULL AND NEW.published_at IS NOT NULL AND
                       EXISTS (SELECT 1 FROM identity_broker.model_provider_credential_revisions r
                           WHERE r.operation_id = NEW.operation_id AND r.credential_slot_id = NEW.credential_slot_id AND
                                 r.revision = NEW.completed_revision AND r.scope = NEW.scope AND
                                 r.project_id IS NOT DISTINCT FROM NEW.project_id AND r.provider_id = NEW.provider_id AND
                                 r.vault_source = NEW.vault_source AND r.vault_uri = NEW.vault_uri AND
                                 r.secret_name = NEW.secret_name AND r.secret_version = NEW.pending_secret_version AND
                                 r.actor_id = NEW.actor_id AND
                                 r.mutation_kind = 'Disable' AND r.state = 'Disabled') THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'DisablePending' AND NEW.state = 'Cancelled' AND
                       OLD.purpose = 'credential-disable' AND OLD.tenant IS NULL AND NEW.tenant IS NOT NULL AND
                       NEW.cancelled_at IS NOT NULL AND NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_transition_invalid';
                END;
                $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '30s';
                LOCK TABLE identity_broker.model_provider_credential_write_claims IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM identity_broker.model_provider_credential_write_claims
                        WHERE state IN ('DisablePending', 'Disabled')) THEN
                        RAISE EXCEPTION 'credential_disable_claims_rollback_blocked' USING ERRCODE = '55000';
                    END IF;
                END;
                $guard$;
                """);

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

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_credential_slot_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

            migrationBuilder.DropIndex(
                name: "IX_model_provider_credential_write_claims_provider_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");

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
                name: "ck_model_provider_credential_write_claims_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "state IN ('Claimed', 'Quarantined', 'Completed', 'PublicationPending', 'Published', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_publication_identity",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "(expected_provider_revision IS NULL AND purpose IS NULL AND issuer IS NULL AND tenant IS NULL) OR (expected_provider_revision IS NOT NULL AND expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807 AND purpose IS NOT NULL AND purpose = 'credential-publication' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND tenant IS NOT NULL AND octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_write_claims_outcome",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                sql: "((state = 'Claimed' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Quarantined' AND quarantined_at IS NOT NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR (state = 'Cancelled' AND cancelled_at IS NOT NULL) OR (state = 'PublicationPending' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NULL AND completed_at IS NULL AND expected_provider_revision IS NOT NULL) OR (state IN ('Completed', 'Published') AND quarantined_at IS NULL AND cancelled_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END AND ((state = 'Completed' AND pending_secret_version IS NULL AND expected_provider_revision IS NULL) OR (state = 'Published' AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND expected_provider_revision IS NOT NULL)))) AND ((state <> 'Published' AND published_provider_revision IS NULL AND published_configuration_hash IS NULL AND published_at IS NULL) OR (state = 'Published' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND CASE WHEN expected_provider_revision IS NOT NULL AND expected_provider_revision < 9223372036854775807 THEN published_provider_revision = expected_provider_revision + 1 ELSE FALSE END AND published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$'))");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION identity_broker.guard_model_provider_credential_write_claim()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.state <> 'Claimed' OR NEW.dispatched_at IS NOT NULL OR
                           NEW.quarantined_at IS NOT NULL OR NEW.recovered_at IS NOT NULL OR NEW.cancelled_at IS NOT NULL OR
                           NEW.pending_secret_version IS NOT NULL OR
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
                    IF OLD.dispatched_at IS NOT NULL AND NEW.dispatched_at IS DISTINCT FROM OLD.dispatched_at THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_dispatch_immutable';
                    END IF;
                    IF OLD.pending_secret_version IS NOT NULL AND NEW.pending_secret_version IS DISTINCT FROM OLD.pending_secret_version THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_pending_version_immutable';
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Claimed' AND
                       OLD.dispatched_at IS NULL AND NEW.dispatched_at IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Quarantined' AND
                       NEW.quarantined_at IS NOT NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'Claimed' AND NEW.state = 'Completed' AND
                       NEW.recovered_at IS NULL AND
                       OLD.expected_provider_revision IS NULL AND OLD.purpose IS NULL AND OLD.issuer IS NULL AND OLD.tenant IS NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND NEW.pending_secret_version IS NULL AND
                       NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
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
                    IF ((OLD.state = 'Claimed' AND NEW.recovered_at IS NULL) OR
                        (OLD.state = 'Quarantined' AND OLD.dispatched_at IS NOT NULL AND NEW.recovered_at IS NOT NULL)) AND
                       NEW.state = 'PublicationPending' AND
                       OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND
                       NEW.pending_secret_version IS NOT NULL AND
                       NEW.completed_revision IS NULL AND NEW.completed_at IS NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state = 'PublicationPending' AND NEW.state = 'Published' AND
                       OLD.expected_provider_revision IS NOT NULL AND OLD.purpose IS NOT NULL AND
                       OLD.issuer IS NOT NULL AND OLD.tenant IS NOT NULL AND
                       NEW.quarantined_at IS NULL AND NEW.cancelled_at IS NULL AND
                       NEW.completed_revision IS NOT NULL AND NEW.completed_at IS NOT NULL AND
                       (CASE WHEN NEW.expected_revision < 9223372036854775807 THEN NEW.completed_revision = NEW.expected_revision + 1 ELSE FALSE END) AND
                       NEW.published_provider_revision IS NOT NULL AND NEW.published_configuration_hash IS NOT NULL AND NEW.published_at IS NOT NULL AND
                       EXISTS (SELECT 1 FROM identity_broker.model_provider_credential_revisions r
                           WHERE r.operation_id = NEW.operation_id AND r.credential_slot_id = NEW.credential_slot_id AND
                                 r.revision = NEW.completed_revision AND r.scope = NEW.scope AND
                                 r.project_id IS NOT DISTINCT FROM NEW.project_id AND r.provider_id = NEW.provider_id AND
                                 r.vault_source = NEW.vault_source AND r.vault_uri = NEW.vault_uri AND
                                 r.secret_name = NEW.secret_name AND r.secret_version = NEW.pending_secret_version AND
                                 r.actor_id = NEW.actor_id AND
                                 r.mutation_kind = 'RecordReference' AND r.state = 'Enabled') THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state IN ('Claimed', 'Quarantined', 'PublicationPending') AND NEW.state = 'Cancelled' AND
                       NEW.cancelled_at IS NOT NULL AND
                       NEW.published_provider_revision IS NULL AND NEW.published_configuration_hash IS NULL AND NEW.published_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_write_claim_transition_invalid';
                END;
                $function$;
                """);
        }
    }
}
