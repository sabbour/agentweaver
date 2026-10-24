/// <reference types="node" />

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { gunzipSync } from 'node:zlib';
import { AddMemberDialog, TeamSyncPanel } from './AgentsTeamControls';
import { GatewayError } from './api';
import { contentArchive } from './__tests__/agentSuiteArchiveFixtures';
import type { ProjectConfiguration, RemoteMcpConnectionDirectoryEntry } from './contracts';

const api = vi.hoisted(() => ({
  listCatalogRoles: vi.fn(),
  createCastingDraftProposal: vi.fn(),
  exportProjectAgentSuite: vi.fn(),
  importProjectAgentSuite: vi.fn(),
  listRemoteMcpConnections: vi.fn(),
}));
// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({ apiCall: (operation: (token: string, tenant: string) => unknown) => operation('token', 'tenant-1') }));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));

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
  vi.clearAllMocks();
  stubDecompressionStream();
  api.listRemoteMcpConnections.mockResolvedValue({ connections: [], nextConnectionId: null });
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
