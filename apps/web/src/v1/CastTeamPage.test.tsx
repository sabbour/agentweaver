import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { CastTeamPage } from './CastTeamPage';
import type { ProjectCastingProposal, ProjectConfiguration } from './contracts';

const api = vi.hoisted(() => ({
  listCastingTemplates: vi.fn(),
  listCastingUniverses: vi.fn(),
  listCatalogRoles: vi.fn(),
  getProjectConfiguration: vi.fn(),
  getCastingProposal: vi.fn(),
  createCastingProposal: vi.fn(),
  createCastingDraftProposal: vi.fn(),
  updateCastingProposal: vi.fn(),
  transitionCastingProposal: vi.fn(),
}));
// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({ apiCall: (operation: (token: string, tenant: string) => unknown) => operation('token', 'tenant-1') }));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));
vi.mock('./pageParts', async (importActual) => ({
  ...(await importActual<typeof import('./pageParts')>()),
  PageHeading: ({ title, actions }: { title: string; actions?: ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
}));

function config(members: Array<[string, string]>): ProjectConfiguration {
  return {
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: members.map(([agentId, name]) => ({ agentId, name, role: agentId, charter: `${name} charter.` })),
    casting: members.map(([agentId], order) => ({ agentId, role: agentId, order })),
    blueprintWorkflowReferences: [],
    skills: [],
    runLimits: {},
  };
}

function proposal(overrides: Partial<ProjectCastingProposal> = {}): ProjectCastingProposal {
  return {
    proposalId: 'pr-1', projectId: 'p1', baseConfigurationRevision: 5, draftRevision: 1, state: 'pending',
    draft: config([['lead', 'Lead'], ['tester', 'Tester']]),
    confirmedConfigurationRevision: null, result: null, transferProvenance: null,
    createdByActorId: 'user-1', createdAt: '2026-01-01T00:00:00Z', updatedByActorId: 'user-1', updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  } as ProjectCastingProposal;
}

/** Shows the current address so tests can check navigation. */
function Location() {
  const location = useLocation();
  return <p data-testid="location">{location.pathname}{location.search}</p>;
}

function renderPage(path = '/projects/p1/team/cast') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/projects/:projectId/team/cast" element={<><CastTeamPage /><Location /></>} />
        <Route path="/projects/:projectId/team" element={<Location />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  api.listCastingTemplates.mockResolvedValue([
    { id: 'small', title: 'Small team', description: 'A lead and a tester.', roleIds: ['lead', 'tester'] },
  ]);
  api.listCastingUniverses.mockResolvedValue({ universes: ['The Matrix'] });
  api.listCatalogRoles.mockResolvedValue([
    { id: 'lead', title: 'Lead', summary: '', responsibilities: [], boundaries: [] },
    { id: 'tester', title: 'Tester', summary: '', responsibilities: [], boundaries: [] },
    { id: 'docs', title: 'Docs', summary: '', responsibilities: [], boundaries: [] },
    { id: 'coordinator', title: 'Coordinator', summary: '', responsibilities: [], boundaries: [] },
  ]);
  api.getProjectConfiguration.mockResolvedValue({ projectId: 'p1', revision: 5, configuration: config([['old', 'Old']]), updatedByActorId: 'u', createdAt: '' });
  api.createCastingProposal.mockResolvedValue(proposal());
  api.transitionCastingProposal.mockResolvedValue(proposal({ state: 'confirmed' }));
});

describe('CastTeamPage', () => {
  it('casts from a template, reviews, and confirms', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    // Reserved roles are never offered.
    expect(screen.queryByRole('checkbox', { name: 'Coordinator' })).toBeNull();
    expect((screen.getByRole('checkbox', { name: 'Tester' }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    await screen.findByText('Review proposal', { selector: 'h3, span, div' });
    expect(api.createCastingProposal).toHaveBeenCalledWith('token', 'p1', {
      mode: 'scenario', expectedConfigurationRevision: 5, templateId: 'small', universe: null,
    }, 'tenant-1');
    expect(within(screen.getByLabelText('Proposed members')).getByText('Tester')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cast team' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team'));
    expect(api.transitionCastingProposal).toHaveBeenCalledWith('token', 'p1', 'pr-1', 'confirm', 1, 'tenant-1');
  });

  it('sends a manual pick when the roles differ from the template', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Docs' }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    await waitFor(() => expect(api.createCastingProposal).toHaveBeenCalledWith(
      'token', 'p1', { mode: 'manual', expectedConfigurationRevision: 5, roleIds: ['lead', 'tester', 'docs'], universe: null }, 'tenant-1',
    ));
  });

  it('shows only server-provided universes and sends the selected value with the proposal', async () => {
    api.listCastingUniverses.mockResolvedValue({ universes: ['The Matrix'] });
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Universe' }));
    const picker = await screen.findByRole('combobox', { name: 'Universe' });
    expect(within(picker).getByRole('option', { name: 'The Matrix' })).toBeTruthy();
    expect(within(picker).queryByRole('option', { name: 'Star Wars' })).toBeNull();
    fireEvent.change(picker, { target: { value: 'The Matrix' } });
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    await waitFor(() => expect(api.createCastingProposal).toHaveBeenCalledWith(
      'token', 'p1', {
        mode: 'scenario', expectedConfigurationRevision: 5, templateId: 'small', universe: 'The Matrix',
      }, 'tenant-1',
    ));
  });

  it('stages an augment proposal with the existing team, selected roles, and universe', async () => {
    api.createCastingDraftProposal.mockResolvedValue(proposal({
      draft: config([['old', 'Old'], ['docs-writer', 'Docs Writer']]),
    }));
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Universe' }));
    fireEvent.change(await screen.findByRole('combobox', { name: 'Universe' }), { target: { value: 'The Matrix' } });
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Docs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    await screen.findByText('Review proposal', { selector: 'h3, span, div' });
    const [, projectId, request] = api.createCastingDraftProposal.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(request.expectedConfigurationRevision).toBe(5);
    expect(request.draft.casting.map((item: { agentId: string }) => item.agentId)).toEqual(['old', 'docs']);
    expect(request.draft.castingUniverse).toBe('The Matrix');
    expect(screen.getByText('This proposal adds 1 member to the current team.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Remove Old' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Remove Docs Writer' })).toBeTruthy();
  });

  it('keeps the current roster intact when reviewing a linked augment proposal', async () => {
    api.getCastingProposal.mockResolvedValue(proposal({
      draft: config([['old', 'Old'], ['docs', 'Docs']]),
    }));
    renderPage('/projects/p1/team/cast?proposal=pr-1');
    await screen.findByText('This proposal adds 1 member to the current team.');
    expect(screen.getByText('This proposal adds 1 member to the current team.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Remove Old' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Remove Docs' })).toBeTruthy();
  });

  it('re-reads the configuration and explains a revision conflict', async () => {
    api.createCastingProposal.mockRejectedValueOnce(new GatewayError(409, {}, 'Conflict'));
    renderPage();
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Lead' }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    expect(await screen.findByText(/changed since you loaded it/)).toBeTruthy();
    expect(api.getProjectConfiguration).toHaveBeenCalledTimes(2);
  });

  it('opens a linked proposal, removes a member, and rejects it', async () => {
    api.getCastingProposal.mockResolvedValue(proposal());
    api.updateCastingProposal.mockResolvedValue(proposal({ draftRevision: 2, draft: config([['lead', 'Lead']]) }));
    renderPage('/projects/p1/team/cast?proposal=pr-1');
    fireEvent.click(await screen.findByRole('button', { name: 'Remove Tester' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Remove Tester' })).toBeNull());
    const [, , proposalId, request] = api.updateCastingProposal.mock.calls[0];
    expect(proposalId).toBe('pr-1');
    expect(request.expectedDraftRevision).toBe(1);
    expect(request.draft.casting.map((item: { agentId: string }) => item.agentId)).toEqual(['lead']);
    // A linked proposal has no Back step; Reject discards it and returns to Agents.
    expect(screen.queryByRole('button', { name: 'Back' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team'));
    expect(api.transitionCastingProposal).toHaveBeenCalledWith('token', 'p1', 'pr-1', 'reject', 2, 'tenant-1');
  });

  it('allows an empty removal proposal to be confirmed', async () => {
    api.getCastingProposal.mockResolvedValue(proposal({ draft: config([]) }));
    renderPage('/projects/p1/team/cast?proposal=pr-1');
    fireEvent.click(await screen.findByRole('button', { name: 'Confirm' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cast team' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team'));
    expect(api.transitionCastingProposal).toHaveBeenCalledWith('token', 'p1', 'pr-1', 'confirm', 1, 'tenant-1');
  });

  it('shows a finished proposal without change controls', async () => {
    api.getCastingProposal.mockResolvedValue(proposal({ state: 'confirmed' }));
    renderPage('/projects/p1/team/cast?proposal=pr-1');
    expect(await screen.findByText(/already confirmed/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Remove/ })).toBeNull();
  });

  it('shows plain copy when the user cannot read casting data', async () => {
    api.listCastingTemplates.mockRejectedValue(new GatewayError(403, {}, 'Forbidden'));
    renderPage();
    expect(await screen.findByText(/do not have permission to view/)).toBeTruthy();
  });
});
