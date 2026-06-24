import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';

const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    startOrchestration: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { StartOrchestrationFab } from '../components/StartOrchestrationFab';
import type { Project } from '../api/types';

function makeProject(id: string, name: string): Project {
  return {
    project_id: id,
    name,
    origin: 'blank',
    source_repository: null,
    working_directory: '/tmp/x',
    default_branch: 'main',
    owner: 'me',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('StartOrchestrationFab', () => {
  it('renders the floating action button', () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([]);
    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: 'Start task' })).toBeDefined();
  });

  it('opens a dialog with a project selector and starts in the selected project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]);
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-77' } as never);

    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));

    // Project selector is present once the list loads.
    const combobox = await screen.findByRole('combobox', { name: 'Project' });
    fireEvent.click(combobox);
    fireEvent.click(await screen.findByRole('option', { name: 'Beta' }));

    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Ship the thing' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Start' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-b', 'Ship the thing'),
    );
    expect(navigateMock).toHaveBeenCalledWith('/projects/proj-b/orchestrations/run-77');
  });

  it('defaults the project selection to the current project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]);
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-9' } as never);

    render(
      <Wrapper>
        <StartOrchestrationFab currentProjectId="proj-a" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    const combobox = await screen.findByRole('combobox', { name: 'Project' });
    // The dropdown shows the active project preselected.
    await waitFor(() => expect((combobox as HTMLInputElement).value).toBe('Alpha'));

    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Default project goal' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-a', 'Default project goal'),
    );
  });

  it('defaults to the active project at open-time even when it resolved after mount', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]);
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-5' } as never);

    // The FAB lives in AppShell and never remounts: it first mounts with no active
    // project, then the active project resolves later (e.g. lastActiveProjectId).
    const { rerender } = render(
      <Wrapper>
        <StartOrchestrationFab currentProjectId={undefined} />
      </Wrapper>,
    );
    rerender(
      <Wrapper>
        <StartOrchestrationFab currentProjectId="proj-b" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    const combobox = await screen.findByRole('combobox', { name: 'Project' });
    await waitFor(() => expect((combobox as HTMLInputElement).value).toBe('Beta'));

    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Resolved project goal' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-b', 'Resolved project goal'),
    );
  });

  it('preselects nothing when there is no active project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]);

    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    const combobox = await screen.findByRole('combobox', { name: 'Project' });
    // No active project → nothing preselected; Start stays disabled until the user picks.
    expect((combobox as HTMLInputElement).value).toBe('');
    expect(screen.getByRole('button', { name: 'Start' })).toHaveProperty('disabled', true);
  });

  it('guides the user to create a project when none exist', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([]);

    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));

    expect(await screen.findByText(/Create a project first/)).toBeDefined();
    expect(screen.getByRole('button', { name: 'Start' })).toHaveProperty('disabled', true);
  });
});
