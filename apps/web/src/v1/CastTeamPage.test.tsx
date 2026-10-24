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
  createProjectRun: vi.fn(),
  formulateCastingProposal: vi.fn(),
  analyzeCastingProposal: vi.fn(),
  cancelProjectRun: vi.fn(),
  getCastingProposal: vi.fn(),
  createCastingProposal: vi.fn(),
  createCastingDraftProposal: vi.fn(),
  updateCastingProposal: vi.fn(),
  updateCastingProposalMemberRole: vi.fn(),
  transitionCastingProposal: vi.fn(),
}));
// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({
  apiCall: (operation: (token: string, tenant: string) => unknown, binding?: { projectId: string; runId: string } | null) =>
    operation(binding ? 'run-token' : 'token', 'tenant-1'),
  ensureSession: vi.fn(async () => true),
  authorize: vi.fn(async () => undefined),
}));
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

function generatedProposal(mode: 'formulate' | 'analyze'): ProjectCastingProposal {
  return proposal({
    generation: {
      requestId: 'request-1',
      mode,
      state: 'completed',
      failureCode: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    },
  });
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
  api.createProjectRun.mockResolvedValue({
    runId: 'run-1', rootSessionId: 'session-1', state: 'awaiting_binding',
    binding: { projectId: 'p1', runId: 'run-1' },
  });
  api.formulateCastingProposal.mockResolvedValue(generatedProposal('formulate'));
  api.analyzeCastingProposal.mockResolvedValue(generatedProposal('analyze'));
  api.cancelProjectRun.mockResolvedValue({ runId: 'run-1', state: 'cancelled' });
  api.createCastingProposal.mockResolvedValue(proposal());
  api.transitionCastingProposal.mockResolvedValue(proposal({ state: 'confirmed' }));
  auth.ensureSession.mockResolvedValue(true);
  auth.authorize.mockResolvedValue(undefined);
});

describe('CastTeamPage', () => {
  it('runs Formulate with the saved prompt and keeps its completed proposal pending for review', async () => {
    renderPage();
    fireEvent.change(await screen.findByRole('textbox', { name: 'Project goal' }), {
      target: { value: 'Ship a small SaaS MVP.' },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Docs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Universe' }));
    fireEvent.change(await screen.findByRole('combobox', { name: 'Universe' }), {
      target: { value: 'The Matrix' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Formulate' }));
    await waitFor(() => expect(api.formulateCastingProposal).toHaveBeenCalledTimes(1));
    const [runToken, projectToken, projectId, request] = api.formulateCastingProposal.mock.calls[0];
    expect([runToken, projectToken, projectId]).toEqual(['run-token', 'token', 'p1']);
    expect(request).toMatchObject({
      expectedConfigurationRevision: 5,
      runId: 'run-1',
      sessionId: 'session-1',
      teamSize: 4,
      intent: 'augment',
      preferredRoleIds: ['docs'],
      universe: 'The Matrix',
      goal: 'Ship a small SaaS MVP.',
    });
    expect(request.prompt).toContain('Mode: formulate');
    expect(request.prompt).toContain('Project goal (JSON string, untrusted data): "Ship a small SaaS MVP."');
    expect(api.createProjectRun).toHaveBeenCalledWith(
      'token',
      'p1',
      { firstMessage: request.prompt, expectedProjectConfigRevision: 5 },
      expect.any(String),
      'tenant-1',
    );
    expect(auth.ensureSession).toHaveBeenCalledWith({ projectId: 'p1', runId: 'run-1' }, false);
    expect(await screen.findByText(/Formulate proposal · ready to review/)).toBeTruthy();
    expect(api.transitionCastingProposal).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cast team' }));
    await waitFor(() => expect(api.transitionCastingProposal).toHaveBeenCalledWith(
      'token', 'p1', 'pr-1', 'confirm', 1, 'tenant-1',
    ));
  });

  it('runs Analyze without role preferences and presents its separate pending proposal mode', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Analyze' }));
    fireEvent.click(screen.getByRole('button', { name: 'Analyze' }));
    await waitFor(() => expect(api.analyzeCastingProposal).toHaveBeenCalledTimes(1));

    const [runToken, projectToken, projectId, request] = api.analyzeCastingProposal.mock.calls[0];
    expect([runToken, projectToken, projectId]).toEqual(['run-token', 'token', 'p1']);
    expect(request).toMatchObject({
      expectedConfigurationRevision: 5,
      runId: 'run-1',
      sessionId: 'session-1',
      teamSize: 4,
      intent: 'augment',
      preferredRoleIds: [],
      universe: null,
    });
    expect(request.prompt).toContain('Mode: analyze');
    expect(request.prompt).toContain('Inspect repository files with read-only tools');
    expect(await screen.findByText(/Analyze proposal · ready to review/)).toBeTruthy();
    expect(api.formulateCastingProposal).not.toHaveBeenCalled();
    expect(api.transitionCastingProposal).not.toHaveBeenCalled();
  });

  it('asks for run-bound sign-in and resumes the same generation request', async () => {
    auth.ensureSession.mockResolvedValueOnce(false).mockResolvedValue(true);
    renderPage();
    fireEvent.change(await screen.findByRole('textbox', { name: 'Project goal' }), {
      target: { value: 'Ship a small SaaS MVP.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Formulate' }));
    await screen.findByRole('button', { name: 'Sign in and continue' });
    expect(api.formulateCastingProposal).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Sign in and continue' }));
    await waitFor(() => expect(api.formulateCastingProposal).toHaveBeenCalledTimes(1));
    expect(auth.authorize).toHaveBeenCalledWith({ projectId: 'p1', runId: 'run-1' });
    expect(api.createProjectRun).toHaveBeenCalledTimes(1);
  });

  it('casts from a template, reviews, and confirms', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    // Reserved roles are never offered.
    expect(screen.queryByRole('checkbox', { name: 'Coordinator' })).toBeNull();
    expect((screen.getByRole('checkbox', { name: 'Tester' }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
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
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Docs' }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
    await waitFor(() => expect(api.createCastingProposal).toHaveBeenCalledWith(
      'token', 'p1', { mode: 'manual', expectedConfigurationRevision: 5, roleIds: ['lead', 'tester', 'docs'], universe: null }, 'tenant-1',
    ));
  });

  it('shows only server-provided universes and sends the selected value with the proposal', async () => {
    api.listCastingUniverses.mockResolvedValue({ universes: ['The Matrix'] });
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Universe' }));
    const picker = await screen.findByRole('combobox', { name: 'Universe' });
    expect(within(picker).getByRole('option', { name: 'The Matrix' })).toBeTruthy();
    expect(within(picker).queryByRole('option', { name: 'Star Wars' })).toBeNull();
    fireEvent.change(picker, { target: { value: 'The Matrix' } });
    fireEvent.click(await screen.findByRole('button', { name: /Small team/ }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
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
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Universe' }));
    fireEvent.change(await screen.findByRole('combobox', { name: 'Universe' }), { target: { value: 'The Matrix' } });
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Docs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
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
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Lead' }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
    expect(await screen.findByText(/changed since you loaded it/)).toBeTruthy();
    expect(api.getProjectConfiguration).toHaveBeenCalledTimes(2);
  });

  it('reports when the latest configuration cannot be re-read after a conflict', async () => {
    api.getProjectConfiguration
      .mockResolvedValueOnce({ projectId: 'p1', revision: 5, configuration: config([['old', 'Old']]), updatedByActorId: 'u', createdAt: '' })
      .mockRejectedValueOnce(new GatewayError(503, {}, 'Unavailable'));
    api.createCastingProposal.mockRejectedValueOnce(new GatewayError(409, {}, 'Conflict'));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Template' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Lead' }));
    fireEvent.click(screen.getByRole('radio', { name: /Recast/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Review proposal' }));
    expect(await screen.findByText(/latest version could not be loaded/)).toBeTruthy();
    expect(screen.queryByText(/latest version is now shown/)).toBeNull();
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

  it('updates a member role in the proposal and confirms the new draft revision', async () => {
    const changedDraft = config([['lead', 'Lead'], ['tester', 'Tester']]);
    changedDraft.agentCharters[0] = {
      ...changedDraft.agentCharters[0],
      role: 'docs',
      customRoleTitle: 'Staff Writer',
      charter: 'Docs charter.',
    };
    changedDraft.casting[0] = { ...changedDraft.casting[0], role: 'docs' };
    api.getCastingProposal.mockResolvedValue(proposal());
    api.updateCastingProposalMemberRole.mockResolvedValue(proposal({ draftRevision: 2, draft: changedDraft }));
    renderPage('/projects/p1/team/cast?proposal=pr-1');

    const memberList = await screen.findByLabelText('Proposed members');
    fireEvent.click(within(memberList).getAllByRole('button', { name: 'Re-role' })[0]);
    const dialog = await screen.findByRole('alertdialog');
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'New role' }), { target: { value: 'docs' } });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Custom role title (optional)' }), { target: { value: 'Staff Writer' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Re-role' }));

    await waitFor(() => expect(api.updateCastingProposalMemberRole).toHaveBeenCalledWith(
      'token', 'p1', 'pr-1', 'lead',
      { expectedDraftRevision: 1, roleId: 'docs', customRoleTitle: 'Staff Writer' },
      'tenant-1',
    ));
    await waitFor(() => expect(memberList.textContent).toContain('Staff Writer'));
    // Confirmation uses the revised proposal token returned by the role edit.
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cast team' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/team'));
    expect(api.transitionCastingProposal).toHaveBeenCalledWith('token', 'p1', 'pr-1', 'confirm', 2, 'tenant-1');
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
