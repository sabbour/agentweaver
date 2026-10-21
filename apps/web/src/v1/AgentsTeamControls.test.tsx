import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AddMemberDialog, TeamSyncPanel } from './AgentsTeamControls';
import { GatewayError } from './api';
import type { ProjectConfiguration } from './contracts';

const api = vi.hoisted(() => ({
  listCatalogRoles: vi.fn(),
  createCastingDraftProposal: vi.fn(),
  exportCastingTransfer: vi.fn(),
  importCastingTransfer: vi.fn(),
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

beforeEach(() => {
  vi.clearAllMocks();
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
    api.exportCastingTransfer.mockResolvedValue({ formatVersion: 1, sourceConfigurationRevision: 7, agentCharters: [], casting: [] });
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    fireEvent.click(screen.getByRole('button', { name: 'Export team' }));
    expect(await screen.findByText('Exported the team from configuration revision 7.')).toBeTruthy();
    expect(api.exportCastingTransfer).toHaveBeenCalledWith('token', 'p1', 7, 'tenant-1');
    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:team');
  });

  it('refuses a file over 1 MiB before uploading', async () => {
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const file = new File(['x'], 'team.json', { type: 'application/json' });
    Object.defineProperty(file, 'size', { value: 1024 * 1024 + 1 });
    fireEvent.change(screen.getByLabelText('Team file'), { target: { files: [file] } });
    expect(await screen.findByText('The file is larger than 1 MiB.')).toBeTruthy();
    expect(api.importCastingTransfer).not.toHaveBeenCalled();
  });

  it('imports a team file as a proposal and opens it', async () => {
    api.importCastingTransfer.mockResolvedValue({ proposalId: 'pr-3' });
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const transfer = { formatVersion: 1, sourceProjectId: 'p0', sourceConfigurationRevision: 2, contentDigest: 'd', agentCharters: [], casting: [] };
    const file = new File([JSON.stringify(transfer)], 'team.json', { type: 'application/json' });
    fireEvent.change(screen.getByLabelText('Team file'), { target: { files: [file] } });
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team/cast?proposal=pr-3'));
    expect(api.importCastingTransfer).toHaveBeenCalledWith('token', 'p1', { expectedConfigurationRevision: 7, transfer }, 'tenant-1');
  });

  it('explains a rejected team file', async () => {
    api.importCastingTransfer.mockRejectedValue(new GatewayError(422, {}, 'Invalid'));
    renderAt(<TeamSyncPanel projectId="p1" configurationRevision={7} />);
    const file = new File(['{}'], 'team.json', { type: 'application/json' });
    fireEvent.change(screen.getByLabelText('Team file'), { target: { files: [file] } });
    expect(await screen.findByText(/not a valid team export/)).toBeTruthy();
  });
});
