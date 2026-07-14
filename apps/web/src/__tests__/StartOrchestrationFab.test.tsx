import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { StartOrchestrationFab } from '../components/StartOrchestrationFab';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { Project } from '../api/types';
import type { ReactNode } from 'react';
const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    startOrchestration: vi.fn(),
    listWorkflows: vi.fn(() => Promise.resolve({ default_workflow_id: 'default', workflows: [] })),
  },
}));

import { ApiError } from '../api/client';

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function projectsPage(items: Project[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

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
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>{children}</MemoryRouter>
    </AzureFluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('StartOrchestrationFab', () => {
  it('renders the inline top-bar action button', () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: 'Start task' })).toBeDefined();
    expect(screen.getByTestId('start-task-topbar-action')).toBeDefined();
  });

  it('opens a dialog with a project selector and starts direct in the selected project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]));
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

    expect(screen.getByText(/Direct starts faster/i)).toBeDefined();
    expect(screen.getByText(/review, tool approval, assembly, and merge gates still apply/i)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-b', 'Ship the thing', null, 'direct'),
    );
    expect(navigateMock).toHaveBeenCalledWith('/projects/proj-b/orchestrations/run-77');
  });

  it('defaults the project selection to the current project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]));
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
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-a', 'Default project goal', null, 'direct'),
    );
  });

  it('defaults to the active project at open-time even when it resolved after mount', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]));
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
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-b', 'Resolved project goal', null, 'direct'),
    );
  });

  it('preselects nothing when there is no active project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([
      makeProject('proj-a', 'Alpha'),
      makeProject('proj-b', 'Beta'),
    ]));

    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    const combobox = await screen.findByRole('combobox', { name: 'Project' });
    // No active project → nothing preselected; Start stays disabled until the user picks.
    expect((combobox as HTMLInputElement).value).toBe('');
    expect(screen.getByRole('button', { name: 'Direct' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Define Outcome' })).toHaveProperty('disabled', true);
  });

  it('guides the user to create a project when none exist', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));

    render(
      <Wrapper>
        <StartOrchestrationFab />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));

    expect(await screen.findByText(/Create a project first/)).toBeDefined();
    expect(screen.getByRole('button', { name: 'Direct' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Define Outcome' })).toHaveProperty('disabled', true);
  });

  it('shows a workflow dropdown and passes the selected workflow override', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([makeProject('proj-a', 'Alpha')]));
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-42' } as never);
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'software-delivery',
      workflows: [
        { id: 'software-delivery', name: 'Software Delivery', valid: true, source: 'catalog', is_built_in: true, is_default: true, warnings: [] },
        { id: 'default', name: 'Generic Workflow', valid: true, source: 'built-in', is_built_in: true, is_default: false, warnings: [] },
      ],
    } as never);

    render(
      <Wrapper>
        <StartOrchestrationFab currentProjectId="proj-a" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));

    const workflow = await screen.findByRole('combobox', { name: 'Workflow' });
    fireEvent.change(workflow, { target: { value: 'software-delivery' } });

    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Ship a feature' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Define Outcome' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-a', 'Ship a feature', 'software-delivery'),
    );
  });

  it('shows a Cast a team CTA for no_team start failures and routes to casting', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([makeProject('proj-a', 'Alpha')]));
    vi.mocked(apiClient.startOrchestration).mockRejectedValue(new ApiError(
      409,
      JSON.stringify({
        error: 'no_team',
        message: 'This project has no team. Cast a team before starting an orchestration.',
      }),
    ));

    render(
      <Wrapper>
        <StartOrchestrationFab currentProjectId="proj-a" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    await screen.findByRole('combobox', { name: 'Project' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Ship a feature' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    expect(await screen.findByText('This project has no team. Cast a team before starting an orchestration.')).toBeDefined();
    expect(document.body.textContent).not.toContain('API error 409');

    fireEvent.click(screen.getByRole('button', { name: 'Cast a team' }));
    expect(navigateMock).toHaveBeenCalledWith('/projects/proj-a/team/cast');
  });
});
