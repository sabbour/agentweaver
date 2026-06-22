import type { RetriableReviewErrorBody, RunDetail, PersistedRunEvent, ReviewRequest, ReviewResponse, SandboxPolicy, SubmitRunRequest, SubmitRunResponse, WorkspaceFileEntry, WorkspaceFileDiff, WorkspaceNode, CommitResponse, WorkspaceFileContent, RequestChangesResponse, Project, CreateProjectRequest, UpdateProjectProviderSettingsRequest, CreateProjectRunRequest, CreateRunRequest, GitHubDeviceFlow, GitHubPollResult, GitHubAuthStatusResponse, GitHubRepo, TeamTemplateDto, CastProposalDto, CreateProposalRequest, AmendProposalRequest, ConfirmProposalRequest, TeamDto, TeamMemberDto, CharterDto, HistoryDto, AddMemberRequest, ReroleRequest, SyncStatusDto, SyncCommitRequest, SyncCommitResponseDto, RoleDto, ServerInfo, WorkflowRunDto, CreateProjectRunResponse, OutcomeSpec, StartOrchestrationResponse, SteerCoordinatorRequest, SteerCoordinatorResponse, WorkPlanResponse, CoordinatorChildResponse, GraphDescriptor, AssemblyReviewDecision, AnswerQuestionResponse, AutoApproveResponse, AutopilotResponse, BoardDto, BacklogTaskDto, BacklogSettingsDto, WorkflowStagesResponse, RetryRunResponse } from './types';

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
  private readonly apiKey: string;

  constructor(baseUrl: string, apiKey: string) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.apiKey = apiKey;
  }

  submitRun(req: SubmitRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', '/api/runs', req);
  }

  getRun(runId: string): Promise<RunDetail> {
    return this.request<RunDetail>('GET', `/api/runs/${encodeURIComponent(runId)}`);
  }

  retryRun(runId: string): Promise<RetryRunResponse> {
    return this.request<RetryRunResponse>('POST', `/api/runs/${encodeURIComponent(runId)}/retry`, {});
  }

  // Persisted run events (FR-022). Seeds the execution timeline for terminal/parked
  // runs whose live SSE stream is closed (e.g. a finished coordinator child). The
  // backend persists and replays the events here; 404 until the log exists.
  getRunEvents(runId: string): Promise<PersistedRunEvent[]> {
    return this.request<PersistedRunEvent[]>('GET', `/api/runs/${encodeURIComponent(runId)}/events`);
  }

  getSandboxPolicy(repositoryPath: string): Promise<SandboxPolicy> {
    const encoded = encodeURIComponent(repositoryPath);
    return this.request<SandboxPolicy>('GET', `/api/sandbox-policy?repository_path=${encoded}`);
  }

  getRunFiles(runId: string, filter?: string): Promise<WorkspaceFileEntry[]> {
    const query = filter ? `?filter=${encodeURIComponent(filter)}` : '';
    return this.request<WorkspaceFileEntry[]>('GET', `/api/runs/${encodeURIComponent(runId)}/files${query}`);
  }

  getRunFileDiff(runId: string, path: string): Promise<WorkspaceFileDiff> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileDiff>('GET', `/api/runs/${encodeURIComponent(runId)}/files/${encoded}`);
  }

  // Collective assembly artifacts for a coordinator run. The coordinator owns no worktree; these
  // diff the integration branch (agentweaver/integration/{id}) vs the originating branch so the
  // standard Changes/Files rail can review the assembled output. Returns [] before assembly runs.
  getAssemblyFiles(runId: string, filter?: string): Promise<WorkspaceFileEntry[]> {
    const query = filter ? `?filter=${encodeURIComponent(filter)}` : '';
    return this.request<WorkspaceFileEntry[]>('GET', `/api/runs/${encodeURIComponent(runId)}/assembly/files${query}`);
  }

  getAssemblyFileDiff(runId: string, path: string): Promise<WorkspaceFileDiff> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileDiff>('GET', `/api/runs/${encodeURIComponent(runId)}/assembly/files/${encoded}`);
  }

  getAssemblyWorkspace(runId: string): Promise<WorkspaceNode[]> {
    return this.request<WorkspaceNode[]>('GET', `/api/runs/${encodeURIComponent(runId)}/assembly/workspace`);
  }

  // Per-file CONTENT of the collective integration branch tip, for the review modal's Preview/source
  // tab. The coordinator owns no worktree, so the standard worktree-backed content endpoint 409s;
  // this reads the blob from agentweaver/integration/{id} instead.
  getAssemblyFileContent(runId: string, path: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileContent>('GET', `/api/runs/${encodeURIComponent(runId)}/assembly/content/${encoded}`);
  }

  getRunFileContent(runId: string, path: string): Promise<WorkspaceFileContent> {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return this.request<WorkspaceFileContent>('GET', `/api/runs/${encodeURIComponent(runId)}/files/${encoded}/content`);
  }

  getRunWorkspace(runId: string): Promise<WorkspaceNode[]> {
    return this.request<WorkspaceNode[]>('GET', `/api/runs/${encodeURIComponent(runId)}/workspace`);
  }

  commitRun(runId: string): Promise<CommitResponse> {
    return this.request<CommitResponse>('POST', `/api/runs/${encodeURIComponent(runId)}/commit`, {});
  }

  requestChanges(runId: string, comment: string): Promise<RequestChangesResponse> {
    return this.request<RequestChangesResponse>('POST', `/api/runs/${encodeURIComponent(runId)}/request-changes`, { comment });
  }

  updateSandboxPolicy(policy: Pick<SandboxPolicy, 'repository_path' | 'shell_enabled' | 'direct' | 'network_enabled'>): Promise<SandboxPolicy> {
    return this.request<SandboxPolicy>('PUT', '/api/sandbox-policy', policy);
  }

  // Projects
  listProjects(): Promise<Project[]> {
    return this.request<Project[]>('GET', '/api/projects');
  }

  getProject(projectId: string): Promise<Project> {
    return this.request<Project>('GET', `/api/projects/${encodeURIComponent(projectId)}`);
  }

  createProject(req: CreateProjectRequest): Promise<Project> {
    return this.request<Project>('POST', '/api/projects', req);
  }

  renameProject(projectId: string, name: string): Promise<void> {
    return this.request<void>('PATCH', `/api/projects/${encodeURIComponent(projectId)}`, { name });
  }

  updateProjectProviderSettings(projectId: string, req: UpdateProjectProviderSettingsRequest): Promise<void> {
    return this.request<void>('PUT', `/api/projects/${encodeURIComponent(projectId)}/provider-settings`, req);
  }

  relinkProject(projectId: string, workingDirectory: string): Promise<void> {
    return this.request<void>('POST', `/api/projects/${encodeURIComponent(projectId)}/relink`, { working_directory: workingDirectory });
  }

  deleteProject(projectId: string): Promise<void> {
    return this.request<void>('DELETE', `/api/projects/${encodeURIComponent(projectId)}?confirm=true`);
  }

  startProjectRun(projectId: string, req: CreateProjectRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', `/api/projects/${encodeURIComponent(projectId)}/runs`, req);
  }

  listProjectRuns(projectId: string): Promise<WorkflowRunDto[]> {
    return this.request<WorkflowRunDto[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/runs`);
  }

  createProjectRun(projectId: string, request: CreateRunRequest): Promise<CreateProjectRunResponse> {
    return this.request<CreateProjectRunResponse>('POST', `/api/projects/${encodeURIComponent(projectId)}/runs`, request);
  }

  getProjectRuns(projectId: string): Promise<WorkflowRunDto[]> {
    return this.request<WorkflowRunDto[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/runs`);
  }

  getWorkflowRun(projectId: string, workflowRunId: string): Promise<WorkflowRunDto> {
    return this.request<WorkflowRunDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(workflowRunId)}`);
  }

  deleteRun(runId: string): Promise<void> {
    return this.request<void>('DELETE', `/api/runs/${encodeURIComponent(runId)}`);
  }

  // GitHub auth
  getServerInfo(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/api/server/info');
  }

  startGitHubDeviceFlow(): Promise<GitHubDeviceFlow> {
    return this.request<GitHubDeviceFlow>('POST', '/api/auth/github/device', {});
  }

  pollGitHubAuth(): Promise<GitHubPollResult> {
    return this.request<GitHubPollResult>('POST', '/api/auth/github/poll', {});
  }

  getGitHubAuthStatus(): Promise<GitHubAuthStatusResponse> {
    return this.request<GitHubAuthStatusResponse>('GET', '/api/auth/github');
  }

  signOutGitHub(): Promise<void> {
    return this.request<void>('POST', '/api/auth/github/sign-out', {});
  }

  listGitHubRepos(): Promise<GitHubRepo[]> {
    return this.request<GitHubRepo[]>('GET', '/api/github/repos');
  }

  // Catalog
  getRoles(): Promise<RoleDto[]> {
    return this.request<RoleDto[]>('GET', '/api/catalog/roles');
  }

  // Casting
  getTemplates(): Promise<TeamTemplateDto[]> {
    return this.request<TeamTemplateDto[]>('GET', '/api/casting/templates');
  }

  getUniverses(projectId: string): Promise<{ universes: string[] }> {
    return this.request<{ universes: string[] }>('GET', `/api/projects/${encodeURIComponent(projectId)}/casting/universes`);
  }

  createProposal(projectId: string, req: CreateProposalRequest): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/casting/proposals`, req);
  }

  getProposal(projectId: string, proposalId: string): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  amendProposal(projectId: string, proposalId: string, req: AmendProposalRequest): Promise<CastProposalDto> {
    return this.request<CastProposalDto>('PATCH', `/api/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`, req);
  }

  confirmProposal(projectId: string, proposalId: string, req: ConfirmProposalRequest): Promise<void> {
    return this.request<void>('POST', `/api/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}/confirm`, req);
  }

  rejectProposal(projectId: string, proposalId: string): Promise<void> {
    return this.request<void>('DELETE', `/api/projects/${encodeURIComponent(projectId)}/casting/proposals/${encodeURIComponent(proposalId)}`);
  }

  // Team
  getTeam(projectId: string): Promise<TeamDto> {
    return this.request<TeamDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/team`);
  }

  getMemberCharter(projectId: string, memberName: string): Promise<CharterDto> {
    return this.request<CharterDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`);
  }

  updateMemberCharter(projectId: string, memberName: string, content: string): Promise<void> {
    return this.request<void>('PUT', `/api/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/charter`, { content });
  }

  addMember(projectId: string, req: AddMemberRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/team/members`, req);
  }

  removeMember(projectId: string, memberName: string): Promise<void> {
    return this.request<void>('DELETE', `/api/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`);
  }

  reroleMember(projectId: string, memberName: string, req: ReroleRequest): Promise<TeamMemberDto> {
    return this.request<TeamMemberDto>('PATCH', `/api/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}`, req);
  }

  getMemberHistory(projectId: string, memberName: string): Promise<HistoryDto> {
    return this.request<HistoryDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/team/members/${encodeURIComponent(memberName)}/history`);
  }

  getDecisions(projectId: string): Promise<import('./types').DecisionDto[]> {
    return this.request<import('./types').DecisionDto[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/decisions`);
  }

  getAgentMemory(projectId: string, agentName: string): Promise<import('./types').AgentMemoryDto[]> {
    return this.request<import('./types').AgentMemoryDto[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/agents/${encodeURIComponent(agentName)}/memory`);
  }

  getProjectMemory(projectId: string): Promise<import('./types').AgentMemoryDto[]> {
    return this.request<import('./types').AgentMemoryDto[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/memory`);
  }

  // Sync
  getSyncStatus(projectId: string): Promise<SyncStatusDto> {
    return this.request<SyncStatusDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/team/sync`);
  }

  commitSync(projectId: string, req: SyncCommitRequest): Promise<SyncCommitResponseDto> {
    return this.request<SyncCommitResponseDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/team/sync`, req);
  }

  // Orchestration (Feature 008 — Squad Coordinator Agent)
  startOrchestration(projectId: string, goal: string): Promise<StartOrchestrationResponse> {
    return this.request<StartOrchestrationResponse>('POST', `/api/projects/${encodeURIComponent(projectId)}/orchestrations`, { goal });
  }

  getOutcomeSpec(runId: string): Promise<OutcomeSpec> {
    return this.request<OutcomeSpec>('GET', `/api/runs/${encodeURIComponent(runId)}/outcome-spec`);
  }

  confirmOutcomeSpec(runId: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/api/runs/${encodeURIComponent(runId)}/outcome-spec/confirm`, {});
  }

  reviseOutcomeSpec(runId: string, feedback: string): Promise<OutcomeSpec | null> {
    return this.request<OutcomeSpec | null>('POST', `/api/runs/${encodeURIComponent(runId)}/outcome-spec/revise`, { feedback });
  }

  // Coordinator steering (Feature 008 Phase 2). The /steer endpoint is added by the
  // backend team in parallel; this codes against the agreed contract.
  steerCoordinator(coordinatorRunId: string, req: SteerCoordinatorRequest): Promise<SteerCoordinatorResponse> {
    return this.request<SteerCoordinatorResponse>('POST', `/api/runs/${encodeURIComponent(coordinatorRunId)}/steer`, req);
  }

  // Coordinator topology REST seed (Feature 008 Phase 2). The SSE topology snapshot is
  // emitted before the stream connects, so the page seeds nodes/edges from these on mount,
  // then applies SSE deltas on top (snapshot-race fix). 404 when the run has no plan yet.
  getWorkPlan(coordinatorRunId: string): Promise<WorkPlanResponse> {
    return this.request<WorkPlanResponse>('GET', `/api/runs/${encodeURIComponent(coordinatorRunId)}/work-plan`);
  }

  getCoordinatorChildren(coordinatorRunId: string): Promise<CoordinatorChildResponse[]> {
    return this.request<CoordinatorChildResponse[]>('GET', `/api/runs/${encodeURIComponent(coordinatorRunId)}/children`);
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
    return this.request<void>('POST', `/api/runs/${encodeURIComponent(coordinatorRunId)}/assembly/review`, body);
  }

  // Answer a worker's bubbled question (agent.question_asked). The answer must be POSTed against
  // the run that ASKED the question: for a coordinator child question/approval that means the
  // childRunId from the event payload, NOT the coordinator run id. 404 = no pending question,
  // 409 = run not InProgress.
  answerQuestion(runId: string, requestId: string, answer: string): Promise<AnswerQuestionResponse> {
    return this.request<AnswerQuestionResponse>(
      'POST',
      `/api/runs/${encodeURIComponent(runId)}/questions/${encodeURIComponent(requestId)}/answer`,
      { answer },
    );
  }

  // Live per-run option toggles. auto-approve cascades to a coordinator's children; autopilot is
  // coordinator-only. Both 404 (not found) / 403 (not owner) / 409 (run not active).
  setAutoApprove(runId: string, enabled: boolean): Promise<AutoApproveResponse> {
    return this.request<AutoApproveResponse>('POST', `/api/runs/${encodeURIComponent(runId)}/auto-approve`, { enabled });
  }

  setAutopilot(runId: string, enabled: boolean): Promise<AutopilotResponse> {
    return this.request<AutopilotResponse>('POST', `/api/runs/${encodeURIComponent(runId)}/autopilot`, { enabled });
  }

  // Dynamic graph descriptor (Feature 008 Phase 3). Returns null on 404 so the caller
  // can fall back to the hardcoded executor graph until the backend endpoint ships.
  async getRunGraph(runId: string): Promise<GraphDescriptor | null> {
    try {
      return await this.request<GraphDescriptor>('GET', `/api/runs/${encodeURIComponent(runId)}/graph`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  }

  // Backlog & Workflow Kanban board (Feature 009). Thin pass-throughs to the
  // snake_case backlog endpoints — all ordering/claim/validation logic lives server-side.
  getBoard(projectId: string, includeTerminalHistory = false): Promise<BoardDto> {
    const query = includeTerminalHistory ? '?include_terminal_history=true' : '';
    return this.request<BoardDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/board${query}`);
  }

  getWorkflowStages(projectId: string): Promise<WorkflowStagesResponse> {
    return this.request<WorkflowStagesResponse>('GET', `/api/projects/${encodeURIComponent(projectId)}/workflow-stages`);
  }

  captureBacklogTask(projectId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks`, body);
  }

  editBacklogTask(projectId: string, taskId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('PATCH', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`, body);
  }

  deleteBacklogTask(projectId: string, taskId: string): Promise<void> {
    return this.request<void>('DELETE', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}`);
  }

  moveTaskToReady(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/ready`, { target_index: targetIndex ?? null });
  }

  moveTaskToBacklog(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/backlog`, { target_index: targetIndex ?? null });
  }

  reorderBacklogTask(projectId: string, taskId: string, targetIndex: number): Promise<BacklogTaskDto> {
    return this.request<BacklogTaskDto>('POST', `/api/projects/${encodeURIComponent(projectId)}/backlog/tasks/${encodeURIComponent(taskId)}/reorder`, { target_index: targetIndex });
  }

  sendAllBacklogToReady(projectId: string): Promise<{ moved: number }> {
    return this.request<{ moved: number }>('POST', `/api/projects/${encodeURIComponent(projectId)}/backlog/ready-all`, {});
  }

  getBacklogSettings(projectId: string): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('GET', `/api/projects/${encodeURIComponent(projectId)}/backlog/settings`);
  }

  setBacklogSettings(projectId: string, settings: BacklogSettingsDto): Promise<BacklogSettingsDto> {
    return this.request<BacklogSettingsDto>('PUT', `/api/projects/${encodeURIComponent(projectId)}/backlog/settings`, settings);
  }

  async submitReview(runId: string, approved: boolean): Promise<ReviewResponse> {
    const body: ReviewRequest = { approved };
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.apiKey}`,
      'Content-Type': 'application/json',
    };
    const response = await fetch(
      `${this.baseUrl}/api/runs/${encodeURIComponent(runId)}/review`,
      { method: 'POST', headers, body: JSON.stringify(body) },
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

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.apiKey}`,
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    const text = await response.text();
    if (!response.ok) throw new ApiError(response.status, text);
    return (text ? JSON.parse(text) : null) as T;
  }
}
