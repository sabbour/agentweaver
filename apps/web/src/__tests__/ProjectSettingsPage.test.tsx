import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import type { ReviewPolicyListResponse, ReviewPolicyDetailDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProject: vi.fn(),
    getServerInfo: vi.fn(),
    getSandboxPolicy: vi.fn(),
    listReviewPolicies: vi.fn(),
    getReviewPolicy: vi.fn(),
    setActiveReviewPolicy: vi.fn(),
    syncReviewPolicies: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { ProjectSettingsPage } from '../pages/ProjectSettingsPage';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderPage(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/settings`]}>
        <Routes>
          <Route path="/projects/:projectId/settings" element={<ProjectSettingsPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const reviewList: ReviewPolicyListResponse = {
  active_policy_name: 'default',
  policies: [
    {
      name: 'default',
      description: 'Built-in default policy.',
      source: 'built-in',
      valid: true,
      error: null,
      is_built_in: true,
      is_active: true,
    },
  ],
};

const reviewDetail: ReviewPolicyDetailDto = {
  name: 'default',
  description: 'Built-in default policy.',
  source: 'built-in',
  is_built_in: true,
  is_active: true,
  steps: [{ kind: 'rai', label: null }],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({
    project_id: 'proj-1',
    name: 'Demo',
    working_directory: 'C:/demo',
    default_model_github_copilot: 'gpt-4',
  } as never);
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: 'C:/data' } as never);
  vi.mocked(apiClient.getSandboxPolicy).mockResolvedValue({} as never);
  vi.mocked(apiClient.listReviewPolicies).mockResolvedValue(reviewList);
  vi.mocked(apiClient.getReviewPolicy).mockResolvedValue(reviewDetail);
});

afterEach(() => {
  cleanup();
});

describe('ProjectSettingsPage', () => {
  it('renders the four settings sections in the rail', async () => {
    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Project settings')).toBeDefined());

    const rail = screen.getByRole('navigation', { name: 'Settings sections' });
    expect(rail).toBeDefined();
    expect(screen.getByRole('button', { name: /General/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Sandbox policy/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Review policy/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Danger Zone/i })).toBeDefined();
  });

  it('switches the pane when a rail item is clicked', async () => {
    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Rename project')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /Review policy/i }));

    await waitFor(() => expect(apiClient.listReviewPolicies).toHaveBeenCalledWith('proj-1'));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Sync/i })).toBeDefined());
  });

  it('shows an inverted "Sandbox enabled" toggle and gates the network switch on it', async () => {
    vi.mocked(apiClient.getSandboxPolicy).mockResolvedValue({
      repository_path: 'C:/demo',
      shell_enabled: true,
      direct: true, // sandbox OFF
      network_enabled: false,
      allowed_repository_roots: ['C:/demo'],
      destructive_command_patterns: ['rm -rf'],
    } as never);

    renderPage('proj-1');
    await waitFor(() => expect(screen.getByText('Project settings')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    // Inverted label present; the old "Direct execution" label is gone.
    await waitFor(() => expect(screen.getByText('Sandbox enabled')).toBeDefined());
    expect(screen.queryByText(/Direct execution/i)).toBeNull();

    // Switch order: Shell execution, Sandbox enabled, Outbound network.
    let switches = screen.getAllByRole('switch') as HTMLInputElement[];
    expect(switches).toHaveLength(3);
    // direct=true => "Sandbox enabled" is unchecked (inverted).
    expect(switches[1].checked).toBe(false);
    // Network toggle is disabled while the sandbox is off, with a hint.
    expect(switches[2].disabled).toBe(true);
    expect(screen.getByText('Only applies when the sandbox is enabled.')).toBeDefined();

    // Turning the sandbox ON sends direct=false and re-enables the network toggle.
    fireEvent.click(switches[1]);
    switches = screen.getAllByRole('switch') as HTMLInputElement[];
    expect(switches[1].checked).toBe(true);
    expect(switches[2].disabled).toBe(false);
  });
});
