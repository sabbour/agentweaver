using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261027123000_EnvironmentP1CreationLinks")]
public sealed class EnvironmentP1CreationLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE TABLE environment.p1_creation_links (
                operation_id uuid NOT NULL,
                tenant_id varchar(256) NOT NULL,
                project_id varchar(256) NOT NULL,
                run_id varchar(256) NOT NULL,
                environment_id varchar(256) NOT NULL,
                resource_kind varchar(24) NOT NULL,
                resource_id varchar(512) NOT NULL,
                lifecycle_generation bigint NOT NULL,
                contract_version integer NOT NULL,
                core_invocation_id uuid NOT NULL,
                setup_invocation_id uuid NOT NULL,
                setup_receipt_hash char(64) NOT NULL,
                accepted_selection_hash char(64) NOT NULL,
                root_session_id varchar(256) NOT NULL,
                actor_issuer varchar(512) NOT NULL,
                actor_subject varchar(512) NOT NULL,
                membership_id uuid NOT NULL,
                membership_revision bigint NOT NULL,
                execution_fence bigint NOT NULL,
                root_state_version bigint NOT NULL,
                accepted_at timestamptz NOT NULL,
                link_state varchar(16) NOT NULL,
                result_resource_generation bigint NULL,
                result_provider_fencing_generation bigint NULL,
                result_transition_revision bigint NULL,
                result_lease_revision bigint NULL,
                result_provider_seam varchar(32) NULL,
                result_provider_id varchar(256) NULL,
                result_provider_resource_id varchar(512) NULL,
                result_provider_binding_json jsonb NULL,
                created_at timestamptz NOT NULL,
                completed_at timestamptz NULL,
                CONSTRAINT pk_environment_p1_creation_links PRIMARY KEY (operation_id),
                CONSTRAINT ck_environment_p1_creation_links_kind
                    CHECK (resource_kind IN ('Environment', 'WorkspaceVolumeMetadata', 'WorkspaceVolume', 'Sandbox')),
                CONSTRAINT ck_environment_p1_creation_links_hashes
                    CHECK (setup_receipt_hash ~ '^[0-9a-f]{64}$'
                        AND accepted_selection_hash ~ '^[0-9a-f]{64}$'),
                CONSTRAINT ck_environment_p1_creation_links_fence
                    CHECK (contract_version = 1 AND lifecycle_generation > 0
                        AND membership_revision > 0 AND execution_fence > 0
                        AND root_state_version > 0),
                CONSTRAINT ck_environment_p1_creation_links_state
                    CHECK (link_state IN ('Pending', 'Completed', 'Failed', 'Uncertain')),
                CONSTRAINT ck_environment_p1_creation_links_provider
                    CHECK ((result_provider_seam IS NULL
                            AND result_provider_id IS NULL
                            AND result_provider_resource_id IS NULL
                            AND result_provider_binding_json IS NULL)
                        OR (result_provider_seam IN ('Storage', 'Sandbox')
                            AND result_provider_id IS NOT NULL
                            AND result_provider_resource_id IS NOT NULL
                            AND result_provider_binding_json IS NOT NULL
                            AND jsonb_typeof(result_provider_binding_json) = 'object')),
                CONSTRAINT ck_environment_p1_creation_links_completion
                    CHECK ((link_state = 'Pending' AND completed_at IS NULL)
                        OR (link_state <> 'Pending' AND completed_at IS NOT NULL)),
                CONSTRAINT ck_environment_p1_creation_links_environment
                    CHECK (resource_kind <> 'Environment'
                        OR (link_state = 'Completed'
                            AND resource_id = environment_id
                            AND result_resource_generation IS NULL
                            AND result_provider_fencing_generation IS NULL
                            AND result_transition_revision IS NULL
                            AND result_lease_revision IS NULL
                            AND result_provider_seam IS NULL
                            AND result_provider_id IS NULL
                            AND result_provider_resource_id IS NULL
                            AND result_provider_binding_json IS NULL)),
                CONSTRAINT ck_environment_p1_creation_links_volume_metadata
                    CHECK (resource_kind <> 'WorkspaceVolumeMetadata'
                        OR (link_state = 'Completed'
                            AND result_transition_revision > 0
                            AND result_resource_generation = 0
                            AND result_provider_fencing_generation IS NULL
                            AND result_lease_revision IS NULL
                            AND result_provider_seam IS NULL
                            AND result_provider_id IS NULL
                            AND result_provider_resource_id IS NULL
                            AND result_provider_binding_json IS NULL)),
                CONSTRAINT ck_environment_p1_creation_links_volume
                    CHECK (resource_kind <> 'WorkspaceVolume'
                        OR (result_transition_revision IS NOT NULL
                            AND result_transition_revision > 0
                            AND result_resource_generation IS NOT NULL
                            AND result_resource_generation > 0
                            AND result_provider_fencing_generation IS NULL
                            AND result_lease_revision IS NULL
                            AND (link_state <> 'Completed'
                                OR (result_provider_seam = 'Storage'
                                    AND result_provider_id IS NOT NULL
                                    AND result_provider_resource_id IS NOT NULL)))),
                CONSTRAINT ck_environment_p1_creation_links_sandbox
                    CHECK (resource_kind <> 'Sandbox'
                        OR (result_resource_generation IS NOT NULL
                            AND result_resource_generation > 0
                            AND result_provider_fencing_generation IS NOT NULL
                            AND result_provider_fencing_generation > 0
                            AND result_transition_revision IS NULL
                            AND (link_state <> 'Completed'
                                OR (result_lease_revision IS NOT NULL
                                    AND result_lease_revision > 0
                                    AND result_provider_seam = 'Sandbox'
                                    AND result_provider_id IS NOT NULL
                                    AND result_provider_resource_id IS NOT NULL)))),
                CONSTRAINT fk_environment_p1_creation_links_owner
                    FOREIGN KEY (tenant_id, project_id, run_id, environment_id)
                    REFERENCES environment.owners
                        (tenant_id, project_id, run_id, environment_id)
                    ON DELETE RESTRICT
            );
            CREATE INDEX ix_environment_p1_creation_links_binding
                ON environment.p1_creation_links
                    (tenant_id, project_id, run_id, core_invocation_id, resource_kind);

            CREATE FUNCTION environment.enforce_p1_creation_link_immutability()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'P1 creation links are append-only'
                        USING ERRCODE = '55000';
                END IF;
                IF ROW(OLD.operation_id, OLD.tenant_id, OLD.project_id, OLD.run_id,
                       OLD.environment_id, OLD.resource_kind, OLD.resource_id,
                       OLD.lifecycle_generation, OLD.contract_version, OLD.core_invocation_id,
                       OLD.setup_invocation_id, OLD.setup_receipt_hash, OLD.accepted_selection_hash,
                       OLD.root_session_id, OLD.actor_issuer, OLD.actor_subject, OLD.membership_id,
                       OLD.membership_revision, OLD.execution_fence, OLD.root_state_version,
                       OLD.accepted_at, OLD.result_resource_generation,
                       OLD.result_provider_fencing_generation, OLD.result_transition_revision,
                       OLD.created_at)
                   IS DISTINCT FROM
                   ROW(NEW.operation_id, NEW.tenant_id, NEW.project_id, NEW.run_id,
                       NEW.environment_id, NEW.resource_kind, NEW.resource_id,
                       NEW.lifecycle_generation, NEW.contract_version, NEW.core_invocation_id,
                       NEW.setup_invocation_id, NEW.setup_receipt_hash, NEW.accepted_selection_hash,
                       NEW.root_session_id, NEW.actor_issuer, NEW.actor_subject, NEW.membership_id,
                       NEW.membership_revision, NEW.execution_fence, NEW.root_state_version,
                       NEW.accepted_at, NEW.result_resource_generation,
                       NEW.result_provider_fencing_generation, NEW.result_transition_revision,
                       NEW.created_at)
                    OR OLD.link_state <> 'Pending'
                    OR NEW.link_state NOT IN ('Completed', 'Failed', 'Uncertain')
                    OR NEW.completed_at IS NULL THEN
                    RAISE EXCEPTION 'P1 creation links are immutable after reservation'
                        USING ERRCODE = '55000';
                END IF;
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER p1_creation_links_immutable
                BEFORE UPDATE OR DELETE ON environment.p1_creation_links
                FOR EACH ROW EXECUTE FUNCTION environment.enforce_p1_creation_link_immutability();
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP TRIGGER p1_creation_links_immutable ON environment.p1_creation_links;
            DROP FUNCTION environment.enforce_p1_creation_link_immutability();
            DROP TABLE environment.p1_creation_links;
            """);
}
