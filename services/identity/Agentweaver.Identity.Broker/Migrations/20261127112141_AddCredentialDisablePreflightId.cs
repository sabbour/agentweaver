using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialDisablePreflightId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '30s';
                LOCK TABLE identity_broker.model_provider_credential_write_claims IN ACCESS EXCLUSIVE MODE;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "preflight_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "uuid",
                nullable: true);

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
                           NEW.issuer IS NOT NULL AND NEW.tenant IS NULL AND NEW.pending_secret_version IS NOT NULL AND
                           NEW.preflight_id IS NOT NULL THEN
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
                           NEW.expected_provider_revision, NEW.purpose, NEW.issuer, NEW.preflight_id)
                       IS DISTINCT FROM
                       ROW(OLD.operation_id, OLD.credential_slot_id, OLD.scope, OLD.project_id,
                           OLD.provider_id, OLD.vault_source, OLD.vault_uri, OLD.secret_name,
                           OLD.expected_revision, OLD.request_fingerprint, OLD.actor_id, OLD.claimed_at,
                           OLD.expected_provider_revision, OLD.purpose, OLD.issuer, OLD.preflight_id) THEN
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
                        WHERE state IN ('DisablePending', 'Disabled') OR preflight_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'credential_disable_claims_rollback_blocked' USING ERRCODE = '55000';
                    END IF;
                END;
                $guard$;
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

            migrationBuilder.DropColumn(
                name: "preflight_id",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");
        }
    }
}
