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
});
