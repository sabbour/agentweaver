/**
 * Defines the deterministic PostgreSQL grants plan for the P1 service principals.
 * This module only builds and hashes the plan; its CLI mode emits JSON and never connects to a database.
 */
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

/** Fixed target identity used to prevent the P1 grants plan from drifting to another database. */
export const P1_POSTGRES_TARGET = Object.freeze({
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'agentweaver',
  clusterName: 'agentweaver-aks',
  postgresHost: 'agentweaver-pg.postgres.database.azure.com',
  database: 'agentweaver',
  operatorName: 'asabbour@microsoft.com',
  operatorObjectId: 'c4279823-a7c7-477e-982e-f4145bb0a83d',
  connectionHost: '127.0.0.1',
  connectionPort: 15432,
  sslMode: 'VerifyFull',
});

const SERVICES = [
  ['projects-config', 'projects_config'],
  ['environment', 'environment'],
  ['knowledge', 'knowledge'],
  ['events-and-sessions', 'events_sessions'],
  ['orchestrator', 'orchestrator'],
];

// Revalidate these target-specific IDs against the current deployment outputs before initialization.
// The P1 role names are fixed product names on every target; only the Entra object IDs differ.
const PRINCIPALS = Object.freeze({
  agentweaver: [
    'df4f3934-9585-47c7-b923-d9abca981dfe',
    '3f94ddf2-a376-4d8e-8e8f-ad24e96f7417',
    '9bc761f0-f192-41c6-a2d7-bbee1b83a1f2',
    '7045d349-8e2d-40d1-a492-039540465780',
    'bb6f5b78-1f0e-4e56-a93c-31d96d9e1aa7',
  ],
  awwus2np01: [
    '2bd657f7-a859-4bf1-815c-09f4e0d32db9',
    '78856a22-8d8a-4cca-bcd3-ef2031fb4a48',
    'b8a20ba6-e488-4640-b9a1-3d5d4d9c6678',
    '7004c77e-5fa0-454c-a217-9d6f0fde2ce3',
    '98a5cdfb-0b79-4fb3-b21c-4df561d43282',
  ],
});

/** Returns the P1 PostgreSQL target for a registered install target name. */
export function p1PostgresTarget(name = 'agentweaver') {
  if (!Object.hasOwn(PRINCIPALS, name)) throw new Error(`No P1 PostgreSQL plan is registered for target '${name}'.`);
  if (name === 'agentweaver') return P1_POSTGRES_TARGET;
  return Object.freeze({
    ...P1_POSTGRES_TARGET,
    resourceGroup: name,
    clusterName: `${name}-aks`,
    postgresHost: `${name}-pg.postgres.database.azure.com`,
  });
}

const GROUPS = {
  projects_config: {
    // Memberships stay read-only. The role-assignment API adds assignments and audit rows, and revokes an
    // assignment only through the column-scoped UPDATE in UPDATE_COLUMNS below.
    SELECT: 'tenant_memberships __ef_migrations_history',
    'SELECT,UPDATE': 'platform_runtime_heads',
    'SELECT,INSERT,UPDATE': 'projects marketplace_sources project_casting_proposals casting_generation_requests hosted_model_reasoning_catalog model_providers',
    'SELECT,INSERT': 'project_configuration_revisions platform_runtime_revisions project_run_selections project_skill_content_revisions project_skill_import_idempotency project_skill_content_revocations project_skill_trust_evidence project_role_assignments authority_audit model_provider_revisions model_provider_reference_reservations native_acs_policy_heads native_acs_policy_preparations native_acs_policy_revisions native_acs_policy_artifact_files native_acs_policy_idempotency native_acs_policy_change_audit native_acs_policy_outbox project_governance_decision_stream_heads project_governance_decisions project_governance_decision_outbox project_governance_decision_checkpoints',
  },
  environment: {
    SELECT: '__ef_migrations_history',
    'SELECT,INSERT,UPDATE': 'owners owner_effects workspace_volume_cleanup sandbox_leases sandbox_provider_attempts sandbox_late_resource_cleanups remote_mcp_connections p1_creation_links',
    'SELECT,INSERT': 'lifecycle_operations remote_mcp_connection_configurations remote_mcp_catalog_snapshots remote_mcp_connection_idempotency provider_lifecycle_reports runtime_bootstrap_profile_replacements platform_policy_apply_reservations platform_policy_proof_holds platform_policy_proof_receipts sandbox_owner_mappings sandbox_egress_flows_attributed sandbox_egress_flows_unattributed',
  },
  knowledge: {
    SELECT: 'knowledge_schema_migrations outbox_schema_migrations',
    'SELECT,INSERT,UPDATE': 'knowledge_records knowledge_write_idempotency outbox_streams outbox_events',
    'SELECT,INSERT': 'knowledge_revisions memory_provider_bindings consumer_inbox_receipts',
  },
  events_sessions: {
    SELECT: 'sessions_schema_migrations outbox_schema_migrations',
    'SELECT,INSERT,UPDATE': 'session_run_streams project_fact_streams addressed_messages outbox_streams outbox_events session_object_inventory',
    'SELECT,INSERT,UPDATE,DELETE': 'addressed_message_threads',
    'SELECT,INSERT': 'messaging_provider_bindings session_events session_object_references project_facts usage_run_cost_bindings usage_source_receipts consumer_inbox_receipts sessions session_provider_bindings session_fork_lineage usage_rate_cards usage_ledger',
  },
  orchestrator: {
    SELECT: 'coordination_schema_migrations outbox_schema_migrations',
    // Migration 023 grants mutable requests and append-only cancellation receipts separately.
    // Migration 024 grants private Chief of Staff rows; only Priority links are deleted.
    // Migration 025 grants caller-specific dismissal receipts as append-only rows.
    // Migration 026 adds backlog task columns only; 027 grants issue sync settings and receipts.
    // Migration 028 grants the Guardrails evidence table insert-only, because evidence is append-only.
    // Migration 029 grants spend_budget_reservations and spend_budget_accounting_cursors without DELETE;
    // their guard triggers also reject deletes and allow only forward updates.
    // Migration 030 grants append-only plan promotion and task origin rows.
    // Migration 031 grants caller dismissals of promotion notices insert-only; a dismissal is never undone.
    // Migration 035 adds persisted backlog queue positions on the existing backlog task table.
    // Migration 036 grants root model-turn admissions insert-only; their rows are immutable.
    // Migration 037 grants child-spawn authorization and consumption receipts insert-only.
    // Migration 038 records the result of an idempotent Source Control pull-request operation.
    // Migration 039 adds an immutable Orchestrator-owned runtime evidence ledger.
    // Migration 040 adds immutable P1 run-request provenance and accepted invocation bindings.
    // Migration 046 grants only the durable workflow-generation job table; jobs retain no credentials.
    'SELECT,INSERT,UPDATE': 'accepted_runs coordination_sessions coordination_messages coordination_requests parent_notifications coordinator_gates executable_action_grants coordination_tree_commands coordination_idle_subscriptions coordination_execution_operations runtime_registration_heads source_control_merge_intents source_control_output_captures maf_execution_run_guards maf_execution_dispatches outbox_streams outbox_events backlog_projects project_run_requests chief_of_staff_priority_lists chief_of_staff_priorities issue_sync_projects issue_sync_receipts source_control_pull_requests source_control_pull_request_operations spend_budget_reservations spend_budget_accounting_cursors runtime_run_start_intents runtime_replacement_operations workflow_generation_jobs',
    'SELECT,INSERT,UPDATE,DELETE': 'backlog_tasks',
    'SELECT,INSERT,DELETE': 'backlog_dependencies chief_of_staff_priority_links runtime_observation_v2_cursors',
    'SELECT,INSERT': 'coordinator_decision_outbox policy_evaluation_receipts coordinator_run_selection_contexts coordinator_run_selection_context_versions coordination_idle_notifications runtime_registration_revisions runtime_sdk_sources runtime_usage_observations source_control_repository_pins source_control_webhook_deliveries maf_execution_output_witnesses consumer_inbox_receipts coordinator_decisions maf_workflow_checkpoints session_consistency_manifests reviewed_remote_tool_snapshots project_run_cancellations chief_of_staff_conversations user_notification_dismissals guardrails_evaluations backlog_plan_promotions backlog_task_origins user_notification_promotion_dismissals maf_root_model_turn_admissions maf_child_spawn_authorization_receipts maf_child_spawn_authorization_consumptions maf_runtime_evidence_receipts p1_run_request_provenance p1_invocation_bindings',
  },
};

const LOCK_GUARDS = {
  events_sessions: {
    messaging_provider_bindings: {
      column: 'run_id', trigger: 'messaging_provider_bindings_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 19,
      message: 'Session identity, provider binding, and fork lineage records cannot be updated.',
      sqlState: '55000',
    },
    sessions: {
      column: 'session_id', trigger: 'sessions_no_update', function: 'reject_row_lock_record_update()',
      triggerType: 19, message: 'Session identity, provider binding, and fork lineage records cannot be updated.',
      sqlState: '55000',
    },
    session_provider_bindings: {
      column: 'run_id', trigger: 'session_provider_bindings_no_update', function: 'reject_row_lock_record_update()',
      triggerType: 19, message: 'Session identity, provider binding, and fork lineage records cannot be updated.',
      sqlState: '55000',
    },
    session_fork_lineage: {
      column: 'target_session_id', trigger: 'session_fork_lineage_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 19,
      message: 'Session identity, provider binding, and fork lineage records cannot be updated.', sqlState: '55000',
    },
    usage_rate_cards: {
      column: 'card_id', trigger: 'usage_rate_cards_immutable', function: 'reject_usage_history_mutation()',
      triggerType: 27, message: 'usage accounting history is append-only', sqlState: null,
    },
    usage_ledger: {
      column: 'event_id', trigger: 'usage_ledger_append_only', function: 'reject_usage_history_mutation()',
      triggerType: 27, message: 'usage accounting history is append-only', sqlState: null,
    },
  },
  orchestrator: {
    coordinator_decisions: {
      column: 'decision_id', trigger: 'coordinator_decisions_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 19,
      message: 'Coordinator decision and MAF checkpoint records cannot be updated.', sqlState: '55000',
    },
    maf_workflow_checkpoints: {
      column: 'checkpoint_id', trigger: 'maf_workflow_checkpoints_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 19,
      message: 'Coordinator decision and MAF checkpoint records cannot be updated.', sqlState: '55000',
    },
    session_consistency_manifests: {
      column: 'manifest_id', trigger: 'session_consistency_manifests_immutable',
      function: 'reject_session_consistency_manifest_mutation()', triggerType: 27,
      message: 'session consistency manifests are immutable', sqlState: null,
    },
    maf_root_model_turn_admissions: {
      column: 'request_id', trigger: 'maf_root_model_turn_admissions_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 27,
      message: 'Coordinator decision and MAF checkpoint records cannot be updated.', sqlState: '55000',
    },
    maf_child_spawn_authorization_receipts: {
      column: 'spawn_intent_id', trigger: 'maf_child_spawn_authorizations_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 27,
      message: 'Coordinator decision and MAF checkpoint records cannot be updated.', sqlState: '55000',
    },
    maf_child_spawn_authorization_consumptions: {
      column: 'spawn_intent_id', trigger: 'maf_child_spawn_consumptions_no_update',
      function: 'reject_row_lock_record_update()', triggerType: 27,
      message: 'Coordinator decision and MAF checkpoint records cannot be updated.', sqlState: '55000',
    },
    p1_run_request_provenance: {
      column: 'provenance_id', trigger: 'p1_run_request_provenance_immutable',
      function: 'reject_p1_invocation_provenance_mutation()', triggerType: 27,
      message: 'P1 invocation provenance is append-only', sqlState: null,
    },
    p1_invocation_bindings: {
      column: 'core_invocation_id', trigger: 'p1_invocation_bindings_immutable',
      function: 'reject_p1_invocation_provenance_mutation()', triggerType: 27,
      message: 'P1 invocation provenance is append-only', sqlState: null,
    },
  },
};

const PROVIDER_REFERENCE_GUARD_SOURCE = `BEGIN
    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'provider create requires read committed isolation'
            USING ERRCODE = '40001';
    END IF;
    IF TG_TABLE_NAME = 'model_providers' THEN
        IF EXISTS (
            SELECT 1 FROM projects_config.model_provider_reference_reservations AS reservation
            WHERE reservation.reference_folded =
                pg_catalog.translate(NEW.provider_id COLLATE "C",
                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
        ) THEN
            RAISE EXCEPTION 'provider reference collision'
                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
        END IF;
    ELSE
        IF EXISTS (
            SELECT 1 FROM projects_config.model_providers AS provider
            WHERE pg_catalog.translate(provider.provider_id COLLATE "C",
                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = NEW.reference_folded
        ) THEN
            RAISE EXCEPTION 'provider reference collision'
                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
        END IF;
    END IF;
    RETURN NEW;
END;`;

const providerReferenceGuard = trigger => ({
  trigger, function: 'guard_model_provider_reference_cross_table()', triggerType: 7,
  message: 'provider reference collision', sqlState: '23514',
  functionSource: PROVIDER_REFERENCE_GUARD_SOURCE,
});

const PROVIDER_WRITE_MARKER_SOURCE = `
DECLARE
    transaction_id text := pg_catalog.pg_current_xact_id()::text;
BEGIN
    IF TG_TABLE_NAME = 'model_providers' AND TG_OP = 'UPDATE'
       AND OLD.write_transaction_id = transaction_id THEN
        RAISE EXCEPTION 'provider transition is unavailable'
            USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
    END IF;
    NEW.write_transaction_id := transaction_id;
    RETURN NEW;
END;`;

const PROVIDER_WRITE_PAIR_SOURCE = `
DECLARE
    h projects_config.model_providers%ROWTYPE;
    r projects_config.model_provider_revisions%ROWTYPE;
    previous projects_config.model_provider_revisions%ROWTYPE;
    transaction_id text := pg_catalog.pg_current_xact_id()::text;
BEGIN
    IF TG_TABLE_NAME = 'model_providers' THEN
        SELECT * INTO h FROM projects_config.model_providers
        WHERE provider_id = NEW.provider_id;
        SELECT * INTO r FROM projects_config.model_provider_revisions
        WHERE provider_id = NEW.provider_id AND provider_revision = NEW.current_revision;
        IF h.provider_id IS NULL OR r.provider_id IS NULL
           OR h.write_transaction_id <> transaction_id
           OR r.write_transaction_id <> transaction_id
           OR h.current_revision <> NEW.current_revision
           OR NEW.state <> 'Draft'
           OR (TG_OP = 'INSERT' AND
               (NEW.current_revision <> 1 OR
                ROW(NEW.created_by_actor_id, NEW.created_at)
                IS DISTINCT FROM ROW(NEW.updated_by_actor_id, NEW.updated_at)))
           OR (TG_OP = 'UPDATE' AND
               (OLD.state <> 'Draft' OR
                NEW.current_revision <> OLD.current_revision + 1 OR
                ROW(NEW.provider_id, NEW.scope, NEW.project_id, NEW.created_by_actor_id, NEW.created_at)
                IS DISTINCT FROM
                ROW(OLD.provider_id, OLD.scope, OLD.project_id, OLD.created_by_actor_id, OLD.created_at))) THEN
            RAISE EXCEPTION 'provider transition is unavailable'
                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
        END IF;
    ELSE
        r := NEW;
        SELECT * INTO h FROM projects_config.model_providers WHERE provider_id = NEW.provider_id;
        IF h.provider_id IS NULL OR h.write_transaction_id <> transaction_id
           OR NEW.write_transaction_id <> transaction_id
           OR h.current_revision <> NEW.provider_revision
           OR h.state <> 'Draft'
           OR ROW(h.scope, h.project_id, h.display_name, h.type, h.base_url, h.deployment,
                  h.wire_api, h.azure_api_version, h.reasoning_efforts, h.prompt_capacity_tokens,
                  h.updated_by_actor_id, h.updated_at)
              IS DISTINCT FROM
              ROW(NEW.scope, NEW.project_id, NEW.display_name, NEW.type, NEW.base_url, NEW.deployment,
                  NEW.wire_api, NEW.azure_api_version, NEW.reasoning_efforts, NEW.prompt_capacity_tokens,
                  NEW.created_by_actor_id, NEW.created_at) THEN
            RAISE EXCEPTION 'provider transition is unavailable'
                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
        END IF;
        IF NEW.provider_revision = 1 THEN
            IF NEW.credential_version IS NOT NULL OR NEW.credential_slot_id IS NOT NULL
               OR NEW.credential_secret_id IS NOT NULL THEN
                RAISE EXCEPTION 'provider transition is unavailable'
                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
            END IF;
        ELSE
            SELECT * INTO previous FROM projects_config.model_provider_revisions
            WHERE provider_id = NEW.provider_id AND provider_revision = NEW.provider_revision - 1;
            IF previous.provider_id IS NULL OR
               ROW(NEW.credential_version, NEW.credential_slot_id, NEW.credential_secret_id)
               IS DISTINCT FROM
               ROW(previous.credential_version, previous.credential_slot_id, previous.credential_secret_id) THEN
                RAISE EXCEPTION 'provider transition is unavailable'
                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
            END IF;
        END IF;
    END IF;
    RETURN NULL;
END;`;

const providerWriteGuard = (trigger, marker, triggerType) => ({
  trigger,
  function: marker ? 'mark_model_provider_write()' : 'check_model_provider_pair()',
  triggerType,
  message: 'provider transition is unavailable',
  sqlState: '23514',
  functionSource: marker ? PROVIDER_WRITE_MARKER_SOURCE : PROVIDER_WRITE_PAIR_SOURCE,
  deferrable: !marker,
});

const APPEND_ONLY_GUARDS = {
  projects_config: {
    model_providers: [
      providerReferenceGuard('model_providers_reference_guard'),
      {
        trigger: 'model_providers_id_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 19,
        message: 'records in %.% are immutable', sqlState: '55000',
        column: 'provider_id', condition: '((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text)',
      },
      providerWriteGuard('model_providers_write_marker', true, 23),
      providerWriteGuard('model_providers_write_pair', false, 21),
    ],
    model_provider_reference_reservations: [
      providerReferenceGuard('model_provider_reference_cross_table_guard'),
      {
        trigger: 'model_provider_reference_reservations_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'model_provider_reference_reservations_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    model_provider_revisions: [
      {
        trigger: 'model_provider_revisions_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'model_provider_revisions_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      providerWriteGuard('model_provider_revisions_write_marker', true, 7),
      providerWriteGuard('model_provider_revisions_write_pair', false, 5),
    ],
    native_acs_policy_revisions: [
      {
        trigger: 'native_acs_policy_revisions_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_revisions_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    native_acs_policy_preparations: [
      {
        trigger: 'native_acs_policy_preparations_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_preparations_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    native_acs_policy_artifact_files: [
      {
        trigger: 'native_acs_policy_artifact_files_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_artifact_files_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_artifact_files_insert_with_revision',
        function: 'require_native_acs_policy_file_insert_in_revision_transaction()',
        triggerType: 7,
        message: 'native ACS policy files must be inserted with their revision',
        sqlState: '55000',
        functionSource: `BEGIN
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
        END;`,
      },
    ],
    native_acs_policy_idempotency: [
      {
        trigger: 'native_acs_policy_idempotency_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_idempotency_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    native_acs_policy_change_audit: [
      {
        trigger: 'native_acs_policy_change_audit_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_change_audit_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    native_acs_policy_outbox: [
      {
        trigger: 'native_acs_policy_outbox_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'native_acs_policy_outbox_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    project_governance_decision_stream_heads: [
      {
        trigger: 'project_governance_decision_head_initialize',
        function: 'require_governance_decision_head_initialize()', triggerType: 7,
        message: 'governance decision stream head must start at genesis', sqlState: '55000',
        functionSource: `BEGIN
          IF NEW.sequence <> 0 OR NEW.fence_generation <> 0
              OR NEW.head_hash <> repeat('0', 64) THEN
              RAISE EXCEPTION 'governance decision stream head must start at genesis'
                  USING ERRCODE = '55000';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'project_governance_decision_head_advance',
        function: 'require_governance_decision_head_advance()', triggerType: 19,
        message: 'governance decision head must advance with its record and outbox', sqlState: '55000',
        functionSource: `BEGIN
          IF NEW.sequence <> OLD.sequence + 1
              OR NEW.fence_generation <> OLD.fence_generation + 1
              OR NEW.head_hash !~ '^[0-9a-f]{64}$'
              OR NOT EXISTS (
                  SELECT 1
                  FROM projects_config.project_governance_decisions AS decision
                  JOIN projects_config.project_governance_decision_outbox AS outbox
                    ON outbox.decision_id = decision.decision_id
                  WHERE decision.scope_type = NEW.scope_type
                    AND decision.scope_id = NEW.scope_id
                    AND decision.resource_type = NEW.resource_type
                    AND decision.resource_id = NEW.resource_id
                    AND decision.sequence = NEW.sequence
                    AND decision.previous_hash = OLD.head_hash
                    AND decision.record_hash = NEW.head_hash
                    AND decision.write_transaction_id = pg_current_xact_id()::text
                    AND outbox.sequence = decision.sequence
                    AND outbox.record_hash = decision.record_hash
                    AND outbox.write_transaction_id = pg_current_xact_id()::text
              ) THEN
              RAISE EXCEPTION 'governance decision head must advance with its record and outbox'
                  USING ERRCODE = '55000';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'project_governance_decision_heads_no_delete',
        function: 'reject_governance_decision_head_removal()', triggerType: 11,
        message: 'governance decision stream heads cannot be removed', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decision_heads_no_truncate',
        function: 'reject_governance_decision_head_removal()', triggerType: 34,
        message: 'governance decision stream heads cannot be removed', sqlState: '55000',
      },
    ],
    project_governance_decisions: [
      {
        trigger: 'project_governance_decisions_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decisions_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decisions_stamp_transaction',
        function: 'stamp_governance_decision_transaction_id()', triggerType: 7,
        functionSource: 'BEGIN NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text; RETURN NEW; END',
      },
    ],
    project_governance_decision_outbox: [
      {
        trigger: 'project_governance_decision_outbox_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decision_outbox_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decision_outbox_stamp_transaction',
        function: 'stamp_governance_decision_transaction_id()', triggerType: 7,
        functionSource: 'BEGIN NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text; RETURN NEW; END',
      },
    ],
    project_governance_decision_checkpoints: [
      {
        trigger: 'project_governance_decision_checkpoints_anchor',
        function: 'require_governance_checkpoint_anchor()', triggerType: 7,
        message: 'governance checkpoint must reference a stream record', sqlState: '55000',
        functionSource: `BEGIN
          IF NOT EXISTS (
              SELECT 1
              FROM projects_config.project_governance_decision_stream_heads AS head
              WHERE head.scope_type = NEW.scope_type
                AND head.scope_id = NEW.scope_id
                AND head.resource_type = NEW.resource_type
                AND head.resource_id = NEW.resource_id
                AND head.sequence >= NEW.sequence
                AND (
                    NEW.sequence = 0 AND head.sequence = 0
                        AND NEW.head_hash = repeat('0', 64)
                    OR NEW.sequence > 0 AND EXISTS (
                        SELECT 1
                        FROM projects_config.project_governance_decisions AS decision
                        WHERE decision.scope_type = NEW.scope_type
                          AND decision.scope_id = NEW.scope_id
                          AND decision.resource_type = NEW.resource_type
                          AND decision.resource_id = NEW.resource_id
                          AND decision.sequence = NEW.sequence
                          AND decision.record_hash = NEW.head_hash
                    )
                )
          ) THEN
              RAISE EXCEPTION 'governance checkpoint must reference a stream record'
                  USING ERRCODE = '55000';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'project_governance_decision_checkpoints_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_governance_decision_checkpoints_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
    project_skill_trust_evidence: [
      {
        trigger: 'project_skill_trust_evidence_immutable',
        function: 'reject_immutable_record_mutation()', triggerType: 27,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
      {
        trigger: 'project_skill_trust_evidence_no_truncate',
        function: 'reject_immutable_record_mutation()', triggerType: 34,
        message: 'records in %.% are immutable', sqlState: '55000',
      },
    ],
  },
  environment: {
    platform_policy_apply_reservations: [
      {
        trigger: 'platform_policy_apply_reservations_consistent_identity',
        function: 'enforce_platform_policy_apply_reservation_identity()', triggerType: 7,
        message: 'platform policy apply reservation content conflicts', sqlState: '23514',
        functionSource: `BEGIN
          PERFORM pg_advisory_xact_lock(hashtextextended(NEW.target_id, 0));
          IF EXISTS (
              SELECT 1
              FROM environment.platform_policy_apply_reservations existing
              WHERE existing.target_id = NEW.target_id
                AND existing.source_epoch = NEW.source_epoch
                AND existing.generation = NEW.generation
                AND (
                    existing.source_id IS DISTINCT FROM NEW.source_id OR
                    existing.source_revision IS DISTINCT FROM NEW.source_revision OR
                    existing.source_hash IS DISTINCT FROM NEW.source_hash OR
                    existing.intent_hash IS DISTINCT FROM NEW.intent_hash OR
                    existing.provider_request_json IS DISTINCT FROM NEW.provider_request_json
                )
          ) THEN
              RAISE EXCEPTION 'platform policy apply reservation content conflicts'
                  USING ERRCODE = '23514';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'platform_policy_apply_reservations_active_hold_fence',
        function: 'enforce_platform_policy_reservation_hold_fence()', triggerType: 7,
        message: 'target has a policy hold', sqlState: '23514',
        functionSource: `BEGIN
          IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
              RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
          END IF;
          PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
          IF EXISTS (SELECT 1 FROM environment.platform_policy_proof_holds h WHERE h.target_id = NEW.target_id) THEN
              RAISE EXCEPTION 'target has a policy hold'
                  USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_reservation_held';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'platform_policy_apply_reservations_immutable',
        function: 'reject_platform_policy_apply_reservation_mutation()', triggerType: 27,
        message: 'platform policy apply reservations are append-only', sqlState: '55000',
      },
      {
        trigger: 'platform_policy_apply_reservations_no_truncate',
        function: 'reject_platform_policy_apply_reservation_mutation()', triggerType: 34,
        message: 'platform policy apply reservations are append-only', sqlState: '55000',
      },
    ],
    platform_policy_proof_holds: [
      {
        trigger: 'platform_policy_proof_holds_reservation_fence',
        function: 'enforce_platform_policy_hold_reservation_fence()', triggerType: 7,
        message: 'policy hold does not match the latest reservation', sqlState: '23514',
        functionSource: `BEGIN
          IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
              RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
          END IF;
          PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
          IF NEW.reservation_id IS DISTINCT FROM (
              SELECT r.reservation_id
              FROM environment.platform_policy_apply_reservations r
              WHERE r.target_id = NEW.target_id
              ORDER BY r.source_epoch DESC, r.generation DESC, r.created_at DESC, r.reservation_id DESC
              LIMIT 1
          ) THEN
              RAISE EXCEPTION 'policy hold does not match the latest reservation'
                  USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_hold_current_reservation';
          END IF;
          RETURN NEW;
        END;`,
      },
      {
        trigger: 'platform_policy_proof_holds_immutable',
        function: 'reject_platform_policy_proof_mutation()', triggerType: 27,
        message: 'platform policy proof records are append-only', sqlState: '55000',
      },
      {
        trigger: 'platform_policy_proof_holds_no_truncate',
        function: 'reject_platform_policy_proof_mutation()', triggerType: 34,
        message: 'platform policy proof records are append-only', sqlState: '55000',
      },
    ],
    platform_policy_proof_receipts: [
      {
        trigger: 'platform_policy_proof_receipts_hold_identity',
        function: 'enforce_platform_policy_proof_receipt_hold()', triggerType: 7,
        message: 'platform policy proof receipt does not match its hold', sqlState: '23514',
        functionSource: `BEGIN
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
        END;`,
      },
      {
        trigger: 'platform_policy_proof_receipts_immutable',
        function: 'reject_platform_policy_proof_mutation()', triggerType: 27,
        message: 'platform policy proof records are append-only', sqlState: '55000',
      },
      {
        trigger: 'platform_policy_proof_receipts_no_truncate',
        function: 'reject_platform_policy_proof_mutation()', triggerType: 34,
        message: 'platform policy proof records are append-only', sqlState: '55000',
      },
    ],
  },
  orchestrator: {
    maf_runtime_evidence_receipts: [
      {
        trigger: 'maf_runtime_evidence_receipts_immutable',
        function: 'reject_maf_runtime_evidence_mutation()', triggerType: 27,
        message: 'MAF runtime evidence receipts are append-only', sqlState: null,
      },
      {
        trigger: 'maf_runtime_evidence_receipts_no_truncate',
        function: 'reject_maf_runtime_evidence_mutation()', triggerType: 34,
        message: 'MAF runtime evidence receipts are append-only', sqlState: null,
      },
    ],
    p1_run_request_provenance: [
      {
        trigger: 'p1_run_request_provenance_no_truncate',
        function: 'reject_p1_invocation_provenance_mutation()', triggerType: 34,
        message: 'P1 invocation provenance is append-only', sqlState: null,
      },
    ],
    p1_invocation_bindings: [
      {
        trigger: 'p1_invocation_bindings_no_truncate',
        function: 'reject_p1_invocation_provenance_mutation()', triggerType: 34,
        message: 'P1 invocation provenance is append-only', sqlState: null,
      },
    ],
  },
};

const FUNCTIONS = {
  projects_config: [
    'mark_model_provider_write()',
    'check_model_provider_pair()',
    'guard_model_provider_reference_cross_table()',
    'reject_immutable_record_mutation()',
    'require_native_acs_policy_file_insert_in_revision_transaction()',
    'require_native_acs_policy_head_advance()',
    'require_governance_decision_head_initialize()',
    'require_governance_decision_head_advance()',
    'reject_governance_decision_head_removal()',
    'require_governance_checkpoint_anchor()',
    'stamp_governance_decision_transaction_id()',
    'reject_authority_audit_mutation()',
    'reject_project_setup_operation_mutation()',
    'lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
    'lock_native_acs_policy_authority(uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[])',
    'issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
    'read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
  ],
  environment: [
    'enforce_p1_creation_link_immutability()',
    'enforce_platform_policy_apply_reservation_identity()',
    'enforce_platform_policy_reservation_hold_fence()',
    'enforce_platform_policy_hold_reservation_fence()',
    'enforce_platform_policy_proof_receipt_hold()',
    'reject_platform_policy_apply_reservation_mutation()',
    'reject_platform_policy_proof_mutation()',
  ],
  knowledge: [
    'reject_knowledge_revision_mutation()',
    'reject_memory_binding_mutation()',
    'reject_knowledge_record_delete()',
    'reject_accepted_effect_receipt_mutation()',
  ],
  events_sessions: [
    'reject_project_fact_mutation()',
    'reject_usage_history_mutation()',
    'reject_usage_receipt_mutation()',
    'reject_row_lock_record_update()',
  ],
  orchestrator: [
    'reject_policy_receipt_mutation()',
    'reject_run_selection_context_mutation()',
    'reject_run_selection_context_version_mutation()',
    'reject_runtime_registration_mutation()',
    'require_runtime_registration_revision_advance()',
    'reject_runtime_usage_mutation()',
    'reject_source_control_pin_mutation()',
    'reject_source_control_merge_request_mutation()',
    'guard_source_control_output_capture_mutation()',
    'guard_maf_execution_run_guard_mutation()',
    'guard_maf_execution_dispatch_mutation()',
    'reject_maf_execution_output_witness_mutation()',
    'reject_maf_execution_truncate()',
    'reject_reviewed_remote_tool_snapshot_mutation()',
    'enforce_suspend_resume_operation_phase()',
    'reject_session_consistency_manifest_mutation()',
    'reject_row_lock_record_update()',
    // Trigger function that keeps Guardrails evidence rows immutable.
    'reject_guardrails_evaluation_mutation()',
    'reject_maf_runtime_evidence_mutation()',
    // Spend budget trigger functions: reservations only move from outstanding to retired, cursors only
    // move forward, rows cannot be deleted, and both tables reject TRUNCATE.
    'guard_spend_budget_reservation_mutation()',
    'guard_spend_budget_cursor_mutation()',
    'reject_spend_budget_truncate()',
    'reject_p1_invocation_provenance_mutation()',
    // Migration 046 uses these owner-only functions to validate inserts and close jobs on cancellation.
    'guard_workflow_generation_job_insert()',
    'close_workflow_generation_jobs_on_cancel()',
    'guard_terminal_cleanup_recovery()',
  ],
};

// Column-scoped UPDATE for rows that may change only in named columns. These tables have no table-level
// UPDATE. Role revocation changes only its state and audit fields; decision heads advance only through
// the fenced append transaction.
const UPDATE_COLUMNS = {
  projects_config: {
    project_role_assignments: ['state', 'revision', 'revoked_by', 'revoked_at'],
    native_acs_policy_heads: ['current_revision', 'updated_at'],
    project_governance_decision_stream_heads: ['sequence', 'fence_generation', 'head_hash', 'updated_at'],
  },
};

const OWNER_ONLY_OBJECTS = {
  projects_config: {
    tables: ['project_setup_operations'],
    sequences: ['platform_runtime_revisions_revision_seq'],
  },
};

/** Builds the complete ordered role, schema, table, and sequence grant description. */
export function buildP1PostgresPlan(targetName = 'agentweaver') {
  const target = p1PostgresTarget(targetName);
  const services = SERVICES.map(([service, schema], index) => ({
    service,
    schema,
    role: `agentweaver-id-${service}`,
    principalObjectId: PRINCIPALS[targetName][index],
    ownerOnlyTables: OWNER_ONLY_OBJECTS[schema]?.tables ?? [],
    ownerOnlySequences: OWNER_ONLY_OBJECTS[schema]?.sequences ?? [],
    tables: Object.entries(GROUPS[schema]).flatMap(([privileges, names]) =>
      names.split(' ').map(table => {
        const appendOnlyGuards = APPEND_ONLY_GUARDS[schema]?.[table];
        const lockGuard = LOCK_GUARDS[schema]?.[table];
        return {
          table,
          privileges: privileges.split(','),
          // Only tables with planned column updates carry this key, so every other table's plan entry is unchanged.
          ...(UPDATE_COLUMNS[schema]?.[table] ? { updateColumns: [...UPDATE_COLUMNS[schema][table]] } : {}),
          ...(lockGuard ? {
            lockColumn: lockGuard.column,
            mutationDenialRequired: true,
            mutationDenialGuard: {
              trigger: lockGuard.trigger,
              function: lockGuard.function,
              triggerType: lockGuard.triggerType,
              message: lockGuard.message,
              sqlState: lockGuard.sqlState,
            },
          } : {}),
          ...(appendOnlyGuards ? {
            mutationDenialRequired: true,
            mutationDenialGuards: appendOnlyGuards,
          } : {}),
        };
      })),
    functions: FUNCTIONS[schema].map(signature => ({
      signature,
      execute: signature.startsWith('lock_casting_authority(') ||
        signature.startsWith('lock_native_acs_policy_authority(') ||
        signature.startsWith('issue_p1_project(') ||
        signature.startsWith('read_p1_project('),
    })),
  }));
  return {
    formatVersion: 1,
    target: { ...target },
    services,
    // 022 adds completed_at; 023 adds interactive requests and cancellation with the exact grants above.
    // 024 adds private Chief of Staff conversations and Priorities; 025 adds caller dismissal receipts;
    // 026 adds backlog task content columns and needs no new grants; 027 adds periodic issue sync settings and receipts.
    // 028 adds the append-only guardrails_evaluations table. 029 adds the spend budget tables.
    // 030 adds append-only plan promotion and task origin rows with SELECT,INSERT grants.
    // 031 adds caller dismissals of promotion notices with SELECT,INSERT grants.
    // 032 adds pull-request records; 033 extends Priority links; 036 and 037 reuse the trigger from 021.
    // 035 adds a position column to backlog_tasks and requires its runtime DELETE grant.
    // 038 adds the retry-safe Source Control pull-request operation ledger; 039 adds runtime evidence receipts;
    // 040 adds P1 request provenance and invocation bindings; 041 adds numeric-limit no-send receipts.
    // 042 adds coordinator run-start intents with exact SELECT,INSERT,UPDATE grants.
    // 043 and 045 add native root-turn evidence kinds; 044 adds replacement operations.
    // 047 guards recovery while terminal cleanup is admitted; 048 binds PR effect and result identity.
    // 049 adds the owner-managed runtime-observation cursor table; 050 adds supersession receipts;
    // 051 adds immutable native-child rebind receipts.
    coreVersions: [...Array.from({ length: 16 }, (_, index) => index + 1), 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51],
    // Bind each owner migration catalog into the reviewed plan hash.
    environmentVersions: Array.from({ length: 16 }, (_, index) => index + 1),
    eventsVersions: Array.from({ length: 12 }, (_, index) => index + 1),
    executionRequirements: [
      'Exact reviewed source and actual Core merge are present in the current base.',
      'A separate approval permits the five SQL login mappings, schema initialization, and exact grants.',
      'The Environment bootstrap profile replacement store receives exactly SELECT,INSERT on environment.runtime_bootstrap_profile_replacements and receives no UPDATE, DELETE, or default privileges.',
      'Every principal object ID must come from the current target identity deployment; unresolved markers block initialization before credential requests.',
      'The exact installed row-lock and append-only mutation-denial triggers are read back before grants; restricted-role PostgreSQL tests prove row locks, mutation denial, and every source-owned DELETE grant.',
      'The already approved private connection is ready; this command does not create a tunnel or cloud resources.',
    ],
  };
}

/** Returns a SHA-256 fingerprint for the serialized plan so callers can detect plan changes. */
export function p1PostgresPlanHash(plan = buildP1PostgresPlan()) {
  return createHash('sha256').update(JSON.stringify(plan)).digest('hex');
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  if (process.argv.length !== 3 || process.argv[2] !== '--json')
    throw new Error('The P1 PostgreSQL plan requires --json and never connects to a database.');
  // The P1 initializer runs this CLI; the installer selects a registered target through this variable.
  process.stdout.write(JSON.stringify(buildP1PostgresPlan(process.env.AGENTWEAVER_INSTALL_TARGET || 'agentweaver')));
}
