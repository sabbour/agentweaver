import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { CastingWizardPage } from '../pages/CastingWizardPage';
import type { TeamTemplateDto, CastProposalDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getTemplates: vi.fn(),
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
const createProposalMock = () => vi.mocked(apiClient.createProposal);

beforeEach(() => {
  vi.clearAllMocks();
  getTemplatesMock().mockResolvedValue([] as TeamTemplateDto[]);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('CastingWizardPage', () => {
  it('renders mode selection step by default', () => {
    renderWithRouter('proj-001');

    expect(screen.getByText('Choose casting mode')).toBeDefined();
    expect(screen.getByText(/Team template/)).toBeDefined();
    expect(screen.getByText(/Describe a goal/)).toBeDefined();
    expect(screen.getByText(/Analyze project/)).toBeDefined();
  });

  it('navigates to configure step when Next is clicked', async () => {
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

    const nextButton = screen.getByRole('button', { name: 'Next' });
    await user.click(nextButton);

    await waitFor(() => {
      expect(screen.getByText('Configure')).toBeDefined();
    });
  });

  it('shows scenario list on configure step for scenario mode', async () => {
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

    const nextButton = screen.getByRole('button', { name: 'Next' });
    await user.click(nextButton);

    await waitFor(() => {
      expect(screen.getByText('Web App Team')).toBeDefined();
      expect(screen.getByText('Frontend, backend, and devops roles')).toBeDefined();
    });
  });

  it('disables Next on configure step until scenario is selected', async () => {
    const templates: TeamTemplateDto[] = [
      { id: 'grp-1', title: 'Web App Team', description: 'A team', roles: [] },
    ];
    getTemplatesMock().mockResolvedValue(templates);
    createProposalMock().mockResolvedValue({} as CastProposalDto);

    const user = userEvent.setup();
    renderWithRouter('proj-004');

    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(screen.getByText('Web App Team')).toBeDefined();
    });

    const nextBtn = screen.getByRole('button', { name: 'Next' });
    expect(
      nextBtn.hasAttribute('disabled') ||
      nextBtn.getAttribute('aria-disabled') === 'true',
    ).toBe(true);
  });
});
