/// <reference types="node" />

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { gunzipSync } from 'node:zlib';
import { AddMemberDialog, TeamSyncPanel } from './AgentsTeamControls';
import { AgentweaverGatewayClient, GatewayError } from './api';
import { IdentityBrokerRepoAppClient } from './repoApp';
import { contentArchive } from './__tests__/agentSuiteArchiveFixtures';
import type { ProjectConfiguration, RemoteMcpConnectionDirectoryEntry } from './contracts';

const api = vi.hoisted(() => ({
  listCatalogRoles: vi.fn(),
  createCastingDraftProposal: vi.fn(),
  exportProjectAgentSuite: vi.fn(),
  importProjectAgentSuite: vi.fn(),
  listRemoteMcpConnections: vi.fn(),
  previewProjectTeamSync: vi.fn(),
  commitProjectTeamSync: vi.fn(),
}));
const broker = vi.hoisted(() => ({
  getRepositories: vi.fn(),
  createRepositorySelection: vi.fn(),
}));
// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({ apiCall: (operation: (token: string, tenant: string) => unknown) => operation('token', 'tenant-1') }));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));
vi.mock('./repoApp', async (importActual) => ({
  ...(await importActual<typeof import('./repoApp')>()),
  repoAppBrokerClient: broker,
}));

const configuration: ProjectConfiguration = {
  providerOverrides: [],
  orderedProviderOverrides: [],
  agentCharters: [{ agentId: 'lead', name: 'Lead', role: 'lead', charter: 'Lead.' }],
  casting: [{ agentId: 'lead', role: 'lead', order: 0 }],
  blueprintWorkflowReferences: [],
  skills: [],
  runLimits: {},
};

/** Shows the current address so tests can check navigation. */
function Location() {
  const location = useLocation();
  return <p data-testid="location">{location.pathname}{location.search}</p>;
}

function renderAt(element: React.ReactElement) {
  render(
    <MemoryRouter initialEntries={['/projects/p1/team']}>
      <Routes><Route path="*" element={<>{element}<Location /></>} /></Routes>
    </MemoryRouter>,
  );
}

function stubDecompressionStream(): void {
  // Happy DOM does not consistently expose the browser gzip stream API.
  class TestDecompressionStream extends TransformStream<Uint8Array, Uint8Array> {
    constructor(format: string) {
      if (format !== 'gzip') throw new Error('Unsupported compression format.');
      const chunks: Uint8Array[] = [];
      super({
        transform(chunk) {
          chunks.push(new Uint8Array(chunk));
        },
        flush(controller) {
          const total = chunks.reduce((size, chunk) => size + chunk.byteLength, 0);
          const encoded = new Uint8Array(total);
          let offset = 0;
          for (const chunk of chunks) {
            encoded.set(chunk, offset);
            offset += chunk.byteLength;
          }
          controller.enqueue(new Uint8Array(gunzipSync(encoded)));
        },
      });
    }
  }
  vi.stubGlobal('DecompressionStream', TestDecompressionStream);
}

function connection(overrides: Partial<RemoteMcpConnectionDirectoryEntry> = {}): RemoteMcpConnectionDirectoryEntry {
  // The display name is safe to show while the endpoint remains private connection data.
  return {
    connectionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    displayName: 'Docs',
    endpointUri: 'https://private.example.test/mcp',
    resourceUri: null,
    authenticationMode: 'delegatedOAuth',
    transportProfile: 'streamableHttp20250618',
    state: 'enabled',
    rowRevision: 1,
    configurationRevision: 1,
    configurationSha256: 'a'.repeat(64),
    identityBound: true,
    registryServerName: null,
    registryExactVersion: null,
    discoveryRevision: 1,
    catalogSha256: 'b'.repeat(64),
    toolCount: 1,
    ...overrides,
  };
}

async function archiveBase64(file: File): Promise<string> {
  // Match the UI encoder so assertions compare the exact request archive bytes.
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

beforeEach(() => {
  vi.resetAllMocks();
  stubDecompressionStream();
  api.listRemoteMcpConnections.mockResolvedValue({ connections: [], nextConnectionId: null });
  broker.getRepositories.mockResolvedValue({
    connectionId: 'connection-1',
    connectionRevision: 2,
    githubLogin: 'octo',
    repositories: [{
      installationId: 101,
      repositoryId: 202,
      fullName: 'octo/team',
      ownerLogin: 'octo',
      isPrivate: true,
      defaultBranch: 'main',
    }],
  });
  broker.createRepositorySelection.mockResolvedValue({ code: 'short-lived-code' });
  api.listCatalogRoles.mockResolvedValue([
    { id: 'tester', title: 'Tester', summary: 'Tests changes.', responsibilities: [], boundaries: [] },
    { id: 'scribe', title: 'Scribe', summary: 'Reserved.', responsibilities: [], boundaries: [] },
  ]);
});
afterEach(() => { vi.restoreAllMocks(); });

describe('AddMemberDialog', () => {
  it('stages a one-member proposal and opens it for review', async () => {
    api.createCastingDraftProposal.mockResolvedValue({ proposalId: 'pr-9' });
    renderAt(<AddMemberDialog projectId="p1" configuration={configuration} configurationRevision={7} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add member' }));
    const select = await screen.findByRole('combobox', { name: /Role/ });
    await waitFor(() => expect(screen.getByRole('option', { name: 'Tester' })).toBeTruthy());
    // Reserved roles are not offered.
    expect(screen.queryByRole('option', { name: 'Scribe' })).toBeNull();
    fireEvent.change(select, { target: { value: 'tester' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cast member' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team/cast?proposal=pr-9'));
    const [, projectId, request] = api.createCastingDraftProposal.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(request.expectedConfigurationRevision).toBe(7);
    expect(request.draft.casting).toEqual([{ agentId: 'lead', role: 'lead', order: 0 }, { agentId: 'tester', role: 'tester', order: 1 }]);
  });

  it('shows plain copy for a missing write permission and re-reads on a conflict', async () => {
    const onConflict = vi.fn();
    api.createCastingDraftProposal.mockRejectedValueOnce(new GatewayError(403, {}, 'Forbidden'));
    renderAt(<AddMemberDialog projectId="p1" configuration={configuration} configurationRevision={7} onConflict={onConflict} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add member' }));
    await waitFor(() => expect(screen.getByRole('option', { name: 'Tester' })).toBeTruthy());
    fireEvent.change(screen.getByRole('combobox', { name: /Role/ }), { target: { value: 'tester' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cast member' }));
    expect(await screen.findByText(/permission to change this project/)).toBeTruthy();
    expect(onConflict).not.toHaveBeenCalled();

    api.createCastingDraftProposal.mockRejectedValueOnce(new GatewayError(409, {}, 'Conflict'));
    fireEvent.click(screen.getByRole('button', { name: 'Cast member' }));
    expect(await screen.findByText(/changed since you loaded it/)).toBeTruthy();
    expect(onConflict).toHaveBeenCalledOnce();
  });
});

describe('TeamSyncPanel', () => {
  it('keeps repository sync unavailable until a GitHub App repository is configured', () => {
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);

    expect(screen.getByText(/Configure a GitHub App repository/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Preview repository sync' })).toBeNull();
    expect(api.previewProjectTeamSync).not.toHaveBeenCalled();
  });

  it('previews repository changes and commits only the reviewed revision and hash', async () => {
    api.previewProjectTeamSync
      .mockResolvedValueOnce({
      configurationRevision: 7,
      changeSetHash: 'a'.repeat(64),
      nothingToSync: false,
      changes: [
        { path: '.squad/team.md', kind: 'modified' },
        { path: '.squad/agents/lead/charter.md', kind: 'added' },
      ],
      })
      .mockResolvedValueOnce({
        configurationRevision: 7,
        changeSetHash: '',
        nothingToSync: true,
        changes: [],
      });
    api.commitProjectTeamSync.mockResolvedValue({
      commitId: 'commit-1',
      branch: 'main',
      htmlUrl: 'https://github.com/octo/team/commit/commit-1',
    });
    renderAt(<TeamSyncPanel
      projectId="p1"
      configurationRevision={7}
      repositoryFullName="octo/team"
    />);

    expect(await screen.findByText('modified: .squad/team.md')).toBeTruthy();
    expect(screen.getByText('added: .squad/agents/lead/charter.md')).toBeTruthy();
    expect(screen.getByText(/Preview hash:/).textContent).toContain('a'.repeat(64));
    expect(broker.getRepositories).toHaveBeenCalledOnce();
    expect(broker.createRepositorySelection).toHaveBeenCalledWith(101, 202);
    expect(api.previewProjectTeamSync).toHaveBeenCalledWith('token', 'p1', 'short-lived-code', 'tenant-1');

    fireEvent.change(screen.getByRole('textbox', { name: 'Commit message (optional)' }), {
      target: { value: 'sync: reviewed team update' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Commit reviewed changes' }));
    expect(await screen.findByText('Committed the team sync to main (commit-1).')).toBeTruthy();
    expect(api.commitProjectTeamSync).toHaveBeenCalledWith('token', 'p1', 'short-lived-code', {
      expectedChangeSetHash: 'a'.repeat(64),
      expectedConfigurationRevision: 7,
      message: 'sync: reviewed team update',
    }, 'tenant-1');
    expect(await screen.findByText(/No repository changes are needed/)).toBeTruthy();
    expect(api.previewProjectTeamSync).toHaveBeenCalledTimes(2);
  });

  it('uses the Broker repository discovery and cookie/CSRF selection contract', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({
        connectionId: 'connection-1',
        connectionRevision: 2,
        githubLogin: 'octo',
        repositories: [{
          installationId: 101,
          repositoryId: 202,
          fullName: 'octo/team',
          ownerLogin: 'octo',
          isPrivate: true,
          defaultBranch: 'main',
        }],
      }))
      .mockResolvedValueOnce(Response.json({ csrf_token: 'csrf-selection' }))
      .mockResolvedValueOnce(Response.json({
        code: 'real-broker-selection-code',
        connectionId: 'connection-1',
        connectionRevision: 2,
        installationId: 101,
        repositoryId: 202,
        repositoryFullName: 'octo/team',
      }));
    api.previewProjectTeamSync.mockResolvedValue({
      configurationRevision: 7,
      changeSetHash: 'd'.repeat(64),
      nothingToSync: false,
      changes: [{ path: '.squad/team.md', kind: 'modified' }],
    });

    const brokerClient = new IdentityBrokerRepoAppClient(
      'https://identity.example.test',
      fetcher,
    );

    renderAt(<TeamSyncPanel
      projectId="p1"
      configurationRevision={7}
      repositoryFullName="octo/team"
      brokerClient={brokerClient}
    />);

    expect((await screen.findByText(/Preview hash:/)).textContent).toContain('d'.repeat(64));
    expect(fetcher.mock.calls.map(([url]) => String(url))).toEqual([
      'https://identity.example.test/auth/github/repo-app/repositories',
      'https://identity.example.test/auth/github/repo-app/csrf',
      'https://identity.example.test/auth/github/repo-app/selection',
    ]);
    const [, selectionRequest] = fetcher.mock.calls[2];
    expect(selectionRequest?.method).toBe('POST');
    expect(selectionRequest?.credentials).toBe('include');
    expect(new Headers(selectionRequest?.headers).get('X-CSRF-TOKEN')).toBe('csrf-selection');
    expect(JSON.parse(String(selectionRequest?.body))).toEqual({
      installationId: 101,
      repositoryId: 202,
    });
    expect(api.previewProjectTeamSync).toHaveBeenCalledWith(
      'token', 'p1', 'real-broker-selection-code', 'tenant-1',
    );
  });

  it('previews, commits, and refreshes status through the real Gateway and Repo App clients', async () => {
    const repository = {
      installationId: 101,
      repositoryId: 202,
      fullName: 'octo/team',
      ownerLogin: 'octo',
      isPrivate: true,
      defaultBranch: 'main',
    };
    const brokerFetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({
        connectionId: 'connection-1', connectionRevision: 2, githubLogin: 'octo', repositories: [repository],
      }))
      .mockResolvedValueOnce(Response.json({ csrf_token: 'csrf-1' }))
      .mockResolvedValueOnce(Response.json({
        code: 'selection-1', connectionId: 'connection-1', connectionRevision: 2,
        installationId: 101, repositoryId: 202, repositoryFullName: 'octo/team',
      }))
      .mockResolvedValueOnce(Response.json({
        connectionId: 'connection-1', connectionRevision: 2, githubLogin: 'octo', repositories: [repository],
      }))
      .mockResolvedValueOnce(Response.json({ csrf_token: 'csrf-2' }))
      .mockResolvedValueOnce(Response.json({
        code: 'selection-2', connectionId: 'connection-1', connectionRevision: 2,
        installationId: 101, repositoryId: 202, repositoryFullName: 'octo/team',
      }))
      .mockResolvedValueOnce(Response.json({
        connectionId: 'connection-1', connectionRevision: 2, githubLogin: 'octo', repositories: [repository],
      }))
      .mockResolvedValueOnce(Response.json({ csrf_token: 'csrf-3' }))
      .mockResolvedValueOnce(Response.json({
        code: 'selection-3', connectionId: 'connection-1', connectionRevision: 2,
        installationId: 101, repositoryId: 202, repositoryFullName: 'octo/team',
      }));
    const gatewayFetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({
        configurationRevision: 7,
        changeSetHash: 'e'.repeat(64),
        nothingToSync: false,
        changes: [{ path: '.squad/team.md', kind: 'modified' }],
      }))
      .mockResolvedValueOnce(Response.json({
        commitId: 'commit-2',
        branch: 'main',
        htmlUrl: 'https://github.com/octo/team/commit/commit-2',
      }))
      .mockResolvedValueOnce(Response.json({
        configurationRevision: 7,
        changeSetHash: '',
        nothingToSync: true,
        changes: [],
      }));

    renderAt(<TeamSyncPanel
      projectId="p1"
      configurationRevision={7}
      repositoryFullName="octo/team"
      brokerClient={new IdentityBrokerRepoAppClient('https://identity.example.test', brokerFetcher)}
      gateway={new AgentweaverGatewayClient('https://gateway.example.test/api/v1', gatewayFetcher)}
    />);

    expect((await screen.findByText(/Preview hash:/)).textContent).toContain('e'.repeat(64));
    fireEvent.change(screen.getByRole('textbox', { name: 'Commit message (optional)' }), {
      target: { value: 'Sync reviewed team' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Commit reviewed changes' }));

    expect(await screen.findByText('Committed the team sync to main (commit-2).')).toBeTruthy();
    expect(await screen.findByText(/No repository changes are needed/)).toBeTruthy();
    expect(brokerFetcher.mock.calls.map(([url]) => String(url))).toEqual([
      'https://identity.example.test/auth/github/repo-app/repositories',
      'https://identity.example.test/auth/github/repo-app/csrf',
      'https://identity.example.test/auth/github/repo-app/selection',
      'https://identity.example.test/auth/github/repo-app/repositories',
      'https://identity.example.test/auth/github/repo-app/csrf',
      'https://identity.example.test/auth/github/repo-app/selection',
      'https://identity.example.test/auth/github/repo-app/repositories',
      'https://identity.example.test/auth/github/repo-app/csrf',
      'https://identity.example.test/auth/github/repo-app/selection',
    ]);
    const apiPath = 'https://gateway.example.test/api/v1/projects/p1/team/sync';
    expect(gatewayFetcher.mock.calls.map(([url]) => String(url))).toEqual([apiPath, apiPath, apiPath]);
    expect(gatewayFetcher.mock.calls.map(([, init]) => init?.method ?? 'GET')).toEqual([
      'GET', 'POST', 'GET',
    ]);
    expect(gatewayFetcher.mock.calls.map(([, init]) =>
      new Headers(init?.headers).get('X-Agentweaver-Team-Sync-Selection'))).toEqual([
      'selection-1', 'selection-2', 'selection-3',
    ]);
    expect(JSON.parse(String(gatewayFetcher.mock.calls[1][1]?.body))).toEqual({
      expectedChangeSetHash: 'e'.repeat(64),
      expectedConfigurationRevision: 7,
      message: 'Sync reviewed team',
    });
    expect(api.previewProjectTeamSync).not.toHaveBeenCalled();
    expect(api.commitProjectTeamSync).not.toHaveBeenCalled();
  });

  it('drops a repository preview after a conflict and reloads the project configuration', async () => {
    const onConflict = vi.fn();
    api.previewProjectTeamSync.mockResolvedValue({
      configurationRevision: 7,
      changeSetHash: 'b'.repeat(64),
      nothingToSync: false,
      changes: [{ path: '.squad/team.md', kind: 'modified' }],
    });
    api.commitProjectTeamSync.mockRejectedValue(new GatewayError(409, {}, 'Conflict'));
    renderAt(<TeamSyncPanel
      projectId="p1"
      configurationRevision={7}
      repositoryFullName="octo/team"
      onConflict={onConflict}
    />);

    expect(await screen.findByText('modified: .squad/team.md')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Commit reviewed changes' }));
    expect(await screen.findByText(/changed after this preview/)).toBeTruthy();
    expect(onConflict).toHaveBeenCalledOnce();
    expect(screen.queryByRole('list', { name: 'Repository team sync preview' })).toBeNull();
  });

  it('rejects a preview for a configuration revision different from the page', async () => {
    const onConflict = vi.fn();
    api.previewProjectTeamSync.mockResolvedValue({
      configurationRevision: 6,
      changeSetHash: 'c'.repeat(64),
      nothingToSync: false,
      changes: [{ path: '.squad/team.md', kind: 'modified' }],
    });
    renderAt(<TeamSyncPanel
      projectId="p1"
      configurationRevision={7}
      repositoryFullName="octo/team"
      onConflict={onConflict}
    />);

    expect(await screen.findByText(/team changed while the preview was loading/)).toBeTruthy();
    expect(onConflict).toHaveBeenCalledOnce();
    expect(screen.queryByRole('button', { name: 'Commit reviewed changes' })).toBeNull();
  });

  it('exports the team at the shown revision', async () => {
    const createObjectURL = vi.fn(() => 'blob:team');
    const revokeObjectURL = vi.fn();
    // jsdom has no object URLs, so provide them for the download.
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    api.exportProjectAgentSuite.mockResolvedValue({
      format: 'content-tar-gzip',
      suiteName: 'Team One',
      sourceConfigurationRevision: 7,
      archive: btoa('archive'),
    });
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    fireEvent.click(screen.getByRole('button', { name: 'Export team' }));
    expect(await screen.findByText('Exported Team One from configuration revision 7.')).toBeTruthy();
    expect(api.exportProjectAgentSuite).toHaveBeenCalledWith('token', 'p1', 7, 'tenant-1');
    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:team');
  });

  it('refuses an archive over 16 MiB before uploading', async () => {
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const file = contentArchive(false);
    Object.defineProperty(file, 'size', { value: 16 * 1024 * 1024 + 1 });
    fireEvent.change(screen.getByLabelText('AgentSuite archive'), { target: { files: [file] } });
    expect(await screen.findByText('Choose an AgentSuite archive that is between 1 byte and 16 MiB.')).toBeTruthy();
    expect(api.importProjectAgentSuite).not.toHaveBeenCalled();
  });

  it('imports a package without remote dependencies as a proposal', async () => {
    api.importProjectAgentSuite.mockResolvedValue({ proposal: { proposalId: 'pr-3' }, unresolvedSkillBindings: [] });
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const file = contentArchive(false);
    fireEvent.change(screen.getByLabelText('AgentSuite archive'), { target: { files: [file] } });
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team/cast?proposal=pr-3'));
    const [, projectId, request, tenant] = api.importProjectAgentSuite.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(request).toEqual({
      expectedConfigurationRevision: 7,
      format: 'content-tar-gzip',
      archive: await archiveBase64(file),
      toolConnections: [],
    });
    expect(tenant).toBe('tenant-1');
  });

  it('binds remote tool providers to usable target connections without showing endpoints', async () => {
    api.listRemoteMcpConnections.mockResolvedValue({
      connections: [
        connection(),
        connection({
          connectionId: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff',
          displayName: 'Not connected',
          identityBound: false,
        }),
        connection({
          connectionId: 'cccccccc-dddd-eeee-ffff-000000000000',
          displayName: 'Disabled',
          state: 'disabled',
        }),
      ],
      nextConnectionId: null,
    });
    api.importProjectAgentSuite.mockResolvedValue({ proposal: { proposalId: 'pr-4' }, unresolvedSkillBindings: [] });
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const file = contentArchive();
    fireEvent.change(screen.getByLabelText('AgentSuite archive'), { target: { files: [file] } });
    const select = await screen.findByRole('combobox', { name: 'search@1.0.0' });
    expect(screen.getByRole('option', { name: 'Docs' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Not connected' })).toBeNull();
    expect(screen.queryByRole('option', { name: 'Disabled' })).toBeNull();
    expect(screen.queryByText(/private\.example\.test/)).toBeNull();
    fireEvent.change(select, { target: { value: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' } });
    fireEvent.click(screen.getByRole('button', { name: 'Review import' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team/cast?proposal=pr-4'));
    const [, , request] = api.importProjectAgentSuite.mock.calls[0];
    expect(request.toolConnections).toEqual([{
      toolProvider: 'search@1.0.0',
      connectionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    }]);
  });

  it('explains a rejected AgentSuite package', async () => {
    api.importProjectAgentSuite.mockRejectedValue(new GatewayError(422, {}, 'Invalid'));
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    fireEvent.change(screen.getByLabelText('AgentSuite archive'), { target: { files: [contentArchive(false)] } });
    expect(await screen.findByText('The AgentSuite package is invalid or incompatible with this project.')).toBeTruthy();
  });
});
