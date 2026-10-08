import { gatewayBaseUrl } from './config';
import type {
  CoordinatorDecisionOperationResponse,
  CoordinatorDecisionStateView,
  CoordinationTreeCommandResult,
  EffectiveRunSelection,
  KnowledgeProposalPromotionResult,
  KnowledgeRecord,
  KnowledgeRecordPage,
  KnowledgeRecordWriteResult,
  OwnerRunStatus,
  ProjectConfiguration,
  ProjectSummary,
  RepoAppAuthorizationStart,
  RepoAppAuthorizationStatus,
  RepoAppAuthorizationTransaction,
  RepoAppInstallationStart,
  RepoAppRepositorySelectionCode,
  RepoAppRepositorySelectionList,
  SessionEventEnvelope,
  SessionEventPage,
  SessionStatusSnapshot,
  SessionTreeSnapshot,
  SourceControlRepositoryPinView,
  UsageRunTotals,
  VersionedProjectConfiguration,
} from './contracts';

export class GatewayError extends Error {
  readonly status: number;
  readonly problem: Record<string, unknown>;
  readonly code?: string;

  constructor(status: number, problem: Record<string, unknown>, message: string) {
    super(message);
    this.name = 'GatewayError';
    this.status = status;
    this.problem = problem;
    this.code = typeof problem.code === 'string' ? problem.code : undefined;
  }
}

function problemMessage(problem: Record<string, unknown>, status: number): string {
  const detail = typeof problem.detail === 'string' ? problem.detail : undefined;
  const title = typeof problem.title === 'string' ? problem.title : undefined;
  const code = typeof problem.code === 'string' ? problem.code : undefined;
  return detail ?? title ?? code ?? `Gateway request failed with HTTP ${status}.`;
}

function encodeSegments(...segments: string[]): string {
  return segments.map((part) => encodeURIComponent(part)).join('/');
}

export class AgentweaverGatewayClient {
  private readonly baseUrl: string;
  private readonly gatewayRootUrl: string;
  private readonly fetcher: typeof fetch;

  constructor(
    baseUrl = gatewayBaseUrl,
    fetcher: typeof fetch = fetch,
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.gatewayRootUrl = this.baseUrl.endsWith('/api/v1')
      ? this.baseUrl.slice(0, -'/api/v1'.length)
      : this.baseUrl;
    this.fetcher = fetcher;
  }

  private async request<T>(
    token: string,
    path: string,
    init: RequestInit = {},
    baseUrl = this.baseUrl,
  ): Promise<T> {
    if (!token) {
      throw new GatewayError(401, { code: 'unauthorized' }, 'Sign in through the Identity Broker to continue.');
    }
    const headers = new Headers(init.headers);
    headers.set('Authorization', `Bearer ${token}`);
    headers.set('Accept', 'application/json');
    if (init.body !== undefined) headers.set('Content-Type', 'application/json');

    const response = await this.fetcher.call(globalThis, `${baseUrl}${path}`, {
      ...init,
      headers,
      credentials: init.credentials ?? 'omit',
      cache: 'no-store',
    });
    if (!response.ok) {
      let body: Record<string, unknown> = {};
      const raw = await response.text();
      if (raw) {
        try {
          const parsed: unknown = JSON.parse(raw);
          if (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
            body = parsed as Record<string, unknown>;
          else body = { detail: raw };
        } catch {
          body = { detail: raw };
        }
      }
      throw new GatewayError(response.status, body, problemMessage(body, response.status));
    }
    if (response.status === 204) return undefined as T;
    return await response.json() as T;
  }

  listProjects(token: string): Promise<ProjectSummary[]> {
    return this.request(token, '/projects');
  }

  createProject(token: string, name: string): Promise<ProjectSummary> {
    return this.request(token, '/projects', { method: 'POST', body: JSON.stringify({ name }) });
  }

  getProject(token: string, projectId: string): Promise<ProjectSummary> {
    return this.request(token, `/projects/${encodeSegments(projectId)}`);
  }

  getProjectConfiguration(token: string, projectId: string): Promise<VersionedProjectConfiguration> {
    return this.request(token, `/projects/${encodeSegments(projectId)}/configuration`);
  }

  updateProjectConfiguration(
    token: string,
    projectId: string,
    expectedRevision: number,
    configuration: ProjectConfiguration,
  ): Promise<VersionedProjectConfiguration> {
    return this.request(token, `/projects/${encodeSegments(projectId)}/configuration`, {
      method: 'PUT',
      body: JSON.stringify({ expectedRevision, configuration }),
    });
  }

  beginRepoAppAuthorization(
    token: string,
    returnRouteKey: 'projects' | 'settings' = 'projects',
  ): Promise<RepoAppAuthorizationStart> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorizations',
      {
        method: 'POST',
        body: JSON.stringify({ returnRouteKey }),
        credentials: 'include',
      },
      this.gatewayRootUrl,
    );
  }

  getRepoAppAuthorizationStatus(token: string): Promise<RepoAppAuthorizationStatus> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization/status',
      {},
      this.gatewayRootUrl,
    );
  }

  getRepoAppAuthorization(
    token: string,
    transactionId: string,
  ): Promise<RepoAppAuthorizationTransaction> {
    return this.request(
      token,
      `/api/auth/github/repo-app/authorizations/${encodeSegments(transactionId)}`,
      {},
      this.gatewayRootUrl,
    );
  }

  refreshRepoAppAuthorization(token: string): Promise<void> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization/refresh',
      { method: 'POST' },
      this.gatewayRootUrl,
    );
  }

  disconnectRepoAppAuthorization(token: string): Promise<void> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization',
      { method: 'DELETE' },
      this.gatewayRootUrl,
    );
  }

  listRepoAppRepositorySelections(token: string): Promise<RepoAppRepositorySelectionList> {
    return this.request(
      token,
      '/api/github/repository-selections',
      {},
      this.gatewayRootUrl,
    );
  }

  issueRepoAppRepositorySelection(
    token: string,
    fullName: string,
  ): Promise<RepoAppRepositorySelectionCode> {
    return this.request(
      token,
      '/api/github/repository-selections',
      { method: 'POST', body: JSON.stringify({ fullName }) },
      this.gatewayRootUrl,
    );
  }

  beginProjectGitHubAppInstallationAuthorization(
    token: string,
    projectId: string,
    runId: string,
    tenantId: string,
  ): Promise<RepoAppInstallationStart> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'github-app-installations', 'authorizations')}`,
      {
        method: 'POST',
        headers: { 'X-Agentweaver-Tenant': tenantId },
        credentials: 'include',
      },
    );
  }

  pinSourceControlRepository(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantId: string,
    selectionCode?: string,
  ): Promise<SourceControlRepositoryPinView> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'pin')}`,
      {
        method: 'POST',
        headers: { 'X-Agentweaver-Tenant': tenantId },
        ...(selectionCode === undefined
          ? {}
          : { body: JSON.stringify({ selectionCode }) }),
      },
    );
  }

  getRunSelection(token: string, projectId: string, runId: string): Promise<EffectiveRunSelection> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'selection')}`);
  }

  getRunStatus(token: string, projectId: string, runId: string): Promise<OwnerRunStatus> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'status')}`);
  }

  getSessionTree(token: string, projectId: string, runId: string, sessionId: string): Promise<SessionTreeSnapshot> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'tree')}`);
  }

  getSessionStatus(token: string, projectId: string, runId: string, sessionId: string): Promise<SessionStatusSnapshot> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'status')}`);
  }

  getDecisions(token: string, projectId: string, runId: string, sessionId: string): Promise<CoordinatorDecisionStateView> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'decisions')}`);
  }

  answerGate(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    requestId: string,
    expectedStateVersion: number,
    answer: { choiceId?: string; freeformAnswer?: string },
    idempotencyKey: string,
  ): Promise<CoordinatorDecisionOperationResponse> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'decisions', 'gates', requestId, 'answer')}`,
      {
        method: 'POST',
        body: JSON.stringify({
          expectedStateVersion,
          idempotencyKey,
          choiceId: answer.choiceId ?? null,
          freeformAnswer: answer.freeformAnswer ?? null,
        }),
      },
    );
  }

  resolveGate(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    requestId: string,
    expectedStateVersion: number,
    approved: boolean,
    idempotencyKey: string,
  ): Promise<CoordinatorDecisionOperationResponse> {
    const action = approved ? 'approve' : 'reject';
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'decisions', 'gates', requestId, action)}`,
      {
        method: 'POST',
        body: JSON.stringify({ expectedStateVersion, idempotencyKey }),
      },
    );
  }

  detachSession(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    executionFence: number,
    idempotencyKey: string,
  ): Promise<CoordinationTreeCommandResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'detach')}`,
      {
        method: 'POST',
        body: JSON.stringify({ executionFence, idempotencyKey }),
      },
    );
  }

  archiveChild(
    token: string,
    projectId: string,
    runId: string,
    parentSessionId: string,
    childSessionId: string,
    executionFence: number,
    idempotencyKey: string,
  ): Promise<CoordinationTreeCommandResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', parentSessionId, 'children', childSessionId, 'archive')}`,
      {
        method: 'POST',
        body: JSON.stringify({ executionFence, idempotencyKey }),
      },
    );
  }

  sendMessage(
    token: string,
    projectId: string,
    runId: string,
    senderSessionId: string,
    recipientSessionId: string,
    text: string,
    idempotencyKey: string,
    mode: 'immediate' | 'enqueue' = 'immediate',
  ): Promise<{ ownerMessageId: string; recipientSessionId: string; status: string; requestId?: string | null }> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', senderSessionId, 'messages')}`,
      {
        method: 'POST',
        body: JSON.stringify({
          recipientSessionId,
          idempotencyKey,
          deliveryMode: mode,
          purpose: 'progress',
          kind: 'text',
          payload: { text },
        }),
      },
    );
  }

  async replayEvents(
    token: string,
    projectId: string,
    runId: string,
    cursor?: string | null,
    limit = 100,
  ): Promise<SessionEventPage> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set('cursor', cursor);
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'events')}?${query.toString()}`,
    );
  }

  async *streamEvents(
    token: string,
    projectId: string,
    runId: string,
    cursor: string | null,
    signal: AbortSignal,
  ): AsyncGenerator<{ cursor: string; event: SessionEventEnvelope }> {
    const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : '';
    const response = await this.fetcher(
      `${this.baseUrl}/projects/${encodeSegments(projectId, 'runs', runId, 'events', 'live')}${query}`,
      {
        headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' },
        credentials: 'omit',
        cache: 'no-store',
        signal,
      },
    );
    if (!response.ok) {
      let body: Record<string, unknown> = {};
      try {
        const parsed: unknown = await response.json();
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
          body = parsed as Record<string, unknown>;
      } catch {
        // Keep the structured HTTP status even when a proxy returned a non-JSON body.
      }
      throw new GatewayError(response.status, body, problemMessage(body, response.status));
    }
    if (!response.body) throw new Error('The Gateway SSE response has no readable body.');

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let eventId = '';
    let data: string[] = [];
    const parseFrame = (): { cursor: string; event: SessionEventEnvelope } | undefined => {
      const cursorValue = eventId;
      const dataValue = data.join('\n');
      eventId = '';
      data = [];
      if (!cursorValue || !dataValue) return undefined;
      const event = JSON.parse(dataValue) as SessionEventEnvelope;
      return { cursor: cursorValue, event };
    };

    try {
      while (true) {
        const { done, value } = await reader.read();
        buffer += decoder.decode(value, { stream: !done });
        let separator: RegExpExecArray | null;
        while ((separator = /\r\n\r\n|\n\n|\r\r/.exec(buffer)) !== null) {
          const frame = buffer.slice(0, separator.index).replace(/\r/g, '');
          buffer = buffer.slice(separator.index + separator[0].length);
          for (const line of frame.split('\n')) {
            if (line.startsWith('id:')) eventId = line.slice(3).trimStart();
            else if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
          }
          const parsed = parseFrame();
          if (parsed) yield parsed;
        }
        if (done) break;
      }
    } finally {
      await reader.cancel().catch(() => undefined);
    }
  }

  getRunUsage(token: string, projectId: string, runId: string): Promise<UsageRunTotals> {
    return this.request(token, `/projects/${encodeSegments(projectId, 'runs', runId, 'usage')}`);
  }

  searchKnowledge(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    options: { query?: string; kind?: string; page?: number; pageSize?: number; includeInactive?: boolean } = {},
  ): Promise<KnowledgeRecordPage> {
    const query = new URLSearchParams();
    if (options.query) query.set('q', options.query);
    if (options.kind) query.set('kind', options.kind);
    query.set('page', String(options.page ?? 1));
    query.set('pageSize', String(options.pageSize ?? 50));
    query.set('includeInactive', String(options.includeInactive ?? true));
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records')}?${query.toString()}`,
    );
  }

  createKnowledgeRecord(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    record: {
      kind: string;
      type: string;
      title?: string | null;
      content: string;
      rationale?: string | null;
      importance: string;
      tags: string[];
    },
    idempotencyKey: string,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify(record),
      },
    );
  }

  updateKnowledgeRecord(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    recordId: string,
    revision: number,
    record: Pick<KnowledgeRecord, 'type' | 'title' | 'content' | 'rationale' | 'importance' | 'tags' | 'state'>,
    idempotencyKey: string,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', recordId)}`,
      {
        method: 'PUT',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision: revision, ...record }),
      },
    );
  }

  promoteKnowledgeProposal(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    proposalId: string,
    expectedRevision: number,
    idempotencyKey: string,
  ): Promise<KnowledgeProposalPromotionResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'proposals', proposalId, 'promote')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision }),
      },
    );
  }

  rejectKnowledgeProposal(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    proposalId: string,
    expectedRevision: number,
    idempotencyKey: string,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'proposals', proposalId, 'reject')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision }),
      },
    );
  }
}

export const gatewayClient = new AgentweaverGatewayClient();
