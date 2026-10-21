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

// Revalidate these target-specific IDs against the current deployment outputs before initialization.
const SERVICES = [
  ['projects-config', 'projects_config', 'df4f3934-9585-47c7-b923-d9abca981dfe'],
  ['environment', 'environment', '3f94ddf2-a376-4d8e-8e8f-ad24e96f7417'],
  ['knowledge', 'knowledge', '9bc761f0-f192-41c6-a2d7-bbee1b83a1f2'],
  ['events-and-sessions', 'events_sessions', '7045d349-8e2d-40d1-a492-039540465780'],
  ['orchestrator', 'orchestrator', 'bb6f5b78-1f0e-4e56-a93c-31d96d9e1aa7'],
];

const GROUPS = {
  projects_config: {
    SELECT: 'tenant_memberships project_role_assignments authority_audit __ef_migrations_history',
    'SELECT,UPDATE': 'platform_runtime_heads',
    'SELECT,INSERT,UPDATE': 'projects marketplace_sources project_casting_proposals',
    'SELECT,INSERT': 'project_configuration_revisions platform_runtime_revisions project_run_selections project_skill_content_revisions project_skill_import_idempotency project_skill_content_revocations',
  },
  environment: {
    SELECT: '__ef_migrations_history',
    'SELECT,INSERT,UPDATE': 'owners owner_effects workspace_volume_cleanup sandbox_leases sandbox_provider_attempts sandbox_late_resource_cleanups remote_mcp_connections',
    'SELECT,INSERT': 'lifecycle_operations remote_mcp_connection_configurations remote_mcp_catalog_snapshots remote_mcp_connection_idempotency provider_lifecycle_reports runtime_bootstrap_profile_replacements',
  },
  knowledge: {
    SELECT: 'knowledge_schema_migrations outbox_schema_migrations',
    'SELECT,INSERT,UPDATE': 'knowledge_records knowledge_write_idempotency outbox_streams outbox_events',
    'SELECT,INSERT': 'knowledge_revisions memory_provider_bindings consumer_inbox_receipts',
  },
  events_sessions: {
    SELECT: 'sessions_schema_migrations outbox_schema_migrations',
    'SELECT,INSERT,UPDATE': 'session_run_streams project_fact_streams addressed_messages outbox_streams outbox_events',
    'SELECT,INSERT,UPDATE,DELETE': 'addressed_message_threads',
    'SELECT,INSERT': 'messaging_provider_bindings session_events session_object_references project_facts usage_run_cost_bindings usage_source_receipts consumer_inbox_receipts sessions session_provider_bindings session_fork_lineage usage_rate_cards usage_ledger',
  },
  orchestrator: {
    SELECT: 'coordination_schema_migrations outbox_schema_migrations',
    // Migration 023 grants mutable requests and append-only cancellation receipts separately.
    // Migration 024 grants private Chief of Staff rows; only Priority links are deleted.
    // Migration 025 grants caller-specific dismissal receipts as append-only rows.
    'SELECT,INSERT,UPDATE': 'accepted_runs coordination_sessions coordination_messages coordination_requests parent_notifications coordinator_gates executable_action_grants coordination_tree_commands coordination_idle_subscriptions coordination_execution_operations runtime_registration_heads source_control_merge_intents source_control_output_captures maf_execution_run_guards maf_execution_dispatches outbox_streams outbox_events backlog_projects backlog_tasks project_run_requests chief_of_staff_priority_lists chief_of_staff_priorities',
    'SELECT,INSERT,DELETE': 'backlog_dependencies chief_of_staff_priority_links',
    'SELECT,INSERT': 'coordinator_decision_outbox policy_evaluation_receipts coordinator_run_selection_contexts coordinator_run_selection_context_versions coordination_idle_notifications runtime_registration_revisions runtime_sdk_sources runtime_usage_observations source_control_repository_pins source_control_webhook_deliveries maf_execution_output_witnesses consumer_inbox_receipts coordinator_decisions maf_workflow_checkpoints session_consistency_manifests reviewed_remote_tool_snapshots project_run_cancellations chief_of_staff_conversations user_notification_dismissals',
  },
};

const LOCK_GUARDS = {
  events_sessions: {
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
  },
};

const FUNCTIONS = {
  projects_config: [
    'reject_immutable_record_mutation()',
    'reject_authority_audit_mutation()',
    'reject_project_setup_operation_mutation()',
    'lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
  ],
  environment: [],
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
  ],
};

const OWNER_ONLY_OBJECTS = {
  projects_config: {
    tables: ['project_setup_operations'],
    sequences: ['platform_runtime_revisions_revision_seq'],
  },
};

/** Builds the complete ordered role, schema, table, and sequence grant description. */
export function buildP1PostgresPlan() {
  const services = SERVICES.map(([service, schema, principalObjectId]) => ({
    service,
    schema,
    role: `agentweaver-id-${service}`,
    principalObjectId,
    ownerOnlyTables: OWNER_ONLY_OBJECTS[schema]?.tables ?? [],
    ownerOnlySequences: OWNER_ONLY_OBJECTS[schema]?.sequences ?? [],
    tables: Object.entries(GROUPS[schema]).flatMap(([privileges, names]) =>
      names.split(' ').map(table => ({
        table,
        privileges: privileges.split(','),
        ...(LOCK_GUARDS[schema]?.[table] ? {
          lockColumn: LOCK_GUARDS[schema][table].column,
          mutationDenialRequired: true,
          mutationDenialGuard: {
            trigger: LOCK_GUARDS[schema][table].trigger,
            function: LOCK_GUARDS[schema][table].function,
            triggerType: LOCK_GUARDS[schema][table].triggerType,
            message: LOCK_GUARDS[schema][table].message,
            sqlState: LOCK_GUARDS[schema][table].sqlState,
          },
        } : {}),
      }))),
    functions: FUNCTIONS[schema].map(signature => ({
      signature,
      execute: signature.startsWith('lock_casting_authority('),
    })),
  }));
  return {
    formatVersion: 1,
    target: { ...P1_POSTGRES_TARGET },
    services,
    // 022 adds completed_at; 023 adds interactive requests and cancellation with the exact grants above.
    // 024 adds private Chief of Staff conversations and Priorities; 025 adds caller dismissal receipts;
    // 026 adds backlog task content columns and needs no new grants.
    coreVersions: [...Array.from({ length: 16 }, (_, index) => index + 1), 18, 19, 20, 21, 22, 23, 24, 25, 26],
    // Bind the separately approved Events reporting migration into the reviewed plan hash.
    eventsVersions: Array.from({ length: 9 }, (_, index) => index + 1),
    executionRequirements: [
      'Exact reviewed source and actual Core merge are present in the current base.',
      'A separate approval permits the five SQL login mappings, schema initialization, and exact grants.',
      'The Environment bootstrap profile replacement store receives exactly SELECT,INSERT on environment.runtime_bootstrap_profile_replacements and receives no UPDATE, DELETE, or default privileges.',
      'Every principal object ID must come from the current target identity deployment; unresolved markers block initialization before credential requests.',
      'The exact installed row-lock mutation-denial triggers are read back before grants; restricted-role PostgreSQL tests prove row locks, mutation denial, and the two bounded DELETE paths.',
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
  process.stdout.write(JSON.stringify(buildP1PostgresPlan()));
}
