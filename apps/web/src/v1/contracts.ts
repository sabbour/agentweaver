export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  [key: string]: unknown;
}

export type ProjectAuthorityResourceType = 'platform' | 'tenant' | 'project';

export type ProjectAuthorizationPermission =
  | 'readProjects'
  | 'writeProjects'
  | 'createProjects'
  | 'readRunSelection'
  | 'acceptRunSelection'
  | 'accessPrivateKnowledge'
  | 'readPlatformRuntimeDefaults'
  | 'writePlatformRuntimeDefaults';

export interface ProjectAuthorizationPermissionGrant {
  permission: ProjectAuthorizationPermission;
  roleRevision: number;
}

export interface EffectiveProjectAuthorization {
  resourceType: ProjectAuthorityResourceType;
  resourceId: string;
  permissions: ProjectAuthorizationPermissionGrant[];
}

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

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

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

export interface ProjectSummary {
  projectId: string;
  name: string;
  state: 'active' | 'archived';
  revision: number;
  configurationRevision: number;
  createdAt: string;
  updatedAt: string;
}

export interface ModelSelectionSettings {
  reference: string;
  credentialReference?: { id: string; version: string } | null;
  connectionId?: string;
}

export type CopilotConnectionScope = 'project' | 'platform';

export type CopilotConnectionState =
  | 'pending'
  | 'connected'
  | 'refreshing'
  | 'transientUnavailable'
  | 'reconnectRequired'
  | 'revoked'
  | 'refreshIndeterminate';

export interface CopilotConnectionReceipt {
  connectionId: string;
  revision: number;
  scope: CopilotConnectionScope;
  scopeId: string;
  state: CopilotConnectionState;
  freshUntil: string;
}

export interface CopilotConnectionBegin {
  connection: CopilotConnectionReceipt;
  authorizationUri: string;
}

export type CopilotAuthorizationCallback =
  | { state: string; code: string; error?: never }
  | { state: string; error: 'access_denied'; code?: never };

export interface CopilotConnectionMutation {
  connectionId: string;
  expectedRevision: number;
}

export type RemoteMcpOAuthConnectionState =
  | 'NotConnected'
  | 'PendingConsent'
  | 'Authorized'
  | 'RefreshInProgress'
  | 'RefreshIndeterminate'
  | 'Disconnected'
  | 'Revoked';

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

export interface RemoteMcpOAuthConnectionRegistration {
  projectId: string;
  connectionId: string;
  expectedConfigurationRevision: number;
  expectedConfigurationSha256: string;
  issuerUri: string;
  scopes: string[];
}

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

export type RemoteMcpOAuthCallbackRequest =
  | { state: string; code: string; error?: never }
  | { state: string; error: 'access_denied'; code?: never };

export interface RemoteMcpOAuthDisconnectRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
  idempotencyKey: string;
}

export interface RemoteMcpOAuthRefreshRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
}

export interface RemoteMcpOAuthConsentRequest {
  expectedConnectionRevision: number;
  expectedCredentialRevision: number;
  expectedConfigurationRevision: number;
  expectedConfigurationSha256: string;
}

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

export function isCopilotConnectionBegin(value: unknown): value is CopilotConnectionBegin {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const begin = value as Record<string, unknown>;
  return hasExactKeys(begin, ['connection', 'authorizationUri']) &&
    isCopilotConnectionReceipt(begin.connection) &&
    isNonEmptyString(begin.authorizationUri);
}

export interface ProjectConfiguration {
  modelSelection?: ModelSelectionSettings | null;
  providerOverrides: Array<{ seam: string; providerId: string }>;
  orderedProviderOverrides: Array<{ seam: string; providerIds: string[] }>;
  agentCharters: Array<{ agentId: string; name: string; role: string; charter: string }>;
  casting: Array<{ agentId: string; role: string; order: number }>;
  blueprintWorkflowReferences: Array<{ blueprintId: string; workflowId: string }>;
  defaultWorkflowId?: string | null;
  skills: Array<{ skillId: string; enabled: boolean; order: number }>;
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

export type SourceControlAuthMode = 'secret' | 'githubApp';

export interface SourceControlProjectSettings {
  authMode?: SourceControlAuthMode;
  appConnectionId?: string;
  repository?: { owner: string; name: string };
  apiSecretReference?: { id: string; version: string } | null;
  checkoutSecretReference?: { id: string; version: string } | null;
  webhookSecretReference?: { id: string; version: string } | null;
}

export interface RepoAppAuthorizationStart {
  authorizationUrl: string;
  transactionId: string;
  expiresAt: string;
}

export interface RepoAppAuthorizationStatus {
  connected: boolean;
  githubLogin: string | null;
  connectionId: string | null;
}

export interface RepoAppAuthorizationTransaction {
  status: string;
}

export interface RepoAppRepositoryCandidate {
  fullName: string;
  ownerLogin: string;
  isPrivate: boolean;
  defaultBranch: string;
  pushedAt: string | null;
}

export interface RepoAppInstallationCandidate {
  accountLogin: string;
  accountType: string;
  repositorySelection: string;
  managementUrl: string;
}

export interface RepoAppRepositorySelectionList {
  repositories: RepoAppRepositoryCandidate[];
  installations: RepoAppInstallationCandidate[];
}

export interface RepoAppRepositorySelectionCode {
  selectionCode: string;
  expiresAt: string;
}

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

export interface VersionedProjectConfiguration {
  projectId: string;
  revision: number;
  configuration: ProjectConfiguration;
  updatedByActorId: string;
  createdAt: string;
}

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

export interface EffectiveProviderSelection {
  cardinality: string;
  seam: string;
  candidates: ProviderCandidate[];
  meterSource?: string | null;
}

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

export type SessionKind = 'coordinator' | 'childWork' | 'scribe' | 'operatorChat' | 'childRun';
export type SessionLifecycle = 'active' | 'cancelled' | 'completed' | 'archived';
export type SessionActivity = 'busy' | 'idle' | 'unknown';

export interface SessionIdentity {
  projectId: string;
  runId: string;
  sessionId: string;
}

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

export interface SessionTreeSnapshot {
  rootSessionId: string;
  nodes: SessionTreeNode[];
}

export type CoordinationBlockerKind =
  | 'awaitingInput'
  | 'awaitingPlanApproval'
  | 'awaitingApproval'
  | 'awaitingOutcomeConfirmation';

export interface SessionStatusBlocker {
  kind: CoordinationBlockerKind;
  requestId: string;
  choices: string[];
  allowsFreeform: boolean;
  prompt?: string | null;
}

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

export interface CoordinatorDecisionStateView {
  stateVersion: number;
  executionFence: number;
  outcomeConfirmed: boolean;
  workflowConfirmed: boolean;
  canDecompose: boolean;
  canDispatch: boolean;
  pendingGate?: CoordinatorGateRequest | null;
}

export interface WorkflowValidationIssue {
  code: string;
  path: string;
  message: string;
}

export interface CoordinatorDecisionOperationResponse {
  decisionId: string;
  stateVersion: number;
  accepted: boolean;
  executionFence: number;
  pendingGate?: CoordinatorGateRequest | null;
  issues: WorkflowValidationIssue[];
  transitionValue?: unknown;
}

export interface CoordinationTreeCommandResult {
  commandId: string;
  command: string;
  sourceSessionId: string;
  targetSessionId?: string | null;
  executionFence: number;
  state: string;
}

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

export interface SessionEventPage {
  events: SessionEventEnvelope[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export interface UsageAmountTotal {
  meterSource: string;
  unit: string;
  amount: number;
  pricedEvents: number;
  unpricedEvents: number;
}

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

export interface UsageRunTotals {
  tenantId: string;
  projectId: string;
  runId: string;
  events: number;
  isFullyPriced: boolean;
  agents: UsageAgentTotals[];
  amounts: UsageAmountTotal[];
}

export type KnowledgeKind = 'memory' | 'proposal' | 'decision' | 'sessionContext';
export type KnowledgeRecordState = 'pending' | 'active' | 'rejected' | 'archived' | 'promoted' | 'superseded';
export type KnowledgeTrustState = 'pending' | 'approved' | 'rejected' | 'legacy';

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

export interface KnowledgeRecordPage {
  items: KnowledgeRecord[];
  totalCount: number;
  page: number;
  pageSize: number;
}

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

export interface KnowledgeRecordRevisionPage {
  items: KnowledgeRecordRevision[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface KnowledgeRecordTransferBundle {
  format: 'agentweaver.knowledge-transfer.v1';
  schemaVersion: 1;
  projectId: string;
  agentId: string;
  records: KnowledgeRecordTransferEntry[];
}

export interface KnowledgeRecordTransferEntry {
  record: KnowledgeRecord;
  revisions: KnowledgeRecordRevision[];
}

export interface KnowledgeRecordImportResult {
  records: KnowledgeRecord[];
  isDuplicate: boolean;
}

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

export interface BrokerConsentPrompt {
  consent_required: true;
  consent_handle: string;
  client_id: string;
  requested_scopes: string[];
  csrf_token: string;
}

export interface BrokerTokenResponse {
  access_token: string;
  token_type: string;
  expires_in: number;
  refresh_token?: string;
  scope?: string;
}
