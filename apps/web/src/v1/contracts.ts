/** JSON contracts exchanged by the v1 browser, Gateway, and resource owners. */
/** Problem details returned for failed Gateway requests. */
export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  [key: string]: unknown;
}

/** Owner-resource scope used when reporting effective project permissions. */
export type ProjectAuthorityResourceType = 'platform' | 'tenant' | 'project';

/** Permission names the Projects owner can grant to a signed-in actor. */
export type ProjectAuthorizationPermission =
  | 'readProjects'
  | 'writeProjects'
  | 'createProjects'
  | 'readRunSelection'
  | 'acceptRunSelection'
  | 'accessPrivateKnowledge'
  | 'readPlatformRuntimeDefaults'
  | 'writePlatformRuntimeDefaults';

/** A permission and the role revision that currently supplies it. */
export interface ProjectAuthorizationPermissionGrant {
  permission: ProjectAuthorizationPermission;
  roleRevision: number;
}

/** Effective permissions for one platform, tenant, or project resource. */
export interface EffectiveProjectAuthorization {
  resourceType: ProjectAuthorityResourceType;
  resourceId: string;
  permissions: ProjectAuthorizationPermissionGrant[];
}

/** Broker-to-owner context for the actor, tenant, and optional run binding. */
export interface ProjectAuthorizationContextResponse {
  contractVersion: 1;
  issuer: string;
  actorId: string;
  tenantId: string;
  membershipRevision: number;
  boundProjectId: string | null;
  boundRunId: string | null;
  effectiveAuthority: EffectiveProjectAuthorization[];
}

/** Requires an exact object shape so unknown authorization fields fail closed. */
function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

/** True only for a non-empty string after trimming whitespace. */
function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

/** Validates the exact versioned authorization context returned by the Projects owner. */
export function isProjectAuthorizationContextResponse(
  value: unknown,
): value is ProjectAuthorizationContextResponse {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const context = value as Record<string, unknown>;
  const contextKeys = [
    'contractVersion',
    'issuer',
    'actorId',
    'tenantId',
    'membershipRevision',
    'boundProjectId',
    'boundRunId',
    'effectiveAuthority',
  ] as const;
  if (!hasExactKeys(context, contextKeys) ||
      context.contractVersion !== 1 ||
      !isNonEmptyString(context.issuer) ||
      !isNonEmptyString(context.actorId) ||
      !isNonEmptyString(context.tenantId) ||
      !Number.isSafeInteger(context.membershipRevision) ||
      (context.membershipRevision as number) < 1 ||
      !(context.boundProjectId === null || isNonEmptyString(context.boundProjectId)) ||
      !(context.boundRunId === null || isNonEmptyString(context.boundRunId)) ||
      (context.boundRunId !== null && context.boundProjectId === null) ||
      !Array.isArray(context.effectiveAuthority))
    return false;

  return context.effectiveAuthority.every((grantValue) => {
    if (!grantValue || typeof grantValue !== 'object' || Array.isArray(grantValue)) return false;
    const grant = grantValue as Record<string, unknown>;
    if (!hasExactKeys(grant, ['resourceType', 'resourceId', 'permissions']) ||
        !['platform', 'tenant', 'project'].includes(String(grant.resourceType)) ||
        !isNonEmptyString(grant.resourceId) ||
        !Array.isArray(grant.permissions))
      return false;

    return grant.permissions.every((permissionValue) => {
      if (!permissionValue || typeof permissionValue !== 'object' || Array.isArray(permissionValue))
        return false;
      const permission = permissionValue as Record<string, unknown>;
      return hasExactKeys(permission, ['permission', 'roleRevision']) &&
        [
          'readProjects',
          'writeProjects',
          'createProjects',
          'readRunSelection',
          'acceptRunSelection',
          'accessPrivateKnowledge',
          'readPlatformRuntimeDefaults',
          'writePlatformRuntimeDefaults',
        ].includes(String(permission.permission)) &&
        Number.isSafeInteger(permission.roleRevision) &&
        (permission.roleRevision as number) > 0;
    });
  });
}

/** Compact owner-backed project summary used by the browser project picker. */
export interface ProjectSummary {
  projectId: string;
  name: string;
  state: 'active' | 'archived';
  revision: number;
  configurationRevision: number;
  createdAt: string;
  updatedAt: string;
}

/** Reference to the selected model and its optional credential resource. */
export interface ModelSelectionSettings {
  reference: string;
  credentialReference?: { id: string; version: string } | null;
  connectionId?: string;
}

/** Owner scope of a user-managed Copilot connection. */
export type CopilotConnectionScope = 'project' | 'platform';

/** Lifecycle reported for a managed Copilot authorization. */
export type CopilotConnectionState =
  | 'pending'
  | 'connected'
  | 'refreshing'
  | 'transientUnavailable'
  | 'reconnectRequired'
  | 'revoked'
  | 'refreshIndeterminate';

/** Revisioned, redacted view of a Copilot authorization connection. */
export interface CopilotConnectionReceipt {
  connectionId: string;
  revision: number;
  scope: CopilotConnectionScope;
  scopeId: string;
  state: CopilotConnectionState;
  freshUntil: string;
}

/** Result of beginning Copilot authorization, including the checked connection receipt. */
export interface CopilotConnectionBegin {
  connection: CopilotConnectionReceipt;
  authorizationUri: string;
}

/** OAuth callback accepted by the Copilot connection completion route. */
export type CopilotAuthorizationCallback =
  | { state: string; code: string; error?: never }
  | { state: string; error: 'access_denied'; code?: never };

/** Connection identifier and optimistic-concurrency revision for a mutation. */
export interface CopilotConnectionMutation {
  connectionId: string;
  expectedRevision: number;
}

/** Lifecycle values for a project's remote MCP OAuth connection. */
export type RemoteMcpOAuthConnectionState =
  | 'NotConnected'
  | 'PendingConsent'
  | 'Authorized'
  | 'RefreshInProgress'
  | 'RefreshIndeterminate'
  | 'Disconnected'
  | 'Revoked';

/** Redacted connection state together with configuration and credential revisions. */
export interface RemoteMcpOAuthManagementStatus {
  connectionId: string;
  projectId: string;
  connectionRevision: number;
  credentialRevision: number;
  state: RemoteMcpOAuthConnectionState;
  storedConfigurationRevision: number;
  currentConfigurationRevision: number | null;
  currentConfigurationMatches: boolean;
  credentialUseAvailable: boolean;
}

/** Project configuration identity pinned when registering an OAuth provider. */
export interface RemoteMcpOAuthConnectionRegistration {
  projectId: string;
  connectionId: string;
  expectedConfigurationRevision: number;
  expectedConfigurationSha256: string;
  issuerUri: string;
  scopes: string[];
}

/** One-time OAuth consent preparation bound to state, PKCE, and config revisions. */
export interface RemoteMcpOAuthConsentPreparation {
  correlationId: string;
  state: string;
  pkceChallenge: string;
  authorizationUri: string;
  expiresAt: string;
  connectionRevision: number;
  configurationRevision: number;
  configurationSha256: string;
}

/** OAuth code or explicit denial submitted to finish the remote MCP callback. */
export type RemoteMcpOAuthCallbackRequest =
  | { state: string; code: string; error?: never }
  | { state: string; error: 'access_denied'; code?: never };

/** Version-fenced and idempotent disconnect request for a remote MCP connection. */
export interface RemoteMcpOAuthDisconnectRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
  idempotencyKey: string;
}

/** Version-fenced request to refresh credentials for a remote MCP connection. */
export interface RemoteMcpOAuthRefreshRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
}

/** Expected revisions and digest required before starting provider consent. */
export interface RemoteMcpOAuthConsentRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
  expectedConfigurationSha256: string;
}

/** One project Remote MCP connection as listed by the Gateway directory route. */
export interface RemoteMcpConnectionDirectoryEntry {
  connectionId: string;
  displayName: string;
  endpointUri: string;
  resourceUri: string | null;
  authenticationMode: 'none' | 'delegatedOAuth';
  transportProfile: 'streamableHttp20250618';
  state: 'draft' | 'enabled' | 'disabled' | 'removed';
  rowRevision: number;
  configurationRevision: number;
  configurationSha256: string;
  identityBound: boolean;
  registryServerName: string | null;
  registryExactVersion: string | null;
  discoveryRevision: number | null;
  catalogSha256: string | null;
  toolCount: number;
}

/** One page of project Remote MCP connections. */
export interface RemoteMcpConnectionDirectoryPage {
  connections: RemoteMcpConnectionDirectoryEntry[];
  nextConnectionId: string | null;
}

const sha256Pattern = /^[0-9a-f]{64}$/;

function isNullableString(value: unknown): boolean {
  return value === null || typeof value === 'string';
}

/** Validates one directory entry, including revisions and configuration checksums. */
export function isRemoteMcpConnectionDirectoryEntry(
  value: unknown,
): value is RemoteMcpConnectionDirectoryEntry {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  return typeof entry.connectionId === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(entry.connectionId) &&
    isNonEmptyString(entry.displayName) &&
    isNonEmptyString(entry.endpointUri) &&
    isNullableString(entry.resourceUri) &&
    ['none', 'delegatedOAuth'].includes(String(entry.authenticationMode)) &&
    entry.transportProfile === 'streamableHttp20250618' &&
    ['draft', 'enabled', 'disabled', 'removed'].includes(String(entry.state)) &&
    Number.isSafeInteger(entry.rowRevision) && (entry.rowRevision as number) > 0 &&
    Number.isSafeInteger(entry.configurationRevision) &&
    (entry.configurationRevision as number) > 0 &&
    typeof entry.configurationSha256 === 'string' &&
    sha256Pattern.test(entry.configurationSha256) &&
    typeof entry.identityBound === 'boolean' &&
    isNullableString(entry.registryServerName) &&
    isNullableString(entry.registryExactVersion) &&
    (entry.discoveryRevision === null ||
      (Number.isSafeInteger(entry.discoveryRevision) && (entry.discoveryRevision as number) > 0)) &&
    (entry.catalogSha256 === null ||
      (typeof entry.catalogSha256 === 'string' && sha256Pattern.test(entry.catalogSha256))) &&
    Number.isSafeInteger(entry.toolCount) && (entry.toolCount as number) >= 0;
}

/** Validates a directory page and every entry in it. */
export function isRemoteMcpConnectionDirectoryPage(
  value: unknown,
): value is RemoteMcpConnectionDirectoryPage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const page = value as Record<string, unknown>;
  return Array.isArray(page.connections) &&
    page.connections.every(isRemoteMcpConnectionDirectoryEntry) &&
    isNullableString(page.nextConnectionId);
}

/** Validates exact fields, identifiers, revisions, and states in the owner response. */
export function isRemoteMcpOAuthManagementStatus(
  value: unknown,
): value is RemoteMcpOAuthManagementStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const status = value as Record<string, unknown>;
  return hasExactKeys(status, [
    'connectionId',
    'projectId',
    'connectionRevision',
    'credentialRevision',
    'state',
    'storedConfigurationRevision',
    'currentConfigurationRevision',
    'currentConfigurationMatches',
    'credentialUseAvailable',
  ]) &&
    typeof status.connectionId === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(status.connectionId) &&
    isNonEmptyString(status.projectId) &&
    Number.isSafeInteger(status.connectionRevision) &&
    (status.connectionRevision as number) > 0 &&
    Number.isSafeInteger(status.credentialRevision) &&
    (status.credentialRevision as number) >= 0 &&
    [
      'NotConnected',
      'PendingConsent',
      'Authorized',
      'RefreshInProgress',
      'RefreshIndeterminate',
      'Disconnected',
      'Revoked',
    ].includes(String(status.state)) &&
    Number.isSafeInteger(status.storedConfigurationRevision) &&
    (status.storedConfigurationRevision as number) > 0 &&
    (status.currentConfigurationRevision === null ||
      Number.isSafeInteger(status.currentConfigurationRevision)) &&
    typeof status.currentConfigurationMatches === 'boolean' &&
    typeof status.credentialUseAvailable === 'boolean';
}

/** Validates the short-lived state, PKCE challenge, expiry, and configuration digest. */
export function isRemoteMcpOAuthConsentPreparation(
  value: unknown,
): value is RemoteMcpOAuthConsentPreparation {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const preparation = value as Record<string, unknown>;
  return hasExactKeys(preparation, [
    'correlationId',
    'state',
    'pkceChallenge',
    'authorizationUri',
    'expiresAt',
    'connectionRevision',
    'configurationRevision',
    'configurationSha256',
  ]) &&
    typeof preparation.correlationId === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(preparation.correlationId) &&
    typeof preparation.state === 'string' &&
    /^[A-Za-z0-9_-]{43}$/.test(preparation.state) &&
    typeof preparation.pkceChallenge === 'string' &&
    /^[A-Za-z0-9_-]{43}$/.test(preparation.pkceChallenge) &&
    typeof preparation.authorizationUri === 'string' &&
    typeof preparation.expiresAt === 'string' &&
    Number.isFinite(Date.parse(preparation.expiresAt)) &&
    Number.isSafeInteger(preparation.connectionRevision) &&
    (preparation.connectionRevision as number) > 0 &&
    Number.isSafeInteger(preparation.configurationRevision) &&
    (preparation.configurationRevision as number) > 0 &&
    typeof preparation.configurationSha256 === 'string' &&
    /^[0-9a-f]{64}$/.test(preparation.configurationSha256);
}

/** Validates the exact receipt shape before displaying or using connection state. */
export function isCopilotConnectionReceipt(value: unknown): value is CopilotConnectionReceipt {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const receipt = value as Record<string, unknown>;
  return hasExactKeys(receipt, [
    'connectionId',
    'revision',
    'scope',
    'scopeId',
    'state',
    'freshUntil',
  ]) &&
    typeof receipt.connectionId === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(receipt.connectionId) &&
    Number.isSafeInteger(receipt.revision) &&
    (receipt.revision as number) > 0 &&
    (receipt.scope === 'project' || receipt.scope === 'platform') &&
    isNonEmptyString(receipt.scopeId) &&
    [
      'pending',
      'connected',
      'refreshing',
      'transientUnavailable',
      'reconnectRequired',
      'revoked',
      'refreshIndeterminate',
    ].includes(String(receipt.state)) &&
    typeof receipt.freshUntil === 'string' &&
    Number.isFinite(Date.parse(receipt.freshUntil));
}

/** Validates the begin response and its embedded connection receipt. */
export function isCopilotConnectionBegin(value: unknown): value is CopilotConnectionBegin {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const begin = value as Record<string, unknown>;
  return hasExactKeys(begin, ['connection', 'authorizationUri']) &&
    isCopilotConnectionReceipt(begin.connection) &&
    isNonEmptyString(begin.authorizationUri);
}

/** Mutable owner configuration, including model, skills, source control, and run limits. */
export interface ProjectConfiguration {
  modelSelection?: ModelSelectionSettings | null;
  providerOverrides: Array<{ seam: string; providerId: string }>;
  orderedProviderOverrides: Array<{ seam: string; providerIds: string[] }>;
  agentCharters: Array<{ agentId: string; name: string; role: string; charter: string }>;
  casting: Array<{ agentId: string; role: string; order: number }>;
  blueprintWorkflowReferences: Array<{ blueprintId: string; workflowId: string }>;
  defaultWorkflowId?: string | null;
  skills: ProjectSkillAssignment[];
  egressNarrowing?: Array<unknown> | null;
  runLimits: {
    maxModelTurns?: number | null;
    maxToolCalls?: number | null;
    maxChildren?: number | null;
    maxConcurrentChildren?: number | null;
    maxWallTimeSeconds?: number | null;
    maxPromptTokens?: number | null;
  };
  sourceControl?: SourceControlProjectSettings | null;
}

/** Owner-reviewed casting draft and its configuration and draft revision fences. */
export interface ProjectCastingProposal {
  proposalId: string;
  projectId: string;
  baseConfigurationRevision: number;
  draftRevision: number;
  state: 'pending' | 'confirmed' | 'rejected';
  draft: ProjectConfiguration;
  confirmedConfigurationRevision: number | null;
  result: ProjectConfiguration | null;
  transferProvenance: ProjectCastingTransferProvenance | null;
  createdByActorId: string;
  createdAt: string;
  updatedByActorId: string;
  updatedAt: string;
}

/** Complete configuration staged for review without applying it. */
export interface CreateProjectCastingProposalRequest {
  expectedConfigurationRevision: number;
  draft: ProjectConfiguration;
}

/** Built-in scenario selected at the current configuration revision. */
export interface CreateScenarioProjectCastingProposalRequest {
  expectedConfigurationRevision: number;
  templateId: string;
}

/** Catalog roles selected at the current configuration revision. */
export interface CreateManualProjectCastingProposalRequest {
  expectedConfigurationRevision: number;
  roleIds: string[];
}

/** Replacement draft fenced to the pending proposal's draft revision. */
export interface UpdateProjectCastingProposalRequest {
  expectedDraftRevision: number;
  draft: ProjectConfiguration;
}

/** Expected draft revision used for confirmation or rejection. */
export interface TransitionProjectCastingProposalRequest {
  expectedDraftRevision: number;
}

/** Verified source identity and digest retained on an imported proposal. */
export interface ProjectCastingTransferProvenance {
  formatVersion: 1;
  sourceProjectId: string;
  sourceConfigurationRevision: number;
  contentDigest: string;
}

/** Versioned, digest-checked charter and casting payload; excludes other settings. */
export interface ProjectCastingTransfer extends ProjectCastingTransferProvenance {
  agentCharters: ProjectConfiguration['agentCharters'];
  casting: ProjectConfiguration['casting'];
}

/** Transfer staged for review against the target project's current revision. */
export interface ImportProjectCastingTransferRequest {
  expectedConfigurationRevision: number;
  transfer: ProjectCastingTransfer;
}

/** Built-in scenario that seeds a proposal from catalog role identifiers. */
export interface ProjectCastingTemplateDefinition {
  id: string;
  title: string;
  description: string;
  roleIds: string[];
}

/** Built-in role text used to generate a project agent charter. */
export interface ProjectCastingRoleDefinition {
  id: string;
  title: string;
  summary: string;
  responsibilities: string[];
  boundaries: string[];
}

/** Revision and optional agent scope for one assigned skill. */
export interface ProjectSkillAssignment {
  skillId: string;
  enabled: boolean;
  order: number;
  revision?: number | null;
  contentDigest?: string | null;
  agentIds?: string[] | null;
}

/** Revisioned Git-backed source registered as a project's skill marketplace. */
export interface MarketplaceSource {
  sourceId: string;
  name: string;
  repository: string;
  requestedRef: string;
  subpath: string | null;
  revision: number;
  state: 'active' | 'removed';
}

/** Fields used to add a Git-backed skill marketplace source. */
export interface MarketplaceSourceInput {
  name?: string;
  repository: string;
  requestedRef?: string;
  subpath?: string;
}

/** Marketplace update request fenced to the source revision being edited. */
export interface MarketplaceSourceUpdateInput extends MarketplaceSourceInput {
  expectedRevision: number;
}

/** Search and paging controls pinned to a marketplace source revision. */
export interface MarketplaceBrowseRequest {
  expectedSourceRevision: number;
  query?: string;
  page?: number;
  pageSize?: number;
}

/** One skill path available from a marketplace browse result. */
export interface MarketplaceSkillCandidate {
  location: string;
  name: string;
  description: string | null;
}

/** Paged browse results pinned to the resolved repository commit. */
export interface MarketplaceBrowsePage {
  sourceId: string;
  sourceRevision: number;
  requestedRef: string;
  resolvedCommitSha: string;
  candidates: MarketplaceSkillCandidate[];
  total: number;
  page: number;
  pageSize: number;
  hasMore: boolean;
}

/** Identifies the exact source revision and skill path to preview. */
export interface MarketplaceSkillContentPreviewRequest {
  expectedSourceRevision: number;
  resolvedCommitSha: string;
  selectedPath: string;
}

/** Import confirmation with digest, idempotency key, and optional update revision. */
export interface MarketplaceSkillContentImportRequest extends MarketplaceSkillContentPreviewRequest {
  idempotencyKey: string;
  expectedContentDigest: string;
  skillId?: string;
  expectedRevision?: number | null;
}

/** Relative path and text content for a resource packaged with a skill. */
export interface SkillContentResourceRequest {
  relativePath: string;
  content: string;
}

/** Markdown plus its relative text resources, before owner-side validation. */
export interface SkillContentCandidateRequest {
  skillMarkdown: string;
  resources: SkillContentResourceRequest[];
}

/** Summary returned after validating candidate content without storing it. */
export interface SkillContentPreview {
  name: string;
  description: string;
  contentDigest: string;
  resourceCount: number;
  totalBytes: number;
}

/** Project import request pinned to a content digest and optional source revision. */
export interface SkillContentImportRequest {
  idempotencyKey: string;
  expectedContentDigest: string;
  skillId?: string;
  expectedRevision?: number;
  source?: {
    sourceId: string;
    sourceRevision: string;
    requestedRef: string;
    resolvedCommitSha: string;
    selectedPath: string;
  };
  candidate: SkillContentCandidateRequest;
}

/** Receipt confirming an imported skill revision and its content digest. */
export interface SkillContentImportReceipt {
  skillId: string;
  revision: number;
  name: string;
  description: string;
  contentDigest: string;
  resourceCount: number;
  totalBytes: number;
}

/** Cursor and page-size controls for skill inventory queries. */
export interface SkillContentInventoryQuery {
  cursor?: string;
  pageSize?: number;
}

/** Metadata for a stored skill-content revision, including revocation and source provenance. */
export interface SkillContentRevisionSummary {
  skillId: string;
  revision: number;
  name: string;
  description: string;
  contentDigest: string;
  resourceCount: number;
  totalBytes: number;
  createdByActorId: string;
  createdAt: string;
  sourceId: string | null;
  sourceRevision: string | null;
  requestedRef: string | null;
  resolvedCommitSha: string | null;
  selectedPath: string | null;
  isRevoked: boolean;
  revokedByActorId: string | null;
  revokedAt: string | null;
}

/** Assignment state and the content revision selected for a skill. */
export interface SkillContentAssignmentSummary {
  skillId: string;
  enabled: boolean;
  order: number;
  revision: number | null;
  contentDigest: string | null;
  agentIds: string[] | null;
}

/** One page of skill assignments and revision metadata under a config revision. */
export interface SkillContentInventoryPage {
  configurationRevision: number;
  assignments: SkillContentAssignmentSummary[];
  revisions: SkillContentRevisionSummary[];
  pageSize: number;
  nextCursor: string | null;
}

/** Derived run state reported by the project run list. */
export type ProjectRunState =
  | 'idle'
  | 'active'
  | 'blocked'
  | 'completed'
  | 'failed'
  | 'indeterminate'
  | 'cancelled';

/** Run-list state filter: one or more states, or the `open` and `terminal` aliases. */
export type ProjectRunStateFilter = ProjectRunState | 'open' | 'terminal';

/** Actor whose accepted run selection created the run. */
export interface ProjectRunStartedBy {
  issuer: string;
  subject: string;
}

/** One run in a project run list. Title and goal stay null until a start request stores them. */
export interface ProjectRunSummary {
  runId: string;
  rootSessionId: string | null;
  title: string | null;
  goal: string | null;
  state: ProjectRunState;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  startedBy: ProjectRunStartedBy;
}

/** One newest-first page of project runs. Pass `nextCursor` back with the same filter and page size. */
export interface ProjectRunPage {
  projectId: string;
  items: ProjectRunSummary[];
  nextCursor: string | null;
}

/** Created and terminal-completed run counts on one UTC calendar day. */
export interface ProjectMetricsDay {
  date: string;
  created: number;
  completed: number;
}

/** Orchestrator-owned current state, lifetime total, and half-open interval run metrics. */
export interface ProjectMetrics {
  projectId: string;
  from: string;
  to: string;
  activeRuns: number;
  blockedRuns: number;
  runsInRange: number;
  runsTotal: number;
  completedRuns: number;
  succeededRuns: number;
  series: ProjectMetricsDay[];
}

/** Persisted cost subtotal; consumers must not combine units or replace unknown amounts with zero. */
export interface ProjectUsageCost {
  priceUnit: string | null;
  disposition: 'estimate' | 'reconciled' | 'unpriced';
  amount: number | null;
}

/** Model or agent ledger aggregate; any missing measurement makes its aggregate null. */
export interface ProjectUsageItem {
  id: string;
  requestCount: number | null;
  inputTokens: number | null;
  outputTokens: number | null;
  cachedTokens: number | null;
  cacheWriteTokens: number | null;
  reasoningTokens: number | null;
  avgDurationMs: number | null;
  runs: number;
  unpricedRequestCount: number | null;
  cost: ProjectUsageCost[];
}

/** Events-owned usage grouped independently by persisted model and agent IDs. */
export interface ProjectUsage {
  projectId: string;
  from: string;
  to: string;
  usageByModel: ProjectUsageItem[];
  usageByAgent: ProjectUsageItem[];
}

/** Revision-fenced skill assignment update sent to the Projects owner. */
export interface SkillAssignmentRequest {
  expectedProjectConfigurationRevision: number;
  revision: number;
  contentDigest: string;
  enabled: boolean;
  order: number;
  agentIds: string[];
}

/** Authentication mechanism used by project source-control settings. */
export type SourceControlAuthMode = 'secret' | 'githubApp';

/** Project's repository and secret references for source-control access. */
export interface SourceControlProjectSettings {
  authMode?: SourceControlAuthMode;
  appConnectionId?: string;
  repository?: { owner: string; name: string };
  apiSecretReference?: { id: string; version: string } | null;
  checkoutSecretReference?: { id: string; version: string } | null;
  webhookSecretReference?: { id: string; version: string } | null;
}

/** Browser redirect and transaction identifier returned when Repo App auth begins. */
export interface RepoAppAuthorizationStart {
  authorizationUrl: string;
  transactionId: string;
  expiresAt: string;
}

/** Redacted connection state for the user's Repo App authorization. */
export interface RepoAppAuthorizationStatus {
  connected: boolean;
  githubLogin: string | null;
  connectionId: string | null;
}

/** Current status of one Repo App authorization transaction. */
export interface RepoAppAuthorizationTransaction {
  status: string;
}

/** Repository metadata offered for selection from a GitHub installation. */
export interface RepoAppRepositoryCandidate {
  fullName: string;
  ownerLogin: string;
  isPrivate: boolean;
  defaultBranch: string;
  pushedAt: string | null;
}

/** GitHub installation account and management destination. */
export interface RepoAppInstallationCandidate {
  accountLogin: string;
  accountType: string;
  repositorySelection: string;
  managementUrl: string;
}

/** Repositories and installations available to the user's Repo App connection. */
export interface RepoAppRepositorySelectionList {
  repositories: RepoAppRepositoryCandidate[];
  installations: RepoAppInstallationCandidate[];
}

/** Expiring code that lets the browser select a repository without sending credentials. */
export interface RepoAppRepositorySelectionCode {
  selectionCode: string;
  expiresAt: string;
}

/** Owner-confirmed repository pin and resource generation for a run session. */
export interface SourceControlRepositoryPinView {
  pinId: string;
  repository: string;
  providerId: string;
  resourceId: string;
  resourceGeneration: number;
  providerRepositoryId: number;
  defaultBranch: string;
  isPrivate: boolean;
  pinnedAt: string;
}

/** Project configuration paired with the owner revision used for compare-and-swap updates. */
export interface VersionedProjectConfiguration {
  projectId: string;
  revision: number;
  configuration: ProjectConfiguration;
  updatedByActorId: string;
  createdAt: string;
}

/** Provider metadata considered when resolving an effective seam selection. */
export interface ProviderCandidate {
  seam: string;
  providerId: string;
  adapterVersion: string;
  optionsSchemaVersion: number;
  optionsRevision: string;
  hosting: string;
  advertisedCapabilities: string[];
  requiredCapabilities: string[];
  layer?: string | null;
}

/** Effective provider candidates and cardinality for one provider seam. */
export interface EffectiveProviderSelection {
  cardinality: string;
  seam: string;
  candidates: ProviderCandidate[];
  meterSource?: string | null;
}

/** Fully resolved project/run model, provider, configuration, and network settings. */
export interface EffectiveRunSelection {
  projectId: string;
  runId: string;
  projectRevision: number;
  projectConfigurationRevision: number;
  platformRuntimeRevision: number;
  contextRevision: string;
  modelSelection: ModelSelectionSettings;
  providers: EffectiveProviderSelection[];
  egressAllowlist: unknown[];
  runLimits: Record<string, number>;
  projectConfiguration: ProjectConfiguration;
  egressBaseline: unknown[];
  projectEgressNarrowing?: unknown[] | null;
  requiredEgress: unknown[];
}

/** Owner-maintained run state and the fence/version that protects current execution. */
export interface OwnerRunStatus {
  projectId: string;
  runId: string;
  rootSessionId: string;
  executionFence: number;
  logicalTurnOrdinal: number;
  executionState: string;
  stateVersion: number;
  causeCode?: string | null;
  reference?: string | null;
}

/** Role of a session in the coordination tree or operator experience. */
export type SessionKind = 'coordinator' | 'childWork' | 'scribe' | 'operatorChat' | 'childRun';
/** Owner lifecycle for a coordination session. */
export type SessionLifecycle = 'active' | 'cancelled' | 'completed' | 'archived';
/** Whether a session is currently executing work, idle, or not observable. */
export type SessionActivity = 'busy' | 'idle' | 'unknown';

/** Project, run, and session identifiers used as an exact owner resource key. */
export interface SessionIdentity {
  projectId: string;
  runId: string;
  sessionId: string;
}

/** One coordination-tree node with parent, lifecycle, and execution-fence metadata. */
export interface SessionTreeNode {
  identity: SessionIdentity;
  parentSessionId?: string | null;
  rootSessionId: string;
  kind: SessionKind;
  detached: boolean;
  lifecycle: SessionLifecycle;
  executionFence: number;
  logicalTurnOrdinal: number;
  stateVersion: number;
  createdAt: string;
  archivedAt?: string | null;
}

/** Owner snapshot of a run's session hierarchy. */
export interface SessionTreeSnapshot {
  rootSessionId: string;
  nodes: SessionTreeNode[];
}

/** Kinds of user or owner action currently blocking a session. */
export type CoordinationBlockerKind =
  | 'awaitingInput'
  | 'awaitingPlanApproval'
  | 'awaitingApproval'
  | 'awaitingOutcomeConfirmation';

/** Pending request and allowed response forms associated with a blocked session. */
export interface SessionStatusBlocker {
  kind: CoordinationBlockerKind;
  requestId: string;
  choices: string[];
  allowsFreeform: boolean;
  prompt?: string | null;
}

/** Current session activity, blockers, lifecycle, and owner run-execution state. */
export interface SessionStatusSnapshot {
  identity: SessionIdentity;
  parentSessionId?: string | null;
  rootSessionId: string;
  kind: SessionKind;
  detached: boolean;
  activity: SessionActivity;
  activityUnavailableCode?: string | null;
  lifecycle: SessionLifecycle;
  executionFence: number;
  stateVersion: number;
  blockers: SessionStatusBlocker[];
  runtimeEffectsState: string;
  runtimeEffectsUnavailableCode: string;
  interruptionIntent: {
    state: 'none' | 'requested' | 'acknowledged';
    ownerMessageId?: string | null;
    causeCode?: string | null;
  };
  runExecution: {
    state: string;
    stateVersion: number;
    causeCode?: string | null;
    reference?: string | null;
  };
}

/** Coordinator decision state and the next gate available to the caller. */
export interface CoordinatorDecisionStateView {
  stateVersion: number;
  executionFence: number;
  outcomeConfirmed: boolean;
  workflowConfirmed: boolean;
  canDecompose: boolean;
  canDispatch: boolean;
  pendingGate?: CoordinatorGateRequest | null;
}

/** One workflow validation issue returned by coordinator decision processing. */
export interface WorkflowValidationIssue {
  code: string;
  path: string;
  message: string;
}

/** Result of a versioned coordinator decision operation, including rejection issues. */
export interface CoordinatorDecisionOperationResponse {
  decisionId: string;
  stateVersion: number;
  accepted: boolean;
  executionFence: number;
  pendingGate?: CoordinatorGateRequest | null;
  issues: WorkflowValidationIssue[];
  transitionValue?: unknown;
}

/** Accepted coordination-tree command and the execution fence it targeted. */
export interface CoordinationTreeCommandResult {
  commandId: string;
  command: string;
  sourceSessionId: string;
  targetSessionId?: string | null;
  executionFence: number;
  state: string;
}

/** Gate contract describing the authorized actor, choices, and execution fence. */
export interface CoordinatorGateRequest {
  requestId: string;
  kind:
    | 'outcomeConfirmation'
    | 'generatedWorkflowConfirmation'
    | 'workPlanConfirmation'
    | 'scopeChangeConfirmation'
    | 'question'
    | 'approval';
  subjectId: string;
  authorizedActorId: string;
  fence: number;
  allowedChoices: string[];
  allowsFreeform: boolean;
  prompt?: string | null;
  resolvesOutcomeClarification?: boolean;
}

/** Versioned owner event with exact session identity and opaque event payload. */
export interface SessionEventEnvelope {
  schemaVersion: number;
  eventVersion: number;
  eventId: string;
  identity: SessionIdentity;
  position: number;
  occurredAt: string;
  kind: string;
  payload: Record<string, unknown>;
  objectReferences: unknown[];
}

/** Persisted event page with a cursor for continued replay. */
export interface SessionEventPage {
  events: SessionEventEnvelope[];
  nextCursor?: string | null;
  hasMore: boolean;
}

/** Amount and pricing coverage for one usage meter and unit. */
export interface UsageAmountTotal {
  meterSource: string;
  unit: string;
  amount: number;
  pricedEvents: number;
  unpricedEvents: number;
}

/** Aggregated usage and priced amounts attributed to one agent. */
export interface UsageAgentTotals {
  agentId: string;
  events: number;
  requestCount?: number | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  cachedTokens?: number | null;
  reasoningTokens?: number | null;
  durationMilliseconds?: number | null;
  cacheWriteTokens?: number | null;
  isFullyPriced: boolean;
  amounts: UsageAmountTotal[];
}

/** Aggregated usage totals for one run, grouped by agent and meter. */
export interface UsageRunTotals {
  tenantId: string;
  projectId: string;
  runId: string;
  events: number;
  isFullyPriced: boolean;
  agents: UsageAgentTotals[];
  amounts: UsageAmountTotal[];
}

/** Knowledge-record category stored by the owner. */
export type KnowledgeKind = 'memory' | 'proposal' | 'decision' | 'sessionContext';
/** Lifecycle state of a knowledge record. */
export type KnowledgeRecordState = 'pending' | 'active' | 'rejected' | 'archived' | 'promoted' | 'superseded';
/** Trust decision associated with a knowledge record. */
export type KnowledgeTrustState = 'pending' | 'approved' | 'rejected' | 'legacy';

/** Owner record scoped to one project and agent, with state and revision history. */
export interface KnowledgeRecord {
  recordId: string;
  projectId: string;
  agentId: string;
  kind: KnowledgeKind;
  type: string;
  title?: string | null;
  content: string;
  rationale?: string | null;
  importance: string;
  tags: string[];
  state: KnowledgeRecordState;
  trustState: KnowledgeTrustState;
  revision: number;
  revisionId: string;
  previousRevisionId?: string | null;
  sourceRunId?: string | null;
  sourceSessionId?: string | null;
  promotedDecisionId?: string | null;
  createdAt: string;
  updatedAt: string;
  supersededByRecordId?: string | null;
}

/** Page of knowledge records matching an exact owner scope and query. */
export interface KnowledgeRecordPage {
  items: KnowledgeRecord[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Immutable snapshot of one knowledge-record revision and its provenance. */
export interface KnowledgeRecordRevision {
  recordId: string;
  revision: number;
  revisionId: string;
  previousRevisionId?: string | null;
  kind: KnowledgeKind;
  type: string;
  title?: string | null;
  content: string;
  rationale?: string | null;
  importance: string;
  tags: string[];
  state: KnowledgeRecordState;
  trustState: KnowledgeTrustState;
  reason?: string | null;
  createdAt: string;
  supersededByRecordId?: string | null;
  sourceRunId?: string | null;
  sourceSessionId?: string | null;
  actorFingerprint?: string | null;
  changeKind?: string | null;
}

/** Page of historical revisions for one knowledge record. */
export interface KnowledgeRecordRevisionPage {
  items: KnowledgeRecordRevision[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Versioned transfer envelope constrained to one project and agent. */
export interface KnowledgeRecordTransferBundle {
  format: 'agentweaver.knowledge-transfer.v1';
  schemaVersion: 1;
  projectId: string;
  agentId: string;
  records: KnowledgeRecordTransferEntry[];
}

/** Record and history included together in a knowledge transfer bundle. */
export interface KnowledgeRecordTransferEntry {
  record: KnowledgeRecord;
  revisions: KnowledgeRecordRevision[];
}

/** Import result indicating whether the owner treated the bundle as a duplicate. */
export interface KnowledgeRecordImportResult {
  records: KnowledgeRecord[];
  isDuplicate: boolean;
}

/** Owner result for a knowledge mutation, including stale and invalid-state outcomes. */
export interface KnowledgeRecordWriteResult {
  status:
    | 'created'
    | 'updated'
    | 'notFound'
    | 'stale'
    | 'idempotencyConflict'
    | 'invalidState'
    | 'invalidReplacement'
    | 'replacementCycle';
  record?: KnowledgeRecord | null;
  currentRevision?: number | null;
  isDuplicate: boolean;
}

/** Mutable knowledge fields and optional replacement-state explanation. */
export interface KnowledgeRecordUpdateInput {
  type: string;
  title?: string | null;
  content: string;
  rationale?: string | null;
  importance: string;
  tags: string[];
  state: KnowledgeRecordState;
  reason?: string | null;
  supersededByRecordId?: string | null;
}

/** Proposal promotion result with both resulting records and delivery status. */
export interface KnowledgeProposalPromotionResult extends KnowledgeRecordWriteResult {
  proposal?: KnowledgeRecord | null;
  decision?: KnowledgeRecord | null;
  outboxEventId?: string | null;
  delivery?: string | null;
  deliveryCode?: string | null;
  requiredAudienceSubject?: string | null;
  requiredAudience?: string | null;
  deliveryAcknowledgment?: unknown;
}

/** Broker consent challenge required before authorization can continue. */
export interface BrokerConsentPrompt {
  consent_required: true;
  consent_handle: string;
  client_id: string;
  requested_scopes: string[];
  csrf_token: string;
}

/** Bearer-token response returned by the Identity Broker token endpoint. */
export interface BrokerTokenResponse {
  access_token: string;
  token_type: string;
  expires_in: number;
  refresh_token?: string;
  scope?: string;
}
