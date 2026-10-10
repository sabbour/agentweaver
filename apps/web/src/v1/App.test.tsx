import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { EffectiveRunSelection } from './contracts';

const mocks = vi.hoisted(() => ({
  binding: null as { projectId: string; runId: string } | null,
  authProvider: vi.fn(),
  tenantSelector: 'tenant-1',
  completeRemoteMcpOAuthCallback: vi.fn(),
  listProjects: vi.fn(),
  getProject: vi.fn(),
  getProjectConfiguration: vi.fn(),
  updateProjectConfiguration: vi.fn(),
  listMarketplaceSources: vi.fn(),
  createMarketplaceSource: vi.fn(),
  updateMarketplaceSource: vi.fn(),
  removeMarketplaceSource: vi.fn(),
  browseMarketplaceSource: vi.fn(),
  previewSkillContent: vi.fn(),
  importSkillContent: vi.fn(),
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
  beginRepoAppInstallationSetup: vi.fn(),
  getSessionTree: vi.fn(),
  getSessionStatus: vi.fn(),
  getDecisions: vi.fn(),
  searchKnowledge: vi.fn(),
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
}));

vi.mock('./api', () => ({
  gatewayClient: {
    completeRemoteMcpOAuthCallback: mocks.completeRemoteMcpOAuthCallback,
    listProjects: mocks.listProjects,
    getProject: mocks.getProject,
    getProjectConfiguration: mocks.getProjectConfiguration,
    updateProjectConfiguration: mocks.updateProjectConfiguration,
    listMarketplaceSources: mocks.listMarketplaceSources,
    createMarketplaceSource: mocks.createMarketplaceSource,
    updateMarketplaceSource: mocks.updateMarketplaceSource,
    removeMarketplaceSource: mocks.removeMarketplaceSource,
    browseMarketplaceSource: mocks.browseMarketplaceSource,
    previewSkillContent: mocks.previewSkillContent,
    importSkillContent: mocks.importSkillContent,
    updateSkillAssignment: mocks.updateSkillAssignment,
    getRunStatus: mocks.getRunStatus,
    getRunSelection: mocks.getRunSelection,
    getRunUsage: mocks.getRunUsage,
    pinSourceControlRepository: mocks.pinSourceControlRepository,
    getSessionTree: mocks.getSessionTree,
    getSessionStatus: mocks.getSessionStatus,
    getDecisions: mocks.getDecisions,
    searchKnowledge: mocks.searchKnowledge,
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
    signOut: () => {},
    busy: false,
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
    }),
  };
});

vi.mock('./config', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./config')>();
  return { ...actual, brokerIssuer: 'https://identity.example.test/' };
});

import App from './App';
import { GatewayError } from './api';

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

function projectSummary(projectId: string, name: string) {
  return {
    projectId,
    name,
    state: 'active' as const,
    revision: 1,
    configurationRevision: 1,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
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
    window.history.replaceState({}, '', '/projects/p1/settings');
    window.name = '';
    window.__AGENTWEAVER_REPO_APP_CALLBACK__ = undefined;
    mocks.authProvider.mockClear();
    mocks.binding = null;
    mocks.tenantSelector = 'tenant-1';
    mocks.completeRemoteMcpOAuthCallback.mockReset().mockResolvedValue({});
    mocks.listProjects.mockReset().mockResolvedValue([]);
    mocks.getProject.mockReset();
    mocks.getProjectConfiguration.mockReset();
    mocks.updateProjectConfiguration.mockReset();
    mocks.listMarketplaceSources.mockReset().mockResolvedValue([]);
    mocks.createMarketplaceSource.mockReset();
    mocks.updateMarketplaceSource.mockReset();
    mocks.removeMarketplaceSource.mockReset();
    mocks.browseMarketplaceSource.mockReset();
    mocks.previewSkillContent.mockReset();
    mocks.importSkillContent.mockReset();
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
    mocks.beginRepoAppInstallationSetup.mockReset();
    mocks.getSessionTree.mockReset();
    mocks.getSessionStatus.mockReset();
    mocks.getDecisions.mockReset();
    mocks.searchKnowledge.mockReset();
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
    mocks.streamEvents.mockReset();
    mocks.streamEvents.mockImplementation(() => {
      throw new Error('The event stream is not used by this test.');
    });
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
        'GitHub authorization timed out before the Identity Broker could start it. Start the connection again.',
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

      navigate('/projects/p1/settings');
      await waitFor(() => expect(statusRequests).toBe(2));
      navigate('/projects');
      navigate('/projects/p1/settings');
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

  it('keeps Run chat disabled until a session has an exact run binding', () => {
    window.history.replaceState({}, '', '/projects?preset=1');
    render(<App />);

    const runChat = screen.getByRole('button', { name: 'Run chat opens from a run' }) as HTMLButtonElement;
    expect(runChat.disabled).toBe(true);
    fireEvent.click(runChat);
    expect(window.location.pathname).toBe('/projects');
    expect(window.location.search).toBe('?preset=1');
  });

  it('opens the exact bound run directly on Chat from the shell', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects');
    render(<App />);

    fireEvent.click(screen.getByRole('button', { name: 'Run chat' }));

    const chatTab = await screen.findByRole('tab', { name: 'Chat' });
    await waitFor(() => expect(chatTab.getAttribute('aria-selected')).toBe('true'));
    expect(window.location.pathname).toBe('/projects/p1/runs/r1');
    expect(new URLSearchParams(window.location.search).get('view')).toBe('chat');
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
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=selection');
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
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=selection');
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
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=selection');
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

  it('defaults invalid run view queries to Topology while preserving other query parameters', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=unsupported&source=notification');
    render(<App />);

    await screen.findByRole('tab', { name: 'Topology' });
    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get('view')).toBe('topology');
      expect(params.get('source')).toBe('notification');
    });
    expect(screen.getByRole('tab', { name: 'Topology' }).getAttribute('aria-selected')).toBe('true');
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
    await screen.findByRole('heading', { name: 'Project One' });

    act(() => {
      window.history.pushState({}, '', '/projects/p2');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p2', 'tenant-1'));
    expect(screen.queryByRole('heading', { name: 'Project One' })).toBeNull();

    act(() => {
      window.history.pushState({}, '', '/projects/p3');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p3', 'tenant-1'));
    await act(async () => {
      oldProjectLoad.resolve(projectSummary('p2', 'Project Two'));
      await oldProjectLoad.promise;
    });
    expect(screen.queryByRole('heading', { name: 'Project Two' })).toBeNull();

    await act(async () => {
      currentProjectLoad.resolve(projectSummary('p3', 'Project Three'));
      await currentProjectLoad.promise;
    });
    await screen.findByRole('heading', { name: 'Project Three' });
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

    render(<App />);
    await waitFor(() => expect(mocks.getProjectConfiguration).toHaveBeenCalledWith('broker-token', 'p1', 'tenant-1'));

    act(() => {
      window.history.pushState({}, '', '/projects/p2/settings');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });

    await screen.findByRole('heading', { name: 'Revision 2' });
    const editor = screen.getByLabelText('Typed ProjectConfiguration JSON') as HTMLTextAreaElement;
    expect(editor.value).toContain('"projectMarker": "p2"');

    await act(async () => {
      oldProjectLoad.resolve(projectConfiguration('p1', 1));
      await oldProjectLoad.promise;
    });

    expect((screen.getByLabelText('Typed ProjectConfiguration JSON') as HTMLTextAreaElement).value)
      .toContain('"projectMarker": "p2"');
    fireEvent.click(screen.getByRole('button', { name: 'Append configuration revision' }));
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
    mocks.getProjectConfiguration.mockResolvedValue(projectConfiguration('p1', 4));
    mocks.listMarketplaceSources.mockResolvedValue([]);

    render(<App />);

    await screen.findByRole('heading', { name: 'Skills and marketplace' });
    await waitFor(() => expect(mocks.listMarketplaceSources).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'tenant-1',
    ));
    expect(screen.getByText(/Runtime load status is not available from this API/)).toBeTruthy();
  });

  it('prevents configuration edits while a skill assignment is pending', async () => {
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

    await screen.findByRole('heading', { name: 'Revision 4' });
    await screen.findByRole('heading', { name: 'Skills and marketplace' });
    const editor = screen.getByLabelText('Typed ProjectConfiguration JSON') as HTMLTextAreaElement;
    const originalEditor = editor.value;
    fireEvent.change(screen.getByLabelText('Skill folder or files'), {
      target: { files: [new File(['# Sample skill'], 'SKILL.md', { type: 'text/markdown' })] },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate skill files' }));
    await screen.findByText('sha256:sample');
    fireEvent.click(screen.getByRole('button', { name: 'Import skill to project' }));
    await screen.findByText(/The Skills owner imported Sample skill at revision 1/);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Agent One' }));
    fireEvent.click(screen.getByRole('button', { name: 'Assign to selected agents' }));
    await waitFor(() => expect(mocks.updateSkillAssignment).toHaveBeenCalledOnce());

    expect(editor.readOnly).toBe(true);
    fireEvent.change(editor, { target: { value: '{"projectMarker":"unsaved edit"}' } });
    expect(editor.value).toBe(originalEditor);

    await act(async () => {
      pendingAssignment.resolve(saved);
      await pendingAssignment.promise;
    });

    await screen.findByText(/saved this assignment in configuration revision 5/);
    expect(editor.value).toBe(JSON.stringify(saved.configuration, null, 2));
    expect(editor.value).not.toContain('unsaved edit');
    expect(editor.readOnly).toBe(false);
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
    await screen.findByRole('tab', { name: 'Selection' });
    fireEvent.click(screen.getByRole('tab', { name: 'Selection' }));
    await screen.findByText('selected-model');
    fireEvent.click(screen.getByRole('tab', { name: 'Usage' }));
    await screen.findByText('model-meter');

    fireEvent.click(screen.getByRole('button', { name: 'Refresh owner snapshots' }));
    await screen.findByText('No usage totals were returned by Events & Sessions.');
    expect(screen.queryByText('model-meter')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Selection' }));
    await screen.findByText('No accepted run-selection snapshot was returned by Projects & Config.');
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

    await screen.findByText('No matching Knowledge records.');
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

    await screen.findByText('No matching Knowledge records.');
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

    await screen.findByText('No matching Knowledge records.');
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

    await screen.findByText('No matching Knowledge records.');
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

    await screen.findByText('No matching Knowledge records.');
    fireEvent.change(screen.getByLabelText('Versioned Knowledge bundle JSON'), {
      target: { value: JSON.stringify(knowledgeTransferBundle()) },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: /I confirm this versioned bundle/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Import scoped Knowledge' }));

    await screen.findByText('Knowledge returned records outside the exact project and agent scope.');
    expect(mocks.searchKnowledge).toHaveBeenCalledOnce();
    expect(screen.queryByRole('heading', { name: 'foreign-record' })).toBeNull();
  });

  it('rejects import input larger than the Gateway 1 MiB limit before submission', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    mocks.searchKnowledge.mockResolvedValue(knowledgePage([]));
    window.history.replaceState({}, '', '/projects/p1/knowledge?runId=r1&agentId=a1');
    render(<App />);

    await screen.findByText('No matching Knowledge records.');
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
