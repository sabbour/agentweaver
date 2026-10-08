export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  [key: string]: unknown;
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
  sourceControl?: unknown;
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
export type KnowledgeRecordState = 'pending' | 'active' | 'rejected' | 'archived' | 'promoted';
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
}

export interface KnowledgeRecordPage {
  items: KnowledgeRecord[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface KnowledgeRecordWriteResult {
  status: 'created' | 'updated' | 'notFound' | 'stale' | 'idempotencyConflict' | 'invalidState';
  record?: KnowledgeRecord | null;
  currentRevision?: number | null;
  isDuplicate: boolean;
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
