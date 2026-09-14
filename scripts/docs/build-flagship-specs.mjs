#!/usr/bin/env node

import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = path.join(repoRoot, 'docs', 'diagrams', 'src', 'flagship');

const badge = (text, tone = 'neutral') => ({ text, tone });
const card = (id, label, subLabel, fluentIcon, tone = 'neutral', extras = {}) => ({
  id, label, subLabel, icon: 'box', fluentIcon, badge: badge(extras.badge ?? 'Concept', tone), ...extras,
});
const k8s = (id, label, subLabel, shape, tone = 'green', extras = {}) => ({
  id, label, subLabel, icon: 'box', library: 'kubernetes', shape: `mxgraph.kubernetes.${shape}`,
  badge: badge(extras.badge ?? 'Kubernetes', tone), ...extras,
});
const azureIconPaths = {
  kubernetes_services: 'containers/Kubernetes_Services',
  container_registries: 'containers/Container_Registries',
  database_for_postgresql_servers: 'databases/Azure_Database_PostgreSQL_Server',
  storage_accounts: 'storage/Storage_Accounts',
  key_vaults: 'security/Key_Vaults',
  managed_identities: 'identity/Managed_Identities',
  application_insights: 'management_governance/Application_Insights',
  azure_active_directory: 'identity/Azure_Active_Directory',
};
const azureShape = key => `image;image=img/lib/azure2/${azureIconPaths[key]}.svg;imageAspect=1`;
const azure = (id, label, subLabel, shape, tone = 'teal', extras = {}) => ({
  id, label, subLabel, icon: 'box', library: 'azure', shape: azureShape(shape),
  badge: badge(extras.badge ?? 'Azure', tone), ...extras,
});
const edge = (from, to, label, extras = {}) => ({ from, to, label, ...extras });
const graph = (title, alt, nodes, edges, groups = []) => ({
  kind: 'graph', title, alt, direction: 'LR', routing: 'separate-ports', groups, nodes, edges,
});
const participant = (id, label, subLabel, fluentIcon, tone = 'neutral', extras = {}) =>
  card(id, label, subLabel, fluentIcon, tone, { badge: extras.badge ?? 'Participant', ...extras });
const archNode = (id, label, subLabel, role, fluentIcon, tone = 'neutral', extras = {}) => ({
  id, label, subLabel, role, fluentIcon, badge: badge(extras.badge ?? role, tone), ...extras,
});

const specs = {
  'canonical-coordinator-architecture': {
    kind: 'architecture',
    notation: 'deployment',
    title: 'Governed orchestration, isolated execution',
    alt: 'Deployment and component view showing web and MCP clients entering the Agentweaver API, API and worker orchestration authority, isolated AgentHost execution, durable PostgreSQL and Azure Files state, and separate identity, repository, and model-provider dependencies.',
    layers: [
      { id: 'interfaces', label: 'Interaction', nodes: ['clients', 'mcp'] },
      { id: 'control', label: 'Orchestration authority', nodes: ['api', 'worker'] },
      { id: 'runtime', label: 'Execution and durable state', nodes: ['agenthost', 'postgres', 'workspace'] },
      { id: 'external', label: 'External authorities', nodes: ['entra', 'github', 'models'] },
    ],
    nodes: [
      archNode('clients', 'Web UI and clients', 'Submit, inspect, steer, and review', 'actor', 'person', 'lavender', { slot: 0, badge: 'Clients' }),
      archNode('mcp', 'MCP server', 'Authenticated tool adapter', 'service', 'mcp', 'teal', { slot: 1, library: 'kubernetes', shape: 'mxgraph.kubernetes.deployment', badge: 'Deployment' }),
      archNode('api', 'API', 'Admission, state, review, and events', 'service', 'api', 'teal', { slot: 0, library: 'kubernetes', shape: 'mxgraph.kubernetes.deployment', badge: 'Deployment' }),
      archNode('worker', 'Worker', 'Background planning and orchestration', 'coordinator', 'coordinator', 'lavender', { slot: 1, library: 'kubernetes', shape: 'mxgraph.kubernetes.deployment', badge: 'Deployment' }),
      archNode('agenthost', 'AgentHost pod', 'Constrained leaf turns and tools', 'worker', 'sandbox', 'green', { slot: 0, library: 'kubernetes', shape: 'mxgraph.kubernetes.pod', badge: 'Kata pod' }),
      archNode('postgres', 'PostgreSQL', 'Plans, runs, events, and leases', 'datastore', 'state', 'teal', { slot: 1, library: 'azure', shape: 'mxgraph.azure2.database_for_postgresql_servers', badge: 'Azure DB' }),
      archNode('workspace', 'Azure Files workspace', 'Repositories, branches, and artifacts', 'workspace', 'workspace', 'marigold', { slot: 2, library: 'azure', shape: 'mxgraph.azure2.storage_accounts', badge: 'Shared files' }),
      archNode('entra', 'Microsoft Entra ID', 'Browser identity and platform roles', 'external', 'identity', 'lavender', { slot: 0, library: 'azure', shape: 'mxgraph.azure2.azure_active_directory', badge: 'Identity' }),
      archNode('github', 'GitHub repositories', 'Purpose-bound source control access', 'external', 'repository', 'lavender', { slot: 1, badge: 'Repository' }),
      archNode('models', 'Model providers', 'Copilot or accepted BYOK boundary', 'external', 'model', 'green', { slot: 2, badge: 'Provider' }),
    ],
    edges: [
      edge('clients', 'api', 'HTTPS / SSE', { relation: 'flow' }),
      edge('clients', 'mcp', 'MCP tools', { relation: 'flow' }),
      edge('mcp', 'api', 'authorized HTTP', { relation: 'dependency' }),
      edge('api', 'agenthost', 'configure / invoke', { relation: 'dependency' }),
      edge('worker', 'agenthost', 'dispatch leaf turns', { relation: 'dependency' }),
      edge('api', 'postgres', 'state and events', { relation: 'association' }),
      edge('worker', 'postgres', 'plans and leases', { relation: 'association' }),
      edge('worker', 'workspace', 'worktrees / assembly', { relation: 'association' }),
      edge('agenthost', 'workspace', 'scoped mount', { relation: 'association' }),
      edge('api', 'entra', 'authenticate', { relation: 'dependency' }),
      edge('api', 'github', 'repository APIs', { relation: 'dependency' }),
      edge('agenthost', 'models', 'provider-bound calls', { relation: 'dependency' }),
    ],
  },

  'canonical-coordinator-runtime-sequence': {
    kind: 'sequence',
    title: 'One parent run: admission to collective completion',
    alt: 'UML sequence showing a caller starting a parent run, the coordinator persisting intent and a work plan, dependency-ready child work executing in AgentHost, collective review and merge, and durable completion observed by the caller.',
    autonumber: true,
    participants: [
      participant('caller', 'Caller or reviewer', 'Start, steer, and decide', 'person', 'lavender'),
      participant('api', 'API / MCP', 'Admission and observation', 'mcp', 'teal'),
      participant('control', 'Coordinator control plane', 'Plan, dispatch, and assemble', 'coordinator', 'green'),
      participant('host', 'AgentHost', 'Isolated leaf execution', 'sandbox', 'marigold', { library: 'kubernetes', shape: 'mxgraph.kubernetes.pod' }),
      participant('store', 'Durable state', 'Runs, plans, events, and artifacts', 'state', 'teal'),
    ],
    steps: [
      { type: 'message', from: 'caller', to: 'api', label: 'Submit goal' },
      { type: 'activation', participant: 'control', action: 'start' },
      { type: 'message', from: 'api', to: 'control', label: 'Admit and activate parent run' },
      { type: 'message', from: 'control', to: 'store', label: 'Persist confirmed intent and work plan' },
      { type: 'message', from: 'api', to: 'caller', label: '201 run ID — not completion', line: 'dashed', arrow: 'open' },
      { type: 'fragment', operator: 'loop', label: 'dependency-ready waves', sections: [{ steps: [
        { type: 'message', from: 'control', to: 'store', label: 'Read eligible frontier' },
        { type: 'message', from: 'control', to: 'host', label: 'Start scoped child turn' },
        { type: 'activation', participant: 'host', action: 'start' },
        { type: 'message', from: 'host', to: 'control', label: 'Evidence and assemble-ready result', line: 'dashed', arrow: 'open' },
        { type: 'activation', participant: 'host', action: 'end' },
        { type: 'message', from: 'control', to: 'store', label: 'Persist child outcome and events' },
      ] }] },
      { type: 'message', from: 'control', to: 'store', label: 'Persist integrated candidate and review request' },
      { type: 'message', from: 'api', to: 'caller', label: 'Present one collective candidate', line: 'dashed', arrow: 'open' },
      { type: 'message', from: 'caller', to: 'api', label: 'Approve, revise, or decline' },
      { type: 'message', from: 'api', to: 'control', label: 'Deliver durable review decision' },
      { type: 'message', from: 'control', to: 'store', label: 'Merge, record, and terminalize parent' },
      { type: 'activation', participant: 'control', action: 'end' },
      { type: 'message', from: 'api', to: 'caller', label: 'Replay final state and artifacts', line: 'dashed', arrow: 'open' },
    ],
  },

  'canonical-coordinator-journey': graph(
    'From intent to one accountable outcome',
    'Activity flow showing intent proposal and confirmation, visible planning, dependency-aware execution, collective review, revision or intervention paths, application of the approved result, and explicit terminal outcomes.',
    [
      card('intent', 'Express intent', 'Define Outcome or Direct', 'person', 'lavender', { badge: 'Human' }),
      card('proposal', 'Propose OutcomeSpec', 'Scope, assumptions, and questions', 'document', 'teal'),
      card('confirm', 'Confirm or revise?', 'Confirmation authorizes work', 'approval', 'marigold', { variant: 'decision', badge: 'Gate' }),
      card('plan', 'See the work plan', 'Tasks, owners, and dependencies', 'workflow', 'lavender'),
      card('execute', 'Watch and steer work', 'Answer questions and direct corrections', 'worker', 'green'),
      card('candidate', 'Assess one candidate', 'Integrated child results and checks', 'review', 'teal'),
      card('review', 'Approve, revise, or decline?', 'No automatic timeout', 'approval', 'marigold', { variant: 'decision', badge: 'Human gate' }),
      card('apply', 'Apply approved result', 'Merge and final recording', 'merge', 'green'),
      card('attention', 'Needs intervention', 'Blocked work retains a reason', 'security', 'marigold'),
      card('outcome', 'Terminal outcome', 'Completed, declined, stopped, or failed', 'outcome', 'green', { variant: 'terminator', badge: 'Result' }),
    ],
    [
      edge('intent', 'proposal', 'Define Outcome'), edge('intent', 'plan', 'Direct'),
      edge('proposal', 'confirm', 'present'), edge('confirm', 'proposal', 'revise', { loopback: true }),
      edge('confirm', 'plan', 'confirmed'), edge('plan', 'execute', 'start'),
      edge('execute', 'candidate', 'eligible work ready'), edge('candidate', 'review', 'present'),
      edge('review', 'execute', 'request changes', { loopback: true }), edge('review', 'apply', 'approve'),
      edge('review', 'outcome', 'decline'), edge('execute', 'attention', 'blocked'),
      edge('attention', 'execute', 'correct and resume', { loopback: true }),
      edge('apply', 'outcome', 'record result'),
    ],
  ),

  'canonical-default-workflow': graph(
    'Default workflow: produce, review, merge, publish, record',
    'Flowchart of the six-stage default workflow: agent production, Responsible AI gate, human review, merge attempt, pull-request publication attempt, and Scribe recording, including revision, no-change, decline, safety, and blocked-merge paths.',
    [
      card('agent', 'Produce or revise work', 'Agent stage', 'agent', 'lavender'),
      card('rai', 'Responsible AI gate', 'Changes, safety, and revision need', 'security', 'marigold', { variant: 'decision', badge: 'Gate' }),
      card('review', 'Human review', 'Approve, request changes, or decline', 'review', 'teal', { variant: 'decision', badge: 'Gate' }),
      card('merge', 'Attempt approved merge', 'Blocked conflicts return to review', 'merge', 'green'),
      card('publish', 'Attempt PR publication', 'Success, skip, or visible failure', 'repository', 'lavender'),
      card('scribe', 'Record outcome', 'Scribe stage', 'document', 'teal'),
      card('done', 'Workflow path finished', 'Not a guarantee of merge or publication', 'outcome', 'green', { variant: 'terminator', badge: 'Outcome' }),
      card('failed', 'Explicit unsuccessful outcome', 'Turn failure, safety failure, or decline', 'security', 'marigold', { variant: 'terminator', badge: 'Stop' }),
    ],
    [
      edge('agent', 'rai', 'turn complete'), edge('agent', 'failed', 'terminal failure'),
      edge('rai', 'agent', 'revise', { loopback: true }), edge('rai', 'review', 'changes'),
      edge('rai', 'scribe', 'no change / safe'), edge('rai', 'failed', 'safety failure'),
      edge('review', 'agent', 'request changes', { loopback: true }), edge('review', 'merge', 'approve'),
      edge('review', 'failed', 'decline'), edge('merge', 'review', 'blocked', { loopback: true }),
      edge('merge', 'publish', 'non-blocked result'), edge('publish', 'scribe', 'continue'),
      edge('scribe', 'done', 'recorded'),
    ],
  ),

  'canonical-workflow-authoring': graph(
    'Generate a draft; save to activate',
    'Flowchart showing one model correction pass, human editing of an unsaved workflow draft, save-time validation, project YAML persistence, registry activation, and distinct pre-write and post-write failures.',
    [
      card('generate', 'Generate YAML draft', 'Description plus project context', 'model', 'lavender'),
      card('draft-valid', 'Draft valid?', 'Structure and binder dry-run', 'test', 'marigold', { variant: 'decision', badge: 'Validate' }),
      card('correct', 'Correct once', 'Failed YAML plus validation error', 'history', 'teal'),
      card('review', 'Review and edit draft', 'Still unsaved', 'document', 'lavender'),
      card('save-valid', 'Save valid?', 'Access, ID, path, syntax, and binding', 'test', 'marigold', { variant: 'decision', badge: 'Validate' }),
      card('write', 'Persist project YAML', '.agentweaver/workflows/{id}.yaml', 'workspace', 'teal', { variant: 'document', badge: 'Write' }),
      card('reload', 'Registry reload available?', 'Synchronize and resolve by ID', 'workflow', 'marigold', { variant: 'decision', badge: 'Activate' }),
      card('active', 'Available for selection', 'Save completed successfully', 'outcome', 'green', { variant: 'terminator', badge: 'Active' }),
      card('generation-failed', 'Generation failed', 'No workflow file was saved', 'security', 'marigold', { variant: 'terminator', badge: 'Stop' }),
      card('activation-failed', 'Persistence or activation failed', 'Post-write state may require inspection', 'security', 'marigold', { variant: 'terminator', badge: 'Stop' }),
    ],
    [
      edge('generate', 'draft-valid', 'candidate'), edge('generate', 'generation-failed', 'provider error'),
      edge('draft-valid', 'review', 'yes'), edge('draft-valid', 'correct', 'no'),
      edge('correct', 'draft-valid', 'second candidate', { loopback: true }),
      edge('draft-valid', 'generation-failed', 'second invalid'),
      edge('review', 'save-valid', 'Save'), edge('save-valid', 'review', 'reject and edit', { loopback: true }),
      edge('save-valid', 'write', 'valid'), edge('write', 'activation-failed', 'write error'),
      edge('write', 'reload', 'persisted'), edge('reload', 'active', 'available'),
      edge('reload', 'activation-failed', 'not discovered'),
    ],
  ),

  'canonical-workflow-selection': graph(
    'Workflow selection: explicit intent first, bounded automation second',
    'Two-phase flowchart showing Blueprint workflow candidates, explicit and conversational overrides, zero/one/multiple candidate handling, bounded model selection with deterministic fallback, and post-decomposition Build and Test compatibility.',
    [
      card('candidates', 'Resolve available candidates', 'Blueprint set plus valid registry definitions', 'archive', 'teal'),
      card('override', 'Available override?', 'Request or conversational choice', 'person', 'marigold', { variant: 'decision', badge: 'Priority' }),
      card('count', 'Candidate count?', 'Zero, one, or multiple', 'workflow', 'marigold', { variant: 'decision', badge: 'Branch' }),
      card('automatic', 'Automatic selection', 'Default, singleton, or bounded model choice', 'model', 'lavender'),
      card('usable', 'Usable candidate?', 'Never accept an invented workflow', 'test', 'marigold', { variant: 'decision', badge: 'Validate' }),
      card('fallback', 'Deterministic fallback', 'Default/standard, then non-review, then first', 'history', 'teal'),
      card('selected', 'Selected definition', 'Record provenance and rationale', 'outcome', 'green'),
      card('decompose', 'Decompose work', 'Selected workflow drives planning', 'workflow', 'lavender'),
      card('compatible', 'Code work has Build and Test?', 'Check actual decomposition', 'test', 'marigold', { variant: 'decision', badge: 'Compatibility' }),
      card('final', 'Persist final workflow ID', 'Honor explicit choice or correct automation', 'state', 'green', { variant: 'terminator', badge: 'WorkPlan' }),
    ],
    [
      edge('candidates', 'override', 'ordered choices'), edge('override', 'selected', 'explicit match'),
      edge('override', 'count', 'none'), edge('count', 'automatic', '0 / 1 / multiple'),
      edge('automatic', 'usable', 'candidate'), edge('usable', 'automatic', 'retry once', { loopback: true }),
      edge('usable', 'selected', 'yes'), edge('usable', 'fallback', 'no'),
      edge('fallback', 'selected', 'fallback'), edge('selected', 'decompose', 'plan'),
      edge('decompose', 'compatible', 'inspect'), edge('compatible', 'final', 'compatible or explicit'),
      edge('compatible', 'automatic', 'automatic reselection', { loopback: true }),
    ],
  ),

  'canonical-workflow-invocation': graph(
    'Workflow invocation: admission, durable handoff, and dispatch',
    'Flow showing interactive starts, library workflow runs, and authorized event or schedule automation converging on durable task staging, atomic coordinator pickup, planning, dependency-aware dispatch, and child execution.',
    [
      card('interactive', 'Interactive start', 'Create a parent run directly', 'person', 'lavender'),
      card('library', 'Library Run', 'Publish a workflow-pinned task', 'workflow', 'teal'),
      card('trigger', 'Event or schedule match', 'Server-owned occurrence', 'event', 'teal'),
      card('authorize', 'Authorize automation', 'Fence activation and bind identity', 'security', 'marigold', { variant: 'decision', badge: 'Admission' }),
      card('ready', 'Publish Ready task', 'Durable queue handoff', 'state', 'teal'),
      card('pickup', 'Atomic claim and reservation', 'Winning pickup links one parent run', 'coordinator', 'lavender'),
      card('plan', 'Activate and plan parent', 'Confirmed intent and persisted WorkPlan', 'workflow', 'green'),
      card('dispatch', 'Dispatch eligible children', 'Dependencies and base branch satisfied', 'dispatch', 'green'),
      card('children', 'Child execution', 'Scoped AgentHost turns', 'worker', 'marigold', { variant: 'terminator', badge: 'Execution' }),
    ],
    [
      edge('interactive', 'plan', 'direct admission'), edge('library', 'ready', 'task ID'),
      edge('trigger', 'authorize', 'matched occurrence'), edge('authorize', 'ready', 'authorized'),
      edge('ready', 'pickup', 'heartbeat polling'), edge('pickup', 'plan', 'commit then activate'),
      edge('plan', 'dispatch', 'nonempty plan'), edge('dispatch', 'children', 'dependency-ready'),
    ],
  ),

  'canonical-durable-event-stream-sequence': {
    kind: 'sequence',
    title: 'Durable events: commit, replay, then live tail',
    alt: 'UML sequence showing events committed with a per-run sequence before local notification, SSE replay from Last-Event-ID, live polling with the same cursor, local snapshot delivery, and transport completion distinct from business success.',
    autonumber: true,
    participants: [
      participant('producer', 'Run producer', 'Creates lifecycle events', 'event', 'lavender'),
      participant('stream', 'Durable event stream', 'Sequence allocation and ordered reads', 'state', 'teal'),
      participant('db', 'RunEvents', 'PostgreSQL rows', 'state', 'marigold', { library: 'azure', shape: 'mxgraph.azure2.database_for_postgresql_servers' }),
      participant('api', 'SSE API', 'Authorized replay and live tail', 'api', 'green'),
      participant('client', 'Browser client', 'Cursor, reconnect, and dedupe', 'window', 'lavender'),
    ],
    steps: [
      { type: 'message', from: 'producer', to: 'stream', label: 'Append event' },
      { type: 'message', from: 'stream', to: 'db', label: 'Lock run, allocate MAX+1, insert, commit' },
      { type: 'message', from: 'db', to: 'stream', label: 'Committed sequence n', line: 'dashed', arrow: 'open' },
      { type: 'message', from: 'client', to: 'api', label: 'Connect with Last-Event-ID = k' },
      { type: 'fragment', operator: 'loop', label: 'replay then live tail', sections: [{ steps: [
        { type: 'message', from: 'api', to: 'stream', label: 'Subscribe after cursor k' },
        { type: 'message', from: 'stream', to: 'db', label: 'SELECT sequence > k ORDER BY sequence' },
        { type: 'message', from: 'stream', to: 'api', label: 'Yield ordered batch', line: 'dashed', arrow: 'open' },
        { type: 'message', from: 'api', to: 'client', label: 'SSE id, event, JSON data' },
      ] }] },
      { type: 'note', over: ['stream', 'client'], label: 'The same positive per-run cursor drives replay, dedupe, and reconnect.' },
      { type: 'message', from: 'stream', to: 'api', label: 'Terminal-containing batch fully drained', line: 'dashed', arrow: 'open' },
      { type: 'message', from: 'api', to: 'client', label: 'event: done — transport closure only' },
    ],
  },

  'canonical-board-lifecycle': graph(
    'Task commitment, coordinator execution, and board projection',
    'State and projection view showing the only persisted task states Backlog, Ready, and Claimed; atomic coordinator-run reservation; and read-only projection of the linked run into Active, Human Review, Problems, or Done.',
    [
      card('backlog', 'Backlog', 'Captured but not committed', 'archive', 'neutral', { variant: 'state', badge: 'Task state' }),
      card('ready', 'Ready', 'Committed; dependencies may still block pickup', 'event', 'teal', { variant: 'state', badge: 'Task state' }),
      card('claimed', 'Claimed', 'Run ID persisted atomically', 'coordinator', 'lavender', { variant: 'state', badge: 'Task state' }),
      card('parent', 'Top-level run and WorkPlan', 'Coordinator owns execution and review', 'workflow', 'green'),
      card('projector', 'Board projection', 'Ordered rules over persisted run state', 'telemetry', 'teal'),
      card('active', 'Active', 'Default in-progress projection', 'worker', 'lavender', { variant: 'state', badge: 'Projection' }),
      card('review', 'Human Review', 'Awaiting collective decision', 'review', 'marigold', { variant: 'state', badge: 'Projection' }),
      card('problems', 'Problems', 'Blocked or unsuccessful outcome', 'security', 'marigold', { variant: 'state', badge: 'Projection' }),
      card('done', 'Done', 'Success-like projection', 'outcome', 'green', { variant: 'state', badge: 'Projection' }),
    ],
    [
      edge('backlog', 'ready', 'commit'), edge('ready', 'backlog', 'withdraw', { loopback: true }),
      edge('ready', 'claimed', 'atomic pickup'), edge('claimed', 'parent', 'RunId'),
      edge('parent', 'projector', 'status + WorkPlan', { dashed: true }),
      edge('projector', 'active', 'remaining states'), edge('projector', 'review', 'awaiting review'),
      edge('projector', 'problems', 'blocked / failed / declined'), edge('projector', 'done', 'completed / merged'),
    ],
  ),

  'canonical-memory-context': graph(
    'Memory: provenance, approval, compilation, and runtime use',
    'Data-flow view showing authenticated memory and decision writes, explicit approval, authoritative database records, trust-aware context compilation, an untrusted JSON envelope, role-specific runtime composition, and derived filesystem mirrors.',
    [
      card('authors', 'Humans and agents', 'Authenticated or run-capability-scoped writes', 'person', 'lavender'),
      card('inbox', 'Decision Inbox', 'Attributed pending proposals', 'archive', 'teal'),
      card('approval', 'Approval authority', 'Owner or verified Coordinator', 'approval', 'marigold', { variant: 'decision', badge: 'Govern' }),
      card('ledger', 'Accepted decisions', 'Active, approved architectural or scope records', 'document', 'green'),
      card('memory', 'Agent memory', 'Core and eligible learnings', 'memory', 'teal'),
      card('session', 'Open project session', 'Focus, issues, and summary', 'history', 'neutral'),
      card('compiler', 'MemoryContextCompiler', 'Project, role, trust, type, and budget filters', 'test', 'lavender'),
      card('envelope', 'Untrusted context JSON', 'Historical data, never executable authority', 'security', 'marigold', { variant: 'document', badge: 'Boundary' }),
      card('runtime', 'Role-specific agent setup', 'Full context or decisions-only child context', 'agent', 'green'),
      card('mirrors', 'Filesystem mirrors', 'Inspectable exports, not compiler authority', 'workspace', 'neutral'),
    ],
    [
      edge('authors', 'inbox', 'submit proposal'), edge('authors', 'memory', 'record pending memory'),
      edge('inbox', 'approval', 'review'), edge('approval', 'ledger', 'promote'),
      edge('ledger', 'compiler', 'approved decisions'), edge('memory', 'compiler', 'eligible rows'),
      edge('session', 'compiler', 'latest open session'), edge('compiler', 'envelope', 'serialize'),
      edge('envelope', 'runtime', 'compose context'), edge('runtime', 'authors', 'new observations', { loopback: true }),
      edge('ledger', 'mirrors', 'export', { dashed: true }), edge('memory', 'mirrors', 'export', { dashed: true }),
    ],
  ),

  'canonical-provider-admission': graph(
    'Provider authorization and execution admission',
    'Guarded flow showing operation and caller authorization, provider and credential resolution, operation policy, protected execution-context acceptance, durable run-boundary capture, point-of-invocation revalidation, and explicit rejection before model calls.',
    [
      card('request', 'Authenticated operation request', 'Operation, caller, and project or run scope', 'person', 'lavender'),
      card('authorize', 'Caller and scope valid?', 'Operation-specific access rules', 'security', 'marigold', { variant: 'decision', badge: 'Authorize' }),
      card('resolve', 'Resolve provider and credential', 'Project, platform, and personal precedence', 'identity', 'teal'),
      card('policy', 'Provider allowed for operation?', 'BYOK support is operation metadata', 'security', 'marigold', { variant: 'decision', badge: 'Policy' }),
      card('prepared', 'Prepared execution context', 'Provider identity plus protected short-lived key', 'document', 'lavender'),
      card('accept', 'Context still matches?', 'Caller, operation, project, expiry, and provider identity', 'test', 'marigold', { variant: 'decision', badge: 'Accept' }),
      card('snapshot', 'Durable run boundary', 'Private provider identity and execution configuration', 'state', 'teal'),
      card('invoke', 'Point-of-invocation validation', 'Revalidate accepted provider before each attempt', 'security', 'marigold', { variant: 'decision', badge: 'Fence' }),
      card('execute', 'Execute model call', 'Use only accepted authority', 'model', 'green', { variant: 'terminator', badge: 'Admitted' }),
      card('reject', 'Reject before invocation', 'Reprepare explicitly; never silently switch provider', 'security', 'marigold', { variant: 'terminator', badge: 'Stop' }),
    ],
    [
      edge('request', 'authorize', 'prepare'), edge('authorize', 'resolve', 'authorized'),
      edge('authorize', 'reject', 'denied'), edge('resolve', 'policy', 'current boundary'),
      edge('policy', 'prepared', 'eligible'), edge('policy', 'reject', 'unavailable'),
      edge('prepared', 'accept', 'submit key'), edge('accept', 'snapshot', 'matching'),
      edge('accept', 'reject', 'changed / expired'), edge('snapshot', 'invoke', 'run-bound identity'),
      edge('invoke', 'execute', 'valid'), edge('invoke', 'reject', 'mismatch'),
      edge('reject', 'request', 'reconnect and reprepare', { loopback: true }),
    ],
  ),

  'canonical-sandbox-boundary': {
    kind: 'architecture',
    notation: 'deployment',
    title: 'Sandbox declarations and enforcement boundaries',
    alt: 'Deployment and trust-boundary view showing project sandbox declarations, Kubernetes SandboxClaim admission, the Kata AgentHost pod, point-of-use tool and filesystem checks, authenticated executor IPC, workspace mounts, network enforcement, and separate Key Vault authority.',
    layers: [
      { id: 'declare', label: 'Declarations and admission', nodes: ['project-policy', 'claim', 'network-policy'] },
      { id: 'mediate', label: 'AgentHost mediation', nodes: ['agenthost', 'governance', 'file-shell'] },
      { id: 'execute', label: 'Kata execution boundary', nodes: ['executor', 'workload', 'workspace'] },
      { id: 'outside', label: 'External authorities', nodes: ['destinations', 'vault'] },
    ],
    nodes: [
      archNode('project-policy', 'Project sandbox settings', 'Declared application policy', 'component', 'settings', 'lavender', { slot: 0, badge: 'Declare' }),
      archNode('claim', 'SandboxClaim', 'Warm-pool allocation request', 'component', 'sandbox', 'green', { slot: 1, library: 'kubernetes', shape: 'mxgraph.kubernetes.custom_resource', badge: 'CRD' }),
      archNode('network-policy', 'NetworkPolicy', 'Additive ingress and egress rules', 'component', 'network', 'marigold', { slot: 2, library: 'kubernetes', shape: 'mxgraph.kubernetes.network_policy', badge: 'Policy' }),
      archNode('agenthost', 'AgentHost container', 'A2A, credentials, and tool mediation', 'worker', 'agent', 'green', { slot: 0, library: 'kubernetes', shape: 'mxgraph.kubernetes.pod', badge: 'Pod' }),
      archNode('governance', 'Tool governance', 'Deny unknown tools; enforce capabilities', 'component', 'security', 'marigold', { slot: 1, badge: 'Point of use' }),
      archNode('file-shell', 'File and shell checks', 'Contain paths; validate and approve commands', 'component', 'test', 'teal', { slot: 2, badge: 'Point of use' }),
      archNode('executor', 'PodExec executor', 'Authenticated Unix-socket dispatcher', 'service', 'security', 'teal', { slot: 0, badge: 'Sidecar' }),
      archNode('workload', 'Model-controlled process', 'Bubblewrap mounts and optional network namespace', 'worker', 'code', 'lavender', { slot: 1, badge: 'Process' }),
      archNode('workspace', 'Workspace PVC', 'Shared storage; scoped projection per run', 'workspace', 'workspace', 'marigold', { slot: 2, library: 'kubernetes', shape: 'mxgraph.kubernetes.persistent_volume_claim', badge: 'PVC' }),
      archNode('destinations', 'Permitted destinations', 'DNS, public HTTPS, selected API/MCP', 'external', 'network', 'teal', { slot: 1, badge: 'Network' }),
      archNode('vault', 'Azure Key Vault', 'Platform-side secret authority', 'external', 'identity', 'lavender', { slot: 2, library: 'azure', shape: 'mxgraph.azure2.key_vaults', badge: 'Azure' }),
    ],
    edges: [
      edge('project-policy', 'agenthost', 'declared policy', { relation: 'dependency' }),
      edge('claim', 'agenthost', 'bind warm pod', { relation: 'dependency' }),
      edge('agenthost', 'governance', 'tool request', { relation: 'flow' }),
      edge('governance', 'file-shell', 'allowed operation', { relation: 'flow' }),
      edge('file-shell', 'executor', 'authenticated IPC', { relation: 'dependency' }),
      edge('executor', 'workload', 'spawn / own', { relation: 'flow' }),
      edge('workspace', 'workload', 'scoped mount', { relation: 'association' }),
      edge('network-policy', 'destinations', 'effective rules', { relation: 'dependency' }),
      edge('workload', 'destinations', 'allowed traffic', { relation: 'dependency' }),
      edge('agenthost', 'vault', 'no direct authorization', { relation: 'association' }),
    ],
  },

  'canonical-pod-process-boundaries': {
    kind: 'architecture',
    notation: 'deployment',
    title: 'AgentHost pod, container, and process boundaries',
    alt: 'UML deployment view of a SandboxClaim selecting a warm-pool template, a Kata-isolated AgentHost pod with separate AgentHost and executor containers, authenticated IPC, model-controlled child processes, private volumes, shared workspace PVC, and network policy.',
    layers: [
      { id: 'control', label: 'Control plane and Kubernetes resources', nodes: ['worker', 'claim', 'template'] },
      { id: 'host', label: 'AgentHost container', nodes: ['agenthost', 'relay', 'ipc'] },
      { id: 'executor', label: 'Executor container', nodes: ['podexec', 'workload', 'private-volumes'] },
      { id: 'resources', label: 'Pod resources and network', nodes: ['workspace', 'network', 'destinations'] },
    ],
    nodes: [
      archNode('worker', 'Worker orchestration', 'Owns claim and pod lifecycle', 'coordinator', 'coordinator', 'lavender', { slot: 0, badge: 'Control plane' }),
      archNode('claim', 'SandboxClaim', 'References the warm pool', 'component', 'sandbox', 'green', { slot: 1, library: 'kubernetes', shape: 'mxgraph.kubernetes.custom_resource', badge: 'CRD' }),
      archNode('template', 'SandboxTemplate', 'Two containers; Kata runtime', 'component', 'settings', 'green', { slot: 2, library: 'kubernetes', shape: 'mxgraph.kubernetes.custom_resource', badge: 'CRD' }),
      archNode('agenthost', 'AgentHost process', 'A2A turns and trusted mediation', 'worker', 'agent', 'green', { slot: 0, badge: 'Container' }),
      archNode('relay', 'Trusted exec relay', 'Supervises long-running spawn streams', 'component', 'dispatch', 'teal', { slot: 1, badge: 'Process' }),
      archNode('ipc', 'exec-ipc emptyDir', 'Token and Unix socket', 'workspace', 'network', 'marigold', { slot: 2, badge: 'tmpfs' }),
      archNode('podexec', 'PodExecServer', 'Authenticated dispatcher and registrations', 'service', 'security', 'teal', { slot: 0, badge: 'Container' }),
      archNode('workload', 'Shell, build, test, preview', 'Executor-owned OS process group', 'worker', 'code', 'lavender', { slot: 1, badge: 'Process' }),
      archNode('private-volumes', 'Private HOME and tmp', 'Distinct container and run views', 'workspace', 'workspace', 'neutral', { slot: 2, badge: 'emptyDir' }),
      archNode('workspace', 'agentweaver-workspace', '50 Gi RWX shared persistence', 'workspace', 'workspace', 'marigold', { slot: 0, library: 'kubernetes', shape: 'mxgraph.kubernetes.persistent_volume_claim', badge: 'PVC' }),
      archNode('network', 'NetworkPolicy', 'Shared pod network with explicit egress', 'component', 'network', 'marigold', { slot: 1, library: 'kubernetes', shape: 'mxgraph.kubernetes.network_policy', badge: 'Policy' }),
      archNode('destinations', 'API, MCP, DNS, and HTTPS', 'Authentication remains separate', 'external', 'network', 'teal', { slot: 2, badge: 'Destinations' }),
    ],
    edges: [
      edge('worker', 'claim', 'create / adopt', { relation: 'dependency' }),
      edge('claim', 'template', 'warmPoolRef', { relation: 'dependency' }),
      edge('template', 'agenthost', 'instantiate Kata pod', { relation: 'dependency' }),
      edge('agenthost', 'relay', 'spawn trusted relay', { relation: 'flow' }),
      edge('agenthost', 'ipc', 'token-authenticated IPC', { relation: 'association' }),
      edge('relay', 'ipc', 'stream frames', { relation: 'association' }),
      edge('ipc', 'podexec', 'exec / spawn / stop', { relation: 'flow' }),
      edge('podexec', 'workload', 'launch and own', { relation: 'flow' }),
      edge('workspace', 'workload', 'registered scoped mount', { relation: 'association' }),
      edge('private-volumes', 'workload', 'private HOME / tmp', { relation: 'association' }),
      edge('network', 'destinations', 'effective allowance', { relation: 'dependency' }),
    ],
  },

  'canonical-aks-components': graph(
    'AKS deployment: application services, isolated execution, and Azure persistence',
    'Deployment view showing Azure Kubernetes Service, application and preview gateways, API, worker, MCP, and frontend deployments, Sandbox custom resources and Kata AgentHost pods, PostgreSQL, Azure Files, Key Vault, managed identities, and observability.',
    [
      azure('aks', 'Azure Kubernetes Service', 'Cilium, workload identity, and Kata capacity', 'kubernetes_services', 'teal', { group: 'azure', badge: 'AKS' }),
      azure('acr', 'Azure Container Registry', 'Images for application and AgentHost workloads', 'container_registries', 'lavender', { group: 'azure', badge: 'ACR' }),
      k8s('gateway', 'Application and preview Gateways', 'HTTPS routes for product and sandbox previews', 'service', 'teal', { group: 'cluster', badge: 'Gateway' }),
      k8s('api', 'API and worker Deployments', 'Admission, orchestration, and persistence', 'deployment', 'green', { group: 'cluster', badge: 'Deployments' }),
      k8s('mcp', 'MCP and frontend Deployments', 'Tool adapter and web/docs host', 'deployment', 'lavender', { group: 'cluster', badge: 'Deployments' }),
      k8s('claims', 'SandboxClaim and warm pool', 'Controller-managed run allocation', 'custom_resource', 'green', { group: 'cluster', badge: 'CRDs' }),
      k8s('pods', 'Kata AgentHost pods', 'Two-container isolated execution', 'pod', 'marigold', { group: 'cluster', badge: 'Pods' }),
      k8s('workspace-pvc', 'Workspace PVC', 'RWX mount provisioned through Azure Files CSI', 'persistent_volume_claim', 'teal', { group: 'cluster', badge: 'PVC' }),
      azure('postgres', 'PostgreSQL Flexible Server', 'Application state, plans, events, and leases', 'database_for_postgresql_servers', 'teal', { group: 'data', badge: 'Database' }),
      azure('files', 'Azure Files', 'Shared repositories, branches, and artifacts', 'storage_accounts', 'marigold', { group: 'data', badge: 'Files' }),
      azure('vault', 'Azure Key Vault', 'Application secrets and certificates', 'key_vaults', 'lavender', { group: 'security', badge: 'Secrets' }),
      azure('identity', 'Managed identities', 'Control-plane and AgentHost trust separation', 'managed_identities', 'green', { group: 'security', badge: 'Identity' }),
      azure('observability', 'Application Insights and Logs', 'Metrics, traces, logs, and alerts', 'application_insights', 'teal', { group: 'operations', badge: 'Telemetry' }),
    ],
    [
      edge('aks', 'api', 'hosts'), edge('aks', 'mcp', 'hosts'), edge('aks', 'pods', 'Kata capacity'),
      edge('acr', 'api', 'images'), edge('acr', 'pods', 'images'),
      edge('gateway', 'api', 'product HTTPS'), edge('gateway', 'mcp', 'web / MCP'),
      edge('gateway', 'pods', 'preview routes'), edge('api', 'claims', 'create / manage'),
      edge('claims', 'pods', 'bind'), edge('api', 'postgres', 'state and events'),
      edge('api', 'workspace-pvc', 'mount'), edge('pods', 'workspace-pvc', 'scoped mount'),
      edge('workspace-pvc', 'files', 'CSI provision'), edge('identity', 'vault', 'authorized access'),
      edge('api', 'observability', 'telemetry'),
    ],
    [
      { id: 'azure', label: 'Azure platform', tier: 1 },
      { id: 'cluster', label: 'Agentweaver namespace in AKS', tier: 1 },
      { id: 'data', label: 'Durable Azure data', tier: 1 },
      { id: 'security', label: 'Identity and secrets', tier: 1 },
      { id: 'operations', label: 'Operations', tier: 1 },
    ],
  ),

  'canonical-blueprint-relationships': {
    kind: 'architecture',
    notation: 'uml-component',
    title: 'Blueprints become project teams and runtime configuration',
    alt: 'UML component view showing catalog selection, repository suggestion, or model generation producing a Blueprint; the Blueprint owning roster and role definitions while referencing workflows, review policy, and sandbox profile; explicit validation and apply materializing project configuration and a cast team consumed by the coordinator.',
    layers: [
      { id: 'acquire', label: 'Acquire a reusable definition', nodes: ['sources', 'blueprint'] },
      { id: 'definition', label: 'Blueprint contract', nodes: ['roles', 'workflows', 'policies'] },
      { id: 'materialize', label: 'Explicit materialization', nodes: ['apply', 'project', 'team'] },
      { id: 'runtime', label: 'Later runtime consumption', nodes: ['coordinator', 'execution'] },
    ],
    nodes: [
      archNode('sources', 'Catalog, suggestion, or generation', 'Produces a definition; never auto-applies it', 'external', 'archive', 'lavender', { slot: 0, badge: 'Acquisition' }),
      archNode('blueprint', 'Blueprint', 'Reusable team and execution contract', 'component', 'document', 'green', { slot: 1, badge: 'Definition' }),
      archNode('roles', 'Roster and role definitions', 'Catalog roles, bespoke charters, and skills', 'component', 'worker', 'lavender', { slot: 0, badge: 'Who' }),
      archNode('workflows', 'Workflow set', 'One or more definitions; first supplies default', 'component', 'workflow', 'teal', { slot: 1, badge: 'How' }),
      archNode('policies', 'Review and sandbox settings', 'Supported review name and bounded sandbox preset', 'component', 'security', 'marigold', { slot: 2, badge: 'Guardrails' }),
      archNode('apply', 'Validate and apply', 'Resolve references, write YAML, cast, and synchronize', 'service', 'test', 'teal', { slot: 0, badge: 'Explicit action' }),
      archNode('project', 'Materialized project', 'Workspace and persisted defaults', 'workspace', 'workspace', 'marigold', { slot: 1, badge: 'Configuration' }),
      archNode('team', 'Cast team and charters', 'Named members plus infrastructure roles', 'component', 'worker', 'green', { slot: 2, badge: 'Team' }),
      archNode('coordinator', 'Coordinator', 'Selects workflow and assigns real members', 'coordinator', 'coordinator', 'lavender', { slot: 1, badge: 'Runtime' }),
      archNode('execution', 'Children and collective assembly', 'Leaf execution, gates, review, and merge', 'worker', 'merge', 'green', { slot: 2, badge: 'Outcome' }),
    ],
    edges: [
      edge('sources', 'blueprint', 'select / recommend / generate', { relation: 'dependency' }),
      edge('blueprint', 'roles', 'owns roster', { relation: 'association' }),
      edge('blueprint', 'workflows', 'references set', { relation: 'dependency' }),
      edge('blueprint', 'policies', 'selects settings', { relation: 'dependency' }),
      edge('blueprint', 'apply', 'validated input', { relation: 'dependency' }),
      edge('apply', 'project', 'persist defaults', { relation: 'flow' }),
      edge('apply', 'team', 'confirm cast', { relation: 'flow' }),
      edge('project', 'coordinator', 'configuration', { relation: 'dependency' }),
      edge('team', 'coordinator', 'dispatchable members', { relation: 'dependency' }),
      edge('workflows', 'coordinator', 'available topology', { relation: 'dependency' }),
      edge('coordinator', 'execution', 'plan / dispatch / assemble', { relation: 'flow' }),
    ],
  },
};

function compactArchitectureAsGraph(name, edgePairs, subLabels = {}) {
  const source = specs[name];
  const groupByNode = new Map(source.layers.flatMap((layer) => layer.nodes.map((nodeId) => [nodeId, layer.id])));
  const allowed = new Set(edgePairs.map(([from, to]) => `${from}->${to}`));
  specs[name] = graph(
    source.title,
    source.alt,
    source.nodes.map(({ role, slot, ...node }) => ({
      icon: 'box',
      ...node,
      subLabel: subLabels[node.id] ?? node.subLabel,
      group: groupByNode.get(node.id),
    })),
    source.edges
      .filter((item) => allowed.has(`${item.from}->${item.to}`))
      .map(({ relation, ...item }) => ({ ...item, label: item.label.replace('token-authenticated ', '').replace('authenticated ', '') })),
    source.layers.map((layer) => ({ id: layer.id, label: layer.label, tier: 1 })),
  );
}

compactArchitectureAsGraph('canonical-coordinator-architecture', [
  ['clients', 'api'], ['clients', 'mcp'], ['mcp', 'api'],
  ['api', 'agenthost'], ['worker', 'agenthost'],
  ['api', 'postgres'], ['worker', 'postgres'], ['worker', 'workspace'],
  ['agenthost', 'workspace'], ['api', 'entra'], ['api', 'github'], ['agenthost', 'models'],
], {
  clients: 'Submit, inspect, steer, and review',
  api: 'Admission, state, review, and events',
  worker: 'Planning and background orchestration',
  agenthost: 'Constrained leaf turns and tools',
  postgres: 'Plans, runs, events, and leases',
  workspace: 'Repositories, branches, and artifacts',
});

compactArchitectureAsGraph('canonical-sandbox-boundary', [
  ['project-policy', 'agenthost'], ['claim', 'agenthost'], ['agenthost', 'governance'],
  ['governance', 'file-shell'], ['file-shell', 'executor'], ['executor', 'workload'],
  ['workspace', 'workload'], ['network-policy', 'destinations'], ['workload', 'destinations'],
], {
  'project-policy': 'Declared application policy',
  'network-policy': 'Additive network enforcement',
  agenthost: 'Credentials and tool mediation',
  governance: 'Capabilities and known-tool checks',
  'file-shell': 'Path and command checks',
  executor: 'Unix-socket dispatcher',
  workload: 'Bubblewrap-scoped process',
  workspace: 'Per-run scoped projection',
});

compactArchitectureAsGraph('canonical-pod-process-boundaries', [
  ['worker', 'claim'], ['claim', 'template'], ['template', 'agenthost'],
  ['agenthost', 'relay'], ['agenthost', 'ipc'], ['relay', 'ipc'],
  ['ipc', 'podexec'], ['podexec', 'workload'], ['workspace', 'workload'],
  ['private-volumes', 'workload'], ['network', 'destinations'],
], {
  template: 'Two containers in a Kata pod',
  agenthost: 'A2A and trusted mediation',
  relay: 'Supervises spawn streams',
  ipc: 'Token and Unix socket',
  podexec: 'Dispatcher and registrations',
  workload: 'Executor-owned process group',
  'private-volumes': 'Distinct HOME and tmp views',
  workspace: 'RWX shared persistence',
});

compactArchitectureAsGraph('canonical-blueprint-relationships', [
  ['sources', 'blueprint'], ['blueprint', 'roles'], ['blueprint', 'workflows'],
  ['blueprint', 'policies'], ['blueprint', 'apply'], ['apply', 'project'],
  ['apply', 'team'], ['project', 'coordinator'], ['team', 'coordinator'],
  ['coordinator', 'execution'],
], {
  sources: 'Select, suggest, or generate; never auto-apply',
  blueprint: 'Reusable team and execution contract',
  roles: 'Roster, charters, and skills',
  workflows: 'Available topologies and default',
  policies: 'Review and sandbox guardrails',
  apply: 'Resolve, persist, cast, synchronize',
  project: 'Workspace and persisted defaults',
  team: 'Named members and infrastructure roles',
  coordinator: 'Selects workflow and assigns members',
  execution: 'Children, gates, review, and merge',
});
specs['canonical-blueprint-relationships'].groups = [];
for (const node of specs['canonical-blueprint-relationships'].nodes) delete node.group;

specs['canonical-default-workflow'].edges =
  specs['canonical-default-workflow'].edges.filter((item) =>
    !(item.from === 'merge' && item.to === 'review') &&
    !(item.from === 'agent' && item.to === 'failed'));
specs['canonical-coordinator-architecture'].edges =
  specs['canonical-coordinator-architecture'].edges.filter((item) =>
    !(item.from === 'mcp' && item.to === 'api') &&
    item.from !== 'github' && item.to !== 'github');
specs['canonical-coordinator-architecture'].nodes =
  specs['canonical-coordinator-architecture'].nodes
    .filter((item) => item.id !== 'github')
    .map((item) => item.id === 'models'
      ? { ...item, label: 'GitHub and model providers', subLabel: 'Purpose-bound external execution dependencies' }
      : item);
specs['canonical-coordinator-architecture'].groups = [];
for (const node of specs['canonical-coordinator-architecture'].nodes) delete node.group;
specs['canonical-memory-context'].nodes =
  specs['canonical-memory-context'].nodes.filter((item) => item.id !== 'session');
specs['canonical-memory-context'].edges =
  specs['canonical-memory-context'].edges.filter((item) => item.from !== 'session' && !(item.from === 'runtime' && item.to === 'authors'));
specs['canonical-memory-context'] = graph(
  'Context compilation and progressive tool disclosure',
  'Two-lane data-flow view showing approved decisions, attributed memory, current project and run state, role and workflow constraints, trust and budget filtering, compact context assembly, a shared tool capability registry, progressive disclosure from summaries to schemas and guidance, coordinator versus role-agent slices, and durable observations feeding later runs.',
  [
    card('knowledge', 'Approved decisions and memory', 'Attributed, governed history', 'memory', 'teal', { group: 'context', badge: 'Durable' }),
    card('current', 'Current run, role, and workflow', 'WorkPlan, task frontier, charter, review, and sandbox', 'state', 'lavender', { group: 'context', badge: 'Authority' }),
    card('compiler', 'Context compiler', 'Resolve provenance, audience, trust, and relevance', 'test', 'lavender', { group: 'context', badge: 'Compile' }),
    card('budget', 'Trust and budget filter', 'Rank approved, recent, relevant, role-eligible material', 'test', 'marigold', { group: 'context', badge: 'Bound' }),
    card('tool-registry', 'Shared tool registry', 'Names, authorization, schemas, and guidance', 'mcp', 'teal', { group: 'tools', badge: 'Shared' }),
    card('summaries', 'Disclosure index', 'Compact tool and context summaries first', 'archive', 'lavender', { group: 'tools', badge: 'Discover' }),
    card('detail', 'On-demand detail', 'Full schema, guidance, or supporting context', 'document', 'marigold', { group: 'tools', badge: 'Expand' }),
    card('context', 'Execution context', 'Task slice, approved memory, and tool summaries', 'document', 'green', { group: 'runtime', badge: 'Prompt input' }),
    card('runtime', 'Coordinator or role agent', 'Broad planning context vs scoped child execution', 'agent', 'green', { group: 'runtime', badge: 'Audience' }),
    card('observations', 'Attributed observations', 'Evidence and learnings return for governance', 'history', 'teal', { group: 'runtime', badge: 'Feedback' }),
  ],
  [
    edge('knowledge', 'compiler', 'approved history'),
    edge('current', 'compiler', 'state + authority'),
    edge('compiler', 'budget', 'eligible candidates'),
    edge('budget', 'context', 'ranked task slice'),
    edge('tool-registry', 'summaries', 'compact catalog'),
    edge('summaries', 'context', 'available tools'),
    edge('context', 'runtime', 'compose prompt'),
    edge('runtime', 'detail', 'request expansion'),
    edge('tool-registry', 'detail', 'schema + guidance'),
    edge('detail', 'runtime', 'just-in-time detail'),
    edge('runtime', 'observations', 'evidence + learning'),
    edge('observations', 'knowledge', 'approve for later use', { dashed: true }),
  ],
  [
    { id: 'context', label: 'Context compilation', tier: 1 },
    { id: 'tools', label: 'Shared tools and progressive disclosure', tier: 1 },
    { id: 'runtime', label: 'Runtime audience and feedback', tier: 1 },
  ],
);
specs['canonical-memory-context'].groups = [];
for (const node of specs['canonical-memory-context'].nodes) delete node.group;
specs['canonical-memory-context'].direction = 'TB';
specs['canonical-memory-context'].edges =
  specs['canonical-memory-context'].edges.filter((item) => !(item.from === 'observations' && item.to === 'knowledge'));
specs['canonical-board-lifecycle'].direction = 'TB';
specs['canonical-pod-process-boundaries'].direction = 'TB';
specs['canonical-provider-admission'].edges =
  specs['canonical-provider-admission'].edges.filter((item) =>
    !(item.from === 'policy' && item.to === 'reject') &&
    !(item.from === 'reject' && item.to === 'request'));
specs['canonical-aks-components'].edges =
  specs['canonical-aks-components'].edges.filter((item) =>
    item.from !== 'identity' && item.to !== 'identity' &&
    item.from !== 'observability' && item.to !== 'observability' &&
    !(item.from === 'aks' && item.to === 'mcp'));
specs['canonical-aks-components'].nodes =
  specs['canonical-aks-components'].nodes
    .filter((item) => !['identity', 'observability', 'mcp', 'acr'].includes(item.id))
    .map((item) => item.id === 'vault'
      ? { ...item, subLabel: 'Managed identities authorize application secret access' }
      : item.id === 'api'
        ? { ...item, label: 'Application deployments', subLabel: 'API, worker, MCP, and frontend workloads' }
      : item);
specs['canonical-aks-components'].edges =
  specs['canonical-aks-components'].edges
    .filter((item) =>
      item.from !== 'mcp' && item.to !== 'mcp' &&
      item.from !== 'acr' && item.to !== 'acr' &&
      !(item.from === 'aks' && item.to === 'pods'))
    .map((item) => item.from === 'gateway' && item.to === 'api'
      ? { ...item, label: 'product, web, and MCP HTTPS' }
      : item);
specs['canonical-aks-components'].groups = [];
for (const node of specs['canonical-aks-components'].nodes) delete node.group;
specs['canonical-aks-components'].direction = 'LR';

specs['canonical-blueprint-relationships'].nodes =
  specs['canonical-blueprint-relationships'].nodes
    .filter((item) => item.id !== 'team')
    .map((item) => item.id === 'project'
      ? { ...item, id: 'materialized', label: 'Materialized project and cast team', subLabel: 'Workspace defaults, named members, and charters' }
      : item);
specs['canonical-blueprint-relationships'].edges =
  specs['canonical-blueprint-relationships'].edges
    .filter((item) => !(item.from === 'apply' && item.to === 'team') && item.from !== 'team')
    .map((item) => item.from === 'apply' && item.to === 'project'
      ? { ...item, to: 'materialized', label: 'persist and cast' }
      : item.from === 'project' && item.to === 'coordinator'
        ? { ...item, from: 'materialized', label: 'configuration + members' }
        : item);

specs['canonical-board-lifecycle'].nodes = [
  ...specs['canonical-board-lifecycle'].nodes.filter((item) => ['backlog', 'ready', 'claimed'].includes(item.id)),
  card(
    'run-projection',
    'Linked run projection',
    'Active · Human Review · Problems · Done',
    'telemetry',
    'green',
    { badge: 'Projection' },
  ),
];
specs['canonical-board-lifecycle'].edges = [
  edge('backlog', 'ready', 'commit'),
  edge('ready', 'claimed', 'atomic pickup'),
  edge('claimed', 'run-projection', 'RunId + WorkPlan'),
];
specs['canonical-board-lifecycle'].direction = 'LR';

specs['canonical-pod-process-boundaries'].nodes =
  specs['canonical-pod-process-boundaries'].nodes
    .filter((item) => !['template', 'ipc', 'private-volumes', 'destinations'].includes(item.id))
    .map((item) => item.id === 'claim'
      ? { ...item, label: 'SandboxClaim, warm pool, and template', subLabel: 'Controller binds a two-container Kata pod' }
      : item.id === 'relay'
        ? { ...item, label: 'Trusted relay and exec-ipc', subLabel: 'Supervised stream over token-authenticated UDS' }
        : item.id === 'workload'
          ? { ...item, subLabel: 'Executor-owned group with private HOME and tmp' }
          : item.id === 'network'
            ? { ...item, label: 'NetworkPolicy and destinations', subLabel: 'API, MCP, DNS, and public HTTPS allowances' }
            : item);
specs['canonical-pod-process-boundaries'].edges = [
  edge('worker', 'claim', 'create / adopt'),
  edge('claim', 'agenthost', 'instantiate Kata pod'),
  edge('agenthost', 'relay', 'spawn / stream'),
  edge('relay', 'podexec', 'exec / spawn / stop'),
  edge('podexec', 'workload', 'launch and own'),
  edge('workspace', 'workload', 'registered scoped mount'),
  edge('network', 'workload', 'pod network policy'),
];

specs['canonical-blueprint-relationships'].nodes =
  specs['canonical-blueprint-relationships'].nodes.filter((item) =>
    ['sources', 'blueprint', 'apply', 'materialized', 'coordinator', 'execution'].includes(item.id));
specs['canonical-blueprint-relationships'].nodes =
  specs['canonical-blueprint-relationships'].nodes.map((item) => item.id === 'blueprint'
    ? { ...item, subLabel: 'Roster, charters, skills, workflows, review, and sandbox settings' }
    : item);
specs['canonical-blueprint-relationships'].edges = [
  edge('sources', 'blueprint', 'select / recommend / generate'),
  edge('blueprint', 'apply', 'validated definition'),
  edge('apply', 'materialized', 'persist and cast'),
  edge('materialized', 'coordinator', 'configuration + members'),
  edge('coordinator', 'execution', 'plan / dispatch / assemble'),
];

for (const spec of Object.values(specs)) {
  for (const node of spec.nodes ?? spec.participants ?? []) {
    if (node.library !== 'azure' || !node.shape?.startsWith('mxgraph.azure2.')) continue;
    const key = node.shape.split('.').at(-1);
    if (!azureIconPaths[key]) throw new Error(`Missing Azure icon mapping for ${key}`);
    node.shape = azureShape(key);
  }
}

function rewriteSequenceParticipant(name, removedId, replacementId, replacementLabel, replacementSubLabel) {
  const spec = specs[name];
  spec.participants = spec.participants
    .filter((item) => item.id !== removedId)
    .map((item) => item.id === replacementId ? { ...item, label: replacementLabel, subLabel: replacementSubLabel } : item);
  const rewriteSteps = steps => {
    for (const step of steps) {
      if (step.from === removedId) step.from = replacementId;
      if (step.to === removedId) step.to = replacementId;
      if (step.participant === removedId) step.participant = replacementId;
      if (step.over) step.over = step.over.map(id => id === removedId ? replacementId : id);
      for (const section of step.sections ?? []) rewriteSteps(section.steps ?? []);
    }
  };
  rewriteSteps(spec.steps);
}

rewriteSequenceParticipant(
  'canonical-coordinator-runtime-sequence',
  'control',
  'api',
  'API and coordinator',
  'Admission, planning, dispatch, and assembly',
);
specs['canonical-coordinator-runtime-sequence'].participants =
  specs['canonical-coordinator-runtime-sequence'].participants.map((item) => item.id === 'host'
    ? { ...item, label: 'AgentHost / A2A role agent', subLabel: 'Authenticated isolated leaf execution' }
    : item);
for (const step of specs['canonical-coordinator-runtime-sequence'].steps) {
  for (const section of step.sections ?? []) {
    for (const nested of section.steps ?? []) {
      if (nested.label === 'Start scoped child turn') nested.label = 'Start authenticated A2A child turn';
      if (nested.label === 'Evidence and assemble-ready result') nested.label = 'Stream A2A evidence and assemble-ready result';
    }
  }
}
specs['canonical-coordinator-architecture'].edges =
  specs['canonical-coordinator-architecture'].edges.map((item) => item.from === 'worker' && item.to === 'agenthost'
    ? { ...item, label: 'authenticated A2A leaf turns' }
    : item.from === 'api' && item.to === 'agenthost'
      ? { ...item, label: 'configure / callbacks' }
      : item);
specs['canonical-workflow-invocation'].edges =
  specs['canonical-workflow-invocation'].edges.map((item) => item.from === 'dispatch' && item.to === 'children'
    ? { ...item, label: 'A2A dependency-ready turn' }
    : item);
rewriteSequenceParticipant(
  'canonical-durable-event-stream-sequence',
  'db',
  'stream',
  'PostgreSQL event stream',
  'Commit, sequence allocation, and ordered reads',
);

await mkdir(outDir, { recursive: true });
for (const [name, spec] of Object.entries(specs)) {
  const withSchema = { $schema: spec.kind === 'sequence' ? './sequence-spec.schema.json' : spec.kind === 'architecture' ? './architecture-spec.schema.json' : './graph-spec.schema.json', ...spec };
  await writeFile(path.join(outDir, `${name}.json`), `${JSON.stringify(withSchema, null, 2)}\n`);
  console.log(`Wrote ${path.relative(repoRoot, path.join(outDir, `${name}.json`))}`);
}
