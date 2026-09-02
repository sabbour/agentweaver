import { getSessionToken } from '../config';
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
  AuthConfigResponse,
  AuthSessionResponse,
  AssemblyReviewDecision,
  AssemblyReviewRequest,
  AssemblyReviewResponse,
  AutoApproveResponse,
  AutopilotResponse,
  BacklogSettingsDto,
  BacklogTaskDto,
  ByokProviderConfig,
  ByokProviderListResponse,
  ByokProviderRequest,
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
  CreateProjectRoleAssignmentRequest,
  CoordinatorChildResponse,
  CreateProjectRepositoryRequest,
  CreateProjectRequest,
  CreateProjectRunRequest,
  CreateProposalRequest,
  DecomposeResponse,
  DetailedSystemDiagnosticsDto,
  GenerateBlueprintResponse,
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
  ProjectAccessOverview,
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
} from './types';
/** A skill file paired with the folder-relative path it should keep on the server (folder drag-and-drop). */
export interface SkillUploadItem {
  file: File;
  relativePath?: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function isOptionalString(value: unknown): value is string | null | undefined {
  return value === undefined || value === null || typeof value === 'string';
}

function isSkillStatus(value: unknown): value is 'active' | 'missing' | 'malformed' {
  return value === 'active' || value === 'missing' || value === 'malformed';
}

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

export function isApplyBlueprintSkillDefaultsResponse(value: unknown): value is ApplyBlueprintSkillDefaultsResponse {
  return isRecord(value)
    && (value.outcome === 'applied' || value.outcome === 'stale' || value.outcome === 'invalid')
    && isStringArray(value.errors)
    && Object.hasOwn(value, 'preview')
    && (value.preview === null || isBlueprintSkillDefaultsPreviewResponse(value.preview));
}

export function parseApplyBlueprintSkillDefaultsResponse(payload: unknown): ApplyBlueprintSkillDefaultsResponse {
  if (!isApplyBlueprintSkillDefaultsResponse(payload)) {
    throw new TypeError('Invalid apply blueprint skill defaults response.');
  }
  return payload;
}

function parseSkillList(payload: unknown): SkillDto[] {
  if (!Array.isArray(payload)) throw new TypeError('Invalid skill list response.');

  const skills: SkillDto[] = [];
  for (const skill of payload) {
    if (!isSkillDto(skill)) throw new TypeError('Invalid skill list response.');
    skills.push(skill);
  }
  return skills;
}

function parseSkillDetail(payload: unknown): SkillDetailDto {
  if (!isSkillDetailDto(payload)) throw new TypeError('Invalid skill detail response.');
  return payload;
}

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

export function normalizeBlueprintList(payload: unknown): Blueprint[] {
  const list = Array.isArray(payload)
    ? payload
    : isRecord(payload) && Array.isArray(payload.blueprints)
      ? payload.blueprints
      : [];

  return list.filter(isBlueprint);
}

export class ApiError extends Error {
  readonly status: number;
  readonly body: string;
  readonly payload: unknown;

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

export class RetriableReviewError extends Error {
  readonly serverMessage: string;
  readonly runStatus: string;

  constructor(serverMessage: string, runStatus: string) {
    super(serverMessage);
    this.name = 'RetriableReviewError';
    this.serverMessage = serverMessage;
    this.runStatus = runStatus;
  }
}

export class AgentweaverApiClient {
  private readonly baseUrl: string;
  private readonly sessionTokenProvider: () => string | null;

  constructor(baseUrl: string, sessionTokenProvider: (() => string | null) | string = getSessionToken) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.sessionTokenProvider = typeof sessionTokenProvider === 'function'
      ? sessionTokenProvider
      : () => sessionTokenProvider || null;
  }

  private authHeaders(): Record<string, string> {
    const token = this.sessionTokenProvider();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }

  getRun(runId: string): Promise<RunDetail> {
    return this.request<RunDetail>('GET', `/runs/${encodeURIComponent(runId)}`);
  }

  retryRun(runId: string): Promise<RetryRunResponse> {
    return this.request<RetryRunResponse>('POST', `/runs/${encodeURIComponent(runId)}/retry`, {});
  }

  // Persisted run events (FR-022). Seeds the execution timeline for terminal/parked
  // runs whose live SSE stream is closed (e.g. a finished coordinator child). The
  // backend persists and replays the events here; 404 until the log exists.
  getRunEvents(runId: string): Promise<PersistedRunEvent[]> {
    return this.request<PersistedRunEvent[]>('GET', `/runs/${encodeURIComponent(runId)}/events`);
  }

  getRunTokenBreakdown(runId: string): Promise<import('./types').RunAgentTokenBreakdownDto> {
    return this.request<import('./types').RunAgentTokenBreakdownDto>('GET', `/runs/${encodeURIComponent(runId)}/token-breakdown`);
  }

  getRunTraces(runId: string): Promise<import('./types').RunTraceDto> {
    return this.request<import('./types').RunTraceDto>('GET', `/metrics/runs/${encodeURIComponent(runId)}/traces`);
  }

  getSandboxPolicy(repositoryPath: string): Promise<SandboxPolicy> {
    const encoded = encodeURIComponent(repositoryPath);
    return this.request<SandboxPolicy>('GET', `/sandbox-policy?repository_path=${encoded}`);
  }

  getRunFiles(runId: string, filter?: string): Promise<WorkspaceFileEntry[]> {
    const query = filter ? `?filter=${encodeURIComponent(filter)}` : '';
    return this.request<WorkspaceFileEntry[]>('GET', `/runs/${encodeURIComponent(runId)}/files${query}`);
  }

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

  getAssemblyFileDiff(runId: string, path: string): Promise<WorkspaceFileDiff> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileDiff>('GET', `/runs/${encodeURIComponent(runId)}/assembly/files/${encoded}`);
  }

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

  getRunFileContent(runId: string, path: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileContent>('GET', `/runs/${encodeURIComponent(runId)}/files/${encoded}/content`);
  }

  getRunWorkspace(runId: string): Promise<WorkspaceNode[]> {
    return this.request<WorkspaceNode[]>('GET', `/runs/${encodeURIComponent(runId)}/workspace`);
  }

  commitRun(runId: string): Promise<CommitResponse> {
    return this.request<CommitResponse>('POST', `/runs/${encodeURIComponent(runId)}/commit`, {});
  }

  requestChanges(runId: string, comment: string): Promise<RequestChangesResponse> {
    return this.request<RequestChangesResponse>('POST', `/runs/${encodeURIComponent(runId)}/request-changes`, { comment });
  }

  updateSandboxPolicy(policy: SandboxPolicy): Promise<SandboxPolicy> {
    return this.request<SandboxPolicy>('PUT', '/sandbox-policy', policy);
  }

  // Projects
  // Paginated per the pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`):
  // the server always returns the `{ items, page, page_size, total_count, total_pages }` envelope.
  listProjects(options?: PagedRequestOptions): Promise<PagedResult<Project>> {
    return this.request<PagedResult<Project>>('GET', `/projects${pagingQuery(options)}`, undefined, options?.signal);
  }

  getProject(projectId: string): Promise<Project> {
    return this.request<Project>('GET', `/projects/${encodeURIComponent(projectId)}`);
  }

  createProject(req: CreateProjectRequest): Promise<Project> {
    return this.request<Project>('POST', '/projects', req);
  }

  listGitHubRepositorySelections(): Promise<GitHubRepositorySelectionListResponse> {
    return this.request<GitHubRepositorySelectionListResponse>('GET', '/github/repository-selections');
  }

  issueGitHubRepositorySelection(fullName: string): Promise<GitHubRepositorySelectionCodeResponse> {
    return this.request<GitHubRepositorySelectionCodeResponse>('POST', '/github/repository-selections', {
      full_name: fullName,
    });
  }

  listBlueprints(): Promise<Blueprint[]> {
    return this.request<Blueprint[] | ListBlueprintsResponse>('GET', '/blueprints')
      .then(normalizeBlueprintList);
  }

  generateBlueprint(description: string, targetRepository?: string | null): Promise<GenerateBlueprintResponse> {
    return this.request<GenerateBlueprintResponse>('POST', '/blueprints/generate', {
      description,
      target_repository: targetRepository || undefined,
    });
  }

  suggestBlueprint(repository: string): Promise<SuggestBlueprintResponse> {
    return this.request<SuggestBlueprintResponse>('POST', '/blueprints/suggest', { repository });
  }

  renameProject(projectId: string, name: string): Promise<void> {
    return this.request<void>('PATCH', `/projects/${encodeURIComponent(projectId)}`, { name });
  }

  updateProjectProviderSettings(projectId: string, req: UpdateProjectProviderSettingsRequest): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/provider-settings`, req);
  }

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

  getUnattendedReadiness(projectId: string): Promise<UnattendedReadiness> {
    return this.request<UnattendedReadiness>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/github/unattended-readiness`,
    );
  }

  beginProjectCopilotAuthorization(projectId: string): Promise<{
    authorization_url: string;
    transaction_id: string;
    expires_at: string;
  }> {
    return this.request('POST', `/projects/${encodeURIComponent(projectId)}/github/copilot/authorizations`, {});
  }

  getProjectCopilotConnection(projectId: string): Promise<ProjectCopilotConnection> {
    return this.request<ProjectCopilotConnection>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/github/copilot/connection`,
    );
  }

  deleteProject(projectId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}?confirm=true`);
  }

  startProjectRun(projectId: string, req: CreateProjectRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', `/projects/${encodeURIComponent(projectId)}/runs`, req);
  }

  // Paginated per the pagination contract — server always returns the paged envelope.
  listProjectRuns(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<WorkflowRunDto>> {
    return this.request<PagedResult<WorkflowRunDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/runs${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

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

  deleteRun(runId: string): Promise<void> {
    return this.request<void>('DELETE', `/runs/${encodeURIComponent(runId)}`);
  }

  cancelRun(runId: string): Promise<{ run_id: string; status: string; cancelled: boolean; already_terminal: boolean }> {
    return this.request('POST', `/runs/${encodeURIComponent(runId)}/cancel`, {});
  }

  archiveRun(runId: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/archive`, {});
  }

  getServerInfo(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/server/info');
  }

  getAuthSession(): Promise<AuthSessionResponse> {
    return this.request<AuthSessionResponse>('GET', '/auth/session');
  }

  getAuthConfig(): Promise<AuthConfigResponse> {
    return this.request<AuthConfigResponse>('GET', '/auth/config');
  }

  // Deployment-wide "bring your own key" inference provider configuration.
  // Multiple providers can be configured and their keys kept at once; exactly one may be
  // marked active (active_provider_id) — a null active id means GitHub Copilot mode.
  listByokProviders(): Promise<ByokProviderListResponse> {
    return this.request<ByokProviderListResponse>('GET', '/admin/byok-providers');
  }

  addByokProvider(req: ByokProviderRequest): Promise<ByokProviderConfig> {
    return this.request<ByokProviderConfig>('POST', '/admin/byok-providers', req);
  }

  updateByokProvider(id: string, req: ByokProviderRequest): Promise<ByokProviderConfig> {
    return this.request<ByokProviderConfig>('PUT', `/admin/byok-providers/${encodeURIComponent(id)}`, req);
  }

  removeByokProvider(id: string): Promise<void> {
    return this.request<void>('DELETE', `/admin/byok-providers/${encodeURIComponent(id)}`);
  }

  activateByokProvider(id: string): Promise<void> {
    return this.request<void>('POST', `/admin/byok-providers/${encodeURIComponent(id)}/activate`);
  }

  deactivateByokProviders(): Promise<void> {
    return this.request<void>('POST', '/admin/byok-providers/deactivate');
  }

  beginPlatformDefaultCopilotAuthorization(): Promise<{
    authorization_url: string;
    transaction_id: string;
    expires_at: string;
  }> {
    return this.request('POST', '/admin/platform-default-copilot/begin', {});
  }

  getPlatformDefaultCopilotConnection(): Promise<PlatformDefaultCopilotConnection> {
    return this.request<PlatformDefaultCopilotConnection>('GET', '/admin/platform-default-copilot/status');
  }

  disconnectPlatformDefaultCopilotConnection(): Promise<void> {
    return this.request<void>('POST', '/admin/platform-default-copilot/disconnect', {});
  }

  beginRepoAppAuthorization(): Promise<{ authorization_url: string; transaction_id: string; expires_at: string }> {
    return this.request('POST', '/auth/github/repo-app/authorizations', {});
  }

  getRepoAppConnectionStatus(): Promise<RepoAppConnectionStatus> {
    return this.request<RepoAppConnectionStatus>('GET', '/auth/github/repo-app/authorization/status');
  }

  signOutSession(): Promise<void> {
    return this.request<void>('POST', '/auth/session/sign-out', {});
  }

  // Post-creation GitHub connection for a currently-unconnected (blank-origin) project.
  listProjectRepositoryOwners(projectId: string): Promise<RepositoryOwner[]> {
    return this.request<RepositoryOwner[]>('GET', `/projects/${encodeURIComponent(projectId)}/github/repository-owners`);
  }

  createProjectRepository(projectId: string, req: CreateProjectRepositoryRequest): Promise<ConnectedRepository> {
    return this.request<ConnectedRepository>('POST', `/projects/${encodeURIComponent(projectId)}/github/repository`, req);
  }

  connectProjectRepository(projectId: string, req: ConnectProjectRepositoryRequest): Promise<ConnectedRepository> {
    return this.request<ConnectedRepository>('POST', `/projects/${encodeURIComponent(projectId)}/github/repository/connection`, req);
  }

  getProjectAccessOverview(projectId: string): Promise<ProjectAccessOverview> {
    return this.request<ProjectAccessOverview>('GET', `/projects/${encodeURIComponent(projectId)}/access`);
  }

  createProjectRoleAssignment(projectId: string, req: CreateProjectRoleAssignmentRequest): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/role-assignments`, req);
  }

  deleteProjectRoleAssignment(projectId: string, assignmentId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/role-assignments/${encodeURIComponent(assignmentId)}`);
  }

  // Catalog
  getRoles(): Promise<RoleDto[]> {
    return this.request<RoleDto[]>('GET', '/catalog/roles');
  }

  // Casting
  getTemplates(): Promise<TeamTemplateDto[]> {
    return this.request<TeamTemplateDto[]>('GET', '/casting/templates');
  }

  getUniverses(projectId: string): Promise<{ universes: string[] }> {
    return this.request<{ universes: string[] }>('GET', `/projects/${encodeURIComponent(projectId)}/casting/universes`);
  }

  createProposal(projectId: string, req: CreateProposalRequest): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('POST', `/projects/${encodeURIComponent(projectId)}/casting/proposals`, req);
  }

  getProposal(projectId: string, proposalId: string): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('GET', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  amendProposal(projectId: string, proposalId: string, req: AmendProposalRequest): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`, req);
  }

  confirmProposal(projectId: string, proposalId: string, req: ConfirmProposalRequest): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}/confirm`, req);
  }

  rejectProposal(projectId: string, proposalId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  // Team
  getTeam(projectId: string): Promise<TeamDto> {
    return this.request<TeamDto>('GET', `/projects/${encodeURIComponent(projectId)}/team`);
  }

  getMemberCharter(projectId: string, memberName: string): Promise<CharterDto> {
    return this.request<CharterDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`);
  }

  updateMemberCharter(projectId: string, memberName: string, content: string): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`, { content });
  }

  addMember(projectId: string, req: AddMemberRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('POST', `/projects/${encodeURIComponent(projectId)}/team/members`, req);
  }

  removeMember(projectId: string, memberName: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`);
  }

  reroleMember(projectId: string, memberName: string, req: ReroleRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`, req);
  }

  getMemberHistory(projectId: string, memberName: string): Promise<HistoryDto> {
    return this.request<HistoryDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/history`);
  }

  // Paginated per the pagination contract — existing `status`/`type`/`agent` filters (if any)
  // are applied server-side before paging.
  getDecisions(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').DecisionDto>> {
    return this.request<PagedResult<import('./types').DecisionDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/decisions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  getDecisionsInbox(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').DecisionInboxEntryDto>> {
    return this.request<PagedResult<import('./types').DecisionInboxEntryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/decisions/inbox${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  mergeDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/merge`, {});
  }

  promoteDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/promote`, {});
  }

  rejectDecisionInboxEntry(projectId: string, entryId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/decisions/inbox/${encodeURIComponent(entryId)}/reject`, {});
  }

  getAgentMemory(projectId: string, agentName: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').AgentMemoryDto>> {
    return this.request<PagedResult<import('./types').AgentMemoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  getProjectMemory(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').AgentMemoryDto>> {
    return this.request<PagedResult<import('./types').AgentMemoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/memory${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  getProjectSessions(projectId: string, options?: PagedRequestOptions): Promise<PagedResult<import('./types').SessionHistoryDto>> {
    return this.request<PagedResult<import('./types').SessionHistoryDto>>(
      'GET',
      `/projects/${encodeURIComponent(projectId)}/sessions${pagingQuery(options)}`,
      undefined,
      options?.signal,
    );
  }

  createAgentMemory(
    projectId: string,
    agentName: string,
    body: { type: string; content: string; importance?: string; tags?: string },
  ): Promise<import('./types').AgentMemoryDto> {
    return this.request<import('./types').AgentMemoryDto>('POST', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory`, body);
  }

  updateAgentMemory(
    projectId: string,
    agentName: string,
    memoryId: string,
    body: { type?: string; content?: string; importance?: string; tags?: string },
  ): Promise<import('./types').AgentMemoryDto> {
    return this.request<import('./types').AgentMemoryDto>('PUT', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory/${encodeURIComponent(memoryId)}`, body);
  }

  // Skills (issues #51/#56) — per-project catalog + agent assignments.
  listSkills(projectId: string): Promise<import('./types').SkillDto[]> {
    return this.request<unknown>('GET', `/projects/${encodeURIComponent(projectId)}/skills`).then(parseSkillList);
  }

  getSkill(projectId: string, skillId: string): Promise<import('./types').SkillDetailDto> {
    return this.request<unknown>('GET', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}`).then(parseSkillDetail);
  }

  deleteSkill(projectId: string, skillId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}`);
  }

  createSkill(projectId: string, body: import('./types').CreateSkillRequest): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills`, body);
  }

  generateSkill(projectId: string, description: string): Promise<import('./types').GeneratedSkillDraft> {
    return this.request<import('./types').GeneratedSkillDraft>('POST', `/projects/${encodeURIComponent(projectId)}/skills/generate`, { description });
  }

  syncSkills(projectId: string): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/sync`, {});
  }

  previewSkillImport(projectId: string, repoUrl: string): Promise<import('./types').SkillImportPreviewResponse> {
    return this.request<import('./types').SkillImportPreviewResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/import/preview`, { repoUrl });
  }

  importSkills(projectId: string, repoUrl: string, locations?: string[]): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skills/import`, { repoUrl, locations });
  }

  // Project-scoped list: built-in config marketplaces + this project's user-added URL sources.
  listSkillMarketplaces(projectId: string): Promise<import('./types').SkillMarketplaceDto[]> {
    return this.request<import('./types').SkillMarketplaceDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces`);
  }

  addSkillMarketplaceSource(projectId: string, body: import('./types').AddSkillMarketplaceSourceRequest): Promise<import('./types').SkillMarketplaceDto> {
    return this.request<import('./types').SkillMarketplaceDto>('POST', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/sources`, body);
  }

  removeSkillMarketplaceSource(projectId: string, name: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/sources/${encodeURIComponent(name)}`);
  }

  browseSkillMarketplace(projectId: string, marketplace: string, query?: string, page?: number, pageSize?: number): Promise<import('./types').SkillMarketplaceBrowseResponse> {
    return this.request<import('./types').SkillMarketplaceBrowseResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/${encodeURIComponent(marketplace)}/browse`, { query, page, pageSize });
  }

  importMarketplaceSkills(projectId: string, marketplace: string, locations: string[]): Promise<import('./types').SkillAcquisitionResponse> {
    return this.request<import('./types').SkillAcquisitionResponse>('POST', `/projects/${encodeURIComponent(projectId)}/skill-marketplaces/${encodeURIComponent(marketplace)}/import`, { locations });
  }

  // Multipart upload of skill file(s)/folder/archive. Bypasses request<T> to send FormData.
  // Accepts plain File objects (single-file / file-picker uploads) or {file, relativePath}
  // pairs (folder drag-and-drop) so nested SKILL.md directories survive the round-trip.
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

  listSkillAssignments(projectId: string): Promise<import('./types').SkillAssignmentDto[]> {
    return this.request<import('./types').SkillAssignmentDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/skills/assignments`);
  }

  assignSkill(projectId: string, skillId: string, agentName: string): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}/assignments/${encodeURIComponent(agentName)}`, {});
  }

  unassignSkill(projectId: string, skillId: string, agentName: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/skills/${encodeURIComponent(skillId)}/assignments/${encodeURIComponent(agentName)}`);
  }

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
  getSyncStatus(projectId: string): Promise<SyncStatusDto> {
    return this.request<SyncStatusDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/sync`);
  }

  commitSync(projectId: string, req: SyncCommitRequest): Promise<SyncCommitResponseDto> {
    return this.request<SyncCommitResponseDto>('POST', `/projects/${encodeURIComponent(projectId)}/team/sync`, req);
  }

  // Orchestration (Feature 008 — Squad Coordinator Agent)
  startOrchestration(
    projectId: string,
    goal: string,
    workflowOverrideId?: string | null,
    startMode?: StartOrchestrationMode,
  ): Promise<StartOrchestrationResponse> {
    const body: Record<string, unknown> = { goal };
    if (workflowOverrideId) body.workflow_override_id = workflowOverrideId;
    if (startMode && startMode !== 'define_outcome') body.start_mode = startMode;
    return this.request<StartOrchestrationResponse>('POST', `/projects/${encodeURIComponent(projectId)}/orchestrations`, body);
  }

  // Project Workspace browsing (read-only). The backend exposes the project repo
  // at its current branch plus active run worktree branches as selectable refs.
  getProjectWorkspaceRefs(projectId: string): Promise<WorkspaceRefsResponse> {
    return this.request<WorkspaceRefsResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/refs`);
  }

  getProjectWorkspace(projectId: string, ref?: string): Promise<WorkspaceNode[]> {
    const query = ref ? `?ref=${encodeURIComponent(ref)}` : '';
    return this.request<WorkspaceNode[]>('GET', `/projects/${encodeURIComponent(projectId)}/workspace${query}`);
  }

  getProjectWorkspaceFileContent(projectId: string, path: string, ref?: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    const query = ref ? `?ref=${encodeURIComponent(ref)}` : '';
    return this.request<WorkspaceFileContent>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/files/${encoded}/content${query}`);
  }

  getOutcomeSpec(runId: string): Promise<OutcomeSpec> {
    return this.request<OutcomeSpec>('GET', `/runs/${encodeURIComponent(runId)}/outcome-spec`);
  }

  confirmOutcomeSpec(runId: string, allowTaskPromotion = false): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/runs/${encodeURIComponent(runId)}/outcome-spec/confirm`, { allowTaskPromotion });
  }

  reviseOutcomeSpec(runId: string, feedback: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/runs/${encodeURIComponent(runId)}/outcome-spec/revise`, { feedback });
  }

  // Coordinator steering (Feature 008 Phase 2). The /steer endpoint is added by the
  // backend team in parallel; this codes against the agreed contract.
  steerCoordinator(coordinatorRunId: string, req: SteerCoordinatorRequest): Promise<SteerCoordinatorResponse> {
    return this.request<SteerCoordinatorResponse>('POST', `/runs/${encodeURIComponent(coordinatorRunId)}/steer`, req);
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
  createAssistantRun(req: CreateAssistantRunRequest): Promise<CreateAssistantRunResponse> {
    return this.request<CreateAssistantRunResponse>('POST', '/assistant/runs', req);
  }

  sendAssistantMessage(
    assistantRunId: string,
    req: SendAssistantMessageRequest,
  ): Promise<SendAssistantMessageResponse> {
    return this.request<SendAssistantMessageResponse>(
      'POST', `/assistant/runs/${encodeURIComponent(assistantRunId)}/messages`, req);
  }

  // GET /api/assistant/runs?limit=50 — the caller's own assistant conversations,
  // newest-first (Tank's #346 follow-up endpoint). Powers the Sessions page.
  listAssistantRuns(limit = 50): Promise<ListAssistantRunsResponse> {
    return this.request<ListAssistantRunsResponse>(
      'GET', `/assistant/runs?limit=${encodeURIComponent(String(limit))}`);
  }

  // Coordinator topology REST seed (Feature 008 Phase 2). The SSE topology snapshot is
  // emitted before the stream connects, so the page seeds nodes/edges from these on mount,
  // then applies SSE deltas on top (snapshot-race fix). 404 when the run has no plan yet.
  getWorkPlan(coordinatorRunId: string): Promise<WorkPlanResponse> {
    return this.request<WorkPlanResponse>('GET', `/runs/${encodeURIComponent(coordinatorRunId)}/work-plan`);
  }

  getCoordinatorChildren(coordinatorRunId: string): Promise<CoordinatorChildResponse[]> {
    return this.request<CoordinatorChildResponse[]>('GET', `/runs/${encodeURIComponent(coordinatorRunId)}/children`);
  }

  // Collective human review over the assembled integration output (Feature 008 Phase 3).
  // Posts the backend AssemblyReviewRequest shape ({ approved, request_changes, feedback }) derived
  // from a friendlier decision verb. approve -> merge/scribe/complete; request_changes -> re-dispatch;
  // decline -> assembly_declined.
  reviewAssembly(coordinatorRunId: string, decision: AssemblyReviewDecision, comment?: string): Promise<void> {
    const body: AssemblyReviewRequest = {
      approved: decision === 'approve',
      request_changes: decision === 'request_changes',
      feedback: comment,
    };
    return this.request<AssemblyReviewResponse>('POST', `/runs/${encodeURIComponent(coordinatorRunId)}/assembly/review`, body)
      .then(() => undefined);
  }

  // Answer a worker's bubbled question (agent.question_asked). The answer must be POSTed against
  // the run that ASKED the question: for a coordinator child question/approval that means the
  // childRunId from the event payload, NOT the coordinator run id. 404 = no pending question,
  // 409 = run not InProgress.
  answerQuestion(runId: string, requestId: string, answer: string): Promise<AnswerQuestionResponse> {
    return this.request<AnswerQuestionResponse>(
      'POST',
      `/runs/${encodeURIComponent(runId)}/questions/${encodeURIComponent(requestId)}/answer`,
      { answer },
    );
  }

  // Live per-run option toggles. auto-approve cascades to a coordinator's children; autopilot is
  // coordinator-only. Both 404 (not found) / 403 (not owner) / 409 (run not active).
  setAutoApprove(runId: string, enabled: boolean): Promise<AutoApproveResponse> {
    return this.request<AutoApproveResponse>('POST', `/runs/${encodeURIComponent(runId)}/auto-approve`, { enabled });
  }

  setAutopilot(runId: string, enabled: boolean): Promise<AutopilotResponse> {
    return this.request<AutopilotResponse>('POST', `/runs/${encodeURIComponent(runId)}/autopilot`, { enabled });
  }

  approveTool(runId: string, requestId: string, scope: 'once' | 'run' | 'always' | 'tool'): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/tool-approvals`, { request_id: requestId, scope });
  }

  denyTool(runId: string, requestId: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/tool-denials`, { request_id: requestId });
  }

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

  approveShell(runId: string, commandHash: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/shell-approvals`, { command_hash: commandHash });
  }

  denyShell(runId: string, commandHash: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/shell-denials`, { command_hash: commandHash });
  }

  // Dynamic graph descriptor (Feature 008 Phase 3). Returns null on 404 so the caller
  // can fall back to the hardcoded executor graph until the backend endpoint ships.
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
  getBoard(projectId: string, includeTerminalHistory = false): Promise<BoardDto> {
    const query = includeTerminalHistory ? '?include_terminal_history=true' : '';
    return this.request<BoardDto>('GET', `/projects/${encodeURIComponent(projectId)}/board${query}`);
  }

  getWorkflowStages(projectId: string): Promise<WorkflowStagesResponse> {
    return this.request<WorkflowStagesResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workflow-stages`);
  }

  captureBacklogTask(projectId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks`, body);
  }

  getBacklogTask(projectId: string, taskId: string): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('GET', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`);
  }

  editBacklogTask(projectId: string, taskId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('PATCH', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`, body);
  }

  deleteBacklogTask(projectId: string, taskId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`);
  }

  archiveBacklogTask(projectId: string, taskId: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/archive`, {});
  }

  moveTaskToReady(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/ready`, { target_index: targetIndex ?? null });
  }

  moveTaskToBacklog(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/backlog`, { target_index: targetIndex ?? null });
  }

  reorderBacklogTask(projectId: string, taskId: string, targetIndex: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/reorder`, { target_index: targetIndex });
  }

  sendAllBacklogToReady(projectId: string): Promise<{ moved: number }> {
    return this.request<{ moved: number }>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/ready-all`, {});
  }

  getBacklogSettings(projectId: string): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('GET', `/projects/${encodeURIComponent(projectId)}/backlog/settings`);
  }

  setBacklogSettings(projectId: string, settings: BacklogSettingsDto): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('PUT', `/projects/${encodeURIComponent(projectId)}/backlog/settings`, settings);
  }

  async submitReview(runId: string, approved: boolean): Promise<ReviewResponse> {
    const body: ReviewRequest = { approved };
    const headers: Record<string, string> = {
      ...this.authHeaders(),
      'Content-Type': 'application/json',
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
  listWorkflows(projectId: string): Promise<import('./types').WorkflowListResponse> {
    return this.request<import('./types').WorkflowListResponse>('GET', `/projects/${encodeURIComponent(projectId)}/workflows`);
  }

  syncWorkflows(projectId: string): Promise<import('./types').WorkflowListResponse> {
    return this.request<import('./types').WorkflowListResponse>('POST', `/projects/${encodeURIComponent(projectId)}/workflows/sync`, {});
  }

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

  runWorkflowNow(projectId: string, workflowId: string): Promise<{ task_id: string }> {
    return this.request<{ task_id: string }>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflowId)}/run`,
      {},
    );
  }

  // Generate a workflow draft from a natural-language description (US10). Returns the generated YAML
  // (unsaved — open it in the editor for review), the workflow id, and whether the single correction
  // pass was needed. Throws ApiError 400 when generation fails after the correction pass.
  generateWorkflow(projectId: string, description: string): Promise<{ yaml: string; workflowId: string; wasCorrected: boolean }> {
    return this.request<{ yaml: string; workflowId: string; wasCorrected: boolean }>(
      'POST',
      `/projects/${encodeURIComponent(projectId)}/workflows/generate`,
      { description },
    );
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

  getOverview(signal?: AbortSignal): Promise<import('./types').OverviewDto> {
    return this.request<import('./types').OverviewDto>('GET', '/overview', undefined, signal);
  }

  // #247 — global notification center: pending Human Review (+ future Tool Approval) requests.
  getNotifications(signal?: AbortSignal): Promise<import('./types').NotificationsResponseDto> {
    return this.request<import('./types').NotificationsResponseDto>('GET', '/notifications', undefined, signal);
  }

  dismissNotification(notificationId: string): Promise<void> {
    return this.request<void>('POST', `/notifications/${encodeURIComponent(notificationId)}/dismiss`);
  }

  // Workspace file tree scoped to the project sandbox (Feature 014, FR-001).
  getWorkspaceFiles(projectId: string): Promise<WorkspaceFileNode[]> {
    return this.request<WorkspaceFileNode[]>('GET', `/projects/${encodeURIComponent(projectId)}/workspace/files`);
  }

  // Decompose a spec file into proposed backlog items (Feature 014, FR-003/004).
  // filePath=null uses the project's confirmed outcome spec stored on the server (requires runId).
  // confirm=false → dry-run preview; confirm=true → create the tasks.
  decomposeSpec(projectId: string, filePath: string | null, confirm: boolean, runId?: string | null, ref?: string): Promise<DecomposeResponse> {
    return this.request<DecomposeResponse>('POST', `/projects/${encodeURIComponent(projectId)}/backlog/decompose`, { file_path: filePath, run_id: runId ?? null, confirm, ...(ref ? { ref } : {}) });
  }

  // Sandbox port-forward (017-preview): tunnel a sandbox pod port to the API server.
  startPortForward(runId: string, targetPort: number): Promise<PortForwardSessionDto> {
    return this.request<PortForwardSessionDto>('POST', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward`, { targetPort });
  }

  stopPortForward(runId: string, sessionId: string): Promise<{ session_id: string; stopped: boolean }> {
    return this.request<{ session_id: string; stopped: boolean }>('DELETE', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward/${encodeURIComponent(sessionId)}`);
  }

  async pingKeepalive(keepaliveUrl: string): Promise<void> {
    const headers = this.authHeaders();
    const response = await fetch(this.apiUrl(keepaliveUrl), { method: 'POST', headers, credentials: 'include' });
    const text = typeof response.text === 'function' ? await response.text() : '';
    if (!response.ok) throw this.createApiError(response.status, text);
  }

  async listPortForwards(runId: string): Promise<PortForwardSessionDto[]> {
    const response = await this.request<PortForwardSessionDto[] | { sessions?: PortForwardSessionDto[] }>('GET', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward`);
    return Array.isArray(response) ? response : (response.sessions ?? []);
  }

  // System runtime info — kubernetes context and pod name (Spec 006).
  getSystemRuntime(): Promise<RuntimeInfo> {
    return this.request<RuntimeInfo>('GET', '/system/runtime');
  }

  private async request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    const headers: Record<string, string> = {
      ...this.authHeaders(),
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const response = await fetch(this.apiUrl(path), {
      method,
      headers,
      credentials: 'include',
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal,
    });

    const text = typeof response.text === 'function' ? await response.text() : '';
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

  private apiUrl(pathOrUrl: string): string {
    if (/^[a-z][a-z\d+\-.]*:\/\//i.test(pathOrUrl)) return pathOrUrl;
    const path = pathOrUrl.startsWith('/') ? pathOrUrl : `/${pathOrUrl}`;
    const apiPath = path === '/api' || path.startsWith('/api/') ? path : `/api${path}`;
    return `${this.baseUrl}${apiPath}`;
  }
}
