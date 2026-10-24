import { gatewayBaseUrl } from './config';
import { isProjectAuthorizationContextResponse, isRemoteMcpConnectionDirectoryPage } from './contracts';
import type {
  AgentSuiteAgentPolicy,
  AgentSuiteImport,
  AgentSuiteImportRequest,
  AgentSuiteImportResult,
  CancelProjectRunResponse,
  ProjectRunMessageRequest,
  ProjectRunMessageReceipt,
  CopilotAuthorizationCallback,
  CreateProjectRunRequest,
  CreateProjectRunResponse,
  CreateManualProjectCastingProposalRequest,
  CreateScenarioProjectCastingProposalRequest,
  CreateProjectCastingProposalRequest,
  ImportProjectCastingTransferRequest,
  PlatformComponentReport,
  ProjectCastingUniverseList,
  ProjectCastingProposal,
  ProjectCastingRoleDefinition,
  ProjectCastingTemplateDefinition,
  ProjectCastingTransfer,
  UpdateProjectCastingProposalRequest,
  BacklogGraph,
  CaptureBacklogTaskRequest,
  EditBacklogTaskContentRequest,
  SetBacklogTaskStateRequest,
  BacklogTaskRevisionRequest,
  BacklogGraphRevisionRequest,
  ClaimBacklogTaskRequest,
  BacklogTaskClaimReceipt,
  ProjectIssuePage,
  ProjectIssueStateFilter,
  ProjectMetrics,
  ProjectRunPage,
  ProjectRunStateFilter,
  ProjectUsage,
  StartProjectRunResponse,
  UserNotificationPage,
  CopilotConnectionBegin,
  CopilotConnectionMutation,
  CopilotConnectionReceipt,
  AddChiefOfStaffPriorityLinkRequest,
  BindChiefOfStaffConversationRequest,
  ChiefOfStaffConversation,
  ChiefOfStaffConversationPage,
  ChiefOfStaffPriority,
  ChiefOfStaffPriorityList,
  CoordinatorDecisionOperationResponse,
  CreateChiefOfStaffPriorityRequest,
  ReorderChiefOfStaffPrioritiesRequest,
  UpdateChiefOfStaffPriorityRequest,
  CoordinatorDecisionStateView,
  CoordinationTreeCommandResult,
  EffectiveRunSelection,
  KnowledgeRecordImportResult,
  KnowledgeProposalPromotionResult,
  KnowledgeRecordPage,
  ProjectKnowledgeRecordPage,
  ProjectAgentHistoryResponse,
  KnowledgeRecordRevisionPage,
  KnowledgeRecordTransferBundle,
  KnowledgeRecordUpdateInput,
  KnowledgeRecordWriteResult,
  MarketplaceBrowsePage,
  MarketplaceBrowseRequest,
  MarketplaceSkillContentImportRequest,
  MarketplaceSkillContentPreviewRequest,
  MarketplaceSource,
  MarketplaceSourceInput,
  MarketplaceSourceUpdateInput,
  OwnerRunStatus,
  ProjectAuthorizationContextResponse,
  ProjectConfiguration,
  ProjectConfigurationChoices,
  HostedModelReasoningCatalogV1,
  ProjectRole,
  ProjectRoleAssignmentList,
  ProjectRoleAssignmentView,
  ProjectPullRequestPage,
  ProjectPullRequestStateFilter,
  SkillAssignmentRequest,
  SkillContentCandidateRequest,
  SkillContentInventoryPage,
  SkillContentInventoryQuery,
  SkillContentImportReceipt,
  SkillContentImportRequest,
  SkillContentPackageResult,
  SkillContentPreview,
  SkillMarketplaceDefinition,
  SkillPackageRequest,
  ProjectSummary,
  RemoteMcpConnectionDirectoryPage,
  RemoteMcpOAuthCallbackRequest,
  RemoteMcpOAuthConsentPreparation,
  RemoteMcpOAuthConsentRequest,
  RemoteMcpOAuthConnectionRegistration,
  RemoteMcpOAuthDisconnectRequest,
  RemoteMcpOAuthManagementStatus,
  RemoteMcpOAuthRefreshRequest,
  RepoAppAuthorizationStart,
  RepoAppAuthorizationStatus,
  RepoAppAuthorizationTransaction,
  RepoAppRepositorySelectionCode,
  RepoAppRepositorySelectionList,
  SessionEventEnvelope,
  SessionEventPage,
  SessionStatusSnapshot,
  SessionTurnContent,
  SessionTreeSnapshot,
  SourceControlOutputCaptureDiff,
  SourceControlOutputCaptureDetail,
  SourceControlOutputFileContent,
  SourceControlOutputCapturePage,
  SourceControlRepositoryPinView,
  UsageRunTotals,
  VersionedProjectConfiguration,
} from './contracts';

/** HTTP error returned by a Gateway request, including the parsed problem payload. */
export class GatewayError extends Error {
  readonly status: number;
  readonly problem: Record<string, unknown>;
  readonly code?: string;

  constructor(status: number, problem: Record<string, unknown>, message: string) {
    super(message);
    this.name = 'GatewayError';
    this.status = status;
    this.problem = problem;
    // Problem details use `code`; Chief of Staff and run routes return `{ error: code }`.
    this.code = typeof problem.code === 'string'
      ? problem.code
      : typeof problem.error === 'string' ? problem.error : undefined;
  }
}

/** Selects the most useful safe message from a Gateway problem response. */
function problemMessage(problem: Record<string, unknown>, status: number): string {
  const detail = typeof problem.detail === 'string' ? problem.detail : undefined;
  const title = typeof problem.title === 'string' ? problem.title : undefined;
  const code = typeof problem.code === 'string' ? problem.code : undefined;
  // Backlog writes reject with `{ issues: [{ code, path, message }] }` instead of a
  // problem document, so fall back to the first issue message when no detail exists.
  const issues = Array.isArray(problem.issues) ? problem.issues : [];
  const firstIssue = issues.find((issue): issue is { message: string } =>
    !!issue && typeof issue === 'object' && typeof (issue as { message?: unknown }).message === 'string');
  return detail ?? title ?? code ?? firstIssue?.message ?? `The request failed with HTTP ${status}.`;
}

/** Encodes each path segment independently so identifiers cannot alter route structure. */
export function encodeSegments(...segments: string[]): string {
  return segments.map((part) => encodeURIComponent(part)).join('/');
}

/**
 * Projects cannot override platform provider components, so writes send empty
 * provider override arrays. The service keeps any legacy stored values, which
 * run selection ignores.
 */
export function withoutProviderOverrides(configuration: ProjectConfiguration): ProjectConfiguration {
  return { ...configuration, providerOverrides: [], orderedProviderOverrides: [] };
}

/**
 * Sends one JSON request to `url` with a Broker token and preserves structured
 * error details in {@link GatewayError}. Requests omit ambient credentials, skip
 * the cache, and send the tenant selector only when one is given.
 */
export async function sendGatewayRequest<T>(
  fetcher: typeof fetch,
  token: string,
  url: string,
  init: RequestInit = {},
  tenantSelector?: string | null,
  readBody?: (response: Response) => Promise<T>,
): Promise<T> {
  if (!token) {
    // Fail before the network call. A request with no token would only return 401 anyway.
    throw new GatewayError(401, { code: 'unauthorized' }, 'Sign in to continue.');
  }
  const headers = new Headers(init.headers);
  headers.set('Authorization', `Bearer ${token}`);
  headers.set('Accept', 'application/json');
  if (tenantSelector !== undefined && tenantSelector !== null)
    headers.set('X-Agentweaver-Tenant', tenantSelector);
  if (init.body !== undefined) headers.set('Content-Type', 'application/json');

  // Call fetch on globalThis: browsers throw "Illegal invocation" for window.fetch on
  // another receiver. Cookies are omitted because auth is the bearer token only, and
  // no-store keeps private responses out of the browser's HTTP cache.
  const response = await fetcher.call(globalThis, url, {
    ...init,
    headers,
    credentials: init.credentials ?? 'omit',
    cache: 'no-store',
  });
  if (!response.ok) {
    let body: Record<string, unknown> = {};
    // Read the error body as text first. JSON objects become the problem details;
    // plain text or other JSON is kept as `detail` so the message is not lost.
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
  // 204 has no body to parse. `readBody` lets a caller read non-JSON bodies such as file bytes.
  if (response.status === 204) return undefined as T;
  if (readBody) return readBody(response);
  return await response.json() as T;
}

/**
 * Authenticated browser client for the v1 Gateway and owner-backed APIs.
 * Requests omit ambient browser credentials by default, avoid caching, and
 * include a tenant selector only when the caller has a confirmed value.
 */
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

  /** Sends one JSON request and preserves structured error details in {@link GatewayError}. */
  private async request<T>(
    token: string,
    path: string,
    init: RequestInit = {},
    baseUrl = this.baseUrl,
    tenantSelector?: string | null,
    rawResponse = false,
  ): Promise<T> {
    if (!token) {
      throw new GatewayError(401, { code: 'unauthorized' }, 'Sign in to continue.');
    }
    const headers = new Headers(init.headers);
    headers.set('Authorization', `Bearer ${token}`);
    // Captured files request bytes; JSON remains the default for other routes.
    if (!headers.has('Accept')) headers.set('Accept', 'application/json');
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
    // Return bytes and headers only after the shared HTTP error handling has succeeded.
    if (rawResponse) return response as T;
    return await response.json() as T;
  }

  /** Reads and validates the Projects owner's authorization context for this session. */
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

  /** Lists projects visible to the supplied Broker session and optional tenant. */
  listProjects(token: string, tenantSelector?: string | null): Promise<ProjectSummary[]> {
    return this.request(token, '/projects', {}, this.baseUrl, tenantSelector);
  }

  /** Creates a project in the selected tenant. */
  createProject(token: string, name: string, tenantSelector?: string | null): Promise<ProjectSummary> {
    return this.request(
      token,
      '/projects',
      { method: 'POST', body: JSON.stringify({ name }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads one project summary. */
  getProject(token: string, projectId: string, tenantSelector?: string | null): Promise<ProjectSummary> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Lists active role assignments. TenantAdmin or the project Owner only; returns 403 otherwise. */
  async listProjectRoleAssignments(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<ProjectRoleAssignmentList> {
    const result = await this.request<ProjectRoleAssignmentList>(
      token, `/projects/${encodeSegments(projectId)}/role-assignments`, {}, this.baseUrl, tenantSelector,
    );
    // Never show roles for a project other than the one asked for. A mismatch means the
    // server answer is broken, so report it as a 502 contract error.
    if (result?.projectId !== projectId || !Array.isArray(result.assignments))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned role assignments for a different project.');
    return result;
  }

  /** Assigns a role to a member. TenantAdmin only; 409 `role_assignment_exists` for duplicates. */
  assignProjectRole(
    token: string,
    projectId: string,
    request: { principalId: string; role: ProjectRole },
    tenantSelector?: string | null,
  ): Promise<ProjectRoleAssignmentView> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}/role-assignments`,
      { method: 'POST', body: JSON.stringify({ principalId: request.principalId, role: request.role }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Revokes one assignment at its current revision. TenantAdmin only. */
  revokeProjectRoleAssignment(
    token: string,
    projectId: string,
    assignmentId: string,
    expectedRevision: number,
    tenantSelector?: string | null,
  ): Promise<ProjectRoleAssignmentView> {
    // The revision guards against revoking an assignment that changed since it was shown.
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}/role-assignments/${encodeSegments(assignmentId)}?expectedRevision=${expectedRevision}`,
      { method: 'DELETE' },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Renames or archives a project. The owner rejects the write when
   * `expectedRevision` is stale, so callers must send the revision they displayed.
   */
  updateProject(
    token: string,
    projectId: string,
    request: { expectedRevision: number; name: string; state: ProjectSummary['state'] },
    tenantSelector?: string | null,
  ): Promise<ProjectSummary> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId)}`,
      { method: 'PATCH', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads the current project configuration together with its revision. */
  getProjectConfiguration(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
    revision?: number,
  ): Promise<VersionedProjectConfiguration> {
    // Without `revision` the owner returns the current configuration; with it, that saved version.
    return this.request(
      token,
      // Without `revision` the server returns the latest configuration; with it, that exact saved version.
      `/projects/${encodeSegments(projectId)}/configuration${revision === undefined ? '' : `?revision=${revision}`}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads current-tenant model choices and platform provider selections for one project. */
  getProjectConfigurationChoices(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<ProjectConfigurationChoices> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'configuration', 'choices')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads current reasoning levels from AgentHost's live hosted model catalog. */
  getHostedModelReasoningCatalog(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<HostedModelReasoningCatalogV1> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'configuration', 'hosted-model-reasoning-catalog')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Replaces project configuration using the caller's expected revision. */
  updateProjectConfiguration(
    token: string,
    projectId: string,
    expectedRevision: number,
    configuration: ProjectConfiguration,
    tenantSelector?: string | null,
  ): Promise<VersionedProjectConfiguration> {
    return this.request(token, `/projects/${encodeSegments(projectId)}/configuration`, {
      method: 'PUT',
      body: JSON.stringify({ expectedRevision, configuration: withoutProviderOverrides(configuration) }),
    }, this.baseUrl, tenantSelector);
  }

  /**
   * Lists the suggested skill marketplaces for a project. Each entry carries
   * `sourceId` when the project already has an active source at the same
   * location. The route takes no query parameters and needs project read access.
   */
  async listMarketplaceDefinitions(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<SkillMarketplaceDefinition[]> {
    const definitions = await this.request<unknown>(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'definitions')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    // The panel renders these directly, so reject a response that is not a list
    // of named repository locations instead of showing broken rows.
    if (!Array.isArray(definitions) || definitions.some((item) =>
      !item || typeof item !== 'object' ||
      typeof (item as SkillMarketplaceDefinition).name !== 'string' ||
      typeof (item as SkillMarketplaceDefinition).repository !== 'string' ||
      typeof (item as SkillMarketplaceDefinition).requestedRef !== 'string')) {
      throw new GatewayError(502, { code: 'invalid_owner_response' }, 'The suggested marketplace list is not valid.');
    }
    return definitions as SkillMarketplaceDefinition[];
  }

  /** Lists configured skill marketplace sources for a project. */
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

  /** Adds a marketplace source to the project. */
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

  /** Updates a marketplace source using the supplied source settings. */
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

  /** Removes a marketplace source only when its expected revision still matches. */
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

  /** Browses one marketplace source at the requested revision and page. */
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

  /** Previews marketplace skill content without importing it. */
  previewMarketplaceSkillContent(
    token: string,
    projectId: string,
    sourceId: string,
    request: MarketplaceSkillContentPreviewRequest,
    tenantSelector?: string | null,
  ): Promise<SkillContentPreview> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources', sourceId, 'preview')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Imports selected content from a marketplace source into the project. */
  importMarketplaceSkillContent(
    token: string,
    projectId: string,
    sourceId: string,
    request: MarketplaceSkillContentImportRequest,
    tenantSelector?: string | null,
  ): Promise<SkillContentImportReceipt> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skill-marketplaces', 'sources', sourceId, 'import')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Previews a skill candidate before project import. */
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

  /**
   * Turns uploaded files, one ZIP archive, or draft form fields into skill
   * candidates without saving anything. The request must hold exactly one of
   * `files`, `archive`, or `draft`. The route limits the JSON body to 3 MiB and
   * needs an unbound token with read scope; it is not scoped to a project.
   */
  packageSkillContent(
    token: string,
    request: SkillPackageRequest,
    tenantSelector?: string | null,
  ): Promise<SkillContentPackageResult> {
    return this.request(
      token,
      '/skills/package',
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Revokes one imported skill revision. The service keeps the record but marks
   * it revoked, and a repeat call for the same revision succeeds without change.
   * `reason` is required (1 to 2000 characters). Needs project admin authority.
   * The route returns `204` with no body.
   */
  async revokeProjectSkillRevision(
    token: string,
    projectId: string,
    skillId: string,
    revision: number,
    reason: string,
    tenantSelector?: string | null,
  ): Promise<void> {
    await this.request<void>(
      token,
      `/projects/${encodeSegments(projectId, 'skills', skillId, 'revisions', String(revision), 'revoke')}`,
      { method: 'POST', body: JSON.stringify({ reason }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Imports a validated skill-content selection into the project. */
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

  /** Lists the project's skills using its optional cursor and page-size controls. */
  listProjectSkillInventory(
    token: string,
    projectId: string,
    options: SkillContentInventoryQuery = {},
    tenantSelector?: string | null,
  ): Promise<SkillContentInventoryPage> {
    const query = new URLSearchParams();
    if (options.cursor !== undefined) query.set('cursor', options.cursor);
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    const suffix = query.toString();
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'skills')}${suffix ? `?${suffix}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Validates and records a native AgentSuite archive, and stages its agents as a casting proposal. */
  importProjectAgentSuite(
    token: string,
    projectId: string,
    request: AgentSuiteImportRequest,
    tenantSelector?: string | null,
  ): Promise<AgentSuiteImportResult> {
    // The archive travels base64-encoded inside JSON, so the request is about 4/3 the archive size.
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'agentsuite-imports')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads the retained import record for one AgentSuite source identity. */
  getProjectAgentSuiteImport(
    token: string,
    projectId: string,
    sourceIdentity: string,
    tenantSelector?: string | null,
  ): Promise<AgentSuiteImport> {
    // sourceIdentity looks like "sha256:<hex>". encodeSegments escapes the ':' so it stays one path segment.
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'agentsuite-imports', sourceIdentity)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads the policy bound to one imported effective agent ID. */
  getProjectAgentSuiteAgentPolicy(
    token: string,
    projectId: string,
    effectiveAgentId: string,
    tenantSelector?: string | null,
  ): Promise<AgentSuiteAgentPolicy> {
    // effectiveAgentId looks like "agentsuite:...". encodeSegments escapes it so it stays one path segment.
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'agentsuite-agents', effectiveAgentId, 'policy')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Lists the project's Remote MCP connections with configuration checksums. */
  async listRemoteMcpConnections(
    token: string,
    projectId: string,
    options: { pageSize?: number; after?: string } = {},
    tenantSelector?: string | null,
  ): Promise<RemoteMcpConnectionDirectoryPage> {
    // Send only the paging options the caller set, so the Gateway applies its own defaults.
    const query = new URLSearchParams();
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    if (options.after !== undefined) query.set('after', options.after);
    const suffix = query.toString();
    const page = await this.request<unknown>(
      token,
      `/projects/${encodeSegments(projectId, 'remote-mcp', 'connections')}${suffix ? `?${suffix}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    // Check the response shape before use. A malformed owner response is reported as a 502, the same
    // as other owner failures, rather than reaching the UI as untyped data.
    if (!isRemoteMcpConnectionDirectoryPage(page)) {
      throw new GatewayError(502, { code: 'invalid_owner_response' }, 'The Remote MCP connection list is not valid.');
    }
    return page;
  }

  /** Changes a skill assignment and returns the resulting versioned configuration. */
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

  /** Begins project- or platform-scoped Copilot authorization through the Gateway root. */
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

  /** Completes Copilot authorization using the callback code and state. */
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

  /** Reads a redacted Copilot connection receipt by connection identifier. */
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

  /** Refreshes a Copilot connection using its expected revision. */
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

  /** Revokes a Copilot connection using its expected revision. */
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

  /** Registers the remote MCP OAuth provider metadata with the Gateway. */
  registerRemoteMcpOAuthConnection(
    token: string,
    request: RemoteMcpOAuthConnectionRegistration,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthManagementStatus> {
    return this.request(
      token,
      '/api/connections/remote-mcp/v1/register',
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Reads the redacted state of one remote MCP OAuth connection. */
  getRemoteMcpOAuthConnection(
    token: string,
    connectionId: string,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthManagementStatus> {
    return this.request(
      token,
      `/api/connections/remote-mcp/v1/${encodeURIComponent(connectionId)}`,
      {},
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Prepares the user's consent flow for the selected remote MCP connection. */
  prepareRemoteMcpOAuthConsent(
    token: string,
    connectionId: string,
    request: RemoteMcpOAuthConsentRequest,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthConsentPreparation> {
    return this.request(
      token,
      `/api/connections/remote-mcp/v1/${encodeURIComponent(connectionId)}/consent`,
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Sends only the callback state and code, or the explicit denial, to the Gateway. */
  completeRemoteMcpOAuthCallback(
    token: string,
    request: RemoteMcpOAuthCallbackRequest,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthManagementStatus> {
    return this.request(
      token,
      '/api/connections/remote-mcp/v1/callback',
      {
        method: 'POST',
        body: JSON.stringify(request.error === 'access_denied'
          ? { state: request.state, error: request.error }
          : { state: request.state, code: request.code }),
      },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Refreshes a remote MCP OAuth connection using its current revision. */
  refreshRemoteMcpOAuthConnection(
    token: string,
    connectionId: string,
    request: RemoteMcpOAuthRefreshRequest,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthManagementStatus> {
    return this.request(
      token,
      `/api/connections/remote-mcp/v1/${encodeURIComponent(connectionId)}/refresh`,
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Disconnects a remote MCP OAuth connection with the caller's expected state. */
  disconnectRemoteMcpOAuthConnection(
    token: string,
    connectionId: string,
    request: RemoteMcpOAuthDisconnectRequest,
    tenantSelector?: string | null,
  ): Promise<RemoteMcpOAuthManagementStatus> {
    return this.request(
      token,
      `/api/connections/remote-mcp/v1/${encodeURIComponent(connectionId)}/disconnect`,
      { method: 'POST', body: JSON.stringify(request) },
      this.gatewayRootUrl,
      tenantSelector,
    );
  }

  /** Starts GitHub Repo App authorization and records where the browser should return. */
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

  /** Reads the user's Repo App authorization state. */
  getRepoAppAuthorizationStatus(token: string): Promise<RepoAppAuthorizationStatus> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization/status',
      {},
      this.gatewayRootUrl,
    );
  }

  /** Reads one Repo App authorization transaction by transaction identifier. */
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

  /** Requests a Repo App credential refresh. */
  refreshRepoAppAuthorization(token: string): Promise<void> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization/refresh',
      { method: 'POST' },
      this.gatewayRootUrl,
    );
  }

  /** Disconnects the user's Repo App authorization. */
  disconnectRepoAppAuthorization(token: string): Promise<void> {
    return this.request(
      token,
      '/api/auth/github/repo-app/authorization',
      { method: 'DELETE' },
      this.gatewayRootUrl,
    );
  }

  /** Lists repositories available for Repo App selection. */
  listRepoAppRepositorySelections(token: string): Promise<RepoAppRepositorySelectionList> {
    return this.request(
      token,
      '/api/github/repository-selections',
      {},
      this.gatewayRootUrl,
    );
  }

  /** Issues a short-lived selection code for the named GitHub repository. */
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

  /** Pins a selected repository to one run session; omitting the code preserves legacy requests. */
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

  /**
   * Lists admitted captures of the files one run session produced, newest first.
   * The route needs a token bound to this run and an explicit tenant. Pass both
   * cursor values from the previous page to read the next one.
   */
  async listRunOutputCaptures(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    options: { limit?: number; beforeCapturedAt?: string | null; beforeCaptureId?: string | null },
    tenantSelector: string | null,
  ): Promise<SourceControlOutputCapturePage> {
    const query = new URLSearchParams();
    if (options.limit !== undefined) query.set('limit', String(options.limit));
    // The service pages by the last capture's time and ID together, so send both or neither.
    if (options.beforeCapturedAt && options.beforeCaptureId) {
      query.set('beforeCapturedAt', options.beforeCapturedAt);
      query.set('beforeCaptureId', options.beforeCaptureId);
    }
    const encoded = query.toString();
    const page = await this.request<SourceControlOutputCapturePage>(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'output-captures')}${encoded ? `?${encoded}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (!page || !Array.isArray(page.captures))
      throw new GatewayError(502, { code: 'invalid_owner_response' }, 'The service returned an invalid capture list.');
    return page;
  }

  /** Reads the text patch of one admitted capture. The token must be bound to this run. */
  async readRunOutputCaptureDiff(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    captureId: string,
    tenantSelector: string | null,
  ): Promise<SourceControlOutputCaptureDiff> {
    const diff = await this.request<SourceControlOutputCaptureDiff>(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'output-captures', captureId, 'diff')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (!diff || typeof diff.patch !== 'string' || diff.capture?.captureId !== captureId)
      throw new GatewayError(502, { code: 'invalid_owner_response' }, 'The service returned a diff for a different capture.');
    return diff;
  }
  /** Reads a saved file set and its manifest without accessing the live workspace. */
  async readSourceControlOutputCapture(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector: string | null,
    captureId: string,
  ): Promise<SourceControlOutputCaptureDetail> {
    const detail = await this.request<SourceControlOutputCaptureDetail>(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'output-captures', captureId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    // Reject a mismatched capture before offering its manifest as the file navigation.
    if (detail?.capture?.captureId !== captureId || !Array.isArray(detail.manifest?.files))
      throw new GatewayError(502, { code: 'invalid_owner_response' }, 'The service returned an invalid file set.');
    return detail;
  }

  /** Reads the exact bytes of a path listed in the saved file manifest. */
  async readSourceControlOutputCaptureFile(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    tenantSelector: string | null,
    captureId: string,
    path: string,
  ): Promise<SourceControlOutputFileContent> {
    // Encode the path as one query value so slashes cannot change the API route.
    const response = await this.request<Response>(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'source-control', 'sessions', sessionId, 'output-captures', captureId, 'files')}?${new URLSearchParams({ path })}`,
      { headers: { Accept: 'application/octet-stream' } },
      this.baseUrl,
      tenantSelector,
      true,
    );
    // Keep bytes unchanged. A null digest means the browser could not read the header.
    return {
      bytes: new Uint8Array(await response.arrayBuffer()),
      sha256: response.headers.get('X-Source-Control-Output-Sha256'),
    };
  }

  /** Reads the effective provider and source-control selection for one run. */
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

  /** Reads the owner-maintained lifecycle status for a run. */
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

  /** Reads the coordination tree snapshot rooted at a run session. */
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

  /** Reads the run session tree using current project-read authority. */
  getProjectRunHistorySessions(
    token: string,
    projectId: string,
    runId: string,
    tenantSelector?: string | null,
  ): Promise<SessionTreeSnapshot> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'history', 'sessions')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads current activity and blockers for one coordination session. */
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

  /** Reads the text of one committed turn event with the run-bound token. */
  readSessionTurnContent(
    token: string,
    projectId: string,
    runId: string,
    sessionId: string,
    eventId: string,
    tenantSelector?: string | null,
  ): Promise<SessionTurnContent> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'sessions', sessionId, 'turns', eventId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }
  /** Reads pending decisions and gate state for one coordination session. */
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

  /** Answers a decision gate with an expected state version and idempotency key. */
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

  /** Approves or rejects a gate without reusing a stale decision-state version. */
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

  /** Detaches a session using its execution fence and idempotency key. */
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

  /** Archives a child session under its parent with an execution fence. */
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

  /** Sends a coordination message with explicit delivery mode and idempotency key. */
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

  /** Lists the caller's private Chief of Staff conversations, newest first. */
  listChiefOfStaffConversations(
    token: string,
    options: { cursor?: string | null; pageSize?: number } = {},
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffConversationPage> {
    const query = new URLSearchParams();
    if (options.cursor) query.set('cursor', options.cursor);
    if (options.pageSize) query.set('pageSize', String(options.pageSize));
    const suffix = query.size > 0 ? `?${query.toString()}` : '';
    return this.request(token, `/chief-of-staff/conversations${suffix}`, {}, this.baseUrl, tenantSelector);
  }

  /** Binds a run the caller created to a conversation entry. Repeating returns the same entry. */
  bindChiefOfStaffConversation(
    token: string,
    body: BindChiefOfStaffConversationRequest,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffConversation> {
    return this.request(
      token,
      '/chief-of-staff/conversations',
      { method: 'POST', body: JSON.stringify(body) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads one of the caller's conversations. */
  getChiefOfStaffConversation(
    token: string,
    conversationId: string,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffConversation> {
    return this.request(
      token,
      `/chief-of-staff/conversations/${encodeSegments(conversationId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Lists the caller's Priorities in order with the order revision. */
  listChiefOfStaffPriorities(token: string, tenantSelector?: string | null): Promise<ChiefOfStaffPriorityList> {
    return this.request(token, '/chief-of-staff/priorities', {}, this.baseUrl, tenantSelector);
  }

  /** Creates a Priority. One key per draft; a replay with the same key returns the original. */
  createChiefOfStaffPriority(
    token: string,
    body: CreateChiefOfStaffPriorityRequest,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriority> {
    return this.request(
      token,
      '/chief-of-staff/priorities',
      { method: 'POST', body: JSON.stringify(body), headers: { 'Idempotency-Key': idempotencyKey } },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads one Priority. */
  getChiefOfStaffPriority(
    token: string,
    priorityId: string,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriority> {
    return this.request(
      token,
      `/chief-of-staff/priorities/${encodeSegments(priorityId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Changes a Priority at `expectedRevision`; a stale revision returns 409. */
  updateChiefOfStaffPriority(
    token: string,
    priorityId: string,
    body: UpdateChiefOfStaffPriorityRequest,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriority> {
    return this.request(
      token,
      `/chief-of-staff/priorities/${encodeSegments(priorityId)}`,
      { method: 'PATCH', body: JSON.stringify(body) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Replaces the whole Priority order at `expectedOrderRevision`; a stale revision returns 409. */
  reorderChiefOfStaffPriorities(
    token: string,
    body: ReorderChiefOfStaffPrioritiesRequest,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriorityList> {
    return this.request(
      token,
      '/chief-of-staff/priorities/order',
      { method: 'PUT', body: JSON.stringify(body) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Adds a typed link to a Priority at `expectedRevision`. */
  addChiefOfStaffPriorityLink(
    token: string,
    priorityId: string,
    body: AddChiefOfStaffPriorityLinkRequest,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriority> {
    return this.request(
      token,
      `/chief-of-staff/priorities/${encodeSegments(priorityId, 'links')}`,
      { method: 'POST', body: JSON.stringify(body) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Removes a link from a Priority at `expectedRevision`. Works without project access. */
  removeChiefOfStaffPriorityLink(
    token: string,
    priorityId: string,
    linkId: string,
    expectedRevision: number,
    tenantSelector?: string | null,
  ): Promise<ChiefOfStaffPriority> {
    return this.request(
      token,
      `/chief-of-staff/priorities/${encodeSegments(priorityId, 'links', linkId)}?expectedRevision=${expectedRevision}`,
      { method: 'DELETE' },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads a bounded page of persisted coordination events after an optional cursor. */
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

  /** Reads a bounded run event page using current project-read authority. */
  async replayProjectRunHistoryEvents(
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
      `/projects/${encodeSegments(projectId, 'runs', runId, 'history', 'events')}?${query.toString()}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Streams live coordination events and yields each event with its resume cursor. */
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
    // Browsers reject window.fetch called with any receiver other than the global object.
    const response = await this.fetcher.call(
      globalThis,
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
    if (!response.body) throw new Error('The live update stream has no readable body.');

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

  /** Reads usage totals for one run. */
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

  /**
   * Lists one page of the project's runs, newest first. Send `cursor` back
   * unchanged with the same filters; a null `nextCursor` marks the last page.
   */
  async listProjectRuns(
    token: string,
    projectId: string,
    options: {
      states?: ProjectRunStateFilter[];
      startedBy?: 'me';
      cursor?: string | null;
      pageSize?: number;
    } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectRunPage> {
    // Add only the parameters that are set. The service rejects empty or repeated ones.
    const query = new URLSearchParams();
    if (options.states?.length) query.set('state', options.states.join(','));
    if (options.startedBy) query.set('startedBy', options.startedBy);
    if (options.cursor) query.set('cursor', options.cursor);
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    const suffix = query.toString();
    const page = await this.request<ProjectRunPage>(
      token,
      `/projects/${encodeSegments(projectId, 'runs')}${suffix ? `?${suffix}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    // Don't show runs that the service returned for a different project.
    if (page?.projectId !== projectId || !Array.isArray(page.items))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned runs for a different project.');
    return page;
  }

  /**
   * Creates a new chat run with its saved first message. Needs a token that is
   * not bound to a run. Reuse the same `idempotencyKey` when retrying the same
   * message, so the service returns the run it already created.
   */
  createProjectRun(
    token: string,
    projectId: string,
    request: CreateProjectRunRequest,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<CreateProjectRunResponse> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs')}`,
      { method: 'POST', body: JSON.stringify(request), headers: { 'Idempotency-Key': idempotencyKey } },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Delivers the saved first message. Needs a token bound to this run. A repeat
   * call returns the same result and does not send the message again, so a
   * `504 run_start_timeout` is safe to retry.
   */
  startProjectRun(
    token: string,
    projectId: string,
    runId: string,
    tenantSelector?: string | null,
  ): Promise<StartProjectRunResponse> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'start')}`,
      { method: 'POST' },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Stops a run. Needs the project token that is not bound to a run; a
   * run-bound token gets 403. A run that already ended returns `alreadyTerminal`.
   */
  cancelProjectRun(
    token: string,
    projectId: string,
    runId: string,
    idempotencyKey: string,
    reason?: string,
    tenantSelector?: string | null,
  ): Promise<CancelProjectRunResponse> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'cancel')}`,
      {
        method: 'POST',
        body: JSON.stringify(reason ? { reason } : {}),
        headers: { 'Idempotency-Key': idempotencyKey },
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Sends user text through the exact run-bound token and returns its journal admission receipt. */
  sendProjectRunMessage(
    token: string,
    projectId: string,
    runId: string,
    request: ProjectRunMessageRequest,
    idempotencyKey: string,
    tenantSelector?: string | null,
  ): Promise<ProjectRunMessageReceipt> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'messages')}`,
      {
        method: 'POST',
        body: JSON.stringify(request),
        headers: { 'Idempotency-Key': idempotencyKey },
      },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Builds the shared `from`/`to` query for metrics and usage. Both ends are optional. */
  private static rangeQuery(range: { from?: string; to?: string }, extra: Record<string, string> = {}): string {
    const query = new URLSearchParams();
    if (range.from) query.set('from', range.from);
    if (range.to) query.set('to', range.to);
    for (const [key, value] of Object.entries(extra)) query.set(key, value);
    const text = query.toString();
    return text ? `?${text}` : '';
  }

  /**
   * Reads run counts and a daily series for a half-open UTC range. The service
   * uses the last 30 days when the range is omitted and allows at most 92 days.
   */
  async getProjectMetrics(
    token: string,
    projectId: string,
    range: { from?: string; to?: string } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectMetrics> {
    const metrics = await this.request<ProjectMetrics>(
      token,
      `/projects/${encodeSegments(projectId, 'metrics')}${AgentweaverGatewayClient.rangeQuery(range, { bucket: 'day' })}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (metrics?.projectId !== projectId || !Array.isArray(metrics.series))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned metrics for a different project.');
    return metrics;
  }

  /** Rejects a backlog graph that is malformed or belongs to another project. */
  private static checkedBacklogGraph(graph: BacklogGraph | undefined, projectId: string): BacklogGraph {
    // The board trusts revision numbers for optimistic writes, so a graph without
    // them, or for another project, must never reach the page.
    if (graph?.projectId !== projectId
      || typeof graph.revision !== 'number'
      || !Array.isArray(graph.tasks)
      || !Array.isArray(graph.dependencies))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned a backlog for a different project.');
    return graph;
  }

  /** Reads the project's backlog tasks and dependencies. Needs read access to the project. */
  async readProjectBacklog(token: string, projectId: string, tenantSelector?: string | null): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /**
   * Adds one task to the project backlog and returns the new graph. Needs write
   * access. A stale `expectedGraphRevision` is rejected with HTTP 409.
   */
  async captureBacklogTask(
    token: string,
    projectId: string,
    request: CaptureBacklogTaskRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /**
   * Replaces one task's content and returns the new graph. Needs write access.
   * Stale revisions, archived tasks, and tasks with a pending automation
   * invocation are rejected with HTTP 409; an unknown task with HTTP 404.
   */
  async editBacklogTaskContent(
    token: string,
    projectId: string,
    taskId: string,
    request: EditBacklogTaskContentRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks', taskId, 'content')}`,
      { method: 'PUT', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /** Sets an unclaimed task's state using both revisions read from the board. */
  async setProjectBacklogTaskState(
    token: string,
    projectId: string,
    taskId: string,
    request: SetBacklogTaskStateRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks', taskId, 'state')}`,
      { method: 'PUT', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /** Archives one unclaimed task at the graph and task revisions read from the board. */
  async archiveProjectBacklogTask(
    token: string,
    projectId: string,
    taskId: string,
    request: BacklogTaskRevisionRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks', taskId, 'archive')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /** Adds a prerequisite edge using the graph revision read from the board. */
  async addProjectBacklogDependency(
    token: string,
    projectId: string,
    taskId: string,
    prerequisiteTaskId: string,
    request: BacklogGraphRevisionRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks', taskId, 'dependencies', prerequisiteTaskId)}`,
      { method: 'PUT', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /** Removes a prerequisite edge using the graph revision read from the board. */
  async removeProjectBacklogDependency(
    token: string,
    projectId: string,
    taskId: string,
    prerequisiteTaskId: string,
    request: BacklogGraphRevisionRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogGraph> {
    const graph = await this.request<BacklogGraph>(
      token,
      `/projects/${encodeSegments(projectId, 'backlog', 'tasks', taskId, 'dependencies', prerequisiteTaskId)}`,
      { method: 'DELETE', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
    return AgentweaverGatewayClient.checkedBacklogGraph(graph, projectId);
  }

  /** Claims eligible work for an accepted run. Reuse the same idempotency key on retry. */
  async claimBacklogTask(
    token: string,
    projectId: string,
    runId: string,
    taskId: string,
    request: ClaimBacklogTaskRequest,
    tenantSelector?: string | null,
  ): Promise<BacklogTaskClaimReceipt> {
    return this.request<BacklogTaskClaimReceipt>(
      token,
      `/projects/${encodeSegments(projectId, 'runs', runId, 'backlog', 'tasks', taskId, 'claim')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Lists one page of the project's synced repository issues. The service reads
   * stored issues only. Pass `cursor` from the previous page with the same filter.
   */
  async listProjectIssues(
    token: string,
    projectId: string,
    options: { state?: ProjectIssueStateFilter; cursor?: string | null; pageSize?: number } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectIssuePage> {
    // Only set parameters are sent, so the service applies its own defaults.
    const query = new URLSearchParams();
    if (options.state) query.set('state', options.state);
    if (options.cursor) query.set('cursor', options.cursor);
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    const encoded = query.toString();
    const suffix = encoded ? `?${encoded}` : '';
    const page = await this.request<ProjectIssuePage>(
      token,
      `/projects/${encodeSegments(projectId, 'source-control', 'issues')}${suffix}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (page?.projectId !== projectId || !Array.isArray(page.items))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned issues for a different project.');
    return page;
  }

  /**
   * Lists pull requests that project runs created or reused, newest recorded
   * first. The read makes no provider call, so `state` is the recorded state.
   * Only the set options are sent, so the service applies its defaults
   * (`open`, default page size).
   */
  async listProjectPullRequests(
    token: string,
    projectId: string,
    options: { state?: ProjectPullRequestStateFilter; cursor?: string | null; pageSize?: number } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectPullRequestPage> {
    const query = new URLSearchParams();
    if (options.state) query.set('state', options.state);
    if (options.cursor) query.set('cursor', options.cursor);
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    const encoded = query.toString();
    const page = await this.request<ProjectPullRequestPage>(
      token,
      `/projects/${encodeSegments(projectId, 'source-control', 'pull-requests')}${encoded ? `?${encoded}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    // Show nothing rather than another project's pull requests.
    if (page?.projectId !== projectId || !Array.isArray(page.items))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned pull requests for a different project.');
    return page;
  }

  /** Reads token use and cost grouped by model and by agent for the same range rules as metrics. */
  async getProjectUsage(
    token: string,
    projectId: string,
    range: { from?: string; to?: string } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectUsage> {
    const usage = await this.request<ProjectUsage>(
      token,
      `/projects/${encodeSegments(projectId, 'usage')}${AgentweaverGatewayClient.rangeQuery(range)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
    if (usage?.projectId !== projectId || !Array.isArray(usage.usageByModel) || !Array.isArray(usage.usageByAgent))
      throw new GatewayError(502, { code: 'owner_contract_invalid' }, 'The service returned usage for a different project.');
    return usage;
  }

  /**
   * Lists the caller's pending questions and approvals across projects. Needs a
   * token that is not bound to a project or run; use `projectId` to narrow it.
   */
  listNotifications(
    token: string,
    options: { projectId?: string; cursor?: string | null; pageSize?: number; includeDismissed?: boolean } = {},
    tenantSelector?: string | null,
  ): Promise<UserNotificationPage> {
    const query = new URLSearchParams();
    if (options.projectId) query.set('projectId', options.projectId);
    if (options.cursor) query.set('cursor', options.cursor);
    if (options.pageSize !== undefined) query.set('pageSize', String(options.pageSize));
    if (options.includeDismissed !== undefined) query.set('includeDismissed', String(options.includeDismissed));
    const suffix = query.toString();
    return this.request(token, `/notifications${suffix ? `?${suffix}` : ''}`, {}, this.baseUrl, tenantSelector);
  }

  /** Hides one notification for this caller. The gate stays open. Repeating the call is safe. */
  dismissNotification(token: string, notificationId: string, tenantSelector?: string | null): Promise<void> {
    return this.request(
      token,
      `/notifications/${encodeSegments(notificationId, 'dismiss')}`,
      { method: 'POST' },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Reads every platform component area and its readiness. Needs a token that
   * is not bound to a run and a tenant or platform administrator. An owner that
   * can't be reached appears as `unknown` with `ownerUnavailable`.
   */
  getPlatformComponents(token: string, tenantSelector?: string | null): Promise<PlatformComponentReport> {
    return this.request(token, '/platform/components', {}, this.baseUrl, tenantSelector);
  }

  /** Lists the built-in team templates that a casting proposal can start from. */
  listCastingTemplates(token: string, tenantSelector?: string | null): Promise<ProjectCastingTemplateDefinition[]> {
    return this.request(token, '/casting/templates', {}, this.baseUrl, tenantSelector);
  }

  /** Lists supported universe names after the Projects owner checks project access. */
  listCastingUniverses(
    token: string,
    projectId: string,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingUniverseList> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'universes')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Lists the built-in roles that a manual casting proposal can pick from. */
  listCatalogRoles(token: string, tenantSelector?: string | null): Promise<ProjectCastingRoleDefinition[]> {
    return this.request(token, '/catalog/roles', {}, this.baseUrl, tenantSelector);
  }

  /** Lists the project's casting proposals, newest first, up to `limit` (1–100). */
  listCastingProposals(
    token: string,
    projectId: string,
    limit?: number,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal[]> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals')}${limit !== undefined ? `?limit=${limit}` : ''}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Creates a casting proposal from a template or from picked roles. The
   * proposal is a draft; nothing changes until it is confirmed.
   */
  createCastingProposal(
    token: string,
    projectId: string,
    request:
      | ({ mode: 'scenario' } & CreateScenarioProjectCastingProposalRequest)
      | ({ mode: 'manual' } & CreateManualProjectCastingProposalRequest),
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    // The service rejects unknown fields, so send the body without `mode`.
    const { mode, ...body } = request;
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals', mode)}`,
      { method: 'POST', body: JSON.stringify(body) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Confirms or rejects a pending proposal. `expectedDraftRevision` must match
   * the draft that was shown, or the service returns 409.
   */
  transitionCastingProposal(
    token: string,
    projectId: string,
    proposalId: string,
    action: 'confirm' | 'reject',
    expectedDraftRevision: number,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals', proposalId, action)}`,
      { method: 'POST', body: JSON.stringify({ expectedDraftRevision }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads one casting proposal, including its draft and current state. */
  getCastingProposal(
    token: string,
    projectId: string,
    proposalId: string,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals', proposalId)}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Stages a complete configuration draft as a casting proposal. Used when the
   * browser builds the draft itself, for example to add one member.
   */
  createCastingDraftProposal(
    token: string,
    projectId: string,
    request: CreateProjectCastingProposalRequest,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals')}`,
      { method: 'POST', body: JSON.stringify({ ...request, draft: withoutProviderOverrides(request.draft) }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Replaces the draft of a pending proposal. `expectedDraftRevision` must
   * match the draft that was shown, or the service returns 409.
   */
  updateCastingProposal(
    token: string,
    projectId: string,
    proposalId: string,
    request: UpdateProjectCastingProposalRequest,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'proposals', proposalId)}`,
      { method: 'PATCH', body: JSON.stringify({ ...request, draft: withoutProviderOverrides(request.draft) }) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Exports the project's agent charters and casting, at `revision` or the current revision. */
  exportCastingTransfer(
    token: string,
    projectId: string,
    revision?: number,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingTransfer> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'export')}${revision === undefined ? '' : `?revision=${revision}`}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /**
   * Stages an exported team as a pending proposal in this project. The service
   * checks the digest and rejects bodies over 1 MiB (413) or invalid transfers (422).
   */
  importCastingTransfer(
    token: string,
    projectId: string,
    request: ImportProjectCastingTransferRequest,
    tenantSelector?: string | null,
  ): Promise<ProjectCastingProposal> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'casting', 'import')}`,
      { method: 'POST', body: JSON.stringify(request) },
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Searches knowledge records within the exact project, run, and agent scope. */
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

  /** Searches private Knowledge across the accessible runs of one project. */
  searchProjectKnowledge(
    token: string,
    projectId: string,
    options: { query?: string; kind?: string; page?: number; pageSize?: number; includeInactive?: boolean } = {},
    tenantSelector?: string | null,
  ): Promise<ProjectKnowledgeRecordPage> {
    const query = new URLSearchParams();
    if (options.query) query.set('q', options.query);
    if (options.kind) query.set('kind', options.kind);
    query.set('page', String(options.page ?? 1));
    query.set('pageSize', String(options.pageSize ?? 50));
    query.set('includeInactive', String(options.includeInactive ?? true));
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'knowledge', 'records')}?${query.toString()}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Reads recent caller-visible history for one configured project agent. */
  getProjectAgentHistory(
    token: string,
    projectId: string,
    agentId: string,
    tenantSelector?: string | null,
  ): Promise<ProjectAgentHistoryResponse> {
    return this.request(
      token,
      `/projects/${encodeSegments(projectId, 'agents', agentId, 'history')}`,
      {},
      this.baseUrl,
      tenantSelector,
    );
  }

  /** Creates a knowledge record and forwards the idempotency key to the owner. */
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

  /** Updates a knowledge record using both its expected revision and idempotency key. */
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

  /** Reads a page of revisions for one knowledge record. */
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

  /** Restores a prior record revision using the current expected revision. */
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

  /** Approves a pending knowledge decision using its expected revision. */
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

  /** Exports the knowledge records in the selected agent scope as a versioned bundle. */
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

  /** Imports a versioned bundle into the selected agent scope with an idempotency key. */
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

  /** Promotes a proposal using the expected proposal revision and idempotency key. */
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

  /** Rejects a proposal using its expected revision and idempotency key. */
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

/** Shared Gateway client used by the v1 browser application. */
export const gatewayClient = new AgentweaverGatewayClient();
