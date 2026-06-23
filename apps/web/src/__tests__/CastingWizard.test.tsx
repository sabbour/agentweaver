import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { CastingWizardPage } from '../pages/CastingWizardPage';
import type { TeamTemplateDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getTemplates: vi.fn(),
    getUniverses: vi.fn(),
    createProposal: vi.fn(),
    amendProposal: vi.fn(),
    confirmProposal: vi.fn(),
    rejectProposal: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderWithRouter(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/team/cast`]}>
        <Routes>
          <Route path="/projects/:projectId/team" element={<div>Team page</div>} />
          <Route path="/projects/:projectId/team/cast" element={<CastingWizardPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getTemplatesMock = () => vi.mocked(apiClient.getTemplates);

beforeEach(() => {
  vi.clearAllMocks();
  getTemplatesMock().mockResolvedValue([] as TeamTemplateDto[]);
  vi.mocked(apiClient.getUniverses).mockResolvedValue({ universes: [] });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('CastingWizardPage', () => {
  it('renders cast step with tabs by default', () => {
    renderWithRouter('proj-001');

    expect(screen.getByText('Cast a team')).toBeDefined();
    expect(screen.getByText(/1\. Cast/)).toBeDefined();
    expect(screen.getByText(/2\. Review proposal/)).toBeDefined();
    expect(screen.getByText(/3\. Confirm/)).toBeDefined();
    expect(screen.getByRole('tab', { name: /Formulate/ })).toBeDefined();
    expect(screen.getByRole('tab', { name: /Template/ })).toBeDefined();
    expect(screen.getByRole('tab', { name: /Analyze/ })).toBeDefined();
  });

  it('Template tab shows available templates', async () => {
    const templates: TeamTemplateDto[] = [
      {
        id: 'grp-1',
        title: 'Web App Team',
        description: 'A typical web application team',
        roles: [],
      },
    ];
    getTemplatesMock().mockResolvedValue(templates);

    const user = userEvent.setup();
    renderWithRouter('proj-002');

    await user.click(screen.getByRole('tab', { name: /Template/ }));

    await waitFor(() => {
      expect(screen.getByText('Web App Team')).toBeDefined();
    });
  });

  it('selecting a template card enables the Review button', async () => {
    const templates: TeamTemplateDto[] = [
      {
        id: 'grp-1',
        title: 'Web App Team',
        description: 'Frontend, backend, and devops roles',
        roles: [],
      },
    ];
    getTemplatesMock().mockResolvedValue(templates);

    const user = userEvent.setup();
    renderWithRouter('proj-003');

    await user.click(screen.getByRole('tab', { name: /Template/ }));

    await waitFor(() => {
      expect(screen.getByText('Web App Team')).toBeDefined();
    });

    const reviewBefore = screen.getByRole('button', { name: 'Review' });
    expect(
      reviewBefore.hasAttribute('disabled') ||
      reviewBefore.getAttribute('aria-disabled') === 'true',
    ).toBe(true);

    await user.click(screen.getByText('Web App Team'));

    await waitFor(() => {
      const reviewBtn = screen.getByRole('button', { name: 'Review' });
      expect(
        !reviewBtn.hasAttribute('disabled') && reviewBtn.getAttribute('aria-disabled') !== 'true',
      ).toBe(true);
    });
  });

  it('Review button is disabled by default when no cast action has been completed', async () => {
    const templates: TeamTemplateDto[] = [
      { id: 'grp-1', title: 'Web App Team', description: 'A team', roles: [] },
    ];
    getTemplatesMock().mockResolvedValue(templates);

    renderWithRouter('proj-004');

    await waitFor(() => {
      const btn = screen.getByRole('button', { name: 'Review' });
      expect(
        btn.hasAttribute('disabled') || btn.getAttribute('aria-disabled') === 'true',
      ).toBe(true);
    });
  });

  it('sends scenario mode when the template roles are unchanged', async () => {
    const templates: TeamTemplateDto[] = [
      {
        id: 'grp-1',
        title: 'Web App Team',
        description: 'A typical web application team',
        roles: [
          { id: 'role-fe', title: 'Frontend Engineer', summary: 'UI', default_model: 'm' },
          { id: 'role-be', title: 'Backend Engineer', summary: 'API', default_model: 'm' },
        ],
      },
    ];
    getTemplatesMock().mockResolvedValue(templates);
    vi.mocked(apiClient.createProposal).mockResolvedValue({
      proposal_id: 'p1',
      mode: 'scenario',
      universe: '',
      members: [],
      existing_team_present: false,
      run_id: null,
      warnings: [],
    });

    const user = userEvent.setup();
    renderWithRouter('proj-010');

    await user.click(screen.getByRole('tab', { name: /Template/ }));
    await waitFor(() => expect(screen.getByText('Web App Team')).toBeDefined());
    await user.click(screen.getByText('Web App Team'));

    await user.click(screen.getByRole('button', { name: 'Review' }));

    await waitFor(() => {
      expect(apiClient.createProposal).toHaveBeenCalledWith('proj-010', { mode: 'scenario', template_id: 'grp-1' });
    });
  });

  it('sends manual mode with role_ids when the user overrides template roles', async () => {
    const templates: TeamTemplateDto[] = [
      {
        id: 'grp-1',
        title: 'Web App Team',
        description: 'A typical web application team',
        roles: [
          { id: 'role-fe', title: 'Frontend Engineer', summary: 'UI', default_model: 'm' },
          { id: 'role-be', title: 'Backend Engineer', summary: 'API', default_model: 'm' },
        ],
      },
      {
        id: 'grp-2',
        title: 'Ops Team',
        description: 'Operations',
        roles: [
          { id: 'role-sre', title: 'SRE', summary: 'Reliability', default_model: 'm' },
        ],
      },
    ];
    getTemplatesMock().mockResolvedValue(templates);
    vi.mocked(apiClient.createProposal).mockResolvedValue({
      proposal_id: 'p2',
      mode: 'manual',
      universe: '',
      members: [],
      existing_team_present: false,
      run_id: null,
      warnings: [],
    });

    const user = userEvent.setup();
    renderWithRouter('proj-011');

    await user.click(screen.getByRole('tab', { name: /Template/ }));
    await waitFor(() => expect(screen.getByText('Web App Team')).toBeDefined());
    await user.click(screen.getByText('Web App Team'));

    // Add an extra role not in the template's defaults (override).
    await user.click(screen.getByRole('checkbox', { name: 'SRE' }));

    await user.click(screen.getByRole('button', { name: 'Review' }));

    await waitFor(() => {
      expect(apiClient.createProposal).toHaveBeenCalledTimes(1);
    });
    const [, req] = vi.mocked(apiClient.createProposal).mock.calls[0];
    expect(req.mode).toBe('manual');
    expect(req.role_ids).toEqual(expect.arrayContaining(['role-fe', 'role-be', 'role-sre']));
    expect(req.template_id).toBeUndefined();
  });
});
