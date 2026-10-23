import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { AgentsView } from './AgentsView';
import type { ProjectCastingProposal, ProjectConfiguration } from './contracts';

const api = vi.hoisted(() => ({
  listCastingProposals: vi.fn(),
  listCatalogRoles: vi.fn(),
  createCastingDraftProposal: vi.fn(),
}));

// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({ apiCall: (operation: (token: string, tenant: string) => unknown) => operation('token', 'tenant-1') }));
const animationDescriptor = Object.getOwnPropertyDescriptor(Element.prototype, 'animate');
beforeAll(() => {
  // Happy DOM's animation cancel rejects; Fluent UI's test fallback avoids noisy teardown errors.
  Object.defineProperty(Element.prototype, 'animate', { configurable: true, value: undefined });
});
afterAll(() => {
  if (animationDescriptor) Object.defineProperty(Element.prototype, 'animate', animationDescriptor);
  else Reflect.deleteProperty(Element.prototype, 'animate');
});
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));
vi.mock('./pageParts', async (importActual) => ({
  ...(await importActual<typeof import('./pageParts')>()),
  PageHeading: ({ title, actions }: { title: string; actions?: ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
}));

beforeEach(() => {
  vi.clearAllMocks();
  api.listCastingProposals.mockResolvedValue([]);
  api.listCatalogRoles.mockResolvedValue([]);
  api.createCastingDraftProposal.mockResolvedValue({ proposalId: 'pr-9' });
});

function config(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return {
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: [
      { agentId: 'a1', name: 'Ada', role: 'Lead', charter: 'Lead the work.' },
      { agentId: 'a2', name: 'Bo', role: 'Tester', charter: 'Test the work.' },
    ],
    casting: [{ agentId: 'a1', role: 'Lead', order: 1 }],
    blueprintWorkflowReferences: [],
    skills: [
      { skillId: 'shared', enabled: true, order: 0 },
      { skillId: 'for-bo', enabled: true, order: 1, agentIds: ['a2'] },
      { skillId: 'off', enabled: false, order: 2 },
    ],
    runLimits: {},
    ...overrides,
  };
}

function renderView(props: Partial<Parameters<typeof AgentsView>[0]> = {}) {
  const onSave = vi.fn().mockResolvedValue(true);
  render(
    <MemoryRouter initialEntries={['/projects/p1/team']}>
      <Routes>
        <Route path="/projects/:projectId/team" element={<AgentsView projectId="p1" configuration={config()} configurationRevision={4} readOnly={false} onSave={onSave} {...props} />} />
        <Route path="/projects/:projectId/team/cast" element={<p>Cast page</p>} />
      </Routes>
    </MemoryRouter>,
  );
  return { onSave };
}

describe('AgentsView', () => {
  it('shows the 0.x empty state with a Cast team action', () => {
    renderView({ configuration: config({ agentCharters: [], casting: [] }) });
    expect(screen.getByText('No team yet')).toBeTruthy();
    expect(screen.queryByText(/not available/i)).toBeNull();
    // The header and the empty state both offer Cast team.
    fireEvent.click(screen.getAllByRole('button', { name: 'Cast team' })[1]);
    expect(screen.getByText('Cast page')).toBeTruthy();
  });

  it('shows Add member, Sync and Cast team in the header', () => {
    renderView();
    expect(screen.getByRole('button', { name: 'Add member' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Sync' }));
    expect(screen.getByRole('button', { name: 'Export team' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Hide sync' }));
    expect(screen.queryByRole('button', { name: 'Export team' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Cast team' }));
    expect(screen.getByText('Cast page')).toBeTruthy();
  });

  it('turns off team changes while a save is running', () => {
    renderView({ readOnly: true });
    expect((screen.getByRole('button', { name: 'Add member' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Cast team' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('lists pending proposals with a review link', async () => {
    const proposal = {
      proposalId: 'pr-1', projectId: 'p1', state: 'pending', draftRevision: 1, baseConfigurationRevision: 4,
      draft: config(), createdAt: '2026-01-01T00:00:00Z', createdByActorId: 'user-1',
    } as unknown as ProjectCastingProposal;
    api.listCastingProposals.mockResolvedValue([proposal, { ...proposal, proposalId: 'pr-2', state: 'confirmed' }]);
    renderView();
    const link = await screen.findByRole('link', { name: 'Review proposal with 1 member' });
    expect(link.getAttribute('href')).toBe('/projects/p1/team/cast?proposal=pr-1');
    expect(screen.getAllByRole('link', { name: /Review proposal/ })).toHaveLength(1);
    expect(api.listCastingProposals).toHaveBeenCalledWith('token', 'p1', 20, 'tenant-1');
  });

  it('filters agents by team membership', () => {
    renderView();
    expect(screen.getByText('Ada')).toBeTruthy();
    expect(screen.getByText('Bo')).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'In the team (1)' }));
    expect(screen.getByText('Ada')).toBeTruthy();
    expect(screen.queryByText('Bo')).toBeNull();
    fireEvent.click(screen.getByRole('tab', { name: 'Not in the team (1)' }));
    expect(screen.queryByText('Ada')).toBeNull();
    expect(screen.getByText('Bo')).toBeTruthy();
  });

  it('opens agent details with team position and applicable skills', async () => {
    renderView();
    fireEvent.click(screen.getByText('Bo'));
    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Not in the team')).toBeTruthy();
    expect(within(drawer).getByText('a2')).toBeTruthy();
    expect(within(drawer).getByText('shared')).toBeTruthy();
    expect(within(drawer).getByText('for-bo')).toBeTruthy();
    // Disabled skills are not listed, even when they apply to every agent.
    expect(within(drawer).queryByText('off')).toBeNull();
    expect(within(drawer).getByRole('link', { name: 'View memories' }).getAttribute('href')).toBe('/projects/p1/memories?agentId=a2');
  });

  it('stages a re-role proposal from the current team member details', async () => {
    api.listCatalogRoles.mockResolvedValue([
      { id: 'frontend-engineer', title: 'Frontend Engineer', summary: 'Builds user-facing experiences.', responsibilities: ['Implement accessible screens'], boundaries: ['Do not change backend services'] },
    ]);
    renderView();
    fireEvent.click(screen.getByText('Ada'));
    const drawer = await screen.findByRole('dialog');
    fireEvent.change(within(drawer).getByRole('combobox', { name: 'New team role' }), { target: { value: 'frontend-engineer' } });
    fireEvent.click(within(drawer).getByRole('button', { name: 'Re-role' }));
    await waitFor(() => expect(api.createCastingDraftProposal).toHaveBeenCalledOnce());
    const [, projectId, request] = api.createCastingDraftProposal.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(request.expectedConfigurationRevision).toBe(4);
    expect(request.draft.agentCharters[0].role).toBe('frontend-engineer');
    expect(request.draft.agentCharters[0].charter).toMatch(/Implement accessible screens/);
    expect(request.draft.casting[0].role).toBe('frontend-engineer');
  });

  it('stages a removal proposal without changing the displayed team', async () => {
    const current = config({
      agentCharters: [{ agentId: 'a1', name: 'Ada', role: 'Lead', charter: 'Lead the work.' }],
      casting: [{ agentId: 'a1', role: 'Lead', order: 1 }],
    });
    renderView({ configuration: current });
    fireEvent.click(screen.getByText('Ada'));
    const drawer = await screen.findByRole('dialog');
    fireEvent.click(within(drawer).getByRole('button', { name: 'Remove from team' }));
    await waitFor(() => expect(api.createCastingDraftProposal).toHaveBeenCalledOnce());
    const [, , request] = api.createCastingDraftProposal.mock.calls[0];
    expect(request.draft.casting).toEqual([]);
    expect(request.draft.agentCharters).toEqual([]);
    expect(within(drawer).getByText('In the team as Lead, position 1')).toBeTruthy();
  });

  it('saves only the selected agent charter', async () => {
    const { onSave } = renderView();
    fireEvent.click(screen.getByText('Ada'));
    fireEvent.click(await screen.findByRole('tab', { name: 'Charter' }));
    const save = screen.getByRole('button', { name: 'Save charter' }) as HTMLButtonElement;
    // Save stays disabled until something changes.
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /Charter content/ }), { target: { value: 'New charter.' } });
    await act(async () => { fireEvent.click(save); });
    // The saved configuration changes Ada's charter and leaves Bo untouched.
    const saved = onSave.mock.calls[0][0] as ProjectConfiguration;
    expect(saved.agentCharters).toEqual([
      { agentId: 'a1', name: 'Ada', role: 'Lead', charter: 'New charter.' },
      { agentId: 'a2', name: 'Bo', role: 'Tester', charter: 'Test the work.' },
    ]);
    expect(await screen.findByText('Charter saved.')).toBeTruthy();
  });

  it('hides Save charter when read-only', async () => {
    renderView({ readOnly: true });
    fireEvent.click(screen.getByText('Ada'));
    fireEvent.click(await screen.findByRole('tab', { name: 'Charter' }));
    expect(screen.queryByRole('button', { name: 'Save charter' })).toBeNull();
  });
});
