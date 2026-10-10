import { gatewayBaseUrl } from './config';
import { isProjectAuthorizationContextResponse } from './contracts';
import type {
  CopilotAuthorizationCallback,
  CopilotConnectionBegin,
  CopilotConnectionMutation,
  CopilotConnectionReceipt,
  CoordinatorDecisionOperationResponse,
  CoordinatorDecisionStateView,
  CoordinationTreeCommandResult,
  EffectiveRunSelection,
  KnowledgeRecordImportResult,
  KnowledgeProposalPromotionResult,
  KnowledgeRecordPage,
  KnowledgeRecordRevisionPage,
  KnowledgeRecordTransferBundle,
  KnowledgeRecordUpdateInput,
  KnowledgeRecordWriteResult,
  MarketplaceBrowsePage,
  MarketplaceBrowseRequest,
  MarketplaceSource,
  MarketplaceSourceInput,
  MarketplaceSourceUpdateInput,
  OwnerRunStatus,
  ProjectAuthorizationContextResponse,
  ProjectConfiguration,
  SkillAssignmentRequest,
  SkillContentCandidateRequest,
  SkillContentImportReceipt,
  SkillContentImportRequest,
  SkillContentPreview,
  ProjectSummary,
  RepoAppAuthorizationStart,
  RepoAppAuthorizationStatus,
  RepoAppAuthorizationTransaction,
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
    tenantSelector?: string | null,
  ): Promise<T> {
    if (!token) {
      throw new GatewayError(401, { code: 'unauthorized' }, 'Sign in through the Identity Broker to continue.');
    }
    const headers = new Headers(init.headers);
    headers.set('Authorization', `Bearer ${token}`);
    headers.set('Accept', 'application/json');
    if (tenantSelector !== undefined && tenantSelector !== null)
      headers.set('X-Agentweaver-Tenant', tenantSelector);
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

  async getAuthorizationContext(
    token: string,
    tenantSelector?: string | null,
  ): Promise<ProjectAuthorizationContextResponse> {
    const context = await this.request<unknown>(
      token,
      '/authorization/context',
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (!isProjectAuthorizationContextResponse(context))
      throw new GatewayError(
        502,
        { code: 'owner_contract_invalid' },
        'The Projects owner returned an invalid authorization-context contract.',
      );
    return context;
  }

  listProjects(token: string, tenantSelector?: string | null): Promise<ProjectSummary[]> {
    return this.request(token, '/projects', {}, this.baseUrl, tenantSelector);
  }

  createProject(token: string, name: string, tenantSelector?: string | null): Promise<ProjectSummary> {
    return this.request(
      token,
      '/projects',
      { method: 'POST', body: JSON.stringify({ name }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  getProject(token: string, projectId: string, tenantSelector?: string | null): Promise<ProjectSummary> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  getProjectConfiguration(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<VersionedProjectConfiguration> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}/configuration`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  updateProjectConfiguration(
    token: string,
    projectId: string,
    expectedRevision: number,
    configuration: ProjectConfiguration,
    tenantSelector?: string | null,
  ): Promise<VersionedProjectConfiguration> {
    return this.request(token, `/projects/${encodeSegments(projectId)}/configuration`, {
      method: 'PUT',
      body: JSON.stringify({ expectedRevision, configuration }),
    }, this.baseUrl, tenantSelector);
  }

  listMarketplaceSources(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<MarketplaceSource[]> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  createMarketplaceSource(
    token: string,
    projectId: string,
    request: MarketplaceSourceInput,
    tenantSelector?: string | null,
  ): Promise<MarketplaceSource> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  updateMarketplaceSource(
    token: string,
    projectId: string,
    sourceId: string,
    request: MarketplaceSourceUpdateInput,
    tenantSelector?: string | null,
  ): Promise<MarketplaceSource> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources', sourceId)}`,
      { method: 'PUT', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  removeMarketplaceSource(
    token: string,
    projectId: string,
    sourceId: string,
    expectedRevision: number,
    tenantSelector?: string | null,
  ): Promise<MarketplaceSource> {
    const query = new URLSearchParams({ expectedRevision: String(expectedRevision) });
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources', sourceId)}?${query}`,
      { method: 'DELETE' },
      this.baseUrl,
      tenantSelector,
    );
  }

  browseMarketplaceSource(
    token: string,
    projectId: string,
    sourceId: string,
    options: MarketplaceBrowseRequest,
    tenantSelector?: string | null,
  ): Promise<MarketplaceBrowsePage> {
    const query = new URLSearchParams({
      expectedSourceRevision: String(options.expectedSourceRevision),
    });
    if (options.query) query.set('query', options.query);
    if (options.page !== undefined) query.set('page', String(options.page));
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources', sourceId, 'browse')}?${query}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  previewSkillContent(
    token: string,
    candidate: SkillContentCandidateRequest,
    tenantSelector?: string | null,
  ): Promise<SkillContentPreview> {
    return this.request(
      token,
      '/skills/preview',
      { method: 'POST', body: JSON.stringify({ candidate }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  importSkillContent(
    token: string,
    projectId: string,
    request: SkillContentImportRequest,
    tenantSelector?: string | null,
  ): Promise<SkillContentImportReceipt> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skills', 'import')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  updateSkillAssignment(
    token: string,
    projectId: string,
    skillId: string,
    request: SkillAssignmentRequest,
    tenantSelector?: string | null,
  ): Promise<VersionedProjectConfiguration> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skills', skillId, 'assignment')}`,
      { method: 'PUT', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  beginCopilotConnection(
    token: string,
    scope: 'project' | 'platform',
    scopeId: string,
    tenantSelector?: string | null,
  ): Promise<CopilotConnectionBegin> {
    return this.request(
      token,
      '/api/connections/copilot-user/v1/begin',
      {
        method: 'POST',
        body: JSON.stringify({ scope, scopeId }),
        credentials: 'include',
      },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  completeCopilotConnection(
    token: string,
    callback: CopilotAuthorizationCallback,
    tenantSelector?: string | null,
  ): Promise<CopilotConnectionReceipt> {
    return this.request(
      token,
      '/api/connections/copilot-user/v1/complete',
      {
        method: 'POST',
        body: JSON.stringify(callback),
        credentials: 'include',
      },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  getCopilotConnection(
    token: string,
    connectionId: string,
    tenantSelector?: string | null,
  ): Promise<CopilotConnectionReceipt> {
    return this.request(
      token,
      `/api/connections/copilot-user/v1/${encodeURIComponent(connectionId)}`,
      {},
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  refreshCopilotConnection(
    token: string,
    request: CopilotConnectionMutation,
    tenantSelector?: string | null,
  ): Promise<CopilotConnectionReceipt> {
    return this.request(
      token,
      '/api/connections/copilot-user/v1/refresh',
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  revokeCopilotConnection(
    token: string,
    request: CopilotConnectionMutation,
    tenantSelector?: string | null,
  ): Promise<CopilotConnectionReceipt> {
    return this.request(
      token,
      '/api/connections/copilot-user/v1/revoke',
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
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

  pinSourceControlRepository(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector: string | null,
    selectionCode?: string,
  ): Promise<SourceControlRepositoryPinView> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'pin')}`,
      {
        method: 'POST',
        ...(selectionCode === undefined
          ? {}
          : { body: JSON.stringify({ selectionCode }) }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  getRunSelection(
    token: string,
    projectId: string,
    runId: string,
    tenantSelector?: string | null,
  ): Promise<EffectiveRunSelection> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'selection')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  getRunStatus(
    token: string,
    projectId: string,
    runId: string,
    tenantSelector?: string | null,
  ): Promise<OwnerRunStatus> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'status')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  getSessionTree(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector?: string | null,
  ): Promise<SessionTreeSnapshot> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'tree')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  getSessionStatus(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector?: string | null,
  ): Promise<SessionStatusSnapshot> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'status')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  getDecisions(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector?: string | null,
  ): Promise<CoordinatorDecisionStateView> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'decisions')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
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
    tenantSelector?: string | null,
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
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
  ): Promise<CoordinatorDecisionOperationResponse> {
    const action = approved ? 'approve' : 'reject';
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'decisions', 'gates', requestId, action)}`,
      {
        method: 'POST',
        body: JSON.stringify({ expectedStateVersion, idempotencyKey }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  detachSession(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    executionFence: number,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<CoordinationTreeCommandResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', sessionId, 'detach')}`,
      {
        method: 'POST',
        body: JSON.stringify({ executionFence, idempotencyKey }),
      },
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
  ): Promise<CoordinationTreeCommandResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'coordination', 'sessions', parentSessionId, 'children', childSessionId, 'archive')}`,
      {
        method: 'POST',
        body: JSON.stringify({ executionFence, idempotencyKey }),
      },
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
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
      this.baseUrl,
      tenantSelector,
    );
  }

  async replayEvents(
    token: string,
    projectId: string,
    runId: string,
    cursor?: string | null,
    limit = 100,
    tenantSelector?: string | null,
  ): Promise<SessionEventPage> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set('cursor', cursor);
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'events')}?${query.toString()}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  async *streamEvents(
    token: string,
    projectId: string,
    runId: string,
    cursor: string | null,
    signal: AbortSignal,
    tenantSelector?: string | null,
  ): AsyncGenerator<{ cursor: string; event: SessionEventEnvelope }> {
    const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : '';
    const headers = new Headers({
      Authorization: `Bearer ${token}`,
      Accept: 'text/event-stream',
    });
    if (tenantSelector !== undefined && tenantSelector !== null)
      headers.set('X-Agentweaver-Tenant', tenantSelector);
    const response = await this.fetcher(
      `${this.baseUrl}/projects/${encodeSegments(projectId, 'runs', runId, 'events', 'live')}${query}`,
      {
        headers,
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

  getRunUsage(
    token: string,
    projectId: string,
    runId: string,
    tenantSelector?: string | null,
  ): Promise<UsageRunTotals> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'usage')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  searchKnowledge(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    options: { query?: string; kind?: string; page?: number; pageSize?: number; includeInactive?: boolean } = {},
    tenantSelector?: string | null,
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
      {},
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify(record),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  updateKnowledgeRecord(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    recordId: string,
    revision: number,
    record: KnowledgeRecordUpdateInput,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', recordId)}`,
      {
        method: 'PUT',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision: revision, ...record }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  readKnowledgeRevisions(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    recordId: string,
    options: { page?: number; pageSize?: number } = {},
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordRevisionPage> {
    const query = new URLSearchParams();
    query.set('page', String(options.page ?? 1));
    query.set('pageSize', String(options.pageSize ?? 50));
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', recordId, 'revisions')}?${query.toString()}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  restoreKnowledgeRecord(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    recordId: string,
    expectedRevision: number,
    revision: number,
    reason: string | null,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', recordId, 'restore')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision, revision, reason }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  approveKnowledgeDecision(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    recordId: string,
    expectedRevision: number,
    reason: string | null,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', recordId, 'approve')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision, reason }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  exportKnowledgeRecords(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordTransferBundle> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', 'export')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  importKnowledgeRecords(
    token: string,
    projectId: string,
    runId: string,
    agentId: string,
    bundle: KnowledgeRecordTransferBundle,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordImportResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'records', 'import')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify(bundle),
      },
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
  ): Promise<KnowledgeProposalPromotionResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'proposals', proposalId, 'promote')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision }),
      },
      this.baseUrl,
      tenantSelector,
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
    tenantSelector?: string | null,
  ): Promise<KnowledgeRecordWriteResult> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'agents', agentId, 'proposals', proposalId, 'reject')}`,
      {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ expectedRevision }),
      },
      this.baseUrl,
      tenantSelector,
    );
  }
}

export const gatewayClient = new AgentweaverGatewayClient();
