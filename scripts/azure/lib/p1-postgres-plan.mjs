import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const P1_POSTGRES_TARGET = Object.freeze({
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'aw-v1-p0',
  clusterName: 'aw-v1-p0-aks',
  postgresHost: 'aw-v1-p0-pg.postgres.database.azure.com',
  database: 'agentweaver',
  operatorName: 'asabbour@microsoft.com',
  operatorObjectId: 'c4279823-a7c7-477e-982e-f4145bb0a83d',
  connectionHost: '127.0.0.1',
  connectionPort: 15432,
  sslMode: 'VerifyFull',
});

const SERVICES = [
  ['projects-config', 'projects_config', '3ef9deea-61e5-4ad7-8789-5fd161d3c8fe'],
  ['environment', 'environment', '31d0fb6a-708e-4248-8445-998fcef828d1'],
  ['knowledge', 'knowledge', '21854284-5fb8-42d5-849f-cb95fe46fa37'],
  ['events-and-sessions', 'events_sessions', 'cc41b395-2199-47af-8403-eb4a9a8c6250'],
  ['orchestrator', 'orchestrator', 'd4083aac-3f0d-4c68-b462-ddef1e8f8043'],
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
    'SELECT,INSERT,UPDATE': 'owners owner_effects workspace_volume_cleanup sandbox_leases sandbox_late_resource_cleanups remote_mcp_connections',
    'SELECT,INSERT': 'lifecycle_operations remote_mcp_connection_configurations remote_mcp_catalog_snapshots remote_mcp_connection_idempotency provider_lifecycle_reports',
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
    'SELECT,INSERT,UPDATE': 'accepted_runs coordination_sessions coordination_messages coordination_requests parent_notifications coordinator_gates executable_action_grants coordination_tree_commands coordination_idle_subscriptions coordination_execution_operations runtime_registration_heads source_control_merge_intents source_control_output_captures maf_execution_run_guards maf_execution_dispatches outbox_streams outbox_events backlog_projects backlog_tasks',
    'SELECT,INSERT,DELETE': 'backlog_dependencies',
    'SELECT,INSERT': 'coordinator_decision_outbox policy_evaluation_receipts coordinator_run_selection_contexts coordinator_run_selection_context_versions coordination_idle_notifications runtime_registration_revisions runtime_sdk_sources runtime_usage_observations source_control_repository_pins source_control_webhook_deliveries maf_execution_output_witnesses consumer_inbox_receipts coordinator_decisions maf_workflow_checkpoints session_consistency_manifests reviewed_remote_tool_snapshots',
  },
};

const LOCK_COLUMNS = {
  events_sessions: {
    sessions: 'session_id',
    session_provider_bindings: 'run_id',
    session_fork_lineage: 'target_session_id',
    usage_rate_cards: 'card_id',
    usage_ledger: 'event_id',
  },
  orchestrator: {
    coordinator_decisions: 'decision_id',
    maf_workflow_checkpoints: 'checkpoint_id',
    session_consistency_manifests: 'manifest_id',
  },
};

const FUNCTIONS = {
  projects_config: [
    'reject_immutable_record_mutation()',
    'reject_authority_audit_mutation()',
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

export function buildP1PostgresPlan() {
  const services = SERVICES.map(([service, schema, principalObjectId]) => ({
    service,
    schema,
    role: `aw-v1-p0-id-${service}`,
    principalObjectId,
    tables: Object.entries(GROUPS[schema]).flatMap(([privileges, names]) =>
      names.split(' ').map(table => ({
        table,
        privileges: privileges.split(','),
        ...(LOCK_COLUMNS[schema]?.[table] ? {
          lockColumn: LOCK_COLUMNS[schema][table],
          mutationDenialRequired: true,
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
    coreVersions: [...Array.from({ length: 16 }, (_, index) => index + 1), 18, 19, 20, 21],
    executionRequirements: [
      'Exact reviewed source and actual Core merge are present in the current base.',
      'A separate approval permits the five SQL login mappings, schema initialization, and exact grants.',
      'Restricted-role PostgreSQL tests prove row locks, mutation denial, and the two bounded DELETE paths.',
      'The already approved private connection is ready; this command does not create a tunnel or cloud resources.',
    ],
  };
}

export function p1PostgresPlanHash(plan = buildP1PostgresPlan()) {
  return createHash('sha256').update(JSON.stringify(plan)).digest('hex');
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  if (process.argv.length !== 3 || process.argv[2] !== '--json')
    throw new Error('The P1 PostgreSQL plan requires --json and never connects to a database.');
  process.stdout.write(JSON.stringify(buildP1PostgresPlan()));
}
