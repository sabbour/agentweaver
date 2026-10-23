import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  BrokerConsentPrompt,
  EffectiveProjectRole,
  EffectiveRunSelection,
  ProjectConfigurationChoices,
} from './contracts';

const mocks = vi.hoisted(() => ({
  binding: null as { projectId: string; runId: string } | null,
  consent: null as BrokerConsentPrompt | null,
  authProvider: vi.fn(),
  ensureSession: vi.fn(),
  tenantSelector: 'tenant-1',
  completeRemoteMcpOAuthCallback: vi.fn(),
  listProjects: vi.fn(),
  listProjectRuns: vi.fn(),
  getProjectMetrics: vi.fn(),
  getProjectUsage: vi.fn(),
  getProject: vi.fn(),
  getProjectConfiguration: vi.fn(),
  getProjectConfigurationChoices: vi.fn(),
  updateProjectConfiguration: vi.fn(),
  listMarketplaceSources: vi.fn(),
  createMarketplaceSource: vi.fn(),
  updateMarketplaceSource: vi.fn(),
  removeMarketplaceSource: vi.fn(),
  browseMarketplaceSource: vi.fn(),
  previewMarketplaceSkillContent: vi.fn(),
  importMarketplaceSkillContent: vi.fn(),
  previewSkillContent: vi.fn(),
  importSkillContent: vi.fn(),
  listProjectSkillInventory: vi.fn(),
  updateSkillAssignment: vi.fn(),
  getRunStatus: vi.fn(),
  getRunSelection: vi.fn(),
  getRunUsage: vi.fn(),
  getRepoAppStatus: vi.fn(),
  getRepoAppRepositories: vi.fn(),
  createRepoAppSelection: vi.fn(),
  disconnectRepoApp: vi.fn(),
  beginRepoAppConnect: vi.fn(),
  useRealRepoAppClient: false,
  repoAppFetch: vi.fn<typeof fetch>(),
  pinSourceControlRepository: vi.fn(),
  listRunOutputCaptures: vi.fn(),
  readRunOutputCaptureDiff: vi.fn(),
  beginRepoAppInstallationSetup: vi.fn(),
  getSessionTree: vi.fn(),
  getSessionStatus: vi.fn(),
  getProjectRunHistorySessions: vi.fn(),
  replayProjectRunHistoryEvents: vi.fn(),
  getDecisions: vi.fn(),
  sendProjectRunMessage: vi.fn(),
  searchKnowledge: vi.fn(),
  searchProjectKnowledge: vi.fn(),
  createKnowledgeRecord: vi.fn(),
  updateKnowledgeRecord: vi.fn(),
  readKnowledgeRevisions: vi.fn(),
  restoreKnowledgeRecord: vi.fn(),
  approveKnowledgeDecision: vi.fn(),
  exportKnowledgeRecords: vi.fn(),
  importKnowledgeRecords: vi.fn(),
  promoteKnowledgeProposal: vi.fn(),
  rejectKnowledgeProposal: vi.fn(),
  replayEvents: vi.fn(),
  streamEvents: vi.fn(),
  getAuthorizationContext: vi.fn(),
  listProjectRoleAssignments: vi.fn(),
  createProject: vi.fn(),
  readProjectBacklog: vi.fn(),
  captureBacklogTask: vi.fn(),
  listProjectIssues: vi.fn(),
  listProjectPullRequests: vi.fn(),
  getPlatformComponents: vi.fn(),
  listMarketplaceDefinitions: vi.fn(),
  packageSkillContent: vi.fn(),
  revokeProjectSkillRevision: vi.fn(),
}));

vi.mock('./api', () => ({
  gatewayClient: {
    completeRemoteMcpOAuthCallback: mocks.completeRemoteMcpOAuthCallback,
    listProjects: mocks.listProjects,
    listProjectRuns: mocks.listProjectRuns,
    getProjectMetrics: mocks.getProjectMetrics,
    getProjectUsage: mocks.getProjectUsage,
    getProject: mocks.getProject,
    getProjectConfiguration: mocks.getProjectConfiguration,
    getProjectConfigurationChoices: mocks.getProjectConfigurationChoices,
    updateProjectConfiguration: mocks.updateProjectConfiguration,
    listMarketplaceSources: mocks.listMarketplaceSources,
    createMarketplaceSource: mocks.createMarketplaceSource,
    updateMarketplaceSource: mocks.updateMarketplaceSource,
    removeMarketplaceSource: mocks.removeMarketplaceSource,
    browseMarketplaceSource: mocks.browseMarketplaceSource,
    previewMarketplaceSkillContent: mocks.previewMarketplaceSkillContent,
    importMarketplaceSkillContent: mocks.importMarketplaceSkillContent,
    previewSkillContent: mocks.previewSkillContent,
    importSkillContent: mocks.importSkillContent,
    listProjectSkillInventory: mocks.listProjectSkillInventory,
    updateSkillAssignment: mocks.updateSkillAssignment,
    getRunStatus: mocks.getRunStatus,
    getRunSelection: mocks.getRunSelection,
    getRunUsage: mocks.getRunUsage,
    pinSourceControlRepository: mocks.pinSourceControlRepository,
    listRunOutputCaptures: mocks.listRunOutputCaptures,
    readRunOutputCaptureDiff: mocks.readRunOutputCaptureDiff,
    getSessionTree: mocks.getSessionTree,
    getSessionStatus: mocks.getSessionStatus,
    getProjectRunHistorySessions: mocks.getProjectRunHistorySessions,
    replayProjectRunHistoryEvents: mocks.replayProjectRunHistoryEvents,
    getDecisions: mocks.getDecisions,
    sendProjectRunMessage: mocks.sendProjectRunMessage,
    searchKnowledge: mocks.searchKnowledge,
    searchProjectKnowledge: mocks.searchProjectKnowledge,
    createKnowledgeRecord: mocks.createKnowledgeRecord,
    updateKnowledgeRecord: mocks.updateKnowledgeRecord,
    readKnowledgeRevisions: mocks.readKnowledgeRevisions,
    restoreKnowledgeRecord: mocks.restoreKnowledgeRecord,
    approveKnowledgeDecision: mocks.approveKnowledgeDecision,
    exportKnowledgeRecords: mocks.exportKnowledgeRecords,
    importKnowledgeRecords: mocks.importKnowledgeRecords,
    promoteKnowledgeProposal: mocks.promoteKnowledgeProposal,
    rejectKnowledgeProposal: mocks.rejectKnowledgeProposal,
    replayEvents: mocks.replayEvents,
    streamEvents: mocks.streamEvents,
    getAuthorizationContext: mocks.getAuthorizationContext,
    listProjectRoleAssignments: mocks.listProjectRoleAssignments,
    createProject: mocks.createProject,
    readProjectBacklog: mocks.readProjectBacklog,
    captureBacklogTask: mocks.captureBacklogTask,
    listProjectIssues: mocks.listProjectIssues,
    listProjectPullRequests: mocks.listProjectPullRequests,
    getPlatformComponents: mocks.getPlatformComponents,
    listMarketplaceDefinitions: mocks.listMarketplaceDefinitions,
    packageSkillContent: mocks.packageSkillContent,
    revokeProjectSkillRevision: mocks.revokeProjectSkillRevision,
  },
  GatewayError: class GatewayError extends Error {
    status: number;
    code?: string;

    constructor(status: number, problem: Record<string, unknown>, message: string) {
      super(message);
      this.status = status;
      this.code = typeof problem.code === 'string' ? problem.code : undefined;
    }
  },
}));

vi.mock('./repoApp', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./repoApp')>();
  const realClient = new actual.IdentityBrokerRepoAppClient(
    'https://identity.example.test',
    mocks.repoAppFetch,
  );
  return {
    ...actual,
    repoAppBrokerClient: {
      getStatus: () => mocks.useRealRepoAppClient ? realClient.getStatus() : mocks.getRepoAppStatus(),
      getRepositories: () => mocks.useRealRepoAppClient ? realClient.getRepositories() : mocks.getRepoAppRepositories(),
      createRepositorySelection: (installationId: number, repositoryId: number) =>
        mocks.useRealRepoAppClient
          ? realClient.createRepositorySelection(installationId, repositoryId)
          : mocks.createRepoAppSelection(installationId, repositoryId),
      disconnect: (connectionId: string, revision: number) =>
        mocks.useRealRepoAppClient
          ? realClient.disconnect(connectionId, revision)
          : mocks.disconnectRepoApp(connectionId, revision),
      beginConnect: (popup: Pick<Window, 'name' | 'closed'>, signal?: AbortSignal) =>
        mocks.useRealRepoAppClient
          ? realClient.beginConnect(popup, signal)
          : mocks.beginRepoAppConnect(popup, signal),
      beginInstallationSetup: (popup: Pick<Window, 'name' | 'closed'>, signal?: AbortSignal) =>
        mocks.useRealRepoAppClient
          ? realClient.beginInstallationSetup(popup, signal)
          : mocks.beginRepoAppInstallationSetup(popup, signal),
    },
  };
});

vi.mock('./AuthContext', () => {
  const auth = {
    session: {
      accessToken: 'broker-token',
      expiresAt: Date.now() + 60_000,
      binding: null,
    },
    apiCall: <T,>(operation: (token: string, tenantSelector: string | null) => Promise<T>) =>
      operation('broker-token', mocks.tenantSelector),
    authorize: async () => {},
    // Silent sign-in for a run binding. Tests control whether it succeeds.
    ensureSession: (binding: unknown, activate?: boolean) => mocks.ensureSession(binding, activate),
    signOut: () => {},
    authorizationContext: { tenantId: 'tenant-1' },
    busy: false,
    restoringSession: false,
    consent: null,
    error: null,
    configurationError: null,
    decideConsent: async () => {},
  };
  return {
    AuthProvider: ({ children }: { children: React.ReactNode }) => {
      mocks.authProvider();
      return children;
    },
    useAuth: () => ({
      ...auth,
      session: { ...auth.session, binding: mocks.binding },
      consent: mocks.consent,
    }),
  };
});

vi.mock('./config', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./config')>();
  return { ...actual, brokerIssuer: 'https://identity.example.test/' };
});

import App from './App';
import { GatewayError } from './api';
import { clearRecentRuns } from './recentRuns';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}

function projectConfiguration(projectId: string, revision: number) {
  return {
    projectId,
    revision,
    updatedByActorId: 'actor-1',
    createdAt: '2026-01-01T00:00:00Z',
    configuration: {
      projectMarker: projectId,
      providerOverrides: [],
      orderedProviderOverrides: [],
      agentCharters: [],
      casting: [],
      blueprintWorkflowReferences: [],
      skills: [],
      runLimits: {},
    },
  };
}

function projectConfigurationChoices(projectId: string): ProjectConfigurationChoices {
  return {
    projectId,
    projectConfigurationRevision: 1,
    platformRuntimeRevision: 1,
    models: {
      state: 'unavailable',
      unavailableReason: 'model_capability_catalog_unavailable',
      runtimeEligibility: 'verifiedAtRunStart',
      bindingsRevision: null,
      defaultReasoningEffort: 'high',
      choices: [],
      savedReferences: [{ reference: 'owner-model', sources: ['platformDefault', 'projectSaved'] }],
    },
    providers: {
      projectOverridesSupported: false,
      seams: [{
        seam: 'sandbox',
        cardinality: 'exclusive',
        state: 'selected',
        unavailableReason: null,
        providerIds: ['sandbox-platform'],
      }],
      ignoredProjectOverrides: [],
    },
  };
}

function projectSummary(projectId: string, name: string, effectiveRoles: EffectiveProjectRole[] = []) {
  return {
    projectId,
    name,
    state: 'active' as const,
    revision: 1,
    configurationRevision: 1,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    effectiveRoles,
  };
}

function selectionSnapshot(projectId: string, runId: string): EffectiveRunSelection {
  return {
    projectId,
    runId,
    projectRevision: 1,
    projectConfigurationRevision: 1,
    platformRuntimeRevision: 1,
    contextRevision: 'selection-context-1',
    modelSelection: { reference: 'selected-model' },
    providers: [],
    egressAllowlist: [],
    runLimits: {},
    projectConfiguration: projectConfiguration(projectId, 1).configuration,
    egressBaseline: [],
    requiredEgress: [],
  };
}

function usageSnapshot(projectId: string, runId: string) {
  return {
    tenantId: 'tenant-1',
    projectId,
    runId,
    events: 1,
    isFullyPriced: true,
    agents: [],
    amounts: [{
      meterSource: 'model-meter',
      unit: 'USD',
      amount: 1,
      pricedEvents: 1,
      unpricedEvents: 0,
    }],
  };
}

function runStatusSnapshot(projectId: string, runId: string) {
  return {
    projectId,
    runId,
    rootSessionId: 's1',
    executionFence: 1,
    logicalTurnOrdinal: 1,
    executionState: 'active',
    stateVersion: 1,
  };
}

function sessionTreeSnapshot(projectId: string, runId: string) {
  return {
    rootSessionId: 's1',
    nodes: [{
      identity: { projectId, runId, sessionId: 's1' },
      rootSessionId: 's1',
      kind: 'coordinator' as const,
      detached: false,
      lifecycle: 'active' as const,
      executionFence: 1,
      logicalTurnOrdinal: 1,
      stateVersion: 1,
      createdAt: '2026-01-01T00:00:00Z',
    }],
  };
}

function sessionStatusSnapshot(projectId: string, runId: string) {
  return {
    identity: { projectId, runId, sessionId: 's1' },
    rootSessionId: 's1',
    kind: 'coordinator' as const,
    detached: false,
    activity: 'idle' as const,
    lifecycle: 'active' as const,
    executionFence: 1,
    stateVersion: 1,
    blockers: [],
    runtimeEffectsState: 'none',
    runtimeEffectsUnavailableCode: '',
    interruptionIntent: { state: 'none' as const },
    runExecution: { state: 'active', stateVersion: 1 },
  };
}

function configureRunSnapshots(projectId: string, runId: string) {
  mocks.getRunStatus.mockResolvedValue(runStatusSnapshot(projectId, runId));
  mocks.getRunSelection.mockResolvedValue(selectionSnapshot(projectId, runId));
  mocks.getRunUsage.mockResolvedValue(usageSnapshot(projectId, runId));
  mocks.getSessionTree.mockResolvedValue(sessionTreeSnapshot(projectId, runId));
  mocks.getSessionStatus.mockResolvedValue(sessionStatusSnapshot(projectId, runId));
  mocks.getDecisions.mockResolvedValue({
    stateVersion: 1,
    executionFence: 1,
    outcomeConfirmed: false,
    workflowConfirmed: false,
    canDecompose: false,
    canDispatch: false,
    pendingGate: null,
  });
}

function knowledgeRecord(recordId: string, projectId = 'p1', agentId = 'a1') {
  return {
    recordId,
    projectId,
    agentId,
    kind: 'memory' as const,
    type: 'note',
    title: recordId,
    content: `Content for ${recordId}`,
    importance: 'medium',
    tags: [],
    state: 'active' as const,
    trustState: 'approved' as const,
    revision: 1,
    revisionId: `${recordId}-revision-1`,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

function knowledgeRevision(recordId: string, revision: number, content: string) {
  return {
    recordId,
    revision,
    revisionId: `${recordId}-revision-${revision}`,
    kind: 'memory' as const,
    type: 'note',
    title: recordId,
    content,
    rationale: null,
    importance: 'medium',
    tags: [],
    state: 'active' as const,
    trustState: 'approved' as const,
    reason: null,
    createdAt: `2026-01-0${revision}T00:00:00Z`,
    changeKind: 'updated',
  };
}

function knowledgePage<T>(items: T[]) {
  return { items, totalCount: items.length, page: 1, pageSize: 50 };
}

function knowledgeTransferBundle(projectId = 'p1', agentId = 'a1') {
  const record = knowledgeRecord('transfer-record', projectId, agentId);
  return {
    format: 'agentweaver.knowledge-transfer.v1' as const,
    schemaVersion: 1 as const,
    projectId,
    agentId,
    records: [{ record, revisions: [knowledgeRevision(record.recordId, 1, record.content)] }],
  };
}

describe('v1 web project scoping', () => {
  beforeEach(() => {
    // Most tests here cover GitHub connections, which now live in their own Settings section.
    window.history.replaceState({}, '', '/projects/p1/settings?section=connections');
    window.name = '';
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ = undefined;
    mocks.authProvider.mockClear();
    mocks.ensureSession.mockReset();
    // By default the silent run sign-in fails, so tests see the manual sign-in path unless they say otherwise.
    mocks.ensureSession.mockResolvedValue(false);
    mocks.binding = null;
    // Recent runs live in browser storage, so clear them to keep tests independent.
    clearRecentRuns();
    mocks.consent = null;
    mocks.tenantSelector = 'tenant-1';
    mocks.completeRemoteMcpOAuthCallback.mockReset().mockResolvedValue({});
    mocks.listProjects.mockReset().mockResolvedValue([]);
    mocks.listProjectRuns.mockReset().mockImplementation(async (_token: string, projectId: string) => ({ projectId, items: [], nextCursor: null }));
    // Metrics and usage start empty so the dashboard and overview render without data.
    mocks.getProjectMetrics.mockReset().mockImplementation(async (_token: string, projectId: string) => ({ projectId, from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z', activeRuns: 0, blockedRuns: 0, runsInRange: 0, runsTotal: 0, completedRuns: 0, succeededRuns: 0, series: [] }));
    mocks.getProjectUsage.mockReset().mockImplementation(async (_token: string, projectId: string) => ({ projectId, from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z', usageByModel: [], usageByAgent: [] }));
    mocks.createProject.mockReset();
    mocks.listProjectPullRequests.mockReset().mockImplementation(async (_token: string, projectId: string) => ({
      projectId, coverage: { recordedSince: '2026-01-01T00:00:00Z', includesEarlierPullRequests: false }, items: [], nextCursor: null,
    }));
    mocks.getPlatformComponents.mockReset().mockResolvedValue({ observedAt: '2026-01-01T00:00:00Z', areas: [] });
    mocks.listMarketplaceDefinitions.mockReset().mockResolvedValue([]);
    mocks.packageSkillContent.mockReset();
    mocks.revokeProjectSkillRevision.mockReset();
    mocks.getProject.mockReset().mockImplementation(async (_token: string, projectId: string) => projectSummary(projectId, 'Fixture project'));
    mocks.getProjectConfiguration.mockReset().mockImplementation(async (_token: string, projectId: string) => projectConfiguration(projectId, 1));
    mocks.getProjectConfigurationChoices.mockReset().mockImplementation(
      async (_token: string, projectId: string) => projectConfigurationChoices(projectId),
    );
    mocks.updateProjectConfiguration.mockReset();
    mocks.listMarketplaceSources.mockReset().mockResolvedValue([]);
    mocks.createMarketplaceSource.mockReset();
    mocks.updateMarketplaceSource.mockReset();
    mocks.removeMarketplaceSource.mockReset();
    mocks.browseMarketplaceSource.mockReset();
    mocks.previewMarketplaceSkillContent.mockReset();
    mocks.importMarketplaceSkillContent.mockReset();
    mocks.previewSkillContent.mockReset();
    mocks.importSkillContent.mockReset();
    mocks.listProjectSkillInventory.mockReset();
    mocks.updateSkillAssignment.mockReset();
    mocks.getRunStatus.mockReset();
    mocks.getRunSelection.mockReset();
    mocks.getRunUsage.mockReset();
    mocks.getRepoAppStatus.mockReset();
    mocks.getRepoAppStatus.mockResolvedValue({
      state: 'not_connected',
      localReadiness: 'not_connected',
      githubLogin: null,
      connectionId: null,
      connectionRevision: null,
      accessTokenExpiresAt: null,
      updatedAt: null,
    });
    mocks.getRepoAppRepositories.mockReset();
    mocks.createRepoAppSelection.mockReset();
    mocks.disconnectRepoApp.mockReset();
    mocks.beginRepoAppConnect.mockReset();
    mocks.useRealRepoAppClient = false;
    mocks.repoAppFetch.mockReset();
    mocks.pinSourceControlRepository.mockReset();
    mocks.listRunOutputCaptures.mockReset();
    mocks.listRunOutputCaptures.mockResolvedValue({ captures: [], nextBeforeCapturedAt: null, nextBeforeCaptureId: null });
    mocks.readRunOutputCaptureDiff.mockReset();
    mocks.beginRepoAppInstallationSetup.mockReset();
    mocks.getSessionTree.mockReset();
    mocks.getSessionStatus.mockReset();
    mocks.getProjectRunHistorySessions.mockReset();
    mocks.replayProjectRunHistoryEvents.mockReset().mockResolvedValue({
      events: [], hasMore: false, nextCursor: null,
    });
    mocks.getDecisions.mockReset();
    mocks.sendProjectRunMessage.mockReset();
    mocks.searchKnowledge.mockReset();
    mocks.searchProjectKnowledge.mockReset();
    mocks.createKnowledgeRecord.mockReset();
    mocks.updateKnowledgeRecord.mockReset();
    mocks.readKnowledgeRevisions.mockReset();
    mocks.restoreKnowledgeRecord.mockReset();
    mocks.approveKnowledgeDecision.mockReset();
    mocks.exportKnowledgeRecords.mockReset();
    mocks.importKnowledgeRecords.mockReset();
    mocks.promoteKnowledgeProposal.mockReset();
    mocks.rejectKnowledgeProposal.mockReset();
    mocks.replayEvents.mockReset();
    mocks.replayEvents.mockResolvedValue({ events: [], hasMore: false, nextCursor: null });
    // An unbound caller who can read and edit p1. Pages use this context to
    // decide which actions to offer.
    mocks.getAuthorizationContext.mockReset().mockResolvedValue({
      contractVersion: 1,
      issuer: 'https://broker.test',
      actorId: 'user-1',
      tenantId: 'tenant-1',
      membershipRevision: 1,
      boundProjectId: null,
      boundRunId: null,
      effectiveAuthority: [{
        resourceType: 'project',
        resourceId: 'p1',
        permissions: [{ permission: 'readProjects', roleRevision: 1 }, { permission: 'writeProjects', roleRevision: 1 }],
      }],
    });
    mocks.listProjectRoleAssignments.mockReset().mockResolvedValue({ projectId: 'p1', assignments: [] });
    mocks.readProjectBacklog.mockReset().mockImplementation(async (_token: string, projectId: string) => ({ projectId, revision: 1, tasks: [], dependencies: [] }));
    mocks.captureBacklogTask.mockReset();
    mocks.listProjectIssues.mockReset().mockImplementation(async (_token: string, projectId: string) => ({ projectId, items: [], nextCursor: null }));
    mocks.streamEvents.mockReset();
    mocks.streamEvents.mockImplementation(() => {
      throw new Error('The event stream is not used by this test.');
    });
  });

  it('retains gallery tiles and opens project creation in a dialog', async () => {
    mocks.listProjects.mockResolvedValue([projectSummary('p1', 'Fixture project', [
      { resourceType: 'project', resourceId: 'p1', role: 'owner' },
    ])]);
    window.history.replaceState({}, '', '/projects');
    render(<App />);
    const gallery = await screen.findByRole('list', { name: 'Projects' });
    expect(within(gallery).getByText('Fixture project')).toBeTruthy();
    // The role label comes directly from the Projects owner summary, not a permission set.
    expect(await within(gallery).findByText('Owner')).toBeTruthy();
    // The create form is not inline on the page; it opens only in a dialog.
    expect(screen.queryByLabelText('Project name')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Create project' }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByLabelText('Project name')).toBeTruthy();
  });

  it('creates a project from a GitHub repository and saves the repository in its configuration', async () => {
    mocks.createProject.mockResolvedValue(projectSummary('p9', 'From GitHub'));
    // Saving the configuration returns the next revision, like the real owner.
    mocks.updateProjectConfiguration.mockImplementation(async (_token: string, projectId: string, revision: number) =>
      projectConfiguration(projectId, revision + 1));
    window.history.replaceState({}, '', '/projects');
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: 'Create project' }));
    fireEvent.change(await screen.findByLabelText('Project name'), { target: { value: 'From GitHub' } });
    fireEvent.click(screen.getByRole('radio', { name: 'GitHub repository' }));
    const submit = within(screen.getByRole('dialog')).getByRole('button', { name: 'Create project' }) as HTMLButtonElement;
    // Text that is not owner/name or a GitHub URL keeps the submit button disabled.
    fireEvent.change(screen.getByLabelText('Repository'), { target: { value: 'not a repo' } });
    expect(submit.disabled).toBe(true);
    // A full clone URL is accepted and reduced to owner and name.
    fireEvent.change(screen.getByLabelText('Repository'), { target: { value: 'https://github.com/octo/agentweaver.git' } });
    fireEvent.click(submit);

    await screen.findByText('Project From GitHub was created. It uses octo/agentweaver.');
    // The repository is saved as a second step, on revision 1 of the new project's configuration.
    expect(mocks.updateProjectConfiguration).toHaveBeenCalledWith(
      'broker-token', 'p9', 1,
      expect.objectContaining({ sourceControl: expect.objectContaining({ repository: { owner: 'octo', name: 'agentweaver' }, authMode: 'githubApp' }) }),
      'tenant-1',
    );
  });

  it('keeps a new project when its repository cannot be saved yet', async () => {
    mocks.createProject.mockResolvedValue(projectSummary('p9', 'No access yet'));
    // The project is created, but its configuration cannot be read yet, as
    // when the new grant has not reached the configuration owner.
    mocks.getProjectConfiguration.mockRejectedValue(new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    window.history.replaceState({}, '', '/projects');
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: 'Create project' }));
    fireEvent.change(await screen.findByLabelText('Project name'), { target: { value: 'No access yet' } });
    fireEvent.click(screen.getByRole('radio', { name: 'GitHub repository' }));
    fireEvent.change(screen.getByLabelText('Repository'), { target: { value: 'octo/agentweaver' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create project' }));

    await screen.findByText(/The repository was not saved\. Add it in Settings > Repository after you have access\./);
    // The project is kept, and no configuration write is tried without a current revision.
    expect(mocks.updateProjectConfiguration).not.toHaveBeenCalled();
  });

  it('uses the retained collapse control and hides unsupported navigation', async () => {
    mocks.getProject.mockResolvedValue(projectSummary('p1', 'Fixture project', [
      { resourceType: 'project', resourceId: 'p1', role: 'owner' },
    ]));
    window.history.replaceState({}, '', '/projects/p1');
    render(<App />);
    await screen.findByText('Fixture project');
    expect(await screen.findByText('Owner')).toBeTruthy();
    const nav = screen.getByRole('navigation', { name: 'Primary navigation' });
    expect(within(nav).getByRole('link', { name: 'Orchestrations' })).toBeTruthy();
    expect(within(nav).getByRole('link', { name: 'Skills' })).toBeTruthy();
    expect(within(nav).getByRole('link', { name: 'Board' }).getAttribute('href')).toBe('/projects/p1/board');
    for (const name of ['Flow', 'Workspace', 'Workflows', 'Observability', 'Diagnostics', 'Heartbeat', 'Cluster']) {
      expect(within(nav).getByRole('link', { name })).toBeTruthy();
    }
    fireEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }));
    expect(nav.getAttribute('data-collapsed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Expand navigation' }));
    expect(nav.getAttribute('data-collapsed')).toBe('false');
  });

  it.each([
    ['flow', 'Open'],
    ['workspace', 'Pull requests'],
    ['workflows', 'Saved workflow references'],
    ['observability/traces', 'Recent runs'],
    ['diagnostics', 'Areas that are not ready'],
    ['heartbeat', 'Active and blocked runs'],
    ['cluster', 'Platform components'],
  ])('opens the %s page with its supported parts and names what is missing', async (path, section) => {
    window.history.replaceState({}, '', `/projects/p1/${path}`);
    render(<App />);
    expect(await screen.findByText(section)).toBeTruthy();
    expect(screen.getByText('Not available in this version')).toBeTruthy();
    expect(window.location.pathname).toBe(`/projects/p1/${path}`);
  });

  it('reads recorded pull requests and synced issues for the Workspace page', async () => {
    mocks.listProjectIssues.mockResolvedValue({
      projectId: 'p1',
      items: [{ taskId: 't1', taskState: 'backlog', providerId: 'github', repository: { owner: 'octo', name: 'repo' }, number: 7, title: 'Fix login', state: 'open', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' }],
      nextCursor: null,
    });
    window.history.replaceState({}, '', '/projects/p1/workspace');
    render(<App />);
    expect(await screen.findByText('#7 Fix login')).toBeTruthy();
    expect(await screen.findByText('No run has opened a pull request yet.')).toBeTruthy();
    expect(mocks.listProjectPullRequests).toHaveBeenCalledWith('broker-token', 'p1', { state: 'all', pageSize: 20 }, 'tenant-1');
    expect(mocks.listProjectIssues).toHaveBeenCalledWith('broker-token', 'p1', { state: 'open', pageSize: 20 }, 'tenant-1');
  });

  it('explains a 403 for the platform report on the Cluster page', async () => {
    mocks.getPlatformComponents.mockRejectedValue(new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    window.history.replaceState({}, '', '/projects/p1/cluster');
    render(<App />);
    expect(await screen.findByText('Only tenant or platform administrators can view platform components.')).toBeTruthy();
  });

  it('counts backlog tasks by Board column on the Flow page', async () => {
    const task = (taskId: string, state: string, extra: Record<string, unknown> = {}) => ({
      reference: { projectId: 'p1', taskId }, revision: 1, state, isArchived: false,
      isAutomationInvocationPending: false, title: taskId, description: null, lifecycle: 'open', repositoryIssue: null, ...extra,
    });
    mocks.readProjectBacklog.mockResolvedValue({
      projectId: 'p1',
      revision: 2,
      tasks: [task('a', 'backlog'), task('b', 'ready'), task('c', 'claimed'), task('d', 'ready', { lifecycle: 'closed' }), task('e', 'backlog', { isArchived: true })],
      dependencies: [],
    });
    window.history.replaceState({}, '', '/projects/p1/flow');
    render(<App />);
    const list = await screen.findByLabelText('Tasks by stage');
    await waitFor(() => expect(within(list).getAllByText('1')).toHaveLength(4));
    expect(within(list).getByText('Done')).toBeTruthy();
  });

  it('opens the project Board from its address and adds a task through Start task', async () => {
    const task = (taskId: string, title: string, revision: number) => ({
      reference: { projectId: 'p1', taskId }, revision, state: 'backlog', isArchived: false,
      isAutomationInvocationPending: false, title, description: null, lifecycle: 'open', repositoryIssue: null,
    });
    mocks.readProjectBacklog.mockResolvedValue({ projectId: 'p1', revision: 3, tasks: [task('t1', 'Existing task', 1)], dependencies: [] });
    mocks.captureBacklogTask.mockImplementation(async (_token: string, projectId: string, request: { taskId: string; content: { title: string } }) => {
      // After the capture, reads return the graph with the new task.
      const graph = { projectId, revision: 4, tasks: [task('t1', 'Existing task', 1), task(request.taskId, request.content.title, 1)], dependencies: [] };
      mocks.readProjectBacklog.mockResolvedValue(graph);
      return graph;
    });
    window.history.replaceState({}, '', '/projects/p1/board');
    render(<App />);
    const backlog = await screen.findByTestId('column-backlog');
    expect(await within(backlog).findByText('Existing task')).toBeTruthy();
    expect(screen.queryByText('Not available in this version')).toBeNull();

    fireEvent.click(await screen.findByRole('button', { name: 'Start task' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'New task title' }), { target: { value: 'Write release notes' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(mocks.captureBacklogTask).toHaveBeenCalledWith(
      'broker-token', 'p1', expect.objectContaining({ content: { title: 'Write release notes' }, expectedGraphRevision: 3 }), 'tenant-1'));
    expect(await within(screen.getByTestId('column-backlog')).findByText('Write release notes')).toBeTruthy();
    expect(window.location.pathname).toBe('/projects/p1/board');
  });

  it('distinguishes unknown addresses from unavailable legacy features', async () => {
    window.history.replaceState({}, '', '/unknown-page');
    render(<App />);
    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeTruthy();
    expect(screen.queryByText('Not available in this version')).toBeNull();
  });

  it('keeps orchestration bookmarks and their selected run view', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/orchestrations/r1?view=chat&source=bookmark');
    render(<App />);
    // The old orchestrations address still opens the run, on the view it named,
    // and keeps the other query parameters.
    const chat = await screen.findByRole('tab', { name: 'Chat' });
    expect(chat.getAttribute('aria-selected')).toBe('true');
    expect(window.location.search).toContain('source=bookmark');
  });

  it('explains known permissions and links each one to Agentweaver documentation', () => {
    mocks.consent = {
      consent_required: true,
      consent_handle: 'consent-1',
      client_id: 'agentweaver-web',
      requested_scopes: [
        'openid',
        'profile',
        'email',
        'offline_access',
        'api.read',
        'projects.admin',
        'projects.orchestrator',
      ],
      csrf_token: 'csrf-1',
    };

    render(<App />);

    const scopeDetails: Record<string, { summary: string; tooltip: string; anchor: string }> = {
      openid: {
        summary: 'Lets Agentweaver confirm who you are.',
        tooltip: 'Lets you sign in to Agentweaver. The sign-in service uses an account ID to recognize you.',
        anchor: 'openid',
      },
      profile: {
        summary: 'Requests your profile details.',
        tooltip: 'Agentweaver does not include profile details in the sign-in tokens it gives to apps.',
        anchor: 'profile',
      },
      email: {
        summary: 'Requests your email address.',
        tooltip: 'Agentweaver does not include your email address in the sign-in tokens it gives to apps.',
        anchor: 'email',
      },
      offline_access: {
        summary: 'Lets the app renew your sign-in while the page is open.',
        tooltip: 'The Web app keeps tokens in memory. After a reload, it tries one silent sign-in in this tab.',
        anchor: 'offline-access',
      },
      'api.read': {
        summary: 'Lets the app send requests to Agentweaver.',
        tooltip: 'This permission alone does not give the app project access or a role. Agentweaver checks your project access and the requested action.',
        anchor: 'api-read',
      },
      'projects.admin': {
        summary: 'Lets the app ask to manage a project.',
        tooltip: 'This permission does not make you a project administrator. Agentweaver checks your project access and role before each action.',
        anchor: 'projects-admin',
      },
      'projects.orchestrator': {
        summary: 'Lets the app work with project runs.',
        tooltip: 'For each run, Agentweaver checks your current project access and your assignment to manage runs. It checks that saved settings match this project and run. This permission alone does not give project access or a role.',
        anchor: 'projects-orchestrator',
      },
    };
    const documentationUrl = 'https://sabbour.github.io/agentweaver/v1/guide/identity-scopes';

    expect(screen.getByRole('heading', { name: 'Review access' })).toBeTruthy();
    expect(screen.getByText("Agentweaver's sign-in service asks to use these permissions for this app.")).toBeTruthy();
    for (const scope of mocks.consent.requested_scopes) {
      const details = scopeDetails[scope];
      const help = screen.getByRole('button', { name: `About ${scope}` });
      const tooltip = screen.getByText(details.tooltip);
      expect(screen.getByText(scope)).toBeTruthy();
      expect(screen.getByText(details.summary)).toBeTruthy();
      expect(help.getAttribute('aria-describedby')).toContain(tooltip.id);
      expect(screen.getByRole('link', { name: `Documentation for ${scope}` }).getAttribute('href'))
        .toBe(`${documentationUrl}#${details.anchor}`);
    }
  });

  it('shows the raw name and a generic explanation for an unknown permission', () => {
    const unknownScopes = ['custom.resource', 'constructor', 'toString', '__proto__'];
    mocks.consent = {
      consent_required: true,
      consent_handle: 'consent-2',
      client_id: 'agentweaver-web',
      requested_scopes: unknownScopes,
      csrf_token: 'csrf-2',
    };

    render(<App />);

    const summary = 'Agentweaver does not explain this permission.';
    const tooltipText = 'The app or service defines what this permission allows. Ask your administrator before approving it.';
    const tooltips = screen.getAllByText(tooltipText);
    expect(screen.getAllByText(summary)).toHaveLength(unknownScopes.length);
    expect(tooltips).toHaveLength(unknownScopes.length);
    for (const [index, scope] of unknownScopes.entries()) {
      expect(screen.getByText(scope)).toBeTruthy();
      expect(screen.getByRole('button', { name: `About ${scope}` }).getAttribute('aria-describedby'))
        .toContain(tooltips[index].id);
      expect(screen.getByRole('link', { name: `Documentation for ${scope}` }).getAttribute('href'))
        .toBe('https://sabbour.github.io/agentweaver/v1/guide/identity-scopes#unknown-scopes');
    }
  });

  it('renders the Copilot popup callback outside the authenticated provider', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    window.__AGENTWEAVER_COPILOT_CALLBACK__ = {
      type: 'agentweaver.copilot-user.callback',
      state: 'request-1',
      code: 'one-time-code',
    };
    window.history.replaceState({}, '', '/auth/github/copilot-app/callback');
    try {
      render(<App />);
      await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.copilot-user.callback',
          state: 'request-1',
          code: 'one-time-code',
        },
        window.location.origin,
      ));
      expect(mocks.authProvider).not.toHaveBeenCalled();
      expect(window.__AGENTWEAVER_COPILOT_CALLBACK__).toBeUndefined();
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
      window.__AGENTWEAVER_COPILOT_CALLBACK__ = undefined;
    }
  });

  it('relays a Remote MCP OAuth authorization-code callback', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    const state = 'A'.repeat(43);
    window.history.replaceState(
      {},
      '',
      `/auth/remote-mcp/oauth/callback?state=${state}&code=provider-code`,
    );
    try {
      render(<App />);
      await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.remote-mcp.oauth.callback',
          state,
          code: 'provider-code',
        },
        window.location.origin,
      ));
      expect(window.location.search).toBe('');
      expect(mocks.authProvider).not.toHaveBeenCalled();
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('relays provider cancellation through the Remote MCP OAuth callback', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    const state = 'C'.repeat(43);
    window.history.replaceState(
      {},
      '',
      `/auth/remote-mcp/oauth/callback?state=${state}&error=access_denied`,
    );
    try {
      render(<App />);
      await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.remote-mcp.oauth.callback',
          state,
          error: 'access_denied',
        },
        window.location.origin,
      ));
      expect(window.location.search).toBe('');
      expect(mocks.authProvider).not.toHaveBeenCalled();
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('submits same-origin Remote MCP popup messages using the current bearer session', async () => {
    const popup = { closed: false } as unknown as Window;
    const state = 'B'.repeat(43);
    render(<App />);

    window.dispatchEvent(new MessageEvent('message', {
      origin: window.location.origin,
      source: popup,
      data: {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        code: 'provider-code',
      },
    }));

    await waitFor(() => expect(mocks.completeRemoteMcpOAuthCallback).toHaveBeenCalledWith(
      'broker-token',
      {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        code: 'provider-code',
      },
      'tenant-1',
    ));
    expect(await screen.findByText(
      'The Remote MCP authorization was received by Identity.',
    )).toBeDefined();
  });

  it('submits provider cancellation to Identity using the current bearer session', async () => {
    const popup = { closed: false } as unknown as Window;
    const state = 'D'.repeat(43);
    mocks.completeRemoteMcpOAuthCallback.mockRejectedValueOnce(
      new GatewayError(400, { code: 'remote_mcp_oauth_consent_denied' }, 'Consent was denied.'),
    );
    render(<App />);

    window.dispatchEvent(new MessageEvent('message', {
      origin: window.location.origin,
      source: popup,
      data: {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        error: 'access_denied',
      },
    }));

    await waitFor(() => expect(mocks.completeRemoteMcpOAuthCallback).toHaveBeenCalledWith(
      'broker-token',
      {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        error: 'access_denied',
      },
      'tenant-1',
    ));
    expect(await screen.findByText(
      'The Remote MCP authorization was canceled.',
    )).toBeDefined();
  });

  it('does not report cancellation when Identity returns success', async () => {
    const popup = { closed: false } as unknown as Window;
    const state = 'E'.repeat(43);
    render(<App />);

    window.dispatchEvent(new MessageEvent('message', {
      origin: window.location.origin,
      source: popup,
      data: {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        error: 'access_denied',
      },
    }));

    await waitFor(() => expect(mocks.completeRemoteMcpOAuthCallback).toHaveBeenCalledWith(
      'broker-token',
      {
        type: 'agentweaver.remote-mcp.oauth.callback',
        state,
        error: 'access_denied',
      },
      'tenant-1',
    ));
    expect(await screen.findByText(
      'The Remote MCP authorization was received by Identity.',
    )).toBeDefined();
    expect(screen.queryByText('The Remote MCP authorization was canceled.')).toBeNull();
  });

  it('relays only identity callbacks from the configured Broker issuer', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ =
      '?code=one-time-code&state=state-value&iss=https%3A%2F%2Fidentity.example.test%2F';
    window.history.replaceState({}, '', '/auth/callback');
    try {
      render(<App />);

      await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.identity.callback',
          state: 'state-value',
          code: 'one-time-code',
        },
        window.location.origin,
      ));
      expect(mocks.authProvider).not.toHaveBeenCalled();
    } finally {
      window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('relays an identity callback from a hidden same-origin frame to its parent', async () => {
    const previousParent = Object.getOwnPropertyDescriptor(window, 'parent');
    const parent = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'parent', { configurable: true, value: parent });
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ =
      '?code=one-time-code&state=state-value&iss=https%3A%2F%2Fidentity.example.test%2F';
    window.history.replaceState({}, '', '/auth/callback');
    try {
      render(<App />);

      await waitFor(() => expect(parent.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.identity.callback',
          state: 'state-value',
          code: 'one-time-code',
        },
        window.location.origin,
      ));
      expect(mocks.authProvider).not.toHaveBeenCalled();
    } finally {
      window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
      if (previousParent)
        Object.defineProperty(window, 'parent', previousParent);
      else
        Reflect.deleteProperty(window, 'parent');
    }
  });

  it('shows an invalid response for an identity callback from another issuer', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ =
      '?code=one-time-code&state=state-value&iss=https%3A%2F%2Fother.example.test%2F';
    window.history.replaceState({}, '', '/auth/callback');
    try {
      render(<App />);

      expect(await screen.findByText(
        'The sign-in response is invalid. Return to Agentweaver and try again.',
      )).toBeDefined();
      expect(opener.postMessage).not.toHaveBeenCalled();
      expect(close).toHaveBeenCalledOnce();
      expect(mocks.authProvider).not.toHaveBeenCalled();
    } finally {
      close.mockRestore();
      window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('returns Repo App callback results to the originating browser window', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    try {
      window.history.replaceState({}, '', '/projects?repo_app_auth=success');
      render(<App />);

      await waitFor(() => {
        expect(opener.postMessage).toHaveBeenCalledWith(
          {
            type: 'agentweaver.repo-app.callback',
            kind: 'authorization',
            outcome: 'success',
          },
          window.location.origin,
        );
      });
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('relays only the exact Broker callback from its tracked popup request', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const previousName = window.name;
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    window.name = 'agentweaver-repo-app-1-abc';
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ = true;
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    window.history.replaceState({}, '', '/settings/source-control');
    try {
      render(<App />);

      await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.repo-app.connected',
        },
        window.location.origin,
      ));
      expect(mocks.authProvider).not.toHaveBeenCalled();
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      window.name = previousName;
      window.__AGENTWEAVER_REPO_APP_CALLBACK__ = undefined;
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('does not infer a Repo App connection from a standalone callback', async () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ = true;
    window.name = '';
    Object.defineProperty(window, 'opener', { configurable: true, value: null });
    window.history.replaceState({}, '', '/settings/source-control');
    try {
      render(<App />);

      expect(await screen.findByText(
        'The Broker callback was received, but no tracked account window is available. Connection status is unverified; return to account settings and refresh status.',
      )).toBeTruthy();
      expect(mocks.authProvider).not.toHaveBeenCalled();
    } finally {
      window.__AGENTWEAVER_REPO_APP_CALLBACK__ = undefined;
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });

  it('uses Broker account status, repository browsing, selection-independent disconnect, and native popup connect', async () => {
    const connectedStatus = {
      state: 'connected',
      localReadiness: 'access_token_available',
      connectionId: 'identity-app-1',
      connectionRevision: 4,
      githubLogin: 'octo',
      accessTokenExpiresAt: '2026-10-08T18:00:00Z',
      updatedAt: '2026-10-08T17:00:00Z',
    };
    mocks.getRepoAppStatus.mockResolvedValue(connectedStatus);
    mocks.getRepoAppRepositories.mockResolvedValue({
      connectionId: 'identity-app-1',
      connectionRevision: 4,
      githubLogin: 'octo',
      repositories: [{
        installationId: 101,
        repositoryId: 202,
        fullName: 'octo/agentweaver',
        ownerLogin: 'octo',
        isPrivate: true,
        defaultBranch: 'main',
      }],
    });
    mocks.disconnectRepoApp.mockResolvedValue({
      state: 'revoked',
      localReadiness: 'reauthorization_required',
      connectionId: 'identity-app-1',
      connectionRevision: 5,
      githubLogin: 'octo',
      accessTokenExpiresAt: null,
      updatedAt: '2026-10-08T18:00:00Z',
    });
    const popup = {
      name: '',
      closed: false,
      close: vi.fn(),
    } as unknown as Window;
    const openPopup = vi.spyOn(window, 'open').mockReturnValue(popup);
    render(<App />);

    await screen.findByText('octo', { selector: 'strong' });
    expect(mocks.getRepoAppStatus).toHaveBeenCalledWith();
    fireEvent.click(screen.getByRole('button', { name: 'Browse repositories' }));
    await screen.findByText('octo/agentweaver', { selector: 'strong' });
    expect(mocks.getRepoAppRepositories).toHaveBeenCalledWith();

    fireEvent.click(screen.getByRole('button', { name: 'Disconnect GitHub' }));
    await screen.findByText('GitHub Repo App was disconnected from this identity.');
    expect(mocks.disconnectRepoApp).toHaveBeenCalledWith('identity-app-1', 4);

    fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Repo App' }));
    await waitFor(() => expect(mocks.beginRepoAppConnect).toHaveBeenCalledWith(
      popup,
      expect.any(AbortSignal),
    ));
    expect(openPopup).toHaveBeenCalledOnce();
    openPopup.mockRestore();
  });

  it('times out a Repo App popup while Broker CSRF startup is stalled', async () => {
    vi.useFakeTimers();
    const start = deferred<void>();
    let signal: AbortSignal | undefined;
    mocks.beginRepoAppConnect.mockImplementation((_popup, requestSignal) => {
      signal = requestSignal;
      return start.promise;
    });
    const popup = {
      name: '',
      closed: false,
      close() { this.closed = true; },
    };
    const openPopup = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);

    try {
      render(<App />);
      await act(async () => { await vi.advanceTimersByTimeAsync(0); });
      expect(screen.getByText('No GitHub Repo App connection is available for this identity.')).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Repo App' }));
      expect(mocks.beginRepoAppConnect).toHaveBeenCalledOnce();
      expect(signal?.aborted).toBe(false);

      await act(async () => { await vi.advanceTimersByTimeAsync(10 * 60 * 1000); });

      expect(signal?.aborted).toBe(true);
      expect(popup.closed).toBe(true);
      expect(screen.getByText(
        'GitHub authorization timed out before it started. Start the connection again.',
      )).toBeTruthy();
      expect((screen.getByRole(
        'button',
        { name: 'Connect GitHub Repo App' },
      ) as HTMLButtonElement).disabled).toBe(false);
    } finally {
      vi.useRealTimers();
      openPopup.mockRestore();
    }
  });

  it('cancels a deferred Repo App CSRF submission and ignores stale status and callbacks after navigation', async () => {
    const csrfResponse = deferred<Response>();
    const staleStatusResponse = deferred<Response>();
    const notConnected = {
      state: 'not_connected',
      localReadiness: 'not_connected',
      connectionId: null,
      connectionRevision: null,
      githubLogin: null,
      accessTokenExpiresAt: null,
      updatedAt: null,
    };
    const staleConnected = {
      state: 'connected',
      localReadiness: 'access_token_available',
      connectionId: 'stale-connection',
      connectionRevision: 1,
      githubLogin: 'stale-user',
      accessTokenExpiresAt: '2026-10-08T18:00:00Z',
      updatedAt: '2026-10-08T17:00:00Z',
    };
    let statusRequests = 0;
    mocks.useRealRepoAppClient = true;
    mocks.getProjectConfiguration.mockResolvedValue(projectConfiguration('p1', 1));
    mocks.repoAppFetch.mockImplementation((input) => {
      const url = String(input);
      if (url.endsWith('/status')) {
        statusRequests += 1;
        return statusRequests === 2
          ? staleStatusResponse.promise
          : Promise.resolve(Response.json(notConnected));
      }
      if (url.endsWith('/csrf')) return csrfResponse.promise;
      throw new Error(`Unexpected Repo App request: ${url}`);
    });
    const popup = {
      name: '',
      closed: false,
      close() { this.closed = true; },
    };
    const closePopup = vi.spyOn(popup, 'close');
    const openPopup = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    const navigate = (path: string) => {
      act(() => {
        window.history.pushState({}, '', path);
        window.dispatchEvent(new PopStateEvent('popstate'));
      });
    };

    try {
      render(<App />);
      await screen.findByText('No GitHub Repo App connection is available for this identity.');
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Repo App' }));
      await waitFor(() => expect(mocks.repoAppFetch).toHaveBeenCalledWith(
        expect.stringMatching(/\/auth\/github\/repo-app\/csrf$/),
        expect.objectContaining({ method: 'GET', signal: expect.any(AbortSignal) }),
      ));

      navigate('/projects');
      expect(closePopup).toHaveBeenCalledOnce();
      expect(popup.closed).toBe(true);

      navigate('/projects/p1/settings?section=connections');
      await waitFor(() => expect(statusRequests).toBe(2));
      navigate('/projects');
      navigate('/projects/p1/settings?section=connections');
      await screen.findByText('No GitHub Repo App connection is available for this identity.');
      expect(statusRequests).toBe(3);

      await act(async () => {
        staleStatusResponse.resolve(Response.json(staleConnected));
        await staleStatusResponse.promise;
      });
      expect(screen.queryByText('Connected as')).toBeNull();
      expect(screen.queryByText('stale-user', { selector: 'strong' })).toBeNull();

      const staleCallback = new MessageEvent('message', {
        origin: window.location.origin,
        data: { type: 'agentweaver.repo-app.connected' },
      });
      Object.defineProperty(staleCallback, 'source', { value: popup });
      act(() => window.dispatchEvent(staleCallback));
      expect(statusRequests).toBe(3);

      await act(async () => {
        csrfResponse.resolve(Response.json({ csrf_token: 'late-csrf' }));
        await csrfResponse.promise;
      });
      await waitFor(() => expect(mocks.repoAppFetch).toHaveBeenCalledTimes(4));
      expect(submit).not.toHaveBeenCalled();
      expect(mocks.repoAppFetch.mock.calls.map(([input]) => String(input)))
        .not.toContain('https://identity.example.test/auth/github/repo-app/connect');
      expect(screen.queryByText(/authorization request was cancelled/i)).toBeNull();
      expect(screen.queryByText('Connected as')).toBeNull();
    } finally {
      submit.mockRestore();
      openPopup.mockRestore();
      closePopup.mockRestore();
    }
  });

  it('does not show a Run chat shortcut in the shell', () => {
    // Even with a run-bound session, chat is reached from the run page, not a shell button.
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    window.history.replaceState({}, '', '/projects');
    render(<App />);

    expect(screen.queryByRole('button', { name: /Run chat/ })).toBeNull();
  });

  it('gets the run sign-in silently when a run opens without one', async () => {
    window.history.replaceState({}, '', '/projects/p1/runs/r1');
    // A promise that never settles keeps the silent sign-in in progress, so
    // the test can check what the page shows while it waits.
    mocks.ensureSession.mockImplementation(() => new Promise(() => {}));
    render(<App />);

    expect(await screen.findByText('Opening this run…')).toBeTruthy();
    expect(mocks.ensureSession).toHaveBeenCalledWith({ projectId: 'p1', runId: 'r1' }, undefined);
    // No sign-in button while the silent attempt may still succeed.
    expect(screen.queryByRole('button', { name: 'Sign in for this project and run' })).toBeNull();
    expect(screen.getByRole('heading', { level: 1, name: 'Orchestration' })).toBeTruthy();
  });

  it('shows the run sign-in button only after the silent attempt fails', async () => {
    // The beforeEach default makes the silent attempt resolve to false (failed).
    window.history.replaceState({}, '', '/projects/p1/runs/r1');
    render(<App />);

    expect(await screen.findByRole('button', { name: 'Sign in for this project and run' })).toBeTruthy();
    // One attempt only: the page must not loop on silent sign-in.
    expect(mocks.ensureSession).toHaveBeenCalledTimes(1);
    // The message must not use internal terms such as Broker or grant.
    expect(screen.queryByText(/Broker|grant/i)).toBeNull();
  });

  it('shows the run tree and run panel with no alerts on the main path', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=chat');
    render(<App />);

    expect(await screen.findByRole('heading', { level: 1, name: 'Orchestration' })).toBeTruthy();
    await screen.findByRole('complementary', { name: 'Run tree' });
    await screen.findByRole('tab', { name: 'Chat' });
    // Visit each run tab that exists. Some tabs may be hidden for this fixture, so skip missing ones.
    for (const view of ['Chat', 'Activity', 'Run settings', 'Usage']) {
      const tab = screen.queryByRole('tab', { name: view });
      if (!tab) continue;
      fireEvent.click(tab);
      await waitFor(() => expect(tab.getAttribute('aria-selected')).toBe('true'));
    }
    // Wait for the last tab's data so any late error would have rendered.
    await waitFor(() => expect(mocks.getRunUsage).toHaveBeenCalled());
    // The happy path shows no alerts and no "different run" binding warning.
    expect(screen.queryAllByRole('alert')).toHaveLength(0);
    expect(screen.queryByText(/different run/)).toBeNull();
  });

  it('shows addressed-message routing details in a run activity journal', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    mocks.replayEvents.mockResolvedValue({
      events: [{
        schemaVersion: 1,
        eventVersion: 1,
        eventId: 'message-event',
        identity: { projectId: 'p1', runId: 'r1', sessionId: 'agent-a' },
        position: 1,
        occurredAt: '2026-01-01T00:00:00Z',
        kind: 'addressedMessage',
        payload: {
          kind: 'addressedMessage',
          messageId: 'message-1',
          sender: { projectId: 'p1', runId: 'r1', sessionId: 'agent-a' },
          recipient: { projectId: 'p1', runId: 'r1', sessionId: 'agent-b' },
          threadSequence: 3,
          purpose: 'handoff',
        },
        objectReferences: [],
      }],
      hasMore: false,
      nextCursor: null,
    });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=activity');
    render(<App />);

    expect(await screen.findByText(/Addressed-message receipt message-1/)).toBeTruthy();
    expect(screen.getByText(/agent-a → agent-b · thread 3 · handoff/)).toBeTruthy();
  });

  it('sends user chat to the coordinator with only one active session', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    mocks.sendProjectRunMessage.mockResolvedValue({
      ownerMessageId: 'm1',
      messageId: 'events-m1',
      threadId: 'thread-1',
      threadSequence: 1,
      projectId: 'p1',
      runId: 'r1',
      rootSessionId: 's1',
      recipientSessionId: 's1',
      status: 'accepted',
      sender: { issuer: 'https://issuer.test', subject: 'user-1' },
    });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=chat');
    render(<App />);

    await screen.findByText('Message the coordinator');
    // A single coordinator session is enough; no session acts as a sender.
    expect(screen.queryByLabelText(/^From/)).toBeNull();
    expect(screen.queryByLabelText(/^To/)).toBeNull();
    fireEvent.change(screen.getByRole('textbox', { name: 'Message' }), { target: { value: 'Status?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(mocks.sendProjectRunMessage).toHaveBeenCalled());
    const [, projectId, runId, request] = mocks.sendProjectRunMessage.mock.calls[0];
    expect([projectId, runId, request]).toEqual(['p1', 'r1', { text: 'Status?' }]);
  });

  it('sends chat to an explicitly selected active run session', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const tree = sessionTreeSnapshot('p1', 'r1');
    mocks.getSessionTree.mockResolvedValue({
      ...tree,
      nodes: [...tree.nodes, {
        ...tree.nodes[0],
        identity: { projectId: 'p1', runId: 'r1', sessionId: 's2' },
        kind: 'childWork' as const,
        parentSessionId: 's1',
      }],
    });
    mocks.sendProjectRunMessage.mockResolvedValue({
      ownerMessageId: 'm2',
      messageId: 'events-m2',
      threadId: 'thread-2',
      threadSequence: 1,
      projectId: 'p1',
      runId: 'r1',
      rootSessionId: 's1',
      recipientSessionId: 's2',
      status: 'accepted',
      sender: { issuer: 'https://issuer.test', subject: 'user-1' },
    });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=chat');
    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: /Agent/ }));
    await screen.findByText('Message the agent');
    fireEvent.change(screen.getByRole('textbox', { name: 'Message' }), { target: { value: 'Status?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(mocks.sendProjectRunMessage).toHaveBeenCalled());
    expect(mocks.sendProjectRunMessage.mock.calls[0].slice(1, 4))
      .toEqual(['p1', 'r1', { text: 'Status?', targetSessionId: 's2' }]);
  });

  it('lists project runs in Orchestrations and opens one on Chat', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    mocks.listProjectRuns.mockImplementation(async (_token: string, projectId: string, options: { states?: string[] }) => ({
      projectId,
      nextCursor: null,
      items: options.states?.includes('open')
        ? [{
          runId: 'r1', rootSessionId: null, title: 'Fix the build', goal: null, workflowId: null,
          state: 'active', needsInputCount: 0, createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z', completedAt: null,
          startedBy: { issuer: 'https://issuer.test', subject: 'user' },
        }]
        : [],
    }));
    window.history.replaceState({}, '', '/projects/p1/orchestrations');
    render(<App />);

    fireEvent.click(await screen.findByRole('link', { name: 'Open run Fix the build' }));

    const chatTab = await screen.findByRole('tab', { name: 'Chat' });
    await waitFor(() => expect(chatTab.getAttribute('aria-selected')).toBe('true'));
    expect(window.location.pathname).toBe('/projects/p1/runs/r1');
  });

  it('opens the Chat tab from the view query and preserves other query parameters', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=chat&source=notification');
    render(<App />);

    const chatTab = await screen.findByRole('tab', { name: 'Chat' });
    await waitFor(() => expect(chatTab.getAttribute('aria-selected')).toBe('true'));
    expect(new URLSearchParams(window.location.search).get('source')).toBe('notification');
  });

  it('updates the run view query when tabs change without dropping other query parameters', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=chat&source=notification');
    render(<App />);

    await screen.findByRole('tab', { name: 'Chat' });
    fireEvent.click(screen.getByRole('tab', { name: 'Activity' }));

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get('view')).toBe('activity');
      expect(params.get('source')).toBe('notification');
    });
    expect(screen.getByRole('tab', { name: 'Activity' }).getAttribute('aria-selected')).toBe('true');
  });

  it('pins a selected Repo App repository only for the exact accepted run and tenant', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const selection = selectionSnapshot('p1', 'r1');
    selection.projectConfiguration.sourceControl = {
      authMode: 'githubApp',
      appConnectionId: 'identity-app-1',
    };
    mocks.getRunSelection.mockResolvedValue(selection);
    mocks.getRepoAppStatus.mockResolvedValue({
      state: 'connected',
      localReadiness: 'access_token_available',
      githubLogin: 'octo',
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      accessTokenExpiresAt: '2026-10-08T18:00:00Z',
      updatedAt: '2026-10-08T17:00:00Z',
    });
    mocks.getRepoAppRepositories.mockResolvedValue({
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      githubLogin: 'octo',
      repositories: [{
        installationId: 101,
        repositoryId: 202,
        fullName: 'octo/agentweaver',
        ownerLogin: 'octo',
        isPrivate: true,
        defaultBranch: 'main',
      }],
    });
    mocks.createRepoAppSelection.mockResolvedValue({
      code: 'opaque-selection-code',
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      installationId: 101,
      repositoryId: 202,
      repositoryFullName: 'octo/agentweaver',
    });
    mocks.pinSourceControlRepository.mockResolvedValue({
      pinId: 'pin-1',
      repository: 'octo/agentweaver',
      providerId: 'github',
      resourceId: 'resource-1',
      resourceGeneration: 1,
      providerRepositoryId: 123,
      defaultBranch: 'main',
      isPrivate: true,
      pinnedAt: '2026-10-08T00:00:00Z',
    });
    const popup = {
      name: '',
      closed: false,
      close: vi.fn(),
      location: { replace: vi.fn() },
    } as unknown as Window;
    const openPopup = vi.spyOn(window, 'open').mockReturnValue(popup);
    mocks.beginRepoAppInstallationSetup.mockResolvedValue(undefined);
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=changes');
    render(<App />);

    await screen.findByRole('heading', { name: 'GitHub App repository access' });
    await screen.findByText('octo', { selector: 'strong' });
    fireEvent.click(screen.getByRole('button', { name: 'Install GitHub App for this run' }));
    await waitFor(() => expect(mocks.beginRepoAppInstallationSetup).toHaveBeenCalledWith(popup, undefined));
    await act(async () => {
      window.dispatchEvent(new MessageEvent('message', {
        origin: window.location.origin,
        source: popup,
        data: { type: 'agentweaver.repo-app.connected' },
      }));
    });
    await screen.findByRole('combobox', { name: 'Repository' });
    expect(mocks.getRepoAppStatus).toHaveBeenCalledTimes(2);
    expect(mocks.getRepoAppRepositories).toHaveBeenCalledOnce();
    expect(screen.queryByText('GitHub App installation completed.')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Pin repository to this run' }));

    await screen.findByText('Pinned repository: octo/agentweaver · main');
    expect(mocks.createRepoAppSelection).toHaveBeenCalledWith(101, 202);
    expect(mocks.getRepoAppStatus).toHaveBeenCalledWith();
    expect(mocks.getRepoAppRepositories).toHaveBeenCalledWith();
    expect(mocks.pinSourceControlRepository).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      's1',
      'tenant-1',
      'opaque-selection-code',
    );
    openPopup.mockRestore();
  });

  it.each(['unavailable', 'different tenant'])(
    'uses the confirmed tenant selector when run usage is %s',
    async (usageState) => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const selection = selectionSnapshot('p1', 'r1');
    selection.projectConfiguration.sourceControl = {
      authMode: 'githubApp',
      appConnectionId: 'identity-app-1',
    };
    mocks.getRunSelection.mockResolvedValue(selection);
    if (usageState === 'unavailable')
      mocks.getRunUsage.mockRejectedValue(new Error('usage unavailable'));
    else
      mocks.getRunUsage.mockResolvedValue({ ...usageSnapshot('p1', 'r1'), tenantId: 'tenant-2' });
    mocks.getRepoAppStatus.mockResolvedValue({
      state: 'connected',
      localReadiness: 'access_token_available',
      githubLogin: 'octo',
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      accessTokenExpiresAt: '2026-10-08T18:00:00Z',
      updatedAt: '2026-10-08T17:00:00Z',
    });
    mocks.getRepoAppRepositories.mockResolvedValue({
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      githubLogin: 'octo',
      repositories: [{
        installationId: 101,
        repositoryId: 202,
        fullName: 'octo/agentweaver',
        ownerLogin: 'octo',
        isPrivate: true,
        defaultBranch: 'main',
      }],
    });
    mocks.createRepoAppSelection.mockResolvedValue({
      code: 'opaque-selection-code',
      connectionId: 'identity-app-1',
      connectionRevision: 3,
      installationId: 101,
      repositoryId: 202,
      repositoryFullName: 'octo/agentweaver',
    });
    mocks.pinSourceControlRepository.mockResolvedValue({
      pinId: 'pin-1',
      repository: 'octo/agentweaver',
      providerId: 'github',
      resourceId: 'resource-1',
      resourceGeneration: 1,
      providerRepositoryId: 123,
      defaultBranch: 'main',
      isPrivate: true,
      pinnedAt: '2026-10-08T00:00:00Z',
    });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=changes');
    render(<App />);

    await screen.findByRole('button', { name: 'Install GitHub App for this run' });
    expect(screen.queryByText(/tenant selector is unavailable/i)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Load repositories' }));
    await screen.findByRole('combobox', { name: 'Repository' });
    fireEvent.click(screen.getByRole('button', { name: 'Pin repository to this run' }));

    await screen.findByText('Pinned repository: octo/agentweaver · main');
    expect(mocks.pinSourceControlRepository).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      's1',
      'tenant-1',
      'opaque-selection-code',
    );
  });

  it('lists produced files on the Changes tab and shows the patch of one file set', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const capture = {
      captureId: 'cap-1', eventId: 'e1', eventPosition: 4, capturedAt: '2026-10-08T00:00:00Z',
      workspaceId: 'w1', branchName: 'agent/feature', baseSha: 'abcdef1234567', outputTreeSha: 't1',
      manifestSha256: 'm', manifestByteLength: 10, patchSha256: 'p', patchByteLength: 2048, packageByteLength: 4096,
    };
    mocks.listRunOutputCaptures.mockResolvedValue({ captures: [capture], nextBeforeCapturedAt: null, nextBeforeCaptureId: null });
    mocks.readRunOutputCaptureDiff.mockResolvedValue({ capture, patch: '+added line' });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=changes');
    render(<App />);

    expect(await screen.findByText('agent/feature')).toBeTruthy();
    expect(mocks.listRunOutputCaptures).toHaveBeenCalledWith('broker-token', 'p1', 'r1', 's1', { limit: 20 }, 'tenant-1');
    fireEvent.click(screen.getByRole('button', { name: 'Show changes' }));
    expect(await screen.findByLabelText('Changes on agent/feature')).toHaveProperty('textContent', expect.stringContaining('added line'));
    expect(mocks.readRunOutputCaptureDiff).toHaveBeenCalledWith('broker-token', 'p1', 'r1', 's1', 'cap-1', 'tenant-1');
  });

  it('explains when produced files are hidden for this run', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    mocks.listRunOutputCaptures.mockRejectedValue(new GatewayError(404, { code: 'source_control_output_capture_unavailable' }, 'Not found'));
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=changes');
    render(<App />);

    expect(await screen.findByText(/The files of this run are not available/)).toBeTruthy();
  });
  it('does not claim the Repo App is disconnected when its status owner is unavailable', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const selection = selectionSnapshot('p1', 'r1');
    selection.projectConfiguration.sourceControl = {
      authMode: 'githubApp',
      appConnectionId: 'identity-app-1',
    };
    mocks.getRunSelection.mockResolvedValue(selection);
    mocks.getRepoAppStatus.mockRejectedValue(new Error('Identity Broker owner unavailable'));
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=changes');
    render(<App />);

    expect(await screen.findByText(
      'GitHub App connection status is unavailable; no connection state is assumed.',
    )).toBeTruthy();
    expect(screen.queryByText('No GitHub App user connection is available for this identity.')).toBeNull();
    expect(screen.queryByRole(
      'link',
      { name: 'Connect GitHub Repo App and configure this project' },
    )).toBeNull();
  });

  it('defaults invalid run view queries to Chat while preserving other query parameters', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=unsupported&source=notification');
    render(<App />);

    await screen.findByRole('tab', { name: 'Chat' });
    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get('view')).toBe('chat');
      expect(params.get('source')).toBe('notification');
    });
    expect(screen.getByRole('tab', { name: 'Chat' }).getAttribute('aria-selected')).toBe('true');
  });

  it('hides the previous project and ignores a stale project response after navigation', async () => {
    const oldProjectLoad = deferred<ReturnType<typeof projectSummary>>();
    const currentProjectLoad = deferred<ReturnType<typeof projectSummary>>();
    mocks.getProject.mockImplementation((_token: string, projectId: string) => {
      if (projectId === 'p1') return Promise.resolve(projectSummary('p1', 'Project One'));
      if (projectId === 'p2') return oldProjectLoad.promise;
      return currentProjectLoad.promise;
    });

    render(<App />);
    act(() => {
      window.history.pushState({}, '', '/projects/p1');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await screen.findByText('Project One');

    act(() => {
      window.history.pushState({}, '', '/projects/p2');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p2', 'tenant-1'));
    expect(screen.queryByText('Project One')).toBeNull();

    act(() => {
      window.history.pushState({}, '', '/projects/p3');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p3', 'tenant-1'));
    await act(async () => {
      oldProjectLoad.resolve(projectSummary('p2', 'Project Two'));
      await oldProjectLoad.promise;
    });
    expect(screen.queryByText('Project Two')).toBeNull();

    await act(async () => {
      currentProjectLoad.resolve(projectSummary('p3', 'Project Three'));
      await currentProjectLoad.promise;
    });
    await screen.findByText('Project Three');
  });

  it('shows the 0.x settings rail with section icons and a Risk badge on Danger zone', async () => {
    mocks.getProjectConfiguration.mockResolvedValue(projectConfiguration('p1', 2));
    window.history.replaceState({}, '', '/projects/p1/settings?section=advanced');
    render(<App />);

    const rail = await screen.findByRole('navigation', { name: 'Settings sections' });
    const links = within(rail).getAllByRole('link');
    expect(links.length).toBeGreaterThan(0);
    // Every section link has an icon, matching the 0.x settings layout.
    for (const link of links) {
      expect(link.querySelector('.v1-settings-rail__icon svg')).not.toBeNull();
    }
    // The section from the URL is marked as the current page.
    expect(within(rail).getByRole('link', { name: 'Advanced' }).getAttribute('aria-current')).toBe('page');
    // Danger zone carries a visible Risk badge so it is not opened by accident.
    const danger = within(rail).getByRole('link', { name: /Danger zone/ });
    expect(within(danger).getByText('Risk')).toBeTruthy();
  });

  it('loads owner configuration choices and shows saved model references as unverified', async () => {
    const current = projectConfiguration('p1', 1);
    mocks.getProjectConfiguration.mockResolvedValue({
      ...current,
      configuration: { ...current.configuration, modelSelection: { reference: 'owner-model' } },
    });
    window.history.replaceState({}, '', '/projects/p1/settings?section=models');
    render(<App />);

    // The owner has no model-binding list, so saved references are shown as unverified, not as choices.
    expect(await screen.findByText(/no runtime model-binding list configured/)).toBeTruthy();
    expect(screen.getByText('owner-model (platformDefault, projectSaved)')).toBeTruthy();
    const modelInput = screen.getByLabelText('Default model reference') as HTMLInputElement;
    expect(modelInput.tagName).toBe('INPUT');
    expect(modelInput.value).toBe('owner-model');
    await waitFor(() => expect(mocks.getProjectConfigurationChoices).toHaveBeenCalledWith(
      'broker-token', 'p1', 'tenant-1',
    ));
  });

  it('does not show or save an old project configuration after navigation', async () => {
    const oldProjectLoad = deferred<ReturnType<typeof projectConfiguration>>();
    mocks.getProjectConfiguration.mockImplementation((_token: string, projectId: string) =>
      projectId === 'p1' ? oldProjectLoad.promise : Promise.resolve(projectConfiguration('p2', 2)));
    mocks.updateProjectConfiguration.mockImplementation(async (
      _token: string,
      projectId: string,
      revision: number,
      configuration: ReturnType<typeof projectConfiguration>['configuration'],
    ) => ({
      ...projectConfiguration(projectId, revision + 1),
      configuration,
    }));

    // The JSON editor is in the Advanced section.
    window.history.replaceState({}, '', '/projects/p1/settings?section=advanced');
    render(<App />);
    await waitFor(() => expect(mocks.getProjectConfiguration).toHaveBeenCalledWith('broker-token', 'p1', 'tenant-1'));

    act(() => {
      window.history.pushState({}, '', '/projects/p2/settings?section=advanced');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });

    await screen.findByText('Revision 2');
    const editor = screen.getByLabelText('Configuration JSON') as HTMLTextAreaElement;
    expect(editor.value).toContain('"projectMarker": "p2"');

    await act(async () => {
      oldProjectLoad.resolve(projectConfiguration('p1', 1));
      await oldProjectLoad.promise;
    });

    expect((screen.getByLabelText('Configuration JSON') as HTMLTextAreaElement).value)
      .toContain('"projectMarker": "p2"');
    const shown = screen.getByLabelText('Configuration JSON') as HTMLTextAreaElement;
    // Save is enabled only after an edit. Re-entering the same JSON without
    // spaces makes the form dirty without changing the configuration.
    fireEvent.change(shown, { target: { value: JSON.stringify(JSON.parse(shown.value)) } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(mocks.updateProjectConfiguration).toHaveBeenCalledOnce());
    expect(mocks.updateProjectConfiguration).toHaveBeenCalledWith(
      'broker-token',
      'p2',
      2,
      expect.objectContaining({ projectMarker: 'p2' }),
      'tenant-1',
    );
  });

  it('loads marketplace sources through the selected project with the current tenant', async () => {
    window.history.replaceState({}, '', '/projects/p1/skills');
    mocks.getProjectConfiguration.mockResolvedValue(projectConfiguration('p1', 4));
    mocks.listMarketplaceSources.mockResolvedValue([{
      sourceId: 'source-1',
      name: 'Team skills',
      repository: 'example/skills',
      requestedRef: 'main',
      subpath: null,
      revision: 7,
      state: 'active',
    }]);
    mocks.browseMarketplaceSource.mockResolvedValue({
      sourceId: 'source-1',
      sourceRevision: 7,
      requestedRef: 'main',
      resolvedCommitSha: '0123456789abcdef',
      candidates: [{ location: 'skills/sample', name: 'Sample skill', description: 'A sample' }],
      total: 1,
      page: 1,
      pageSize: 25,
      hasMore: false,
    });
    mocks.previewMarketplaceSkillContent.mockResolvedValue({
      name: 'Sample skill',
      description: 'A sample',
      contentDigest: 'sha256:source',
      resourceCount: 0,
      totalBytes: 14,
    });
    mocks.importMarketplaceSkillContent.mockResolvedValue({
      skillId: 'skill-source',
      revision: 2,
      name: 'Sample skill',
      description: 'A sample',
      contentDigest: 'sha256:source',
      resourceCount: 0,
      totalBytes: 14,
    });

    render(<App />);

    await screen.findByRole('tab', { name: 'Catalog' });
    await waitFor(() => expect(mocks.listMarketplaceSources).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'tenant-1',
    ));
    fireEvent.click(screen.getByRole('button', { name: 'Browse marketplaces' }));
    // happy-dom loses Tabster's modal focus when the busy button disables, so it marks the
    // dialog aria-hidden. Chromium keeps it exposed; `hidden: true` keeps these role queries.
    // The flow is browse, preview, then import at the exact commit the preview showed.
    fireEvent.click(await screen.findByRole('button', { name: 'Browse skills', hidden: true }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill', hidden: true }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill', hidden: true }));
    await screen.findByText('Imported Sample skill at revision 2.');

    expect(mocks.previewMarketplaceSkillContent).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'source-1',
      {
        expectedSourceRevision: 7,
        resolvedCommitSha: '0123456789abcdef',
        selectedPath: 'skills/sample',
      },
      'tenant-1',
    );
    expect(mocks.importMarketplaceSkillContent).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'source-1',
      expect.objectContaining({
        expectedSourceRevision: 7,
        resolvedCommitSha: '0123456789abcdef',
        selectedPath: 'skills/sample',
        idempotencyKey: expect.any(String),
        expectedContentDigest: 'sha256:source',
      }),
      'tenant-1',
    );
  });

  it('keeps Skills separate from configuration editing while assignment is pending', async () => {
    window.history.replaceState({}, '', '/projects/p1/skills');
    const base = projectConfiguration('p1', 4);
    const configured = {
      ...base,
      configuration: {
        ...base.configuration,
        agentCharters: [{ agentId: 'agent-1', name: 'Agent One' }],
        casting: [{ agentId: 'agent-1' }],
      },
    };
    const assignment = {
      skillId: 'skill-1',
      enabled: true,
      order: 0,
      revision: 1,
      contentDigest: 'sha256:sample',
      agentIds: ['agent-1'],
    };
    const saved = {
      ...configured,
      revision: 5,
      configuration: {
        ...configured.configuration,
        skills: [assignment],
      },
    };
    const pendingAssignment = deferred<typeof saved>();
    mocks.getProjectConfiguration.mockResolvedValue(configured);
    mocks.previewSkillContent.mockResolvedValue({
      name: 'Sample skill',
      description: 'A sample skill',
      contentDigest: 'sha256:sample',
      resourceCount: 0,
      totalBytes: 14,
    });
    mocks.packageSkillContent.mockResolvedValue({
      skills: [{ location: '.', candidate: { skillMarkdown: '# Sample skill', resources: [] }, preview: null, error: null, skippedPaths: [] }],
    });
    mocks.importSkillContent.mockResolvedValue({
      ...assignment,
      skillId: 'skill-1',
      name: 'Sample skill',
      description: 'A sample skill',
      resourceCount: 0,
      totalBytes: 14,
    });
    mocks.updateSkillAssignment.mockReturnValue(pendingAssignment.promise);
    render(<App />);

    await screen.findByRole('tab', { name: 'Catalog' });
    expect(screen.queryByLabelText('Typed ProjectConfiguration JSON')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Import skill' }));
    fireEvent.change(await screen.findByLabelText('Skill files or ZIP'), {
      target: { files: [new File(['# Sample skill'], 'SKILL.md', { type: 'text/markdown' })] },
    });
    // See the marketplace test: happy-dom can mark the open dialog aria-hidden.
    fireEvent.click(screen.getByRole('button', { name: 'Check skill files', hidden: true }));
    await screen.findByText('sha256:sample');
    fireEvent.click(screen.getByRole('button', { name: 'Import skill to project', hidden: true }));
    await screen.findByText('Imported Sample skill at revision 1.');
    fireEvent.click(screen.getByRole('button', { name: 'Close', hidden: true }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Agent One' }));
    fireEvent.click(screen.getByRole('button', { name: 'Assign to selected agents' }));
    await waitFor(() => expect(mocks.updateSkillAssignment).toHaveBeenCalledOnce());

    // While the save is pending, the button is disabled so it cannot be sent twice.
    expect((screen.getByRole('button', { name: 'Saving assignment…' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByLabelText('Typed ProjectConfiguration JSON')).toBeNull();

    await act(async () => {
      pendingAssignment.resolve(saved);
      await pendingAssignment.promise;
    });

    await screen.findByText(/Saved this assignment in configuration revision 5/);
    expect(mocks.updateSkillAssignment).toHaveBeenCalledOnce();
  });

  it('clears an accepted selection and usage after a later snapshot read fails', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.getRunStatus.mockResolvedValue(runStatusSnapshot('p1', 'r1'));
    mocks.getRunSelection
      .mockResolvedValueOnce(selectionSnapshot('p1', 'r1'))
      .mockRejectedValueOnce(new Error('selection unavailable'));
    mocks.getRunUsage
      .mockResolvedValueOnce(usageSnapshot('p1', 'r1'))
      .mockRejectedValueOnce(new Error('usage unavailable'));
    mocks.getSessionTree.mockResolvedValue(sessionTreeSnapshot('p1', 'r1'));
    mocks.getSessionStatus.mockResolvedValue(sessionStatusSnapshot('p1', 'r1'));
    mocks.getDecisions.mockResolvedValue({
      stateVersion: 1,
      executionFence: 1,
      outcomeConfirmed: false,
      workflowConfirmed: false,
      canDecompose: false,
      canDispatch: false,
      pendingGate: null,
    });

    window.history.replaceState({}, '', '/projects/p1/runs/r1');
    render(<App />);
    await screen.findByRole('tab', { name: 'Run settings' });
    fireEvent.click(screen.getByRole('tab', { name: 'Run settings' }));
    await screen.findByText('selected-model');
    fireEvent.click(screen.getByRole('tab', { name: 'Usage' }));
    await screen.findByText('model-meter');

    // The second reads fail. Refresh must clear the old values instead of
    // showing them as if they were still current.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh run' }));
    await screen.findByText('No usage was reported for this run.');
    expect(screen.queryByText('model-meter')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Run settings' }));
    await screen.findByText('The service returned no settings for this run.');
    expect(screen.queryByText('selected-model')).toBeNull();
  });

  it('paginates Knowledge search results beyond the first page', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockImplementation((
      _token: string,
      projectId: string,
      _runId: string,
      agentId: string,
      options: { page?: number; pageSize?: number } = {},
    ) => {
      const page = options.page ?? 1;
      return Promise.resolve({
        items: [knowledgeRecord(`record-${page}`, projectId, agentId)],
        totalCount: 51,
        page,
        pageSize: options.pageSize ?? 50,
      });
    });

    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);
    await screen.findByRole('heading', { name: 'record-1' });
    expect(screen.getByText(/Page 1 of 2/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await screen.findByRole('heading', { name: 'record-2' });
    expect(screen.queryByRole('heading', { name: 'record-1' })).toBeNull();
    expect(mocks.searchKnowledge).toHaveBeenLastCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      expect.objectContaining({ page: 2, pageSize: 50 }),
      'tenant-1',
    );
  });

  it('discovers records across a project without run or agent selection and reads their history', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    const record = { ...knowledgeRecord('project-record', 'p1', 'agent-b'), sourceRunId: 'origin-run' };
    mocks.searchProjectKnowledge.mockResolvedValue(knowledgePage([
      { accessRunId: 'run-b', record },
    ]));
    mocks.listProjectRuns.mockResolvedValue({
      projectId: 'p1',
      items: [{
        runId: 'work-run',
        rootSessionId: 'coordinator',
        title: 'Recent work',
        goal: null,
        workflowId: null,
        state: 'completed',
        needsInputCount: 0,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        completedAt: '2026-01-01T00:01:00Z',
        startedBy: { issuer: 'https://broker.test', subject: 'user-1' },
      }],
      nextCursor: null,
    });
    mocks.readKnowledgeRevisions.mockResolvedValue(knowledgePage([
      knowledgeRevision('project-record', 1, 'Earlier content'),
    ]));
    window.history.replaceState({}, '', '/projects/p1/knowledge');

    render(<App />);

    await screen.findByRole('heading', { name: 'project-record' });
    expect(mocks.ensureSession).toHaveBeenCalledWith(null, false);
    expect(mocks.searchProjectKnowledge).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      expect.objectContaining({ page: 1, pageSize: 50 }),
      'tenant-1',
    );
    expect(screen.getByRole('link', { name: 'run-b' }).getAttribute('href'))
      .toBe('/projects/p1/runs/run-b');
    expect(screen.getByText(/Source run origin-run/)).toBeTruthy();
    expect(screen.getByText(/Access run/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Session tree' }).getAttribute('href'))
      .toBe('/projects/p1/runs/work-run/history?view=sessions');
    expect(screen.getByRole('link', { name: 'Activity' }).getAttribute('href'))
      .toBe('/projects/p1/runs/work-run/history?view=activity');

    fireEvent.click(screen.getByRole('button', { name: 'View revision history' }));

    await screen.findByText('Earlier content');
    expect(mocks.readKnowledgeRevisions).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'run-b',
      'agent-b',
      'project-record',
      expect.objectContaining({ page: 1, pageSize: 25 }),
      'tenant-1',
    );
  });

  it('opens project run history with project-read routes instead of run-owner snapshots', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    mocks.getProjectRunHistorySessions.mockResolvedValue({
      rootSessionId: 'root-1',
      nodes: [{
        identity: { projectId: 'p1', runId: 'work-run', sessionId: 'root-1' },
        rootSessionId: 'root-1',
        kind: 'coordinator',
        detached: false,
        lifecycle: 'completed',
        executionFence: 1,
        logicalTurnOrdinal: 1,
        stateVersion: 1,
        createdAt: '2026-01-01T00:00:00Z',
      }],
    });
    mocks.replayProjectRunHistoryEvents.mockResolvedValue({
      events: [{
        schemaVersion: 1,
        eventVersion: 1,
        eventId: 'event-1',
        identity: { projectId: 'p1', runId: 'work-run', sessionId: 'root-1' },
        position: 1,
        occurredAt: '2026-01-01T00:01:00Z',
        kind: 'addressedMessage',
        payload: {},
        objectReferences: [],
      }],
      nextCursor: null,
      hasMore: false,
    });
    window.history.replaceState({}, '', '/projects/p1/runs/work-run/history?view=sessions');

    render(<App />);

    await screen.findByText('root-1');
    expect(mocks.ensureSession).toHaveBeenCalledWith(null, false);
    expect(mocks.getProjectRunHistorySessions).toHaveBeenCalledWith(
      'broker-token', 'p1', 'work-run', 'tenant-1',
    );
    expect(mocks.getRunStatus).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('tab', { name: 'Activity' }));
    expect(await screen.findByText('Position 1')).toBeTruthy();
    expect(mocks.replayProjectRunHistoryEvents).toHaveBeenCalledWith(
      'broker-token', 'p1', 'work-run', null, 100, 'tenant-1',
    );
  });

  it('uses the shared pager for project-wide discovery', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    mocks.searchProjectKnowledge.mockResolvedValue({
      ...knowledgePage([
        { accessRunId: 'run-a', record: knowledgeRecord('page-record', 'p1', 'agent-a') },
      ]),
      totalCount: 51,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge');
    render(<App />);

    const pager = await screen.findByRole('navigation', { name: 'Pagination' });
    fireEvent.click(within(pager).getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(mocks.searchProjectKnowledge).toHaveBeenLastCalledWith(
      'broker-token',
      'p1',
      expect.objectContaining({ page: 2, pageSize: 50 }),
      'tenant-1',
    ));
    expect(await screen.findByText(/Page 2 of 2/)).toBeTruthy();
  });

  it('moves to the last valid page when a refresh shrinks project-wide results', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    mocks.searchProjectKnowledge
      .mockResolvedValueOnce({
        ...knowledgePage([
          { accessRunId: 'run-a', record: knowledgeRecord('first-page-record', 'p1', 'agent-a') },
        ]),
        totalCount: 51,
      })
      .mockResolvedValueOnce({
        ...knowledgePage([
          { accessRunId: 'run-b', record: knowledgeRecord('second-page-record', 'p1', 'agent-b') },
        ]),
        page: 2,
        totalCount: 51,
      })
      .mockResolvedValueOnce({
        ...knowledgePage([]),
        page: 2,
        totalCount: 3,
      })
      .mockResolvedValueOnce(knowledgePage([
        { accessRunId: 'run-a', record: knowledgeRecord('refreshed-record', 'p1', 'agent-a') },
      ]));
    window.history.replaceState({}, '', '/projects/p1/knowledge');
    render(<App />);

    const pager = await screen.findByRole('navigation', { name: 'Pagination' });
    await screen.findByRole('heading', { name: 'first-page-record' });
    fireEvent.click(within(pager).getByRole('button', { name: 'Next' }));
    await screen.findByRole('heading', { name: 'second-page-record' });
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await screen.findByRole('heading', { name: 'refreshed-record' });
    expect(mocks.searchProjectKnowledge).toHaveBeenLastCalledWith(
      'broker-token',
      'p1',
      expect.objectContaining({ page: 1, pageSize: 50 }),
      'tenant-1',
    );
  });

  it('shows empty, failed, and denied project-wide search states', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    mocks.searchProjectKnowledge
      .mockResolvedValueOnce(knowledgePage([]))
      .mockRejectedValueOnce(new Error('Search unavailable'))
      .mockRejectedValueOnce(new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    window.history.replaceState({}, '', '/projects/p1/knowledge');
    render(<App />);

    expect(await screen.findByText('No project-wide records match this search.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText(/Search unavailable/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText(/Forbidden|403/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Sign in for project-wide search' })).toBeNull();
  });

  it('reloads project-wide search results', async () => {
    mocks.ensureSession.mockResolvedValue(true);
    mocks.searchProjectKnowledge.mockResolvedValue(knowledgePage([
      { accessRunId: 'run-a', record: knowledgeRecord('before-refresh', 'p1', 'agent-a') },
    ]));
    window.history.replaceState({}, '', '/projects/p1/knowledge');
    render(<App />);

    await screen.findByRole('heading', { name: 'before-refresh' });
    mocks.searchProjectKnowledge.mockResolvedValue(knowledgePage([
      { accessRunId: 'run-b', record: knowledgeRecord('after-refresh', 'p1', 'agent-b') },
    ]));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await screen.findByRole('heading', { name: 'after-refresh' });
    expect(screen.queryByRole('heading', { name: 'before-refresh' })).toBeNull();
  });

  it('limits Knowledge creation to supported kinds and importance values', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([knowledgeRecord('record-1')]));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'record-1' });
    const createKind = screen.getByLabelText('Knowledge record kind') as HTMLSelectElement;
    expect(Array.from(createKind.options).map((option) => option.value))
      .toEqual(['memory', 'proposal', 'sessionContext']);
    const importance = screen.getByLabelText('Knowledge record importance') as HTMLSelectElement;
    expect(importance.value).toBe('medium');
    expect(Array.from(importance.options).map((option) => option.value)).toEqual(['low', 'medium', 'high']);
  });

  it('corrects an active record with its expected revision and refreshes from Knowledge', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    const current = knowledgeRecord('record-1');
    const corrected = {
      ...current,
      content: 'Corrected from the owner',
      revision: 2,
      revisionId: 'record-1-revision-2',
    };
    mocks.searchKnowledge.mockResolvedValueOnce(knowledgePage([current])).mockResolvedValue(knowledgePage([corrected]));
    mocks.updateKnowledgeRecord.mockResolvedValue({
      status: 'updated',
      record: corrected,
      currentRevision: 2,
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'record-1' });
    fireEvent.click(screen.getByRole('button', { name: 'Correct record' }));
    fireEvent.change(screen.getByLabelText('Correction content'), { target: { value: 'Corrected from the owner' } });
    fireEvent.change(screen.getByLabelText('Correction reason'), { target: { value: 'Fix inaccurate note' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save correction' }));

    await screen.findByText('Corrected from the owner');
    expect(mocks.updateKnowledgeRecord).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'record-1',
      1,
      expect.objectContaining({
        content: 'Corrected from the owner',
        importance: 'medium',
        state: 'active',
        reason: 'Fix inaccurate note',
      }),
      expect.any(String),
      'tenant-1',
    );
    expect(mocks.searchKnowledge).toHaveBeenCalledTimes(2);
    expect(mocks.searchKnowledge).toHaveBeenLastCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      expect.objectContaining({ page: 1, pageSize: 50 }),
      'tenant-1',
    );
  });

  it('rejects a correction response containing a record outside the exact project and agent scope', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    const current = knowledgeRecord('record-1');
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([current]));
    mocks.updateKnowledgeRecord.mockResolvedValue({
      status: 'updated',
      record: knowledgeRecord('foreign-record', 'p2', 'a2'),
      currentRevision: 2,
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'record-1' });
    fireEvent.click(screen.getByRole('button', { name: 'Correct record' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save correction' }));

    await screen.findByText('Knowledge returned a mutation record outside the exact project and agent scope.');
    expect(screen.queryByRole('heading', { name: 'foreign-record' })).toBeNull();
    expect(mocks.searchKnowledge).toHaveBeenCalledOnce();
  });

  it('inspects revision history and restores a historical revision using the current CAS revision', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    const current = {
      ...knowledgeRecord('record-1'),
      revision: 2,
      revisionId: 'record-1-revision-2',
    };
    const restored = {
      ...current,
      content: 'Restored by the owner',
      revision: 3,
      revisionId: 'record-1-revision-3',
    };
    mocks.searchKnowledge.mockResolvedValueOnce(knowledgePage([current])).mockResolvedValue(knowledgePage([restored]));
    mocks.readKnowledgeRevisions.mockResolvedValue({
      items: [
        knowledgeRevision('record-1', 1, 'Earlier version'),
        knowledgeRevision('record-1', 2, 'Current version'),
      ],
      totalCount: 2,
      page: 1,
      pageSize: 50,
    });
    mocks.restoreKnowledgeRecord.mockResolvedValue({
      status: 'updated',
      record: restored,
      currentRevision: 3,
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'record-1' });
    fireEvent.click(screen.getByRole('button', { name: 'View revision history' }));
    await screen.findByText('Earlier version');
    expect(mocks.readKnowledgeRevisions).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'record-1',
      { page: 1, pageSize: 50 },
      'tenant-1',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Restore revision 1' }));
    await screen.findByText('Restored by the owner');
    expect(mocks.restoreKnowledgeRecord).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'record-1',
      2,
      1,
      null,
      expect.any(String),
      'tenant-1',
    );
    expect(mocks.searchKnowledge).toHaveBeenCalledTimes(2);
  });

  it('does not display a late revision response under another record', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([
      knowledgeRecord('record-a'),
      knowledgeRecord('record-b'),
    ]));
    const lateFirstHistory = deferred<{
      items: ReturnType<typeof knowledgeRevision>[];
      totalCount: number;
      page: number;
      pageSize: number;
    }>();
    mocks.readKnowledgeRevisions.mockImplementation((
      _token: string,
      _projectId: string,
      _runId: string,
      _agentId: string,
      recordId: string,
    ) => recordId === 'record-a'
      ? lateFirstHistory.promise
      : Promise.resolve({
        items: [knowledgeRevision('record-b', 1, 'History for record B')],
        totalCount: 1,
        page: 1,
        pageSize: 50,
      }));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'record-a' });
    fireEvent.click(screen.getAllByRole('button', { name: 'View revision history' })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'View revision history' }));
    await screen.findByText('History for record B');
    expect(screen.queryByText('History for record A')).toBeNull();

    await act(async () => {
      lateFirstHistory.resolve({
        items: [knowledgeRevision('record-a', 1, 'History for record A')],
        totalCount: 1,
        page: 1,
        pageSize: 50,
      });
    });
    expect(screen.getByText('History for record B')).toBeTruthy();
    expect(screen.queryByText('History for record A')).toBeNull();
  });

  it('approves an Active Pending Decision with its expected revision and refreshes owner state', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    const decision = {
      ...knowledgeRecord('decision-1'),
      kind: 'decision' as const,
      trustState: 'pending' as const,
    };
    const approved = {
      ...decision,
      revision: 2,
      revisionId: 'decision-1-revision-2',
      trustState: 'approved' as const,
    };
    mocks.searchKnowledge.mockResolvedValueOnce(knowledgePage([decision])).mockResolvedValue(knowledgePage([approved]));
    mocks.approveKnowledgeDecision.mockResolvedValue({
      status: 'updated',
      record: approved,
      currentRevision: 2,
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'decision-1' });
    expect(screen.queryByRole('button', { name: 'Correct record' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Archive record' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Approve Decision' }));
    await screen.findByText(/trust: approved/);
    expect(mocks.approveKnowledgeDecision).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'decision-1',
      1,
      null,
      expect.any(String),
      'tenant-1',
    );
    expect(mocks.searchKnowledge).toHaveBeenCalledTimes(2);
  });

  it('supersedes an Active Decision with another scoped Decision using the current revision', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    const current = {
      ...knowledgeRecord('decision-1'),
      kind: 'decision' as const,
      revision: 4,
      revisionId: 'decision-1-revision-4',
    };
    const replacement = {
      ...knowledgeRecord('decision-2'),
      kind: 'decision' as const,
    };
    const superseded = {
      ...current,
      state: 'superseded' as const,
      revision: 5,
      revisionId: 'decision-1-revision-5',
      supersededByRecordId: replacement.recordId,
    };
    mocks.searchKnowledge
      .mockResolvedValueOnce(knowledgePage([current, replacement]))
      .mockResolvedValue(knowledgePage([superseded, replacement]));
    mocks.updateKnowledgeRecord.mockResolvedValue({
      status: 'updated',
      record: superseded,
      currentRevision: 5,
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByRole('heading', { name: 'decision-1' });
    fireEvent.change(screen.getByLabelText('Replacement Decision for decision-1'), {
      target: { value: 'decision-2' },
    });
    const decisionCard = screen.getByRole('heading', { name: 'decision-1' }).closest('article');
    if (!decisionCard) throw new Error('Decision record card was not rendered.');
    fireEvent.click(within(decisionCard).getByRole('button', { name: 'Supersede with selected Decision' }));

    await screen.findByText('Superseded by Decision decision-2.');
    expect(mocks.updateKnowledgeRecord).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'decision-1',
      4,
      expect.objectContaining({
        state: 'superseded',
        supersededByRecordId: 'decision-2',
      }),
      expect.any(String),
      'tenant-1',
    );
    expect(mocks.searchKnowledge).toHaveBeenCalledTimes(2);
  });

  it('exports a versioned bundle only for the selected exact scope', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    mocks.exportKnowledgeRecords.mockResolvedValue(knowledgeTransferBundle());
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.click(screen.getByRole('button', { name: 'Export scoped Knowledge' }));
    await screen.findByLabelText('Exported Knowledge bundle');

    expect(mocks.exportKnowledgeRecords).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      'tenant-1',
    );
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:knowledge-export');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    try {
      fireEvent.click(screen.getByRole('button', { name: 'Download versioned bundle' }));
      expect(createObjectUrl).toHaveBeenCalledWith(expect.any(Blob));
      expect(anchorClick).toHaveBeenCalledOnce();
      await waitFor(() => expect(revokeObjectUrl).toHaveBeenCalledWith('blob:knowledge-export'));
    } finally {
      createObjectUrl.mockRestore();
      revokeObjectUrl.mockRestore();
      anchorClick.mockRestore();
    }
  });

  it('rejects an exported bundle that contains records outside the selected scope', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    mocks.exportKnowledgeRecords.mockResolvedValue(knowledgeTransferBundle('other-project', 'a1'));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.click(screen.getByRole('button', { name: 'Export scoped Knowledge' }));
    await screen.findByText('Knowledge transfer bundle is outside the exact project, run, and agent scope.');
    expect(screen.queryByLabelText('Exported Knowledge bundle')).toBeNull();
  });

  it('requires explicit import confirmation and imports a versioned bundle before refreshing owner state', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    mocks.importKnowledgeRecords.mockResolvedValue({
      records: [knowledgeRecord('imported-record')],
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.change(screen.getByLabelText('Versioned Knowledge bundle JSON'), {
      target: { value: JSON.stringify(knowledgeTransferBundle()) },
    });
    const importButton = screen.getByRole('button', { name: 'Import scoped Knowledge' });
    expect((importButton as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('checkbox', { name: /I confirm this versioned bundle/ }));
    expect((importButton as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(importButton);

    await waitFor(() => expect(mocks.importKnowledgeRecords).toHaveBeenCalledOnce());
    expect(mocks.importKnowledgeRecords).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      'a1',
      knowledgeTransferBundle(),
      expect.any(String),
      'tenant-1',
    );
    await waitFor(() => expect(mocks.searchKnowledge).toHaveBeenCalledTimes(2));
  });

  it('rejects an import bundle whose project scope does not match before submission', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.change(screen.getByLabelText('Versioned Knowledge bundle JSON'), {
      target: { value: JSON.stringify(knowledgeTransferBundle('p2', 'a1')) },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: /I confirm this versioned bundle/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Import scoped Knowledge' }));

    await screen.findByText('Knowledge transfer bundle is outside the exact project, run, and agent scope.');
    expect(mocks.importKnowledgeRecords).not.toHaveBeenCalled();
  });

  it('hides records outside the selected scope in an import response', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    mocks.importKnowledgeRecords.mockResolvedValue({
      records: [knowledgeRecord('foreign-record', 'p2', 'a2')],
      isDuplicate: false,
    });
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.change(screen.getByLabelText('Versioned Knowledge bundle JSON'), {
      target: { value: JSON.stringify(knowledgeTransferBundle()) },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: /I confirm this versioned bundle/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Import scoped Knowledge' }));

    await screen.findByText('Knowledge returned records outside the exact project and agent scope.');
    expect(mocks.searchKnowledge).toHaveBeenCalledOnce();
    expect(screen.queryByRole('heading', { name: 'foreign-record' })).toBeNull();
  });

  it('rejects import input larger than the 1 MiB limit before submission', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching records.');
    fireEvent.change(screen.getByLabelText('Versioned Knowledge bundle JSON'), {
      target: { value: `${JSON.stringify(knowledgeTransferBundle())}${' '.repeat(1024 * 1024 + 1)}` },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: /I confirm this versioned bundle/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Import scoped Knowledge' }));

    await screen.findByText('The Knowledge import bundle exceeds the 1 MiB transfer limit.');
    expect(mocks.importKnowledgeRecords).not.toHaveBeenCalled();
  });

  it('hides Knowledge search results returned for another project or agent', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([
      knowledgeRecord('valid-record'),
      knowledgeRecord('foreign-record', 'p2', 'a2'),
    ]));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('Knowledge returned records outside the exact project and agent scope.');
    expect(screen.queryByRole('heading', { name: 'valid-record' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'foreign-record' })).toBeNull();
  });

});
