import type { RetriableReviewErrorBody, RunDetail, ReviewRequest, ReviewResponse, SandboxPolicy, SubmitRunRequest, SubmitRunResponse, WorkspaceFileEntry, WorkspaceFileDiff, WorkspaceNode, CommitResponse, WorkspaceFileContent, RequestChangesResponse, Project, CreateProjectRequest, UpdateProjectProviderSettingsRequest, CreateProjectRunRequest, ProjectRunSummary, GitHubDeviceFlow, GitHubPollResult, GitHubAuthStatusResponse } from './types';

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

export class ScaffolderApiClient {
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

  listProjectRuns(projectId: string): Promise<ProjectRunSummary[]> {
    return this.request<ProjectRunSummary[]>('GET', `/api/projects/${encodeURIComponent(projectId)}/runs`);
  }

  // GitHub auth
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
