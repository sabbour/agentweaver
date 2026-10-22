import {
  clearSessionAuth,
  getSessionToken,
  notifySessionAuthInvalid,
  requestSessionAuthFromPeer,
} from '../config';
import {
  MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT,
  isModelProviderConnectionRequirement,
} from './modelProviderConnectionRequirement';
import { isSkillProvenance } from './types';
import type {
  AddMemberRequest,
  ApplyBlueprintSkillDefaultsResponse,
  AmendProposalRequest,
  AnswerQuestionResponse,
  AiExecutionContext,
  AuthConfigResponse,
  AuthSessionResponse,
  AssemblyReviewDecision,
  AssemblyReviewRequest,
  AssemblyReviewResponse,
  AutoApproveResponse,
  AutopilotResponse,
  BacklogSettingsDto,
  BacklogDependenciesResponse,
  BacklogTaskDto,
  ByokProviderConfig,
  ByokProviderListResponse,
  ByokProviderRequest,
  UserAiAccessStatus,
  Blueprint,
  BlueprintSkillDefaultsPreviewResponse,
  BoardDto,
  CastProposalDto,
  CharterDto,
  ClusterDiagnosticsDto,
  CommitResponse,
  ConfirmProposalRequest,
  ConnectedRepository,
  ConnectProjectRepositoryRequest,
  CoordinatorChildResponse,
  CreateProjectRepositoryRequest,
  CreateProjectRequest,
  CreateProjectRunRequest,
  CreateProposalRequest,
  DecomposeResponse,
  DetailedSystemDiagnosticsDto,
  GenerateBlueprintResponse,
  BlueprintGenerationJob,
  GitHubRepositorySelectionCodeResponse,
  GitHubRepositorySelectionListResponse,
  GraphDescriptor,
  HeartbeatStatusDto,
  HistoryDto,
  ListBlueprintsResponse,
  OutcomeSpec,
  PersistedRunEvent,
  PortForwardSessionDto,
  PagedRequestOptions,
  PagedResult,
  PlatformDefaultCopilotConnection,
  Project,
  ProjectCopilotConnection,
  RepoAppConnectionStatus,
  RequestChangesResponse,
  RepositoryOwner,
  ReroleRequest,
  RetriableReviewErrorBody,
  RetryRunResponse,
  ReviewRequest,
  ReviewResponse,
  RoleDto,
  RunDetail,
  RuntimeInfo,
  SandboxPolicy,
  SkillDetailDto,
  SkillDto,
  ServerInfo,
  StartOrchestrationMode,
  StartOrchestrationResponse,
  RunApprovalPolicy,
  SteerCoordinatorRequest,
  SteerCoordinatorResponse,
  CreateAssistantRunRequest,
  CreateAssistantRunResponse,
  ListAssistantRunsResponse,
  SendAssistantMessageRequest,
  SendAssistantMessageResponse,
  SubmitRunResponse,
  SuggestBlueprintResponse,
  SyncCommitRequest,
  SyncCommitResponseDto,
  SyncStatusDto,
  SystemDiagnosticsDto,
  TeamDto,
  TeamMemberDto,
  TeamTemplateDto,
  UpdateProjectProviderSettingsRequest,
  WorkflowRunDto,
  WorkflowStagesResponse,
  WorkPlanResponse,
  WorkspaceFileContent,
  WorkspaceFileDiff,
  WorkspaceFileEntry,
  WorkspaceFileNode,
  WorkspaceNode,
  WorkspaceRefsResponse,
  UnattendedReadiness,
  AutomationActivationStatus,
} from './types';

/** Adds the confirmed provider execution key header when an operation has one. */
const providerHeaders = (providerKey?: string): Record<string, string> | undefined =>
  providerKey ? { 'If-Model-Provider-Key': providerKey } : undefined;

/** A skill file paired with the folder-relative path it should keep on the server (folder drag-and-drop). */
export interface SkillUploadItem {
  file: File;
  relativePath?: string;
}

/** Narrows parsed JSON to a non-null object before reading contract fields. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Validates string-array fields in skill and blueprint API responses. */
function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

/** Accepts the nullable optional string fields used by skill wire contracts. */
function isOptionalString(value: unknown): value is string | null | undefined {
  return value === undefined || value === null || typeof value === 'string';
}

/** Narrows the API's skill availability field to a supported status. */
function isSkillStatus(value: unknown): value is 'active' | 'missing' | 'malformed' {
  return value === 'active' || value === 'missing' || value === 'malformed';
}

/** Validates the complete list-row shape before trusting a skill response. */
function isSkillDto(value: unknown): value is SkillDto {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.name === 'string'
    && typeof value.description === 'string'
    && isSkillProvenance(value.provenance)
    && isOptionalString(value.source_repository)
    && isOptionalString(value.source_location)
    && isOptionalString(value.marketplace_name)
    && isSkillStatus(value.status)
    && typeof value.content_hash === 'string'
    && typeof value.resource_count === 'number'
    && isStringArray(value.assigned_agents)
    && typeof value.created_at === 'string'
    && typeof value.updated_at === 'string';
}

/** Validates full skill instructions, resources, provenance, and metadata. */
function isSkillDetailDto(value: unknown): value is SkillDetailDto {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.name === 'string'
    && typeof value.description === 'string'
    && typeof value.instructions === 'string'
    && Array.isArray(value.resources)
    && value.resources.every((resource) => isRecord(resource)
      && typeof resource.relative_path === 'string'
      && typeof resource.content === 'string')
    && isSkillProvenance(value.provenance)
    && isOptionalString(value.source_repository)
    && isOptionalString(value.source_location)
    && isOptionalString(value.marketplace_name)
    && isSkillStatus(value.status)
    && typeof value.content_hash === 'string'
    && typeof value.created_at === 'string'
    && typeof value.updated_at === 'string';
}

/** Validates a blueprint-default preview before the UI offers its digest for apply. */
export function isBlueprintSkillDefaultsPreviewResponse(value: unknown): value is BlueprintSkillDefaultsPreviewResponse {
  return isRecord(value)
    && typeof value.blueprint_id === 'string'
    && typeof value.blueprint_version === 'string'
    && typeof value.digest === 'string'
    && typeof value.can_apply === 'boolean'
    && isStringArray(value.errors)
    && Array.isArray(value.assignments)
    && value.assignments.every((assignment) =>
      isRecord(assignment)
      && typeof assignment.role_id === 'string'
      && typeof assignment.agent_name === 'string'
      && typeof assignment.skill_name === 'string'
      && (assignment.action === 'create'
        || assignment.action === 'reactivate'
        || assignment.action === 'assign'
        || assignment.action === 'blocked'));
}

/** Validates the apply result, including stale/invalid outcomes and nullable preview. */
export function isApplyBlueprintSkillDefaultsResponse(value: unknown): value is ApplyBlueprintSkillDefaultsResponse {
  return isRecord(value)
    && (value.outcome === 'applied' || value.outcome === 'stale' || value.outcome === 'invalid')
    && isStringArray(value.errors)
    && Object.hasOwn(value, 'preview')
    && (value.preview === null || isBlueprintSkillDefaultsPreviewResponse(value.preview));
}

/** Returns a validated apply result or throws when the server response violates its contract. */
export function parseApplyBlueprintSkillDefaultsResponse(payload: unknown): ApplyBlueprintSkillDefaultsResponse {
  if (!isApplyBlueprintSkillDefaultsResponse(payload)) {
    throw new TypeError('Invalid apply blueprint skill defaults response.');
  }
  return payload;
}

/** Parses a skill list without accepting partial or malformed entries. */
function parseSkillList(payload: unknown): SkillDto[] {
  if (!Array.isArray(payload)) throw new TypeError('Invalid skill list response.');

  const skills: SkillDto[] = [];
  for (const skill of payload) {
    if (!isSkillDto(skill)) throw new TypeError('Invalid skill list response.');
    skills.push(skill);
  }
  return skills;
}

/** Parses a full skill response or throws a TypeError on a contract mismatch. */
function parseSkillDetail(payload: unknown): SkillDetailDto {
  if (!isSkillDetailDto(payload)) throw new TypeError('Invalid skill detail response.');
  return payload;
}

/** Validates the blueprint fields needed by the v1 browser. */
function isBlueprint(value: unknown): value is Blueprint {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.name === 'string'
    && typeof value.description === 'string'
    && isStringArray(value.roster)
    && typeof value.workflow === 'string'
    && typeof value.review_policy === 'string'
    && typeof value.sandbox_profile === 'string';
}

/** Builds pagination query parameters without overriding server defaults when options are absent. */
// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `page`/`page_size`
// query params, 1-based page, page_size default 25 / max 100 (server-clamped). Builds the query
// string for a paged list request; returns '' when no paging options were supplied so callers that
// haven't opted into paging get the server's own default page/page_size.
function pagingQuery(options?: PagedRequestOptions): string {
  const query = new URLSearchParams();
  if (options?.page != null) query.set('page', String(options.page));
  if (options?.pageSize != null) query.set('page_size', String(options.pageSize));
  const qs = query.toString();
  return qs ? `?${qs}` : '';
}

/** Accepts either supported blueprint-list envelope and drops malformed entries. */
export function normalizeBlueprintList(payload: unknown): Blueprint[] {
  const list = Array.isArray(payload)
    ? payload
    : isRecord(payload) && Array.isArray(payload.blueprints)
      ? payload.blueprints
      : [];

  return list.filter(isBlueprint);
}

/** HTTP failure carrying its status, raw body, and best-effort parsed JSON payload. */
export class ApiError extends Error {
  readonly status: number;
  readonly body: string;
  readonly payload: unknown;

  /** Preserves response details and parses JSON when possible; malformed bodies yield a null payload. */
  constructor(status: number, body: string) {
    super(`API error ${status}: ${body}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    try {
      this.payload = JSON.parse(body) as unknown;
    } catch {
      this.payload = null;
    }
  }
}

/** Represents a review conflict that includes the current run status for retry UI. */
export class RetriableReviewError extends Error {
  readonly serverMessage: string;
  readonly runStatus: string;

  /** Stores the server's retry message and the run state that caused the conflict. */
  constructor(serverMessage: string, runStatus: string) {
    super(serverMessage);
    this.name = 'RetriableReviewError';
    this.serverMessage = serverMessage;
    this.runStatus = runStatus;
  }
}

/** Opaque durable job links required to resume workflow generation after polling stops. */
export interface WorkflowGenerationHandle {
  jobId: string;
  statusUrl: string;
  resultUrl: string;
  aiExecutionContext?: AiExecutionContext | null;
}

/** Completed workflow draft or a still-running durable generation job. */
export type WorkflowGenerationOutcome =
  | {
      status: 'completed';
      yaml: string;
      workflowId: string;
      wasCorrected: boolean;
      ai_execution_context?: AiExecutionContext | null;
    }
  | {
      status: 'pending';
      job: WorkflowGenerationHandle;
    };

/** Raw job status shape returned by the workflow-generation endpoints. */
interface WorkflowGenerationJob {
  job_id: string;
  status: string;
  status_url: string;
  result_url: string;
  failure?: { code: string; message: string; retryable: boolean } | null;
  ai_execution_context?: AiExecutionContext | null;
}

/**
 * REST client for the legacy project/run API used by the existing application.
 * Methods encode path parameters and share JSON, cookie, bearer-token, session
 * recovery, and ApiError handling through request(); special endpoints document
 * their own response or retry behavior next to the method.
 */
export class AgentweaverApiClient {
  private readonly baseUrl: string;
  private readonly sessionTokenProvider: () => string | null;
  private readonly supportsSessionRecovery: boolean;
  private sessionRecovery: Promise<boolean> | null = null;

  /** Configures the API origin and token source; only the default source enables peer recovery. */
  constructor(baseUrl: string, sessionTokenProvider: (() => string | null) | string = getSessionToken) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.supportsSessionRecovery = sessionTokenProvider === getSessionToken;
    this.sessionTokenProvider = typeof sessionTokenProvider === 'function'
      ? sessionTokenProvider
      : () => sessionTokenProvider || null;
  }

  private authHeaders(): Record<string, string> {
    const token = this.sessionTokenProvider();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }

  /** Reads the run detail used by run pages and status surfaces. */
  getRun(runId: string): Promise<RunDetail> {
    return this.request<RunDetail>('GET', `/runs/${encodeURIComponent(runId)}`);
  }

  /** Reads the effective permission decision and supporting evidence for a run. */
  getRunEffectivePermissions(runId: string): Promise<import('./types').EffectivePermissionInspection> {
    return this.request<import('./types').EffectivePermissionInspection>(
      'GET',
      `/runs/${encodeURIComponent(runId)}/effective-permissions`,
    );
  }

  /** Reads the execution identity projection bound to a run. */
  getRunExecutionIdentity(runId: string): Promise<import('./types').ExecutionIdentityProjection> {
    return this.request<import('./types').ExecutionIdentityProjection>(
      'GET',
      `/runs/${encodeURIComponent(runId)}/execution-identity`,
    );
  }

  /** Reads the diagnostic explanation for a run's terminal state. */
  getRunTerminalDiagnostic(runId: string): Promise<import('./types').RunTerminalDiagnostic> {
    return this.request<import('./types').RunTerminalDiagnostic>(
      'GET',
      `/runs/${encodeURIComponent(runId)}/terminal-diagnostic`,
    );
  }

  /** Requests a retry of a run, optionally pinning the confirmed AI execution key. */
  retryRun(runId: string, providerKey?: string): Promise<RetryRunResponse> {
    return this.request<RetryRunResponse>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/retry`,
      {},
      undefined,
      providerHeaders(providerKey),
    );
  }

  // Persisted run events (FR-022). Seeds the execution timeline for terminal/parked
  // runs whose live SSE stream is closed (e.g. a finished coordinator child). The
  // backend persists and replays the events here; 404 until the log exists.
  getRunEvents(runId: string, options?: { type?: string; after?: number; limit?: number }): Promise<PersistedRunEvent[]> {
    const query = new URLSearchParams();
    if (options?.type != null) query.set('type', options.type);
    if (options?.after != null) query.set('after', String(options.after));
    if (options?.limit != null) query.set('limit', String(options.limit));
    const suffix = query.size > 0 ? `?${query.toString()}` : '';
    return this.request<PersistedRunEvent[]>('GET', `/runs/${encodeURIComponent(runId)}/events${suffix}`);
  }

  /** Reads pending tool and human approval requests for a run. */
  getPendingApprovals(runId: string): Promise<import('./types').PendingApprovalsResponse> {
    return this.request<import('./types').PendingApprovalsResponse>(
      'GET',
      `/runs/${encodeURIComponent(runId)}/pending-approvals`,
    );
  }

  /** Reads per-agent token totals reported for a run. */
  getRunTokenBreakdown(runId: string): Promise<import('./types').RunAgentTokenBreakdownDto> {
    return this.request<import('./types').RunAgentTokenBreakdownDto>('GET', `/runs/${encodeURIComponent(runId)}/token-breakdown`);
  }

  /** Reads one paged trace segment; pass the returned cursor to continue chronologically. */
  getRunTraces(runId: string, options?: { cursor?: string; pageSize?: number }): Promise<import('./types').RunTraceDto> {
    const parameters = new URLSearchParams();
    if (options?.cursor) parameters.set('cursor', options.cursor);
    if (options?.pageSize) parameters.set('pageSize', String(options.pageSize));
    const query = parameters.size ? `?${parameters}` : '';
    return this.request<import('./types').RunTraceDto>('GET', `/metrics/runs/${encodeURIComponent(runId)}/traces${query}`);
  }

  /** Reads the sandbox policy associated with a repository path. */
  getSandboxPolicy(repositoryPath: string): Promise<SandboxPolicy> {
    const encoded = encodeURIComponent(repositoryPath);
    return this.request<SandboxPolicy>('GET', `/sandbox-policy?repository_path=${encoded}`);
  }

  /** Lists files changed by a run, optionally filtered by the API's file group. */
  getRunFiles(runId: string, filter?: string): Promise<WorkspaceFileEntry[]> {
    const query = filter ? `?filter=${encodeURIComponent(filter)}` : '';
    return this.request<WorkspaceFileEntry[]>('GET', `/runs/${encodeURIComponent(runId)}/files${query}`);
  }

  /** Reads the diff for one run-worktree path, encoding path segments independently. */
  getRunFileDiff(runId: string, path: string): Promise<WorkspaceFileDiff> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileDiff>('GET', `/runs/${encodeURIComponent(runId)}/files/${encoded}`);
  }

  // Collective assembly artifacts for a coordinator run. The coordinator owns no worktree; these
  // diff the integration branch (agentweaver/integration/{id}) vs the originating branch so the
  // standard Changes/Files rail can review the assembled output. Returns [] before assembly runs.
  getAssemblyFiles(runId: string, filter?: string): Promise<WorkspaceFileEntry[]> {
    const query = filter ? `?filter=${encodeURIComponent(filter)}` : '';
    return this.request<WorkspaceFileEntry[]>('GET', `/runs/${encodeURIComponent(runId)}/assembly/files${query}`);
  }

  /** Reads one assembled integration-branch file diff for a coordinator run. */
  getAssemblyFileDiff(runId: string, path: string): Promise<WorkspaceFileDiff> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileDiff>('GET', `/runs/${encodeURIComponent(runId)}/assembly/files/${encoded}`);
  }

  /** Lists the coordinator's assembled workspace tree from the integration branch. */
  getAssemblyWorkspace(runId: string): Promise<WorkspaceNode[]> {
    return this.request<WorkspaceNode[]>('GET', `/runs/${encodeURIComponent(runId)}/assembly/workspace`);
  }

  // Per-file CONTENT of the collective integration branch tip, for the review modal's Preview/source
  // tab. The coordinator owns no worktree, so the standard worktree-backed content endpoint 409s;
  // this reads the blob from agentweaver/integration/{id} instead.
  getAssemblyFileContent(runId: string, path: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileContent>('GET', `/runs/${encodeURIComponent(runId)}/assembly/content/${encoded}`);
  }

  /** Reads source content for one run-worktree file. */
  getRunFileContent(runId: string, path: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileContent>('GET', `/runs/${encodeURIComponent(runId)}/files/${encoded}/content`);
  }

  /** Lists the saved output revisions for a run. */
  getOutputRevisionHistory(runId: string): Promise<import('./types').OutputRevision[]> {
    return this.request<import('./types').OutputRevision[]>('GET', `/runs/${encodeURIComponent(runId)}/output-revisions`);
  }

  /** Reads metadata for one saved output revision. */
  getOutputRevision(runId: string, revisionId: string): Promise<import('./types').OutputRevision> {
    return this.request<import('./types').OutputRevision>(
      'GET', `/runs/${encodeURIComponent(runId)}/output-revisions/${encodeURIComponent(revisionId)}`);
  }

  /** Reads one file from a saved output revision. */
  getOutputRevisionFile(runId: string, revisionId: string, path: string): Promise<import('./types').OutputRevisionFile> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<import('./types').OutputRevisionFile>(
      'GET', `/runs/${encodeURIComponent(runId)}/output-revisions/${encodeURIComponent(revisionId)}/files/${encoded}`);
  }

  /** Compares two saved output revisions and returns the server's comparison result. */
  compareOutputRevisions(runId: string, before: string, after: string): Promise<import('./types').OutputRevisionComparison> {
    return this.request<import('./types').OutputRevisionComparison>(
      'GET', `/runs/${encodeURIComponent(runId)}/output-revisions/${encodeURIComponent(before)}/compare/${encodeURIComponent(after)}`);
  }

  /** Lists the files and directories in a run's workspace. */
  getRunWorkspace(runId: string): Promise<WorkspaceNode[]> {
    return this.request<WorkspaceNode[]>('GET', `/runs/${encodeURIComponent(runId)}/workspace`);
  }

  /** Requests the server to commit and merge the reviewed run output. */
  commitRun(runId: string): Promise<CommitResponse> {
    return this.request<CommitResponse>('POST', `/runs/${encodeURIComponent(runId)}/commit`, {});
  }

  /** Requests another implementation pass with owner feedback and optional provider confirmation. */
  requestChanges(runId: string, comment: string, providerKey?: string): Promise<RequestChangesResponse> {
    return this.request<RequestChangesResponse>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/request-changes`,
      { comment },
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Saves the sandbox policy and returns the server's normalized policy. */
  updateSandboxPolicy(policy: SandboxPolicy): Promise<SandboxPolicy> {
    return this.request<SandboxPolicy>('PUT', '/sandbox-policy', policy);
  }

  // Projects
  // Paginated per the pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`):
  // the server always returns the `{ items, page, page_size, total_count, total_pages }` envelope.
  /** Lists the current user's projects using the server's paged response envelope. */
  listProjects(options?: PagedRequestOptions): Promise<PagedResult<Project>> {
    return this.request<PagedResult<Project>>('GET', `/projects${pagingQuery(options)}`, undefined, options?.signal);
  }

  /** Reads one project by its identifier. */
  getProject(projectId: string): Promise<Project> {
    return this.request<Project>('GET', `/projects/${encodeURIComponent(projectId)}`);
  }

  /** Creates a project from its submitted name and configuration. */
  createProject(req: CreateProjectRequest): Promise<Project> {
    return this.request<Project>('POST', '/projects', req);
  }

  /** Lists temporary GitHub repository selections available to the current user. */
  listGitHubRepositorySelections(): Promise<GitHubRepositorySelectionListResponse> {
    return this.request<GitHubRepositorySelectionListResponse>('GET', '/github/repository-selections');
  }

  /** Requests an opaque selection code for a repository name; the code is later exchanged by the API. */
  issueGitHubRepositorySelection(fullName: string): Promise<GitHubRepositorySelectionCodeResponse> {
    return this.request<GitHubRepositorySelectionCodeResponse>('POST', '/github/repository-selections', {
      full_name: fullName,
    });
  }

  /** Reads available blueprints and normalizes the supported server response shapes. */
  listBlueprints(): Promise<Blueprint[]> {
    return this.request<Blueprint[] | ListBlueprintsResponse>('GET', '/blueprints')
      .then(normalizeBlueprintList);
  }

  /** Starts durable blueprint generation and reuses the supplied idempotency key for that job. */
  generateBlueprint(
    description: string,
    targetRepository?: string | null,
    providerKey?: string,
    idempotencyKey = crypto.randomUUID(),
  ): Promise<BlueprintGenerationJob> {
    return this.request<BlueprintGenerationJob>('POST', '/blueprints/generate', {
      description,
      target_repository: targetRepository || undefined,
    }, undefined, {
      ...providerHeaders(providerKey),
      'Idempotency-Key': idempotencyKey,
    });
  }

  /** Reads the current status of a blueprint-generation job. */
  getBlueprintGenerationJob(jobId: string): Promise<BlueprintGenerationJob> {
    return this.request<BlueprintGenerationJob>(
      'GET',
      `/blueprints/generation-jobs/${encodeURIComponent(jobId)}`,
    );
  }

  /** Reads the completed result for a blueprint-generation job. */
  getBlueprintGenerationResult(jobId: string): Promise<GenerateBlueprintResponse> {
    return this.request<GenerateBlueprintResponse>(
      'GET',
      `/blueprints/generation-jobs/${encodeURIComponent(jobId)}/result`,
    );
  }

  /** Requests cancellation and returns the job's resulting status. */
  cancelBlueprintGeneration(jobId: string): Promise<BlueprintGenerationJob> {
    return this.request<BlueprintGenerationJob>(
      'POST',
      `/blueprints/generation-jobs/${encodeURIComponent(jobId)}/cancel`,
    );
  }

  /** Requests a retry of a failed or retryable blueprint-generation job. */
  retryBlueprintGeneration(jobId: string): Promise<BlueprintGenerationJob> {
    return this.request<BlueprintGenerationJob>(
      'POST',
      `/blueprints/generation-jobs/${encodeURIComponent(jobId)}/retry`,
    );
  }

  /** Requests a blueprint suggestion for the supplied repository reference. */
  suggestBlueprint(repository: string): Promise<SuggestBlueprintResponse> {
    return this.request<SuggestBlueprintResponse>('POST', '/blueprints/suggest', { repository });
  }

  /** Changes the display name of a project. */
  renameProject(projectId: string, name: string): Promise<void> {
    return this.request<void>('PATCH', `/projects/${encodeURIComponent(projectId)}`, { name });
  }

  /** Replaces provider settings for one project. */
  updateProjectProviderSettings(projectId: string, req: UpdateProjectProviderSettingsRequest): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/provider-settings`, req);
  }

  /** Saves preview settings and returns the server's normalized settings state. */
  updateProjectPreviewSettings(
    projectId: string,
    req: import('./types').UpdateProjectPreviewSettingsRequest,
  ): Promise<import('./types').ProjectPreviewSettingsResponse> {
    return this.request<import('./types').ProjectPreviewSettingsResponse>(
      'PUT',
      `/projects/${encodeURIComponent(projectId)}/preview-settings`,
      req,
    );
  }

  /** Reads the prerequisites that determine whether unattended execution is ready. */
  getUnattendedReadiness(projectId: string): Promise<UnattendedReadiness> {
    return this.request<UnattendedReadiness>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/github/unattended-readiness`,
    );
  }

  /** Reads the project's automation activation state. */
  getAutomationStatus(projectId: string): Promise<AutomationActivationStatus> {
    return this.request<AutomationActivationStatus>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/automation/status`,
    );
  }

  /** Requests automation activation and returns the owner's resulting status. */
  activateAutomation(projectId: string): Promise<AutomationActivationStatus> {
    return this.request<AutomationActivationStatus>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/automation/activate`,
      {},
    );
  }

  /** Requests automation deactivation and returns the owner's resulting status. */
  deactivateAutomation(projectId: string): Promise<AutomationActivationStatus> {
    return this.request<AutomationActivationStatus>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/automation/deactivate`,
      {},
    );
  }

  /** Begins project-scoped Copilot authorization and returns the Broker redirect details. */
  beginProjectCopilotAuthorization(projectId: string, returnRouteKey?: string): Promise<{
    authorization_url: string;
    transaction_id: string;
    expires_at: string;
  }> {
    return this.request('POST', `/projects/${encodeURIComponent(projectId)}/github/copilot/authorizations`, {
      ...(returnRouteKey ? { return_route_key: returnRouteKey } : {}),
    });
  }

  /** Starts a project GitHub App installation authorization. */
  beginProjectRepoAppInstallation(projectId: string): Promise<{
    installation_url: string;
    transaction_id: string;
    expires_at: string;
  }> {
    return this.request(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/github/repo-app-installation/authorizations`,
      {},
    );
  }

  /** Reads the project's Copilot authorization status. */
  getProjectCopilotConnection(projectId: string): Promise<ProjectCopilotConnection> {
    return this.request<ProjectCopilotConnection>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/github/copilot/connection`,
    );
  }

  /** Deletes a project using the API's explicit confirmation query. */
  deleteProject(projectId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}?confirm=true`);
  }

  /** Starts a run for the project and returns the submitted run status. */
  startProjectRun(projectId: string, req: CreateProjectRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', `/projects/${encodeURIComponent(projectId)}/runs`, req);
  }

  // Paginated per the pagination contract — server always returns the paged envelope.
  /** Lists project runs in the server-paged response format. */
  listProjectRuns(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<WorkflowRunDto>> {
    return this.request<PagedResult<WorkflowRunDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/runs${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Lists filtered runs, retaining the legacy limit alias when pageSize is not supplied. */
  getProjectRuns(projectId: string, options?: {
    agentName?: string;
    terminalOnly?: boolean;
    includeChildren?: boolean;
    limit?: number;
    page?: number;
    pageSize?: number;
    signal?: AbortSignal;
  }): Promise<PagedResult<WorkflowRunDto>> {
    const query = new URLSearchParams();
    if (options?.agentName) query.set('agent', options.agentName);
    if (options?.terminalOnly) query.set('terminal_only', 'true');
    if (options?.includeChildren) query.set('include_children', 'true');
    // `limit` (legacy alias for `page_size`, deprecated per the pagination contract) is kept for
    // existing callers; `page`/`pageSize` take precedence when both are supplied.
    if (options?.pageSize != null) query.set('page_size', String(options.pageSize));
    else if (options?.limit != null) query.set('limit', String(options.limit));
    if (options?.page != null) query.set('page', String(options.page));
    const queryString = query.toString();
    const suffix = queryString ? `?${queryString}` : '';
    return this.request<PagedResult<WorkflowRunDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/runs${suffix}`,
      undefined,
      options?.signal,
    );
  }

  /** Deletes a run record and its API-managed history. */
  deleteRun(runId: string): Promise<void> {
    return this.request<void>('DELETE', `/runs/${encodeURIComponent(runId)}`);
  }

  /** Requests cancellation and reports whether the run was already terminal. */
  cancelRun(runId: string): Promise<{ run_id: string; status: string; cancelled: boolean; already_terminal: boolean }> {
    return this.request('POST', `/runs/${encodeURIComponent(runId)}/cancel`, {});
  }

  /** Archives a run through the server's lifecycle endpoint. */
  archiveRun(runId: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/archive`, {});
  }

  /** Reads API server version and capability information. */
  getServerInfo(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/server/info');
  }

  /** Reads the current browser session without using a cached response. */
  getAuthSession(): Promise<AuthSessionResponse> {
    return this.request<AuthSessionResponse>('GET', '/auth/session', undefined, undefined, undefined, 'no-store');
  }

  /** Prepares an operation-scoped provider confirmation for a project or run action. */
  prepareAiExecutionContext(
    operation: string,
    projectId?: string,
    runId?: string,
  ): Promise<AiExecutionContext> {
    return this.request<AiExecutionContext>('POST', '/ai/execution-context', {
      operation,
      project_id: projectId,
      run_id: runId,
    });
  }

  /** Reads the public authentication configuration used to initialize sign-in. */
  getAuthConfig(): Promise<AuthConfigResponse> {
    return this.request<AuthConfigResponse>('GET', '/auth/config');
  }

  // Deployment-wide "bring your own key" inference provider configuration.
  // Multiple providers can be configured and their keys kept at once; exactly one may be
  // marked active (active_provider_id) — a null active id means GitHub Copilot mode.
  /** Lists deployment-level BYOK providers without caching the admin response. */
  listByokProviders(): Promise<ByokProviderListResponse> {
    return this.request<ByokProviderListResponse>(
      'GET',
      '/admin/byok-providers',
      undefined,
      undefined,
      undefined,
      'no-store',
    );
  }

  /** Adds a deployment-level inference provider configuration. */
  addByokProvider(req: ByokProviderRequest): Promise<ByokProviderConfig> {
    return this.request<ByokProviderConfig>('POST', '/admin/byok-providers', req);
  }

  /** Replaces one deployment-level inference provider configuration. */
  updateByokProvider(id: string, req: ByokProviderRequest): Promise<ByokProviderConfig> {
    return this.request<ByokProviderConfig>('PUT', `/admin/byok-providers/${encodeURIComponent(id)}`, req);
  }

  /** Removes a deployment-level inference provider configuration. */
  removeByokProvider(id: string): Promise<void> {
    return this.request<void>('DELETE', `/admin/byok-providers/${encodeURIComponent(id)}`);
  }

  /** Selects one deployment-level BYOK provider as active. */
  activateByokProvider(id: string): Promise<void> {
    return this.request<void>('POST', `/admin/byok-providers/${encodeURIComponent(id)}/activate`);
  }

  /** Clears the active deployment-level BYOK provider selection. */
  deactivateByokProviders(): Promise<void> {
    return this.request<void>('POST', '/admin/byok-providers/deactivate');
  }

  /** Reads the signed-in user's AI provider preference and access state. */
  getUserAiAccess(): Promise<UserAiAccessStatus> {
    return this.request<UserAiAccessStatus>('GET', '/account/ai-access');
  }

  /** Saves the user's BYOK configuration without exposing an active-provider flag. */
  setUserByokProvider(req: ByokProviderRequest): Promise<Omit<ByokProviderConfig, 'is_active'>> {
    return this.request<Omit<ByokProviderConfig, 'is_active'>>('PUT', '/account/ai-access/byok', req);
  }

  /** Removes the signed-in user's BYOK configuration. */
  removeUserByokProvider(): Promise<void> {
    return this.request<void>('DELETE', '/account/ai-access/byok');
  }

  /** Selects whether the user prefers GitHub Copilot or their BYOK provider. */
  setUserAiPreference(preference: 'github-copilot' | 'byok'): Promise<void> {
    return this.request<void>('POST', `/account/ai-access/preference/${preference}`);
  }

  /** Starts authorization for the signed-in user's Copilot access. */
  beginUserCopilotAuthorization(): Promise<{ authorization_url: string }> {
    return this.request<{ authorization_url: string }>('POST', '/account/ai-access/copilot/begin');
  }

  /** Disconnects the signed-in user's Copilot connection. */
  disconnectUserCopilot(): Promise<void> {
    return this.request<void>('POST', '/account/ai-access/copilot/disconnect');
  }

  /** Begins authorization of the deployment-wide default Copilot connection. */
  beginPlatformDefaultCopilotAuthorization(): Promise<{
    authorization_url: string;
    transaction_id: string;
    expires_at: string;
  }> {
    return this.request('POST', '/admin/platform-default-copilot/begin', {});
  }

  /** Reads deployment-wide default Copilot status without caching the response. */
  getPlatformDefaultCopilotConnection(): Promise<PlatformDefaultCopilotConnection> {
    return this.request<PlatformDefaultCopilotConnection>(
      'GET',
      '/admin/platform-default-copilot/status',
      undefined,
      undefined,
      undefined,
      'no-store',
    );
  }

  /** Removes the deployment-wide default Copilot connection. */
  disconnectPlatformDefaultCopilotConnection(): Promise<void> {
    return this.request<void>('POST', '/admin/platform-default-copilot/disconnect', {});
  }

  /** Starts user-level GitHub App authorization and returns its short-lived transaction details. */
  beginRepoAppAuthorization(returnRouteKey?: string): Promise<{ authorization_url: string; transaction_id: string; expires_at: string }> {
    return this.request('POST', '/auth/github/repo-app/authorizations', {
      ...(returnRouteKey ? { return_route_key: returnRouteKey } : {}),
    });
  }

  /** Reads the current user's GitHub App connection status. */
  getRepoAppConnectionStatus(): Promise<RepoAppConnectionStatus> {
    return this.request<RepoAppConnectionStatus>('GET', '/auth/github/repo-app/authorization/status');
  }

  /** Ends the server-side session associated with the current browser. */
  signOutSession(): Promise<void> {
    return this.request<void>('POST', '/auth/session/sign-out', {});
  }

  // Post-creation GitHub connection for a currently-unconnected (blank-origin) project.
  /** Lists GitHub owners available for connecting a newly created project. */
  listProjectRepositoryOwners(projectId: string): Promise<RepositoryOwner[]> {
    return this.request<RepositoryOwner[]>('GET', `/projects/${encodeURIComponent(projectId)}/github/repository-owners`);
  }

  /** Creates and connects a GitHub repository for a project. */
  createProjectRepository(projectId: string, req: CreateProjectRepositoryRequest): Promise<ConnectedRepository> {
    return this.request<ConnectedRepository>('POST', `/projects/${encodeURIComponent(projectId)}/github/repository`, req);
  }

  /** Connects an existing GitHub repository to a project. */
  connectProjectRepository(projectId: string, req: ConnectProjectRepositoryRequest): Promise<ConnectedRepository> {
    return this.request<ConnectedRepository>('POST', `/projects/${encodeURIComponent(projectId)}/github/repository/connection`, req);
  }

  // Catalog
  /** Reads the role catalog used when building a project team. */
  getRoles(): Promise<RoleDto[]> {
    return this.request<RoleDto[]>('GET', '/catalog/roles');
  }

  // Casting
  /** Reads available team templates. */
  getTemplates(): Promise<TeamTemplateDto[]> {
    return this.request<TeamTemplateDto[]>('GET', '/casting/templates');
  }

  /** Lists casting universes configured for a project. */
  getUniverses(projectId: string): Promise<{ universes: string[] }> {
    return this.request<{ universes: string[] }>('GET', `/projects/${encodeURIComponent(projectId)}/casting/universes`);
  }

  /** Requests a team proposal, optionally passing the confirmed provider execution key. */
  createProposal(projectId: string, req: CreateProposalRequest, providerKey?: string): Promise<CastProposalDto> {
    return this.request<CastProposalDto>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/casting/proposals`,
      req,
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Reads one project casting proposal. */
  getProposal(projectId: string, proposalId: string): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('GET', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  /** Updates an unconfirmed proposal with owner edits. */
  amendProposal(projectId: string, proposalId: string, req: AmendProposalRequest): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`, req);
  }

  /** Confirms a proposal and submits its explicit confirmation payload. */
  confirmProposal(projectId: string, proposalId: string, req: ConfirmProposalRequest): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}/confirm`, req);
  }

  /** Rejects a casting proposal by removing it through the API. */
  rejectProposal(projectId: string, proposalId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  // Team
  /** Reads the current project roster. */
  getTeam(projectId: string): Promise<TeamDto> {
    return this.request<TeamDto>('GET', `/projects/${encodeURIComponent(projectId)}/team`);
  }

  /** Reads a team member's charter text and metadata. */
  getMemberCharter(projectId: string, memberName: string): Promise<CharterDto> {
    return this.request<CharterDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`);
  }

  /** Replaces a team member's charter content. */
  updateMemberCharter(projectId: string, memberName: string, content: string): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`, { content });
  }

  /** Adds a member to the project team. */
  addMember(projectId: string, req: AddMemberRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('POST', `/projects/${encodeURIComponent(projectId)}/team/members`, req);
  }

  /** Removes a member from the project team. */
  removeMember(projectId: string, memberName: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`);
  }

  /** Changes a team member's role and returns the updated member. */
  reroleMember(projectId: string, memberName: string, req: ReroleRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`, req);
  }

  /** Reads the activity history for a team member. */
  getMemberHistory(projectId: string, memberName: string): Promise<HistoryDto> {
    return this.request<HistoryDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/history`);
  }

  // Paginated per the pagination contract — existing `status`/`type`/`agent` filters (if any)
  // are applied server-side before paging.
  /** Lists project decisions, with optional server-side pagination. */
  getDecisions(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').DecisionDto>> {
    return this.request<PagedResult<import('./types').DecisionDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/decisions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Lists decisions awaiting triage in the project inbox. */
  getDecisionsInbox(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').DecisionInboxEntryDto>> {
    return this.request<PagedResult<import('./types').DecisionInboxEntryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/decisions/inbox${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Lists addressed agent messages, optionally limited to one run. */
  getAddressedMessages(projectId: string, runId?: string): Promise<import('./types').AddressedMessageDto[]> {
    const query = runId ? `?run_id=${encodeURIComponent(runId)}` : '';
    return this.request<import('./types').AddressedMessageDto[]>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/agent-messages${query}`,
    );
  }

  /** Lists revision history for one decision. */
  getDecisionRevisions(
    projectId: string,
    decisionId: string,
    options?: PagedRequestOptions,
  ): Promise<PagedResult<import('./types').DecisionRevisionDto>> {
    return this.request<PagedResult<import('./types').DecisionRevisionDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/decisions/${encodeURIComponent(decisionId)}/revisions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Restores a prior decision revision only if its current revision matches expectedRevision. */
  restoreDecision(
    projectId: string,
    decisionId: string,
    expectedRevision: number,
    revision: number,
  ): Promise<import('./types').DecisionDto> {
    return this.request(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/decisions/${encodeURIComponent(decisionId)}/restore`,
      { expected_revision: expectedRevision, revision },
    );
  }

  /** Merges one inbox entry into the durable decision store. */
  mergeDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/merge`, {});
  }

  /** Promotes one inbox entry for use as project knowledge. */
  promoteDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/promote`, {});
  }

  /** Rejects one decision inbox entry. */
  rejectDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/reject`, {});
  }

  /** Lists a single agent's memory records in pages. */
  getAgentMemory(projectId: string, agentName: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').AgentMemoryDto>> {
    return this.request<PagedResult<import('./types').AgentMemoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Searches project memory with optional query, type, tag, and status filters. */
  getProjectMemory(
    projectId: string,
    options?: PagedRequestOptions & { query?: string; type?: string; tags?: string; status?: string },
  ): Promise<PagedResult<import('./types').AgentMemoryDto>> {
    const query = new URLSearchParams();
    if (options?.page != null) query.set('page', String(options.page));
    if (options?.pageSize != null) query.set('page_size', String(options.pageSize));
    if (options?.query) query.set('q', options.query);
    if (options?.type) query.set('type', options.type);
    if (options?.tags) query.set('tags', options.tags);
    if (options?.status) query.set('status', options.status);
    const qs = query.toString();
    return this.request<PagedResult<import('./types').AgentMemoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/memory${qs ? `?${qs}` : ''}`,
      undefined,
      options?.signal,
    );
  }

  /** Lists project session history in pages. */
  getProjectSessions(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').SessionHistoryDto>> {
    return this.request<PagedResult<import('./types').SessionHistoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/sessions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Creates a memory record for one project agent. */
  createAgentMemory(
    projectId: string,
    agentName: string,
    body: { type: string; content: string; importance?: string; tags?: string },
  ): Promise<import('./types').AgentMemoryDto> {
    return this.request<import('./types').AgentMemoryDto>('POST', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory`, body);
  }

  /** Updates one memory record against its expected revision to avoid overwriting newer edits. */
  updateAgentMemory(
    projectId: string,
    agentName: string,
    memoryId: string,
    body: {
      expected_revision: number;
      type?: string;
      content?: string;
      importance?: string;
      tags?: string;
      status?: 'active' | 'superseded' | 'archived';
      replaced_by_id?: number;
      reason?: string;
    },
  ): Promise<import('./types').AgentMemoryDto> {
    return this.request<import('./types').AgentMemoryDto>('PUT', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory/${encodeURIComponent(memoryId)}`, body);
  }

  /** Lists revision history for a specific agent memory record. */
  getAgentMemoryRevisions(
    projectId: string,
    agentName: string,
    memoryId: string,
    options?: PagedRequestOptions,
  ): Promise<PagedResult<import('./types').AgentMemoryRevisionDto>> {
    return this.request<PagedResult<import('./types').AgentMemoryRevisionDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory/${encodeURIComponent(memoryId)}/revisions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  /** Reads a pair of revisions for one memory record for client-side comparison. */
  compareAgentMemoryRevisions(
    projectId: string,
    agentName: string,
    memoryId: string,
    fromRevision: number,
    toRevision: number,
  ): Promise<{ from: import('./types').AgentMemoryRevisionDto; to: import('./types').AgentMemoryRevisionDto }> {
    const query = new URLSearchParams({
      from_revision: String(fromRevision),
      to_revision: String(toRevision),
    });
    return this.request('GET', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory/${encodeURIComponent(memoryId)}/compare?${query}`);
  }

  /** Restores a prior memory revision only when the current revision matches expectedRevision. */
  restoreAgentMemory(
    projectId: string,
    agentName: string,
    memoryId: string,
    expectedRevision: number,
    revision: number,
  ): Promise<import('./types').AgentMemoryDto> {
    return this.request(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory/${encodeURIComponent(memoryId)}/restore`,
      { expected_revision: expectedRevision, revision },
    );
  }

  // Skills (issues #51/#56) — per-project catalog + agent assignments.
  /** Lists and validates the project's skill catalog. */
  listSkills(projectId: string): Promise<import('./types').SkillDto[]> {
    return this.request<unknown>('GET', `/projects/${encodeURIComponent(projectId)}/skills`).then(parseSkillList);
  }

  /** Reads and validates one skill's instructions and resource content. */
  getSkill(projectId: string, skillId: string): Promise<import('./types').SkillDetailDto> {
    return this.request<unknown>('GET', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}`).then(parseSkillDetail);
  }

  /** Removes one skill from the project's catalog. */
  deleteSkill(projectId: string, skillId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}`);
  }

  /** Creates a project skill from owner-supplied instructions. */
  createSkill(projectId: string, body: import('./types').CreateSkillRequest): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills`, body);
  }

  /** Generates a skill draft from a description using the confirmed provider key when supplied. */
  generateSkill(projectId: string, description: string, providerKey?: string): Promise<import('./types').GeneratedSkillDraft> {
    return this.request<import('./types').GeneratedSkillDraft>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/skills/generate`,
      { description },
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Reconciles the project catalog with its connected repository skills. */
  syncSkills(projectId: string): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/sync`, {});
  }

  /** Discovers and validates repository skills without importing them. */
  previewSkillImport(projectId: string, repoUrl: string): Promise<import('./types').SkillImportPreviewResponse> {
    return this.request<import('./types').SkillImportPreviewResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/import/preview`, { repoUrl });
  }

  /** Imports selected discovered skills, or all candidates when locations are omitted. */
  importSkills(projectId: string, repoUrl: string, locations?: string[]): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/import`, { repoUrl, locations });
  }

  // Project-scoped list: built-in config marketplaces + this project's user-added URL sources.
  /** Lists built-in and project-added skill marketplace sources. */
  listSkillMarketplaces(projectId: string): Promise<import('./types').SkillMarketplaceDto[]> {
    return this.request<import('./types').SkillMarketplaceDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces`);
  }

  /** Adds a repository-backed marketplace source to the project. */
  addSkillMarketplaceSource(projectId: string, body: import('./types').AddSkillMarketplaceSourceRequest): Promise<import('./types').SkillMarketplaceDto> {
    return this.request<import('./types').SkillMarketplaceDto>('POST', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/sources`, body);
  }

  /** Removes one project-added marketplace source. */
  removeSkillMarketplaceSource(projectId: string, name: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/sources/${encodeURIComponent(name)}`);
  }

  /** Searches a marketplace page, optionally using the confirmed provider execution key. */
  browseSkillMarketplace(projectId: string, marketplace: string, query?: string, page?: number, pageSize?: number, providerKey?: string): Promise<import('./types').SkillMarketplaceBrowseResponse> {
    return this.request<import('./types').SkillMarketplaceBrowseResponse>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/${encodeURIComponent(marketplace)}/browse`,
      { query, page, pageSize },
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Imports selected marketplace candidate locations into the project catalog. */
  importMarketplaceSkills(projectId: string, marketplace: string, locations: string[]): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/${encodeURIComponent(marketplace)}/import`, { locations });
  }

  // Multipart upload of skill file(s)/folder/archive. Bypasses request<T> to send FormData.
  // Accepts plain File objects (single-file / file-picker uploads) or {file, relativePath}
  // pairs (folder drag-and-drop) so nested SKILL.md directories survive the round-trip.
  /** Uploads skill files as multipart form data, preserving optional folder-relative paths. */
  async uploadSkills(
    projectId: string,
    files: Array<File | SkillUploadItem>,
  ): Promise<import('./types').SkillAcquisitionResponse> {
    const form = new FormData();
    files.forEach((entry, index) => {
      const file = entry instanceof File ? entry : entry.file;
      const rel = entry instanceof File
        ? (file as File & { webkitRelativePath?: string }).webkitRelativePath || undefined
        : entry.relativePath;
      // Each file gets a UNIQUE form field name so the backend can pair it with its own
      // relative path (it reads a `path:{fieldName}` field). A shared field name would
      // collapse every file's path down to the first one.
      const field = `files${index}`;
      form.append(field, file, file.name);
      if (rel) form.append(`path:${field}`, rel);
    });
    const response = await fetch(`${this.baseUrl}/api/projects/${encodeURIComponent(projectId)}/skills/upload`, {
      method: 'POST',
      headers: this.authHeaders(),
      credentials: 'include',
      body: form,
    });
    const text = typeof response.text === 'function' ? await response.text() : '';
    if (!response.ok) throw this.createApiError(response.status, text);
    return text ? JSON.parse(text) as import('./types').SkillAcquisitionResponse : { results: [], marked_missing: [] };
  }

  /** Lists skill-to-agent assignments for the project. */
  listSkillAssignments(projectId: string): Promise<import('./types').SkillAssignmentDto[]> {
    return this.request<import('./types').SkillAssignmentDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/skills/assignments`);
  }

  /** Assigns one project skill to a team agent. */
  assignSkill(projectId: string, skillId: string, agentName: string): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}/assignments/${encodeURIComponent(agentName)}`, {});
  }

  /** Removes one skill-to-agent assignment. */
  unassignSkill(projectId: string, skillId: string, agentName: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}/assignments/${encodeURIComponent(agentName)}`);
  }

  /** Previews default skills and returns the digest required to apply that exact proposal. */
  previewBlueprintSkillDefaults(
    projectId: string,
    blueprintId: string,
  ): Promise<import('./types').BlueprintSkillDefaultsPreviewResponse> {
    return this.request<import('./types').BlueprintSkillDefaultsPreviewResponse>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/skill-defaults/preview`,
      { blueprint_id: blueprintId },
    );
  }

  /** Applies defaults only against the preview digest; validates the server response before returning. */
  async applyBlueprintSkillDefaults(
    projectId: string,
    blueprintId: string,
    digest: string,
  ): Promise<ApplyBlueprintSkillDefaultsResponse> {
    const payload = await this.request<unknown>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/skill-defaults/apply`,
      { blueprint_id: blueprintId, digest },
    );
    return parseApplyBlueprintSkillDefaultsResponse(payload);
  }

  // Sync
  /** Reads the current team synchronization preview. */
  getSyncStatus(projectId: string): Promise<SyncStatusDto> {
    return this.request<SyncStatusDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/sync`);
  }

  /** Commits the submitted team synchronization choices. */
  commitSync(projectId: string, req: SyncCommitRequest): Promise<SyncCommitResponseDto> {
    return this.request<SyncCommitResponseDto>('POST', `/projects/${encodeURIComponent(projectId)}/team/sync`, req);
  }

  // Orchestration (Feature 008 — Squad Coordinator Agent)
  /** Starts a coordinator run with optional workflow, approval, and provider settings. */
  startOrchestration(
    projectId: string,
    goal: string,
    workflowOverrideId?: string | null,
    startMode?: StartOrchestrationMode,
    providerKey?: string,
    approvalPolicy?: RunApprovalPolicy,
  ): Promise<StartOrchestrationResponse> {
    const body: Record<string, unknown> = { goal };
    if (workflowOverrideId) body.workflow_override_id = workflowOverrideId;
    if (startMode && startMode !== 'define_outcome') body.start_mode = startMode;
    if (approvalPolicy) {
      body.auto_approve_tools = approvalPolicy.auto_approve_tools;
      body.autopilot = approvalPolicy.autopilot;
    }
    return this.request<StartOrchestrationResponse>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/orchestrations`,
      body,
      undefined,
      providerHeaders(providerKey),
    );
  }

  // Project Workspace browsing (read-only). The backend exposes the project repo
  // at its current branch plus active run worktree branches as selectable refs.
  /** Lists repository and active-worktree refs available for read-only project browsing. */
  getProjectWorkspaceRefs(projectId: string): Promise<WorkspaceRefsResponse> {
    return this.request<WorkspaceRefsResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/refs`);
  }

  /** Lists a project workspace tree at the selected ref, or the current default ref. */
  getProjectWorkspace(projectId: string, ref?: string): Promise<WorkspaceNode[]> {
    const query = ref ? `?ref=${encodeURIComponent(ref)}` : '';
    return this.request<WorkspaceNode[]>('GET', `/projects/${encodeURIComponent(projectId)}/workspace${query}`);
  }

  /** Reads project workspace file content at a selected ref, encoding each path segment. */
  getProjectWorkspaceFileContent(projectId: string, path: string, ref?: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    const query = ref ? `?ref=${encodeURIComponent(ref)}` : '';
    return this.request<WorkspaceFileContent>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/files/${encoded}/content${query}`);
  }

  /** Reads the current outcome specification for a run. */
  getOutcomeSpec(runId: string): Promise<OutcomeSpec> {
    return this.request<OutcomeSpec>('GET', `/runs/${encodeURIComponent(runId)}/outcome-spec`);
  }

  /** Confirms the proposed outcome, optionally allowing the server to promote tasks. */
  confirmOutcomeSpec(runId: string, allowTaskPromotion = false, providerKey?: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/outcome-spec/confirm`,
      { allowTaskPromotion },
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Sends owner feedback and requests a revised outcome specification. */
  reviseOutcomeSpec(runId: string, feedback: string, providerKey?: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/outcome-spec/revise`,
      { feedback },
      undefined,
      providerHeaders(providerKey),
    );
  }

  // Coordinator steering (Feature 008 Phase 2). The /steer endpoint is added by the
  // backend team in parallel; this codes against the agreed contract.
  /** Sends an owner steering instruction to an active coordinator run. */
  steerCoordinator(coordinatorRunId: string, req: SteerCoordinatorRequest, providerKey?: string): Promise<SteerCoordinatorResponse> {
    return this.request<SteerCoordinatorResponse>(
      'POST',
      `/runs/${encodeURIComponent(coordinatorRunId)}/steer`,
      req,
      undefined,
      providerHeaders(providerKey),
    );
  }

  // ─── Assistant (operator) run (#346) ──────────────────────────────────────
  // MCP-driven operator assistant (#346). The first composer submit creates the run;
  // subsequent submits append messages. The transcript streams over the EXISTING
  // run-stream endpoints (getRunEvents + useRunStream), so no new stream client is
  // needed here.
  //
  // POST /api/assistant/runs → 201 {run_id, status:"in_progress", message?, tools_invoked?}
  //   429 with error:"operator_run_limit" when the per-user concurrency cap is reached.
  //   Accepts optional `resume_from_run_id`: auto-seeds the new run's model context with a
  //   prior run's full history so a genuinely-gone conversation (see below — NOT plain idle
  //   timeout, which now wakes the same run transparently) can continue seamlessly in a new
  //   run (the old run itself is never modified/revived). 404 run_not_found / 403
  //   forbidden if that referenced run doesn't exist or isn't owned by the caller.
  // POST /api/assistant/runs/{id}/messages → 200 {run_id, role:"assistant", message, status, tools_invoked}
  //   Idle timeout is no longer terminal: an idle run goes dormant server-side and wakes as
  //   the SAME run (same run id, full history intact) on the next message, with a normal
  //   200 — no error, nothing for the frontend to special-case.
  //   404 with error:"run_not_found" for a foreign/nonexistent run id, or a legacy pre-fix
  //   zombie row from before idle runs stopped being terminal.
  //   409 with error:"operator_run_closed" when the run's durable event stream is already
  //   sealed with a genuinely terminal run.completed event (a real end-of-conversation, not
  //   mere inactivity) — the server refuses to revive a sealed run instead of silently
  //   flipping it back to in-progress.
  /** Creates a durable operator-assistant run; the server enforces ownership and concurrency limits. */
  createAssistantRun(req: CreateAssistantRunRequest, providerKey?: string): Promise<CreateAssistantRunResponse> {
    return this.request<CreateAssistantRunResponse>(
      'POST',
      '/assistant/runs',
      req,
      undefined,
      providerHeaders(providerKey),
    );
  }

  /** Adds a message to an assistant run; terminal-stream conflicts are reported by the API. */
  sendAssistantMessage(
    assistantRunId: string,
    req: SendAssistantMessageRequest,
    providerKey?: string,
  ): Promise<SendAssistantMessageResponse> {
    return this.request<SendAssistantMessageResponse>(
      'POST',
      `/assistant/runs/${encodeURIComponent(assistantRunId)}/messages`,
      req,
      undefined,
      providerHeaders(providerKey),
    );
  }

  // GET /api/assistant/runs?limit=50 — the caller's own assistant conversations,
  // newest-first (Tank's #346 follow-up endpoint). Powers the Sessions page.
  /** Lists the current user's assistant conversations, newest first. */
  listAssistantRuns(limit = 50): Promise<ListAssistantRunsResponse> {
    return this.request<ListAssistantRunsResponse>(
      'GET', `/assistant/runs?limit=${encodeURIComponent(String(limit))}`);
  }

  // Coordinator topology REST seed (Feature 008 Phase 2). The SSE topology snapshot is
  // emitted before the stream connects, so the page seeds nodes/edges from these on mount,
  // then applies SSE deltas on top (snapshot-race fix). 404 when the run has no plan yet.
  /** Reads a coordinator's work plan as the initial topology snapshot. */
  getWorkPlan(coordinatorRunId: string): Promise<WorkPlanResponse> {
    return this.request<WorkPlanResponse>('GET', `/runs/${encodeURIComponent(coordinatorRunId)}/work-plan`);
  }

  /** Lists current child-run status for the coordinator. */
  getCoordinatorChildren(coordinatorRunId: string): Promise<CoordinatorChildResponse[]> {
    return this.request<CoordinatorChildResponse[]>('GET', `/runs/${encodeURIComponent(coordinatorRunId)}/children`);
  }

  // Collective human review over the assembled integration output (Feature 008 Phase 3).
  // Posts the backend AssemblyReviewRequest shape ({ approved, request_changes, feedback }) derived
  // from a friendlier decision verb. approve -> merge/scribe/complete; request_changes -> re-dispatch;
  // decline -> assembly_declined.
  /** Converts the owner decision into the assembly review contract and submits it. */
  reviewAssembly(coordinatorRunId: string, decision: AssemblyReviewDecision, comment?: string, providerKey?: string): Promise<void> {
    const body: AssemblyReviewRequest = {
      approved: decision === 'approve',
      request_changes: decision === 'request_changes',
      feedback: comment,
    };
    return this.request<AssemblyReviewResponse>(
      'POST',
      `/runs/${encodeURIComponent(coordinatorRunId)}/assembly/review`,
      body,
      undefined,
      providerHeaders(providerKey),
    )
      .then(() => undefined);
  }

  // Answer a worker's bubbled question (agent.question_asked). The answer must be POSTed against
  // the run that ASKED the question: for a coordinator child question/approval that means the
  // childRunId from the event payload, NOT the coordinator run id. 404 = no pending question,
  // 409 = run not InProgress.
  /** Answers a pending question on the run that owns the request ID. */
  answerQuestion(runId: string, requestId: string, answer: string): Promise<AnswerQuestionResponse> {
    return this.request<AnswerQuestionResponse>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/questions/${encodeURIComponent(requestId)}/answer`,
      { answer },
    );
  }

  // Live per-run option toggles. auto-approve cascades to a coordinator's children; autopilot is
  // coordinator-only. Both 404 (not found) / 403 (not owner) / 409 (run not active).
  /** Toggles tool auto-approval for a run; coordinator settings cascade to children. */
  setAutoApprove(runId: string, enabled: boolean): Promise<AutoApproveResponse> {
    return this.request<AutoApproveResponse>('POST', `/runs/${encodeURIComponent(runId)}/auto-approve`, { enabled });
  }

  /** Toggles coordinator autopilot; the API rejects unsupported or inactive runs. */
  setAutopilot(runId: string, enabled: boolean): Promise<AutopilotResponse> {
    return this.request<AutopilotResponse>('POST', `/runs/${encodeURIComponent(runId)}/autopilot`, { enabled });
  }

  /** Approves a pending tool request at the requested persistence scope. */
  approveTool(runId: string, requestId: string, scope: 'once' | 'run' | 'always' | 'tool'): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/tool-approvals`, { request_id: requestId, scope });
  }

  /** Denies one pending tool request. */
  denyTool(runId: string, requestId: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/tool-denials`, { request_id: requestId });
  }

  /** Retries one preview approval and returns the new pending request identity. */
  retryPreviewApproval(runId: string, requestId: string): Promise<{
    run_id: string;
    request_id: string;
    retry_of_request_id: string;
    expires_at: string;
    state: 'pending';
  }> {
    return this.request(
      'POST',
      `/runs/${encodeURIComponent(runId)}/sandbox/preview-approvals/${encodeURIComponent(requestId)}/retry`,
      {},
    );
  }

  /** Approves the shell command identified by its server-provided hash. */
  approveShell(runId: string, commandHash: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/shell-approvals`, { command_hash: commandHash });
  }

  /** Denies the shell command identified by its server-provided hash. */
  denyShell(runId: string, commandHash: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/shell-denials`, { command_hash: commandHash });
  }

  // Dynamic graph descriptor (Feature 008 Phase 3). Returns null on 404 so the caller
  // can fall back to the hardcoded executor graph until the backend endpoint ships.
  /** Reads the run graph, returning null on 404 so callers can use their fallback graph. */
  async getRunGraph(runId: string): Promise<GraphDescriptor | null> {
    try {
      return await this.request<GraphDescriptor>('GET', `/runs/${encodeURIComponent(runId)}/graph`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  }

  // Backlog & Workflow Kanban board (Feature 009). Thin pass-throughs to the
  // snake_case backlog endpoints — all ordering/claim/validation logic lives server-side.
  /** Reads the project board and optionally includes terminal task history. */
  getBoard(projectId: string, includeTerminalHistory = false): Promise<BoardDto> {
    const query = includeTerminalHistory ? '?include_terminal_history=true' : '';
    return this.request<BoardDto>('GET', `/projects/${encodeURIComponent(projectId)}/board${query}`);
  }

  /** Reads the project's workflow stage definitions. */
  getWorkflowStages(projectId: string): Promise<WorkflowStagesResponse> {
    return this.request<WorkflowStagesResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workflow-stages`);
  }

  /** Creates a task in the project's backlog. */
  captureBacklogTask(projectId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks`, body);
  }

  /** Reads one backlog task. */
  getBacklogTask(projectId: string, taskId: string): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('GET', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`);
  }

  /** Updates a backlog task's title and description. */
  editBacklogTask(projectId: string, taskId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`, body);
  }

  /** Previews or saves dependency changes against the task's expected revision. */
  editBacklogDependencies(
    projectId: string,
    taskId: string,
    body: { expected_revision: number; add?: string[]; remove?: string[]; replace?: string[] },
    preview = false,
  ): Promise<BacklogDependenciesResponse> {
    return this.request<BacklogDependenciesResponse>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/dependencies?preview=${preview}`,
      body,
    );
  }

  /** Deletes a backlog task. */
  deleteBacklogTask(projectId: string, taskId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`);
  }

  /** Archives a backlog task without deleting its history. */
  archiveBacklogTask(projectId: string, taskId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/archive`, {});
  }

  /** Moves a task into the ready queue at an optional target index. */
  moveTaskToReady(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/ready`, { target_index: targetIndex ?? null });
  }

  /** Moves a ready task back to the backlog at an optional target index. */
  moveTaskToBacklog(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/backlog`, { target_index: targetIndex ?? null });
  }

  /** Reorders one task at the supplied list index. */
  reorderBacklogTask(projectId: string, taskId: string, targetIndex: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/reorder`, { target_index: targetIndex });
  }

  /** Moves every eligible backlog task to ready and returns the moved count. */
  sendAllBacklogToReady(projectId: string): Promise<{ moved: number }> {
    return this.request<{ moved: number }>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/ready-all`, {});
  }

  /** Reads backlog settings that control task intake and ordering. */
  getBacklogSettings(projectId: string): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('GET', `/projects/${encodeURIComponent(projectId)}/backlog/settings`);
  }

  /** Saves backlog settings and returns the server's resulting values. */
  setBacklogSettings(projectId: string, settings: BacklogSettingsDto): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('PUT', `/projects/${encodeURIComponent(projectId)}/backlog/settings`, settings);
  }

  /** Submits a run review; a server conflict with a retryable body becomes RetriableReviewError. */
  async submitReview(runId: string, approved: boolean, providerKey?: string): Promise<ReviewResponse> {
    const body: ReviewRequest = { approved };
    const headers: Record<string, string> = {
      ...this.authHeaders(),
      'Content-Type': 'application/json',
      ...providerHeaders(providerKey),
    };
    const response = await fetch(
      `${this.baseUrl}/api/runs/${encodeURIComponent(runId)}/review`,
      { method: 'POST', headers, credentials: 'include', body: JSON.stringify(body) },
    );
    const text = await response.text();
    if (response.status === 409) {
      let parsed: RetriableReviewErrorBody | null = null;
      try {
        parsed = JSON.parse(text) as RetriableReviewErrorBody;
      } catch {
        // fall through to ApiError below
      }
      if (parsed?.error) throw new RetriableReviewError(parsed.error, parsed.status ?? 'awaiting_review');
      throw new ApiError(409, text);
    }
    if (!response.ok) throw new ApiError(response.status, text);
    return (text ? JSON.parse(text) : null) as ReviewResponse;
  }

  // Lightweight API-reachability probe for the shell status dot (Spec 011, FR-013).
  // Prefers a dedicated /api/health endpoint when present; falls back to the root
  // ("Agentweaver API") endpoint. Returns true when the API responds, false on a
  // network error. Reachability is "the API answered", so any HTTP response counts.
  async checkHealth(): Promise<boolean> {
    const headers = this.authHeaders();
    try {
      const res = await fetch(`${this.baseUrl}/api/health`, { method: 'GET', headers, credentials: 'include' });
      if (res.status !== 404) return res.ok;
    } catch {
      // /api/health unreachable; fall through to the root probe.
    }
    try {
      const res = await fetch(`${this.baseUrl}/`, { method: 'GET', headers, credentials: 'include' });
      return res.ok;
    } catch {
      return false;
    }
  }

  // System diagnostics snapshot (Spec 011, FR-016).
  getDiagnostics(): Promise<SystemDiagnosticsDto> {
    return this.request<SystemDiagnosticsDto>('GET', '/diagnostics');
  }

  // Detailed system diagnostics (spec-018 capacity visibility). Returns null when the
  // endpoint is not yet deployed (404), so callers can fall back gracefully.
  async getDetailedDiagnostics(): Promise<DetailedSystemDiagnosticsDto | null> {
    try {
      return await this.request<DetailedSystemDiagnosticsDto>('GET', '/diagnostics/detailed');
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null;
      throw err;
    }
  }

  // Cluster diagnostics (spec-018, GET /api/diagnostics/cluster). Returns null when
  // the endpoint is not yet deployed (404) so ClusterPage can show a placeholder.
  async getClusterDiagnostics(): Promise<ClusterDiagnosticsDto | null> {
    try {
      return await this.request<ClusterDiagnosticsDto>('GET', '/diagnostics/cluster');
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null;
      throw err;
    }
  }

  /** Reads selected cluster topology layers; returns null when the optional endpoint is absent. */
  async getClusterTopology(
    layers: import('./types').KubernetesTopologyLayer[] = ['runtime'],
  ): Promise<import('./types').KubernetesTopologyDto | null> {
    try {
      const query = new URLSearchParams({ layers: layers.join(',') });
      return await this.request<import('./types').KubernetesTopologyDto>(
        'GET',
        `/diagnostics/cluster/topology?${query.toString()}`,
      );
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null;
      throw err;
    }
  }

  // Project-scoped diagnostics (Spec 011, FR-016). Owner-authorized.
  getProjectDiagnostics(projectId: string): Promise<import('./types').ProjectDiagnosticsDto> {
    return this.request<import('./types').ProjectDiagnosticsDto>('GET', `/projects/${encodeURIComponent(projectId)}/diagnostics`);
  }

  // Heartbeat service status (Spec 011, FR-017).
  getHeartbeatStatus(): Promise<HeartbeatStatusDto> {
    return this.request<HeartbeatStatusDto>('GET', '/diagnostics/heartbeat');
  }

  // Workflow definitions (Spec 010, FR-039). Project-scoped, owner-authorized.
  // List discovered workflows + validation status; Sync re-reads .agentweaver/
  // workflows/ from disk and returns the refreshed set; Get returns one full
  // definition.
  getWorkflowGrammar(): Promise<import('./types').WorkflowGrammar> {
    return this.request<import('./types').WorkflowGrammar>('GET', '/workflows/grammar');
  }

  /** Lists discovered workflow definitions and their validation status. */
  listWorkflows(projectId: string): Promise<import('./types').WorkflowListResponse> {
    return this.request<import('./types').WorkflowListResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workflows`);
  }

  /** Re-reads the project's workflow files and returns the refreshed list. */
  syncWorkflows(projectId: string): Promise<import('./types').WorkflowListResponse> {
    return this.request<import('./types').WorkflowListResponse>('POST', `/projects/${encodeURIComponent(projectId)}/workflows/sync`, {});
  }

  /** Reads one complete project workflow definition. */
  getWorkflow(projectId: string, workflowId: string): Promise<import('./types').WorkflowDetailDto> {
    return this.request<import('./types').WorkflowDetailDto>('GET', `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}`);
  }

  // Set the project's default workflow (Feature 010, FR-041). A null id clears back
  // to the built-in default. Returns the refreshed list (with default_workflow_id).
  setDefaultWorkflow(projectId: string, workflowId: string | null): Promise<import('./types').WorkflowListResponse> {
    return this.request<import('./types').WorkflowListResponse>('PUT', `/projects/${encodeURIComponent(projectId)}/workflows/default`, { workflow_id: workflowId });
  }

  // Set a per-task workflow override (Feature 010, FR-042). A null id clears it.
  // Throws ApiError 409 (body { error: 'task_claimed' }) if the task is already claimed.
  setTaskWorkflowOverride(projectId: string, taskId: string, workflowId: string | null): Promise<import('./types').WorkflowOverrideResponse> {
    return this.request<import('./types').WorkflowOverrideResponse>('PUT', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/workflow-override`, { workflow_id: workflowId });
  }

  // Get the raw YAML content of a project workflow file (US7). Returns the YAML string; throws
  // ApiError 404 when the workflow has no on-disk file (e.g. a built-in workflow).
  getWorkflowYaml(projectId: string, workflowId: string): Promise<string> {
    return this.request<import('./types').WorkflowYamlResponse>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}/yaml`,
    ).then((r) => r.yaml);
  }

  // Save (create or update) a workflow by its YAML content (US7). Returns the parsed WorkflowDetailDto
  // on success. Throws ApiError 400 with body { error: string, line?: number } on validation failure.
  saveWorkflowYaml(projectId: string, workflowId: string, yaml: string): Promise<import('./types').WorkflowDetailDto> {
    return this.request<import('./types').WorkflowDetailDto>(
      'PUT',
      `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}`,
      { yaml },
    );
  }

  /** Starts a workflow run immediately using the optional confirmed provider key. */
  runWorkflowNow(projectId: string, workflowId: string, providerKey?: string): Promise<{ task_id: string }> {
    return this.request<{ task_id: string }>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}/run`,
      {},
      undefined,
      providerHeaders(providerKey),
    );
  }

  // Accept durable workflow generation, then poll the job while preserving the existing UI result shape.
  async generateWorkflow(projectId: string, description: string, providerKey?: string, contentOnly = false): Promise<WorkflowGenerationOutcome> {
    const idempotencyKey = globalThis.crypto?.randomUUID?.()
      ?? `workflow-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const headers = {
      ...providerHeaders(providerKey),
      'Idempotency-Key': idempotencyKey,
    };
    const submit = () => this.request<WorkflowGenerationJob>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/workflows/generate`,
      { description, content_only: contentOnly },
      undefined,
      headers,
    );
    let job: WorkflowGenerationJob;
    try {
      job = await submit();
    } catch (error) {
      if (error instanceof ApiError) throw error;
      job = await submit();
    }
    const executionContext = job.ai_execution_context;
    const deadline = Date.now() + 5 * 60_000 + 30_000;
    while (job.status === 'queued' || job.status === 'running') {
      if (Date.now() >= deadline) {
        return {
          status: 'pending',
          job: {
            jobId: job.job_id,
            statusUrl: job.status_url,
            resultUrl: job.result_url,
            aiExecutionContext: executionContext,
          },
        };
      }
      await new Promise(resolve => setTimeout(resolve, 500));
      job = await this.request<WorkflowGenerationJob>('GET', job.status_url.replace(/^\/api/, ''));
    }
    return this.resolveWorkflowGeneration(job, executionContext);
  }

  /** Rechecks a durable generation job and returns either pending state or its completed draft. */
  async resumeWorkflowGeneration(handle: WorkflowGenerationHandle): Promise<WorkflowGenerationOutcome> {
    const job = await this.request<WorkflowGenerationJob>('GET', handle.statusUrl.replace(/^\/api/, ''));
    if (job.status === 'queued' || job.status === 'running') {
      return {
        status: 'pending',
        job: {
          ...handle,
          statusUrl: job.status_url,
          resultUrl: job.result_url,
        },
      };
    }
    return this.resolveWorkflowGeneration(job, handle.aiExecutionContext);
  }

  private async resolveWorkflowGeneration(
    job: WorkflowGenerationJob,
    executionContext?: AiExecutionContext | null,
  ): Promise<WorkflowGenerationOutcome> {
    if (job.status !== 'completed') {
      throw new Error(job.failure?.message ?? 'Workflow generation failed before producing a draft.');
    }
    const result = await this.request<{
      yaml: string;
      workflow_id: string;
      was_corrected: boolean;
    }>('GET', job.result_url.replace(/^\/api/, ''));
    return {
      status: 'completed',
      yaml: result.yaml,
      workflowId: result.workflow_id,
      wasCorrected: result.was_corrected,
      ai_execution_context: executionContext,
    };
  }

  // Get the static graph descriptor for a workflow definition (US6). Returns a WorkflowGraphDto
  // with nodes/edges ready for WorkflowDefinitionInlinePanel; 404 when the workflow is unknown.
  getWorkflowGraph(projectId: string, workflowId: string): Promise<import('./types').WorkflowGraphDto> {
    return this.request<import('./types').WorkflowGraphDto>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}/graph`,
    );
  }

  // Review policies (Spec 010, FR-025/027/033). Project-scoped, owner-authorized.
  // List discovered policies + active selection; Get returns one policy's steps;
  // SetActive selects the active policy by name (null clears to the built-in
  // default); Sync re-reads .agentweaver/review-policies/ and returns the set.
  // Metrics (web IA reorg) — per-project dashboard + global "Now" overview.
  // `includeMetrics=false` (#208 point 4): skip the server's own internal metrics fan-out when the
  // caller (e.g. DashboardPage) already fetches the full metrics DTO separately via `getProjectMetrics`.
  // `signal` (#208 point 5): forwarded to `fetch` so callers can abort in-flight requests on
  // unmount/range-change instead of leaving them to complete and update unmounted state.
  /** Reads project dashboard data, optionally skipping its metrics fan-out and supporting abort. */
  getProjectDashboard(projectId: string, options?: { includeMetrics?: boolean; signal?: AbortSignal }): Promise<import('./types').ProjectDashboardDto> {
    const query = new URLSearchParams();
    if (options?.includeMetrics === false) query.set('includeMetrics', 'false');
    const qs = query.toString();
    return this.request<import('./types').ProjectDashboardDto>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/dashboard${qs ? `?${qs}` : ''}`,
      undefined,
      options?.signal,
    );
  }

  /** Reads project metrics for an optional date range and forwards cancellation. */
  getProjectMetrics(projectId: string, from?: string, to?: string, signal?: AbortSignal): Promise<import('./types').ProjectMetricsDto> {
    const query = new URLSearchParams();
    if (from) query.set('from', from);
    if (to) query.set('to', to);
    const qs = query.toString();
    return this.request<import('./types').ProjectMetricsDto>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/metrics${qs ? `?${qs}` : ''}`,
      undefined,
      signal,
    );
  }

  /** Reads the global overview and forwards cancellation to the HTTP request. */
  getOverview(signal?: AbortSignal): Promise<import('./types').OverviewDto> {
    return this.request<import('./types').OverviewDto>('GET', '/overview', undefined, signal);
  }

  // #247 — global notification center: pending Human Review (+ future Tool Approval) requests.
  /** Reads pending notifications for the signed-in user. */
  getNotifications(signal?: AbortSignal): Promise<import('./types').NotificationsResponseDto> {
    return this.request<import('./types').NotificationsResponseDto>('GET', '/notifications', undefined, signal);
  }

  /** Dismisses one notification for the current user. */
  dismissNotification(notificationId: string): Promise<void> {
    return this.request<void>('POST', `/notifications/${encodeURIComponent(notificationId)}/dismiss`);
  }

  // Workspace file tree scoped to the project sandbox (Feature 014, FR-001).
  /** Lists workspace paths available for spec decomposition. */
  getWorkspaceFiles(projectId: string): Promise<WorkspaceFileNode[]> {
    return this.request<WorkspaceFileNode[]>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/files`);
  }

  // Decompose a spec file into proposed backlog items (Feature 014, FR-003/004).
  // filePath=null uses the project's confirmed outcome spec stored on the server (requires runId).
  // confirm=false → dry-run preview; confirm=true → create the tasks.
  /** Previews or confirms decomposition of a spec into backlog items. */
  decomposeSpec(projectId: string, filePath: string | null, confirm: boolean, runId?: string | null, ref?: string, providerKey?: string): Promise<DecomposeResponse> {
    return this.request<DecomposeResponse>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/backlog/decompose`,
      { file_path: filePath, run_id: runId ?? null, confirm, ...(ref ? { ref } : {}) },
      undefined,
      providerHeaders(providerKey),
    );
  }

  // Sandbox port-forward (017-preview): tunnel a sandbox pod port to the API server.
  /** Opens a tunnel from a sandbox service port for this run. */
  startPortForward(runId: string, targetPort: number): Promise<PortForwardSessionDto> {
    return this.request<PortForwardSessionDto>('POST', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward`, { targetPort });
  }

  /** Stops one port-forward session belonging to the run. */
  stopPortForward(runId: string, sessionId: string): Promise<{ session_id: string; stopped: boolean }> {
    return this.request<{ session_id: string; stopped: boolean }>('DELETE', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward/${encodeURIComponent(sessionId)}`);
  }

  /** Sends an authenticated keepalive request and throws ApiError when it is rejected. */
  async pingKeepalive(keepaliveUrl: string): Promise<void> {
    const headers = this.authHeaders();
    const response = await fetch(this.apiUrl(keepaliveUrl), { method: 'POST', headers, credentials: 'include' });
    const text = typeof response.text === 'function' ? await response.text() : '';
    if (!response.ok) throw this.createApiError(response.status, text);
  }

  /** Lists port-forward sessions and accepts either supported response envelope. */
  async listPortForwards(runId: string): Promise<PortForwardSessionDto[]> {
    const response = await this.request<PortForwardSessionDto[] | { sessions?: PortForwardSessionDto[] }>('GET', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward`);
    return Array.isArray(response) ? response : (response.sessions ?? []);
  }

  // System runtime info — kubernetes context and pod name (Spec 006).
  /** Reads the current Kubernetes context and runtime pod information. */
  getSystemRuntime(): Promise<RuntimeInfo> {
    return this.request<RuntimeInfo>('GET', '/system/runtime');
  }

  /**
   * Sends one authenticated JSON request, retries once after eligible session recovery,
   * and throws ApiError for non-success responses. Abort signals and provider headers
   * are forwarded; a provider-connection requirement is emitted as a browser event.
   */
  private async request<T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
    extraHeaders?: Record<string, string>,
    cache?: RequestCache,
  ): Promise<T> {
    const send = async () => {
      const sessionToken = this.sessionTokenProvider();
      const headers: Record<string, string> = {
        ...(sessionToken ? { Authorization: `Bearer ${sessionToken}` } : {}),
        ...extraHeaders,
      };
      if (body !== undefined) headers['Content-Type'] = 'application/json';
      const response = await fetch(this.apiUrl(path), {
        method,
        headers,
        credentials: 'include',
        cache,
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal,
      });
      const text = typeof response.text === 'function' ? await response.text() : '';
      return { response, text, sessionToken };
    };

    let { response, text, sessionToken } = await send();
    if (response.status === 401 && this.supportsSessionRecovery &&
        !isModelProviderConnectionRequirement(this.createApiError(response.status, text).payload)) {
      const currentToken = this.sessionTokenProvider();
      const restored = currentToken && currentToken !== sessionToken
        ? true
        : await this.recoverSessionAuth(sessionToken);
      if (restored) {
        ({ response, text, sessionToken } = await send());
      }
      if (!restored || response.status === 401) {
        if (this.sessionTokenProvider() === sessionToken) {
          clearSessionAuth();
        }
        notifySessionAuthInvalid();
      }
    }

    if (!response.ok) throw this.createApiError(response.status, text);
    if (text) return JSON.parse(text) as T;
    if (typeof response.json === 'function') {
      try {
        return await response.json() as T;
      } catch {
        // fall through to null
      }
    }
    return null as T;
  }

  /** Shares a single peer session-recovery attempt across concurrent unauthorized requests. */
  private recoverSessionAuth(rejectedToken: string | null): Promise<boolean> {
    if (this.sessionRecovery) return this.sessionRecovery;
    if (this.sessionTokenProvider() === rejectedToken) {
      clearSessionAuth();
    }
    this.sessionRecovery = requestSessionAuthFromPeer(rejectedToken ?? undefined)
      .finally(() => {
        this.sessionRecovery = null;
      });
    return this.sessionRecovery;
  }

  /** Creates the standard HTTP error and announces provider-connection requirements to the UI. */
  private createApiError(status: number, body: string): ApiError {
    const error = new ApiError(status, body);
    if (typeof window !== 'undefined' && isModelProviderConnectionRequirement(error.payload)) {
      window.dispatchEvent(new CustomEvent(
        MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT,
        { detail: error.payload },
      ));
    }
    return error;
  }

  /** Preserves absolute service URLs and prefixes relative paths with the API base path. */
  private apiUrl(pathOrUrl: string): string {
    if (/^[a-z][a-z\d+\-.]*:\/\//i.test(pathOrUrl)) return pathOrUrl;
    const path = pathOrUrl.startsWith('/') ? pathOrUrl : `/${pathOrUrl}`;
    const apiPath = path === '/api' || path.startsWith('/api/') ? path : `/api${path}`;
    return `${this.baseUrl}${apiPath}`;
  }
}
