import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { EffectiveRunSelection } from './contracts';

const mocks = vi.hoisted(() => ({
  binding: null as { projectId: string; runId: string } | null,
  listProjects: vi.fn(),
  getProject: vi.fn(),
  getProjectConfiguration: vi.fn(),
  updateProjectConfiguration: vi.fn(),
  getRunStatus: vi.fn(),
  getRunSelection: vi.fn(),
  getRunUsage: vi.fn(),
  getRepoAppAuthorizationStatus: vi.fn(),
  listRepoAppRepositorySelections: vi.fn(),
  issueRepoAppRepositorySelection: vi.fn(),
  pinSourceControlRepository: vi.fn(),
  getSessionTree: vi.fn(),
  getSessionStatus: vi.fn(),
  getDecisions: vi.fn(),
  searchKnowledge: vi.fn(),
  replayEvents: vi.fn(),
  streamEvents: vi.fn(),
}));

vi.mock('./api', () => ({
  gatewayClient: {
    listProjects: mocks.listProjects,
    getProject: mocks.getProject,
    getProjectConfiguration: mocks.getProjectConfiguration,
    updateProjectConfiguration: mocks.updateProjectConfiguration,
    getRunStatus: mocks.getRunStatus,
    getRunSelection: mocks.getRunSelection,
    getRunUsage: mocks.getRunUsage,
    getRepoAppAuthorizationStatus: mocks.getRepoAppAuthorizationStatus,
    listRepoAppRepositorySelections: mocks.listRepoAppRepositorySelections,
    issueRepoAppRepositorySelection: mocks.issueRepoAppRepositorySelection,
    pinSourceControlRepository: mocks.pinSourceControlRepository,
    getSessionTree: mocks.getSessionTree,
    getSessionStatus: mocks.getSessionStatus,
    getDecisions: mocks.getDecisions,
    searchKnowledge: mocks.searchKnowledge,
    replayEvents: mocks.replayEvents,
    streamEvents: mocks.streamEvents,
  },
  GatewayError: class GatewayError extends Error {
    status = 0;
    code?: string;
  },
}));

vi.mock('./AuthContext', () => {
  const auth = {
    session: {
      accessToken: 'broker-token',
      expiresAt: Date.now() + 60_000,
      binding: null,
    },
    apiCall: <T,>(operation: (token: string) => Promise<T>) => operation('broker-token'),
    authorize: async () => {},
    signOut: () => {},
    busy: false,
    consent: null,
    error: null,
    configurationError: null,
    decideConsent: async () => {},
  };
  return {
    AuthProvider: ({ children }: { children: React.ReactNode }) => children,
    useAuth: () => ({
      ...auth,
      session: { ...auth.session, binding: mocks.binding },
    }),
  };
});

import App from './App';

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
    importance: 'normal',
    tags: [],
    state: 'active' as const,
    trustState: 'approved' as const,
    revision: 1,
    revisionId: `${recordId}-revision-1`,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('v1 web project scoping', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/projects/p1/settings');
    mocks.binding = null;
    mocks.listProjects.mockReset().mockResolvedValue([]);
    mocks.getProject.mockReset();
    mocks.getProjectConfiguration.mockReset();
    mocks.updateProjectConfiguration.mockReset();
    mocks.getRunStatus.mockReset();
    mocks.getRunSelection.mockReset();
    mocks.getRunUsage.mockReset();
    mocks.getRepoAppAuthorizationStatus.mockReset();
    mocks.getRepoAppAuthorizationStatus.mockResolvedValue({
      connected: false,
      githubLogin: null,
      connectionId: null,
    });
    mocks.listRepoAppRepositorySelections.mockReset();
    mocks.issueRepoAppRepositorySelection.mockReset();
    mocks.pinSourceControlRepository.mockReset();
    mocks.getSessionTree.mockReset();
    mocks.getSessionStatus.mockReset();
    mocks.getDecisions.mockReset();
    mocks.searchKnowledge.mockReset();
    mocks.replayEvents.mockReset();
    mocks.replayEvents.mockResolvedValue({ events: [], hasMore: false, nextCursor: null });
    mocks.streamEvents.mockReset();
    mocks.streamEvents.mockImplementation(() => {
      throw new Error('The event stream is not used by this test.');
    });
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
    mocks.getRepoAppAuthorizationStatus.mockResolvedValue({
      connected: true,
      githubLogin: 'octo',
      connectionId: 'identity-app-1',
    });
    mocks.listRepoAppRepositorySelections.mockResolvedValue({
      repositories: [{
        fullName: 'octo/agentweaver',
        ownerLogin: 'octo',
        isPrivate: true,
        defaultBranch: 'main',
        pushedAt: null,
      }],
      installations: [],
    });
    mocks.issueRepoAppRepositorySelection.mockResolvedValue({
      selectionCode: 'opaque-selection-code',
      expiresAt: '2026-10-08T01:00:00Z',
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

    await screen.findByRole('heading', { name: 'GitHub App repository access' });
    await screen.findByText('octo', { selector: 'strong' });
    fireEvent.click(screen.getByRole('button', { name: 'Load repositories' }));
    await screen.findByRole('combobox', { name: 'Repository' });
    fireEvent.click(screen.getByRole('button', { name: 'Pin repository to this run' }));

    await screen.findByText('Pinned repository: octo/agentweaver · main');
    expect(mocks.issueRepoAppRepositorySelection).toHaveBeenCalledWith(
      'broker-token',
      'octo/agentweaver',
    );
    expect(mocks.pinSourceControlRepository).toHaveBeenCalledWith(
      'broker-token',
      'p1',
      'r1',
      's1',
      'tenant-1',
      'opaque-selection-code',
    );
  });

  it('disables run-bound Repo App operations when the tenant selector is unavailable', async () => {
    mocks.binding = { projectId: 'p1', runId: 'r1' };
    configureRunSnapshots('p1', 'r1');
    const selection = selectionSnapshot('p1', 'r1');
    selection.projectConfiguration.sourceControl = {
      authMode: 'githubApp',
      appConnectionId: 'identity-app-1',
    };
    mocks.getRunSelection.mockResolvedValue(selection);
    mocks.getRunUsage.mockRejectedValue(new Error('usage unavailable'));
    mocks.getRepoAppAuthorizationStatus.mockResolvedValue({
      connected: true,
      githubLogin: 'octo',
      connectionId: 'identity-app-1',
    });
    window.history.replaceState({}, '', '/projects/p1/runs/r1?view=selection');
    render(<App />);

    expect(await screen.findByText(
      'The current run tenant selector is unavailable; run-bound App installation and pinning are disabled.',
    )).toBeTruthy();
    const installButton = await screen.findByRole('button', { name: 'Install GitHub App for this run' });
    expect(
      (installButton as HTMLButtonElement).disabled,
    ).toBe(true);
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
    mocks.getRepoAppAuthorizationStatus.mockRejectedValue(new Error('Identity Broker owner unavailable'));
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
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p2'));
    expect(screen.queryByRole('heading', { name: 'Project One' })).toBeNull();

    act(() => {
      window.history.pushState({}, '', '/projects/p3');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(mocks.getProject).toHaveBeenCalledWith('broker-token', 'p3'));
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
    await waitFor(() => expect(mocks.getProjectConfiguration).toHaveBeenCalledWith('broker-token', 'p1'));

    act(() => {
      window.history.pushState({}, '', '/projects/p2/settings');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });

    await screen.findByRole('heading', { name: 'Revision 2' });
    const editor = screen.getByRole('textbox') as HTMLTextAreaElement;
    expect(editor.value).toContain('"projectMarker": "p2"');

    await act(async () => {
      oldProjectLoad.resolve(projectConfiguration('p1', 1));
      await oldProjectLoad.promise;
    });

    expect((screen.getByRole('textbox') as HTMLTextAreaElement).value).toContain('"projectMarker": "p2"');
    fireEvent.click(screen.getByRole('button', { name: 'Append configuration revision' }));
    await waitFor(() => expect(mocks.updateProjectConfiguration).toHaveBeenCalledOnce());
    expect(mocks.updateProjectConfiguration).toHaveBeenCalledWith(
      'broker-token',
      'p2',
      2,
      expect.objectContaining({ projectMarker: 'p2' }),
    );
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
    );
  });

});
