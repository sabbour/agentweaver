export type ModelSource = 'github-copilot' | 'microsoft-foundry';

export interface ServerInfo {
  data_directory: string;
  workspace_auto_assigned?: boolean;
}

export type RunStatus =
  | 'pending'
  | 'in_progress'
  | 'completed'
  | 'failed'
  | 'awaiting_review'
  | 'merging'
  | 'merged'
  | 'declined'
  | 'merge_failed'
  | 'assemble_ready';

export interface RunSandboxInfo {
  backend: string;
  isRealIsolation: boolean;
}

export interface SandboxPolicy {
  repository_path: string;
  shell_enabled: boolean;
  direct: boolean;
  network_enabled: boolean;
  allowed_repository_roots: string[];
  destructive_command_patterns: string[];
}

export interface SubmitRunRequest {
  repository_path: string;
  originating_branch: string;
  task: string;
  model_source: ModelSource;
}

export interface SubmitRunResponse {
  run_id: string;
  status: RunStatus;
}

export interface RetryRunResponse {
  run_id: string;
  retried_from: string;
  status: string;
}

export interface RunDetail {
  run_id: string;
  status: RunStatus;
  retried_from?: string | null;
  model_source: ModelSource;
  started_at: string;
  ended_at: string | null;
  result: string | null;
  diff: string | null;
  step_count: number;
  tree_hash: string | null;
  sandbox?: RunSandboxInfo | null;
  worktree_branch?: string | null;
  // Feature 008 — coordinator child runs. Non-null parent_run_id ⇒ this run is a
  // dispatched CHILD of a coordinator (trimmed agent → RAI → assemble-ready pipeline).
  parent_run_id?: string | null;
  subtask_id?: string | null;
  // GET /api/runs/{id} also carries the cast agent name for a child run (the list
  // endpoint omits child runs entirely). Optional — absent on plain runs.
  agent_name?: string | null;
  // Feature 008 Phase 3 — orchestration lifecycle string surfaced on a coordinator
  // run (dispatching | awaiting_assembly | assembling | in_review | complete | failed).
  // Added by the backend concurrently; treat as optional and degrade gracefully.
  coordinator_status?: string | null;
  coordinator_status_reason?: string | null;
  // Per-run options (live-toggleable). auto_approve_tools auto-grants non-dangerous tool HITLs;
  // autopilot (coordinator only) auto-answers clarifying questions via the coordinator model.
  // Both cascade to a coordinator's children. Optional — default false when absent.
  auto_approve_tools?: boolean;
  autopilot?: boolean;
}

// GET /api/runs/{id}/events — persisted append-only event log (FR-022). Used to seed
// the execution timeline for terminal/parked runs whose live SSE stream is closed.
// Shape mirrors the SSE frame: per-run sequence, event type, and JSON payload.
export interface PersistedRunEvent {
  sequence: number;
  type: string;
  payload: Record<string, unknown>;
}

export interface ReviewRequest {
  approved: boolean;
}

export interface ReviewResponse {
  run_id: string;
  status: string;
  merge_result: string | null;
}

export interface RetriableReviewErrorBody {
  error: string;
  status: string;
}

export interface WorkspaceFileEntry {
  path: string;
  status: 'added' | 'modified' | 'deleted';
  scope: 'committed' | 'uncommitted' | 'merged';
  added_lines: number;
  removed_lines: number;
}

export interface WorkspaceFileContent {
  path: string;
  content: string | null;
  is_binary: boolean;
  language: string | null;
}

export interface WorkspaceFileDiff {
  path: string;
  diff: string | null;
  status: 'added' | 'modified' | 'deleted';
  is_binary: boolean;
}

export interface WorkspaceNode {
  path: string;
  is_folder: boolean;
  status: 'added' | 'modified' | 'deleted' | null;
}

// Project Workspace browsing (read-only). A ref is either the project's base
// branch or an active run's worktree branch, selectable in the Workspace page.
export interface WorkspaceRef {
  kind: 'base' | 'worktree' | 'assembly';
  branch: string;
  label: string;
  run_id?: string;
  run_status?: string;
  originating_branch?: string;
}

export interface WorkspaceRefsResponse {
  current_branch: string;
  refs: WorkspaceRef[];
}

export interface CommitResponse {
  run_id: string;
  status: string;
  merge_result: string | null;
  conflicting_files: string[] | null;
}

export interface RequestChangesResponse {
  run_id: string;
  status: string;
}

// Projects
export type ProjectOrigin = 'blank' | 'github';
export type ProjectState = 'active' | 'deleting';

export interface Project {
  project_id: string;
  name: string;
  origin: ProjectOrigin;
  source_repository: string | null;
  working_directory: string;
  default_branch: string;
  owner: string;
  default_provider: ModelSource;
  default_model_github_copilot: string | null;
  default_model_microsoft_foundry: string | null;
  available: boolean;
  state: ProjectState;
  created_at: string;
  updated_at: string;
}

export interface Blueprint {
  id: string;
  name: string;
  description: string;
  roster: string[];
  workflow: string;
  review_policy: string;
  sandbox_profile: string;
}

export interface ListBlueprintsResponse {
  blueprints: Blueprint[];
}

export interface GenerateBlueprintRequest {
  description: string;
  target_repository?: string | null;
}

export interface GenerateBlueprintResponse {
  blueprint: Blueprint;
  generated_workflow_yaml?: string | null;
}

export interface CreateProjectRequest {
  name: string;
  origin: ProjectOrigin;
  source_repository?: string;
  working_directory: string;
  default_provider?: ModelSource;
  default_model_github_copilot?: string;
  default_model_microsoft_foundry?: string;
  blueprint_id?: string;
  blueprint?: Blueprint;
  generated_workflow_yaml?: string | null;
}

export interface UpdateProjectProviderSettingsRequest {
  default_provider?: ModelSource;
  default_model_github_copilot?: string;
  default_model_microsoft_foundry?: string;
}

export interface CreateProjectRunRequest {
  task: string;
  model_source?: ModelSource;
  model_id?: string;
  base_branch?: string;
}

export interface ProjectRunSummary {
  run_id: string;
  status: string;
  model_source: ModelSource;
  model_id: string | null;
  task: string | null;
  started_at: string;
  ended_at: string | null;
}

export interface WorkflowRunDto {
  workflow_run_id: string;
  execution_id: string;
  task: string;
  status: string;
  result?: string;
  agent_name?: string;
  reviewed_by?: string;
  started_at: string;
  ended_at?: string;
  model_id?: string;
  total_tokens?: number | null;
  total_nano_aiu?: number | null;
  // Feature 008 Phase 3 — orchestration lifecycle for a coordinator run. Optional;
  // present only once the backend adds it, so render the bare status as a fallback.
  coordinator_status?: string;
  coordinator_status_reason?: string;
  archived_at?: string | null;
}

export interface DecisionDto {
  id: string;
  agent_name: string;
  type: string;
  status: string;
  title: string;
  content: string;
  rationale?: string;
  tags?: string;
  created_at: string;
  updated_at: string;
}

export interface DecisionInboxEntryDto {
  id: string;
  agent_name: string;
  slug: string;
  type: string;
  title: string;
  content: string;
  rationale?: string;
  status: string;
  created_at: string;
  updated_at: string;
}

export interface AgentMemoryDto {
  id: string;
  agent_name: string;
  type: string;
  importance: string;
  content: string;
  tags?: string;
  created_at: string;
  updated_at: string;
}

export interface CreateRunRequest {
  repository_path?: string;
  originating_branch: string;
  task: string;
  model_source?: string;
  agent_name?: string;
}

export interface CreateProjectRunResponse {
  run_id: string;        // execution id
  workflow_run_id: string;
  status: string;
}

export interface RunDto {
  run_id: string;
  status: string;
  model_source: string;
  model_id?: string;
  agent_name?: string;
  reviewed_by?: string;
  task: string;
  started_at: string;
  ended_at?: string;
  step_count?: number;
  originating_branch?: string;
  workflow_run_id?: string;
}

// --- Feature 008 Phase 3 — Dynamic graph descriptor ---
// GET /api/runs/{id}/graph returns a descriptor that replaces the hardcoded executor
// lists in WorkflowRunPage. The client renders it as-is; node ids equal the logical
// step keys already used by the status reducer (agent/rai/review/merge/scribe/assemble-ready).

export type GraphNodeKind = 'live' | 'planned';
export type GraphEdgeCardinality = 'direct' | 'fanout' | 'fanin';
export type GraphVariant = 'full' | 'child' | 'coordinator';

// node_type — self-declared structural category separate from `role` and `kind`.
// Drives card shape and size in the generic renderer.
export type GraphNodeType = 'agent' | 'action' | 'gate' | 'terminal' | 'subtask';

export interface GraphNode {
  id: string;              // logical step key; also the status reducer lookup key
  label: string;           // display label shown on the card
  role: string;            // drives icon + color: agent|rai|review|merge|scribe|coordinator|subtask|assembly
  kind: GraphNodeKind;     // 'planned' nodes render dashed/muted; never show a pending spinner
  node_type?: GraphNodeType; // drives card shape + size (agent=largest, action/gate=medium, terminal=small, subtask=expandable)
  child_graph_ref?: string;  // coordinator subtask nodes: ref to child run graph, e.g. "run:{childRunId}"
  // Optional display fields emitted by coordinator subtask nodes (may be flat fields OR nested in a `data` map —
  // read defensively from both locations).
  agent?: string;
  model?: string;
  phase?: string;
  isolation?: string;
  child_run_id?: string;
  data?: Record<string, unknown>;
}

export interface GraphEdge {
  from: string;
  to: string;
  cardinality: GraphEdgeCardinality;
  loopback: boolean;       // true = back-edge excluded from dagre input, drawn as loopback arc
}

export interface GraphDescriptor {
  graph_id: string;
  variant: GraphVariant;
  start_node_id: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
}

// GitHub auth
export type GitHubAuthStatus = 'signed_in' | 'signed_out' | 'never_signed_in';

export interface GitHubDeviceFlow {
  user_code: string;
  verification_uri: string;
  expires_in: number;
  interval: number;
}

export interface GitHubPollResult {
  status: 'pending' | 'success' | 'expired' | 'denied';
  login: string | null;
}

export interface GitHubAuthStatusResponse {
  status: GitHubAuthStatus;
  login: string | null;
  avatar_url?: string;
}

export interface GitHubRepo {
  fullName: string | null;
  htmlUrl?: string | null;
  description?: string | null;
  private: boolean;
  defaultBranch: string;
}

export interface GitHubAccount {
  login: string;
  name: string | null;
  avatar_url: string;
  type: 'user' | 'org';
}

// --- Casting / Team types ---

export interface TeamTemplateDto {
  id: string;
  title: string;
  description: string;
  roles: RoleDto[];
}

export interface RoleDto {
  id: string;
  title: string;
  summary: string;
  default_model: string;
}

export interface ProposedMemberDto {
  proposed_name: string;
  role: RoleDto;
  charter_markdown: string;
  is_named: boolean;
  default_model: string;
  justification: string | null;
}

export interface CastProposalDto {
  proposal_id: string;
  mode: 'scenario' | 'free_text' | 'analysis' | 'manual';
  universe: string;
  members: ProposedMemberDto[];
  existing_team_present: boolean;
  run_id: string | null;
  warnings: string[];
  rationale?: string;
}

export interface CreateProposalRequest {
  mode: 'scenario' | 'free_text' | 'analysis' | 'manual';
  template_id?: string;
  goal?: string;
  universe?: string;
  model_id?: string;
  role_ids?: string[];
  team_size?: number;
}

export interface AmendProposalRequest {
  members?: ProposedMemberDto[];
  universe?: string;
}

export interface ConfirmProposalRequest {
  intent?: 'new' | 'augment' | 'recast';
}

export interface TeamMemberDto {
  name: string;
  role_title: string;
  charter_path: string;
  status: 'active' | 'retired';
  default_model: string;
  is_named: boolean;
  is_built_in: boolean;
  charter_created_at?: string | null;
  charter_updated_at?: string | null;
}

export interface HistoryDto {
  member_name: string;
  content: string;
}

export interface TeamDto {
  project_name: string;
  universe: string;
  members: TeamMemberDto[];
  layout: 'canonical' | 'legacy' | 'absent';
  migration_available: boolean;
}

export interface CharterDto {
  member_name: string;
  content: string;
}

export interface AddMemberRequest {
  role_id: string;
  custom_role_title?: string;
  model_id?: string;
}

export interface ReroleRequest {
  new_role_id: string;
  custom_role_title?: string;
}

export interface SyncChangeDto {
  path: string;
  kind: 'added' | 'modified' | 'removed';
}

export interface SyncStatusDto {
  changes: SyncChangeDto[];
  change_set_hash: string;
  nothing_to_sync: boolean;
}

export interface SyncCommitRequest {
  expected_change_set_hash: string;
  message?: string;
}

export interface SyncCommitResponseDto {
  commit_id: string;
}

// Feature 008 — Squad Coordinator Agent (orchestration / outcome spec)
export type OutcomeSpecStatus = 'drafting' | 'awaiting_confirmation' | 'confirmed' | 'declined';

// Server-authored outcome spec. Scope/assumptions/clarifyingQuestions may arrive
// either as a single string or as a list depending on the coordinator's output;
// the panel renders them defensively (Principle III — render server state as-is).
export interface OutcomeSpec {
  goal?: string;
  desiredOutcome?: string;
  scope?: string | string[];
  assumptions?: string | string[];
  clarifyingQuestions?: string[];
  status: OutcomeSpecStatus;
  confirmedBy?: string;
}

export interface StartOrchestrationRequest {
  goal: string;
}

export interface StartOrchestrationResponse {
  runId: string;
}

export interface ReviseOutcomeSpecRequest {
  feedback: string;
}

// --- Feature 008 Phase 2 — Coordinator dynamic topology + steering ---
// All of these mirror server event contracts. The client renders them as-is
// (Principle III — thin client, no topology computation).

export type SubtaskStatus =
  | 'pending'
  | 'dispatched'
  | 'running'
  | 'assemble_ready'
  | 'rai_flagged'
  | 'completed'
  | 'failed'
  | 'pending_capacity';

// coordinator.work_plan event payload.
export interface WorkPlanSubtask {
  id: string | number;
  title: string;
  assignedAgent?: string;
  selectedModelId?: string;
  phase?: string;
  isolation?: string;
  dependsOn?: number[];
}

export interface CoordinatorWorkPlan {
  workPlanId: string;
  status: string;
  subtasks: WorkPlanSubtask[];
}

export type TopologyNodeKind = 'coordinator' | 'subtask';

// A node in the coordinator.topology graph (snapshot node or delta-changed node).
export interface TopologyNode {
  id: string;
  kind: TopologyNodeKind;
  title: string;
  status: string;
  assignedAgent?: string;
  selectedModelId?: string;
  childRunId?: string;
  /** Pod name for the execution environment of this specific node (spec-018). Null today — all agents share the API pod; set per-node after distributed phases. */
  executionPodName?: string | null;
}

// Dependency edge: from = dependency, to = dependent. Edges never change.
export interface TopologyEdge {
  from: string;
  to: string;
}

// coordinator.topology snapshot (seq 0).
export interface TopologySnapshot {
  version: number;
  seq: number;
  nodes: TopologyNode[];
  edges: TopologyEdge[];
}

// coordinator.topology delta (seq > 0) — changed nodes merged by id.
export interface TopologyDelta {
  version: number;
  seq: number;
  changed: TopologyNode[];
}

// subtask.* event payload.
export interface SubtaskEvent {
  subtaskId: string;
  childRunId?: string;
  assignedAgent?: string;
  selectedModelId?: string;
  status: SubtaskStatus;
}

// GET /api/runs/{coordinatorRunId}/work-plan — the persisted plan (subtasks + dependency edges).
// Used to seed the topology view on page load before SSE deltas arrive (snapshot-race fix).
export interface WorkPlanSubtaskResponse {
  subtaskId: number;
  title: string;
  scope: string;
  assignedAgent: string;
  selectedModelId: string;
  phase: string;
  isolation: string;
  status: string;
  childRunId?: string;
}

export interface WorkPlanDependencyResponse {
  subtaskId: number;
  dependsOnSubtaskId: number;
}

export interface WorkPlanResponse {
  workPlanId: number;
  coordinatorRunId: string;
  outcomeSpecId: number;
  status: string;
  isolationSummary?: string;
  subtasks: WorkPlanSubtaskResponse[];
  dependencies: WorkPlanDependencyResponse[];
}

// GET /api/runs/{coordinatorRunId}/children — dispatched child runs paired with subtask status.
export interface CoordinatorChildResponse {
  subtaskId: number;
  childRunId: string;
  subtaskStatus: string;
  assignedAgent: string;
  selectedModelId: string;
  childRunStatus?: string;
  worktreeBranch?: string;
  treeHash?: string;
  stepCount: number;
}

export type SteerKind = 'send' | 'redirect' | 'amend' | 'stop';

// POST /api/runs/{coordinatorRunId}/steer body.
// kind "send"     {instruction}                         — informational; no re-plan, no subtask mutation.
// kind "redirect" {instruction, target_child_run_id?}   — re-plans/re-arms; a target child is force-completed to unblock it.
// kind "amend"    {instruction}                          — additive; extends the outcome spec/plan; never discards in-flight work.
// kind "stop"     {}                                     — stop the orchestration.
export interface SteerCoordinatorRequest {
  kind: SteerKind;
  target_child_run_id?: string;
  instruction?: string;
}

// POST /api/runs/{coordinatorRunId}/steer response body.
// status:"queued"  — run was live; directive applies at the next turn boundary.
// status:"applied" — run was parked/failed and has been recovered (subtasks reset, run resumed).
export interface SteerCoordinatorResponse {
  status: 'queued' | 'applied' | string;
}

// coordinator.steering event payload — steering directive state surfaced on a node.
export interface SteeringDirective {
  directiveId: string;
  kind: SteerKind;
  targetChildRunId?: string;
  status: string;
  instruction?: string;
}

// POST /api/runs/{coordinatorRunId}/assembly/review body — the collective human review
// over the assembled integration output (approve / request_changes / decline).
export type AssemblyReviewDecision = 'approve' | 'request_changes' | 'decline';

export interface AssemblyReviewRequest {
  decision: AssemblyReviewDecision;
  comment?: string;
}

// POST /api/runs/{id}/questions/{requestId}/answer — answer a worker's bubbled question
// (agent.question_asked). For a coordinator child question/approval the answer is routed to the
// childRunId (the run that asked), not the coordinator run id.
export interface AnswerQuestionResponse {
  run_id: string;
  request_id: string;
  answered: boolean;
}

// POST /api/runs/{id}/auto-approve and /autopilot — live per-run option toggles.
export interface AutoApproveResponse {
  run_id: string;
  auto_approve_tools: boolean;
}

export interface AutopilotResponse {
  run_id: string;
  autopilot: boolean;
}

// -----------------------------------------------------------------------
// Feature 009 — Backlog & Workflow Kanban board. snake_case JSON DTOs,
// mirroring apps/Agentweaver.Api/Contracts/Dtos.cs (the Web is a thin client).
// -----------------------------------------------------------------------

// Full backlog-task projection returned by capture/edit/move/reorder.
export interface BacklogTaskDto {
  task_id: string;
  project_id: string;
  title: string;
  description: string | null;
  state: string; // backlog | ready | claimed
  order_key: string;
  captured_by: string;
  created_at: string;
  committed_at?: string | null;
  claimed_at?: string | null;
  run_id?: string | null;
  archived_at?: string | null;
}

// A Backlog/Ready intake card (board column kind === "intake").
export interface TaskCardDto {
  kind: 'task';
  task_id: string;
  title: string;
  description: string | null;
  state: string; // backlog | ready
  order_key: string;
  captured_by: string;
  created_at: string;
  committed_at?: string | null;
  archived_at?: string | null;
}

// A coordinator-run card placed in a workflow column (read-only).
export interface RunCardDto {
  kind: 'run';
  run_id: string;
  workflow_run_id?: string | null;
  backlog_task_id?: string | null;
  task: string;
  status: string;
  retried_from?: string | null;
  work_plan_status?: string | null;
  assembly_stage?: string | null;
  stage_id: string;
  agent_name?: string | null;
  started_at: string;
  ended_at?: string | null;
  archived_at?: string | null;
  has_pending_approval?: boolean;
  total_tokens?: number | null;
  total_nano_aiu?: number | null;
}

export type BoardCardDto = TaskCardDto | RunCardDto;

// A board column with its cards. Columns are server-ordered; the Web never
// hardcodes workflow stage names (FR-015) — it renders whatever the API returns.
export interface BoardColumnDto {
  id: string;
  kind: 'intake' | 'workflow';
  label: string;
  cards: BoardCardDto[];
  collapsed_count?: number;
}

// Per-agent load summary rolled up across all coordinator runs in the project.
// Added by the backend as board.agent_queues (FR-phase2-rail).
export interface AgentOrchestrationQueueDto {
  run_id:        string;
  title:         string | null;
  active:        number;
  queued:        number;
  blocked:       number;
  done:          number;
  sample_titles: string[];   // up to 3 subtask titles for THIS orchestration
}

export interface AgentQueueDto {
  agent_name:    string;
  active:        number;
  queued:        number;
  blocked:       number;
  done:          number;
  run_ids:       string[];   // coordinator run ids with ≥1 subtask for this agent
  sample_titles: string[];   // up to 3 subtask titles
  orchestrations: AgentOrchestrationQueueDto[]; // per-orchestration breakdown of this agent's work
}

// Response body for GET /api/projects/{projectId}/board.
export interface BoardDto {
  project_id: string;
  workflow_stages_available: boolean;
  columns: BoardColumnDto[];
  agent_queues?: AgentQueueDto[]; // optional — degrades gracefully if backend not yet deployed
}

// Per-project pickup settings (FR-008a).
export interface BacklogSettingsDto {
  max_ready_per_heartbeat: number;
  pickup_autopilot: boolean;
  pickup_auto_approve_tools: boolean;
}

// A single workflow-stage column descriptor.
export interface WorkflowStageDto {
  id: string;
  label: string;
}

// Response body for GET /api/projects/{projectId}/workflow-stages.
export interface WorkflowStagesResponse {
  available: boolean;
  stages: WorkflowStageDto[];
}

// GET /api/system/runtime — Kubernetes execution context for the running API pod.
export interface RuntimeInfo {
  kubernetes: boolean;
  podName: string | null;
}

// A single executed diagnostic probe with its outcome (FR-016). snake_case wire.
export interface DiagnosticsCheckDto {
  name: string;
  status: string; // "pass" | "warn" | "fail"
  detail: string;
  duration_ms: number;
}

// Detailed diagnostic check from GET /api/diagnostics/detailed (spec-018 capacity visibility).
// Optional fields are populated only when relevant (e.g. quota checks emit used/limit/unit).
export interface DetailedDiagnosticsCheckDto {
  name: string;
  status: 'healthy' | 'warning' | 'critical' | 'unknown';
  message?: string;
  latencyMs?: number;
  used?: number;
  limit?: number;
  unit?: string;
  pendingCount?: number;
}

// Detailed system diagnostics snapshot from GET /api/diagnostics/detailed.
export interface DetailedSystemDiagnosticsDto {
  generated_utc: string;
  total_duration_ms: number;
  checks: DetailedDiagnosticsCheckDto[];
}

// ── Cluster diagnostics (GET /api/diagnostics/cluster) ──────────────────────
// ── Cluster diagnostics (GET /api/diagnostics/cluster) ─────────────────────
export interface DetailedHealthCheckDto {
  name: string;
  status: string; // 'healthy' | 'degraded' | 'warning' | 'critical' | 'unknown'
  message: string;
  latencyMs: number;
  used?: number | null;
  limit?: number | null;
  unit?: string | null;
  pendingCount?: number | null;
}

export interface AgentPodInfoDto {
  claim_name: string;
  run_id?: string | null;
  pod_name?: string | null;
  status: string; // 'ready' | 'pending'
  age_seconds?: number | null;
}

export interface PendingCapacityRunDto {
  subtask_id: number;
  work_plan_id: number;
  child_run_id?: string | null;
  status: string;
  reason?: string | null;
  age_seconds: number;
}

export interface WarmPoolStatusDto {
  name: string;
  desired_replicas: number;
  ready_replicas: number;
  available_replicas: number;
  status: string; // 'healthy' | 'warning' | 'critical'
  age_seconds?: number | null;
}

export interface SandboxObjectDto {
  name: string;
  phase: string; // 'running' | 'pending' | 'standby' | 'unknown'
  ready: boolean;
  pod_name?: string | null;
  template_ref?: string | null;
  warm_pool?: string | null;
  age_seconds?: number | null;
}

export interface SandboxClaimObjectDto {
  name: string;
  phase: string; // 'bound' | 'pending' | 'unknown'
  ready: boolean;
  run_id?: string | null;
  bound_sandbox?: string | null;
  sandbox_template_ref?: string | null;
  warm_pool?: string | null;
  age_seconds?: number | null;
}

export interface ClusterDiagnosticsDto {
  generated_utc: string;
  total_duration_ms: number;
  checks: DetailedHealthCheckDto[];
  active_agent_pods: AgentPodInfoDto[];
  orphaned_agent_pods: AgentPodInfoDto[];
  pending_capacity_runs: PendingCapacityRunDto[];
  warm_pools?: WarmPoolStatusDto[];
  sandbox_objects?: SandboxObjectDto[];
  sandbox_claims?: SandboxClaimObjectDto[];
}

// Global system diagnostics snapshot (FR-016). All fields sourced from live state.
export interface SystemDiagnosticsDto {
  api_version: string;
  process_started_utc: string;
  uptime_seconds: number;
  total_projects: number;
  total_runs: number;
  active_runs: number;
  generated_utc: string;
  total_duration_ms: number;
  checks: DiagnosticsCheckDto[];
}

// Project-scoped diagnostics snapshot for one project's workspace/config (FR-016).
export interface ProjectDiagnosticsDto {
  project_id: string;
  project_name: string;
  generated_utc: string;
  total_duration_ms: number;
  checks: DiagnosticsCheckDto[];
}

// One heartbeat tick outcome in the ring buffer (FR-017).
export interface HeartbeatTickDto {
  timestamp_utc: string;
  acted_count: number;
  error_count: number;
  duration_ms: number;
  error: string | null;
  automation_name: string;
}

// One real background automation running in the API process (FR-017).
export interface HeartbeatAutomationDto {
  name: string;
  description: string;
  cadence_seconds: number;
  last_run_utc: string | null;
  last_acted_count: number | null;
  status: string;
}

// Read-only snapshot of the coordinator heartbeat service (FR-017).
export interface HeartbeatStatusDto {
  enabled: boolean;
  interval_seconds: number;
  last_tick_utc: string | null;
  service_status: string;
  last_error: string | null;
  recent_activity: HeartbeatTickDto[];
  automations: HeartbeatAutomationDto[];
}

// ── Workflow definitions (Spec 010, FR-039/041) ──────────────────────────────
// Trigger descriptor in workflow responses (snake_case over the wire).
export interface WorkflowTriggerDto {
  type: string;
  event?: string | null;
}

// Response body for GET raw YAML content of a project workflow file (US7).
export interface WorkflowYamlResponse {
  yaml: string;
}

// A workflow in the project's list response: identity, trigger, validation.
export interface WorkflowSummaryDto {
  id: string | null;
  name: string | null;
  description: string | null;
  trigger: WorkflowTriggerDto | null;
  source: string;
  valid: boolean;
  error: string | null;
  is_built_in: boolean;
  is_default: boolean;
}

// Response body for GET/POST the project's workflows list.
export interface WorkflowListResponse {
  default_workflow_id: string;
  workflows: WorkflowSummaryDto[];
}

// Response body for PUT a per-task workflow override (Feature 010, FR-042).
export interface WorkflowOverrideResponse {
  task_id: string;
  workflow_override_id: string | null;
}

// A node in a workflow detail response.
export interface WorkflowNodeDto {
  id: string;
  type: string;
  label: string;
  role?: string | null;
  kind?: string | null;
  gate_kind?: string | null;
  agent?: string | null;
  prompt?: string | null;
  target?: string | null;
  steps?: string[] | null;
  branches?: string[] | null;
}

// An edge in a workflow detail response.
export interface WorkflowEdgeDto {
  from: string;
  to: string;
  when?: string | null;
}

// Full definition for GET a single workflow.
export interface WorkflowDetailDto {
  id: string;
  name: string;
  description: string | null;
  trigger: WorkflowTriggerDto;
  start: string;
  source: string;
  is_built_in: boolean;
  is_default: boolean;
  nodes: WorkflowNodeDto[];
  edges: WorkflowEdgeDto[];
}

// Workflow graph descriptor (US6). Matches GraphDescriptor shape for WorkflowGraphPanel.
export interface WorkflowGraphNodeDto {
  id: string;
  label: string;
  role: string;
  kind: 'planned';
  node_type?: GraphNodeType;
}
export interface WorkflowGraphEdgeDto {
  from: string;
  to: string;
  cardinality: 'direct';
  loopback: boolean;
  label?: string | null;
}
export interface WorkflowGraphDto {
  graph_id: string;
  variant: string;
  start_node_id: string;
  nodes: WorkflowGraphNodeDto[];
  edges: WorkflowGraphEdgeDto[];
}

// ── Review policies (Spec 010, FR-025/027/033) ───────────────────────────────
// A single review step within a policy (snake_case over the wire).
export interface ReviewStepDto {
  kind: string;
  label?: string | null;
}

// A review policy in the project's list response: identity, validation, source.
export interface ReviewPolicySummaryDto {
  name: string | null;
  description: string | null;
  source: string;
  valid: boolean;
  error: string | null;
  is_built_in: boolean;
  is_active: boolean;
}

// Response body for GET/POST the project's review-policies list.
export interface ReviewPolicyListResponse {
  active_policy_name: string;
  policies: ReviewPolicySummaryDto[];
}

// Full definition for GET a single review policy (its ordered review steps).
export interface ReviewPolicyDetailDto {
  name: string;
  description: string | null;
  source: string;
  is_built_in: boolean;
  is_active: boolean;
  steps: ReviewStepDto[];
}

// Request body for PUT the project's active review policy (null clears to default).
export interface SetActiveReviewPolicyRequest {
  name: string | null;
}

// ── Metrics: Dashboard + Overview (web IA reorg) ─────────────────────────────
// Mirrors Agentweaver.Api.Metrics DTOs (snake_case over the wire). All values are
// sourced from live stores; cost and per-workflow health are intentionally absent.

// Per-project dashboard headline counters.
export interface DashboardSummaryDto {
  runs_this_week: number;
  runs_total: number;
  active_runs: number;
  active_agents: number;
  tasks_done_this_week: number;
}

// One day in the 30-day throughput series.
export interface ThroughputPointDto {
  date: string;
  created: number;
  done: number;
}

// Per-agent activity + quality on a single project.
export interface AgentLeaderboardEntryDto {
  agent: string;
  role_title?: string | null;
  runs_this_week: number;
  runs_total: number;
  success_rate: number;        // successful terminal runs / terminal runs, [0,1]
  successful_runs: number;
  terminal_runs: number;
  avg_duration_ms: number | null;
  total_tokens?: number | null;
  total_nano_aiu?: number | null;
}

// ── Feature 019 — AI token and credit usage ──────────────────────────────────
// Per-model breakdown row returned by the usage endpoints.
export interface TokenUsageByModel {
  model_id: string;
  input_tokens: number;
  output_tokens: number;
  total_nano_aiu: number;
}

// Aggregate token usage summary for a single run or project window.
export interface TokenUsageSummary {
  input_tokens: number;
  output_tokens: number;
  total_tokens: number;
  total_nano_aiu: number;
  by_model: TokenUsageByModel[];
}

// Per-project rollup inside an app-level usage snapshot.
export interface ProjectUsage {
  project_id: string;
  project_name: string;
  total_tokens: number;
  total_nano_aiu: number;
  by_model: TokenUsageByModel[];
}

// App-level usage snapshot (admin only — GET /api/usage).
export interface AppUsage {
  generated_utc: string;
  from_utc: string;
  to_utc: string;
  total_tokens: number;
  total_nano_aiu: number;
  by_project: ProjectUsage[];
  by_model: TokenUsageByModel[];
}

// Response body for GET /api/projects/{id}/dashboard.
export interface ProjectDashboardDto {
  project_id: string;
  project_name: string;
  generated_utc: string;
  summary: DashboardSummaryDto;
  throughput: ThroughputPointDto[];
  agent_leaderboard: AgentLeaderboardEntryDto[];
  token_usage?: TokenUsageSummary;
}

// Global overview "at a glance" counters.
export interface AtAGlanceDto {
  in_flight: number;
  queued_work: number;
  done_today: number;
  active_projects: number;
  health: string;              // "healthy" | "degraded"
}

// An active run surfaced as a live session.
export interface LiveSessionDto {
  project_id: string;
  project_name: string;
  agent: string | null;
  status: string;
  started_utc: string;
  last_activity_utc: string;
}

// An in-progress/pending orchestration run.
export interface ActiveWorkflowRunDto {
  project_id: string;
  project_name: string;
  trigger: string;
  status: string;
  started_utc: string;
}

// Per-project rollup of active + queued work.
export interface ActiveProjectDto {
  project_id: string;
  project_name: string;
  active_count: number;
  queued_count: number;
  last_activity_utc: string | null;
}

// A recent run/orchestration lifecycle event.
export interface RecentActivityDto {
  project_id: string;
  project_name: string;
  label: string;
  kind: string;
  timestamp_utc: string;
}

// Response body for GET /api/overview.
export interface OverviewDto {
  generated_utc: string;
  at_a_glance: AtAGlanceDto;
  live_sessions: LiveSessionDto[];
  active_workflow_runs: ActiveWorkflowRunDto[];
  active_projects: ActiveProjectDto[];
  recent_activity: RecentActivityDto[];
  token_usage?: AppUsage;
}

// ── Feature 014 — Spec-to-Backlog decomposition ───────────────────────────────
// GET /api/projects/{id}/workspace/files — scoped file tree for the project sandbox.
export interface WorkspaceFileNode {
  name: string;
  relative_path: string;
  is_directory: boolean;
  children?: WorkspaceFileNode[];
}

// A single backlog item proposed by the decomposition agent.
export interface ProposedBacklogItem {
  title: string;
  description?: string;
  already_exists: boolean;
}

// POST /api/projects/{id}/backlog/decompose response.
// When confirm=false: dry-run preview (no tasks created).
// When confirm=true:  tasks are created; proposed_items reflects what was written.
export interface DecomposeResponse {
  proposed_items: ProposedBacklogItem[];
  was_capped: boolean;
  total_found: number;
}

// ── Feature 017 — Sandbox preview port-forward ───────────────────────────────
// POST /api/runs/{runId}/sandbox/port-forward
export interface PortForwardSessionDto {
  session_id: string;
  local_port: number;
  target_port: number;
  pod_name: string;
  started_at: string;
  preview_url?: string | null;
  previewUrl?: string | null;
  keepalive_url?: string | null;
  keepaliveUrl?: string | null;
}
