import type { RetriableReviewErrorBody, RunDetail, PersistedRunEvent, ReviewRequest, ReviewResponse, SandboxPolicy, SubmitRunRequest, SubmitRunResponse, WorkspaceFileEntry, WorkspaceFileDiff, WorkspaceNode, CommitResponse, WorkspaceFileContent, RequestChangesResponse, WorkspaceRefsResponse, Project, CreateProjectRequest, Blueprint, ListBlueprintsResponse, GenerateBlueprintResponse, UpdateProjectProviderSettingsRequest, CreateProjectRunRequest, CreateRunRequest, GitHubDeviceFlow, GitHubPollResult, GitHubAuthStatusResponse, GitHubRepo, GitHubAccount, TeamTemplateDto, CastProposalDto, CreateProposalRequest, AmendProposalRequest, ConfirmProposalRequest, TeamDto, TeamMemberDto, CharterDto, HistoryDto, AddMemberRequest, ReroleRequest, SyncStatusDto, SyncCommitRequest, SyncCommitResponseDto, RoleDto, ServerInfo, WorkflowRunDto, CreateProjectRunResponse, OutcomeSpec, StartOrchestrationResponse, SteerCoordinatorRequest, SteerCoordinatorResponse, WorkPlanResponse, CoordinatorChildResponse, GraphDescriptor, AssemblyReviewDecision, AnswerQuestionResponse, AutoApproveResponse, AutopilotResponse, BoardDto, BacklogTaskDto, BacklogSettingsDto, WorkflowStagesResponse, RetryRunResponse, SystemDiagnosticsDto, HeartbeatStatusDto, WorkspaceFileNode, DecomposeResponse, PortForwardSessionDto, RuntimeInfo, DetailedSystemDiagnosticsDto, ClusterDiagnosticsDto } from './types';
import { getSessionToken } from '../config';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
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

  constructor(status: number, body: string) {
    super(`API error ${status}: ${body}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
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

  submitRun(req: SubmitRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', '/runs', req);
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
  listProjects(): Promise<Project[]> {
    return this.request<Project[]>('GET', '/projects');
  }

  getProject(projectId: string): Promise<Project> {
    return this.request<Project>('GET', `/projects/${encodeURIComponent(projectId)}`);
  }

  createProject(req: CreateProjectRequest): Promise<Project> {
    return this.request<Project>('POST', '/projects', req);
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

  renameProject(projectId: string, name: string): Promise<void> {
    return this.request<void>('PATCH', `/projects/${encodeURIComponent(projectId)}`, { name });
  }

  updateProjectProviderSettings(projectId: string, req: UpdateProjectProviderSettingsRequest): Promise<void> {
    return this.request<void>('PUT', `/projects/${encodeURIComponent(projectId)}/provider-settings`, req);
  }

  relinkProject(projectId: string, workingDirectory: string): Promise<void> {
    return this.request<void>('POST', `/projects/${encodeURIComponent(projectId)}/relink`, { working_directory: workingDirectory });
  }

  deleteProject(projectId: string): Promise<void> {
    return this.request<void>('DELETE', `/projects/${encodeURIComponent(projectId)}?confirm=true`);
  }

  startProjectRun(projectId: string, req: CreateProjectRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', `/projects/${encodeURIComponent(projectId)}/runs`, req);
  }

  listProjectRuns(projectId: string): Promise<WorkflowRunDto[]> {
    return this.request<WorkflowRunDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/runs`);
  }

  createProjectRun(projectId: string, request: CreateRunRequest): Promise<CreateProjectRunResponse> {
    return this.request<CreateProjectRunResponse>('POST', `/projects/${encodeURIComponent(projectId)}/runs`, request);
  }

  getProjectRuns(projectId: string, options?: {
    agentName?: string;
    terminalOnly?: boolean;
    includeChildren?: boolean;
    limit?: number;
  }): Promise<WorkflowRunDto[]> {
    const query = new URLSearchParams();
    if (options?.agentName) query.set('agent', options.agentName);
    if (options?.terminalOnly) query.set('terminal_only', 'true');
    if (options?.includeChildren) query.set('include_children', 'true');
    if (options?.limit != null) query.set('limit', String(options.limit));
    const queryString = query.toString();
    const suffix = queryString ? `?${queryString}` : '';
    return this.request<WorkflowRunDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/runs${suffix}`);
  }

  getWorkflowRun(projectId: string, workflowRunId: string): Promise<WorkflowRunDto> {
    return this.request<WorkflowRunDto>('GET', `/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(workflowRunId)}`);
  }

  deleteRun(runId: string): Promise<void> {
    return this.request<void>('DELETE', `/runs/${encodeURIComponent(runId)}`);
  }

  archiveRun(runId: string): Promise<void> {
    return this.request<void>('POST', `/runs/${encodeURIComponent(runId)}/archive`, {});
  }

  // GitHub auth
  getServerInfo(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/server/info');
  }

  startGitHubDeviceFlow(): Promise<GitHubDeviceFlow> {
    return this.request<GitHubDeviceFlow>('POST', '/auth/github/device', {});
  }

  pollGitHubAuth(): Promise<GitHubPollResult> {
    return this.request<GitHubPollResult>('POST', '/auth/github/poll', {});
  }

  getGitHubAuthStatus(): Promise<GitHubAuthStatusResponse> {
    return this.request<GitHubAuthStatusResponse>('GET', '/auth/github');
  }

  signOutGitHub(): Promise<void> {
    return this.request<void>('POST', '/auth/github/sign-out', {});
  }

  listGitHubAccounts(): Promise<GitHubAccount[]> {
    return this.request<GitHubAccount[]>('GET', '/github/accounts');
  }

  listGitHubRepos(account?: string): Promise<GitHubRepo[]> {
    const path = account ? `/github/repos?account=${encodeURIComponent(account)}` : '/github/repos';
    return this.request<GitHubRepo[]>('GET', path);
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

  getDecisions(projectId: string): Promise<import('./types').DecisionDto[]> {
    return this.request<import('./types').DecisionDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/decisions`);
  }

  getDecisionsInbox(projectId: string): Promise<import('./types').DecisionInboxEntryDto[]> {
    return this.request<import('./types').DecisionInboxEntryDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/decisions/inbox`);
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

  getAgentMemory(projectId: string, agentName: string): Promise<import('./types').AgentMemoryDto[]> {
    return this.request<import('./types').AgentMemoryDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory`);
  }

  getProjectMemory(projectId: string): Promise<import('./types').AgentMemoryDto[]> {
    return this.request<import('./types').AgentMemoryDto[]>('GET', `/projects/${encodeURIComponent(projectId)}/memory`);
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

  // Sync
  getSyncStatus(projectId: string): Promise<SyncStatusDto> {
    return this.request<SyncStatusDto>('GET', `/projects/${encodeURIComponent(projectId)}/team/sync`);
  }

  commitSync(projectId: string, req: SyncCommitRequest): Promise<SyncCommitResponseDto> {
    return this.request<SyncCommitResponseDto>('POST', `/projects/${encodeURIComponent(projectId)}/team/sync`, req);
  }

  // Orchestration (Feature 008 — Squad Coordinator Agent)
  startOrchestration(projectId: string, goal: string, workflowOverrideId?: string | null): Promise<StartOrchestrationResponse> {
    const body: Record<string, unknown> = { goal };
    if (workflowOverrideId) body.workflow_override_id = workflowOverrideId;
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

  confirmOutcomeSpec(runId: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/runs/${encodeURIComponent(runId)}/outcome-spec/confirm`, {});
  }

  reviseOutcomeSpec(runId: string, feedback: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/runs/${encodeURIComponent(runId)}/outcome-spec/revise`, { feedback });
  }

  // Coordinator steering (Feature 008 Phase 2). The /steer endpoint is added by the
  // backend team in parallel; this codes against the agreed contract.
  steerCoordinator(coordinatorRunId: string, req: SteerCoordinatorRequest): Promise<SteerCoordinatorResponse> {
    return this.request<SteerCoordinatorResponse>('POST', `/runs/${encodeURIComponent(coordinatorRunId)}/steer`, req);
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
    const body = {
      approved: decision === 'approve',
      request_changes: decision === 'request_changes',
      feedback: comment,
    };
    return this.request<void>('POST', `/runs/${encodeURIComponent(coordinatorRunId)}/assembly/review`, body);
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
      `${this.baseUrl}/runs/${encodeURIComponent(runId)}/review`,
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
      const res = await fetch(`${this.baseUrl}/health`, { method: 'GET', headers, credentials: 'include' });
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
  listReviewPolicies(projectId: string): Promise<import('./types').ReviewPolicyListResponse> {
    return this.request<import('./types').ReviewPolicyListResponse>('GET', `/projects/${encodeURIComponent(projectId)}/review-policies`);
  }

  getReviewPolicy(projectId: string, policyName: string): Promise<import('./types').ReviewPolicyDetailDto> {
    return this.request<import('./types').ReviewPolicyDetailDto>('GET', `/projects/${encodeURIComponent(projectId)}/review-policies/${encodeURIComponent(policyName)}`);
  }

  setActiveReviewPolicy(projectId: string, name: string | null): Promise<import('./types').ReviewPolicyListResponse> {
    return this.request<import('./types').ReviewPolicyListResponse>('PUT', `/projects/${encodeURIComponent(projectId)}/review-policies/active`, { name });
  }

  syncReviewPolicies(projectId: string): Promise<import('./types').ReviewPolicyListResponse> {
    return this.request<import('./types').ReviewPolicyListResponse>('POST', `/projects/${encodeURIComponent(projectId)}/review-policies/sync`, {});
  }

  // Metrics (web IA reorg) — per-project dashboard + global "Now" overview.
  getProjectDashboard(projectId: string): Promise<import('./types').ProjectDashboardDto> {
    return this.request<import('./types').ProjectDashboardDto>('GET', `/projects/${encodeURIComponent(projectId)}/dashboard`);
  }

  getProjectMetrics(projectId: string, from?: string, to?: string): Promise<import('./types').ProjectMetricsDto> {
    const query = new URLSearchParams();
    if (from) query.set('from', from);
    if (to) query.set('to', to);
    const qs = query.toString();
    return this.request<import('./types').ProjectMetricsDto>('GET', `/projects/${encodeURIComponent(projectId)}/metrics${qs ? `?${qs}` : ''}`);
  }

  getOverview(): Promise<import('./types').OverviewDto> {
    return this.request<import('./types').OverviewDto>('GET', '/overview');
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
    await fetch(keepaliveUrl, { method: 'POST', headers, credentials: 'include' });
  }

  listPortForwards(runId: string): Promise<PortForwardSessionDto[]> {
    return this.request<PortForwardSessionDto[]>('GET', `/runs/${encodeURIComponent(runId)}/sandbox/port-forward`);
  }

  // System runtime info — kubernetes context and pod name (Spec 006).
  getSystemRuntime(): Promise<RuntimeInfo> {
    return this.request<RuntimeInfo>('GET', '/system/runtime');
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      ...this.authHeaders(),
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      credentials: 'include',
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    const text = await response.text();
    if (!response.ok) throw new ApiError(response.status, text);
    return (text ? JSON.parse(text) : null) as T;
  }
}
