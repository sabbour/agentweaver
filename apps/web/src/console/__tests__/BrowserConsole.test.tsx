import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';

vi.mock('../../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    getBoard: vi.fn(),
    captureBacklogTask: vi.fn(),
    moveTaskToReady: vi.fn(),
    listProjectRuns: vi.fn(),
    startOrchestration: vi.fn(),
    getRun: vi.fn(),
    getRunEvents: vi.fn().mockResolvedValue([]),
    steerCoordinator: vi.fn(),
    confirmOutcomeSpec: vi.fn(),
    reviseOutcomeSpec: vi.fn(),
    reviewAssembly: vi.fn(),
  },
}));

// The bound-run panel reuses the shared SSE hook; stub it so tests never open a real
// stream. The command engine + prose gating is what we assert here.
vi.mock('../../api/sse', () => ({
  useRunStream: () => ({ events: [], droppedEventCount: 0, status: 'connecting', error: null, reconnect: vi.fn() }),
}));

import { apiClient } from '../../api/apiClient';
import { BrowserConsole } from '../BrowserConsole';
import type { Project } from '../../api/types';

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
  } as Project;
}

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

function type(text: string) {
  fireEvent.change(screen.getByRole('textbox', { name: 'Console input' }), { target: { value: text } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
}

beforeEach(() => vi.clearAllMocks());
afterEach(() => cleanup());

describe('BrowserConsole (terminal TUI)', () => {
  it('renders a greeting and responds to /help', async () => {
    render(<Wrapper><BrowserConsole /></Wrapper>);
    expect(screen.getByText(/control console/i)).toBeDefined();
    type('/help');
    expect(await screen.findByText(/Commands:/)).toBeDefined();
  });

  it('lists projects with clickable links', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('p1', 'Alpha'), makeProject('p2', 'Beta')]);
    render(<Wrapper><BrowserConsole /></Wrapper>);
    type('/projects');
    const link = await screen.findByRole('link', { name: /Alpha \(p1\)/ });
    expect(link.getAttribute('href')).toBe('/projects/p1');
  });

  it('requires a selected project before creating backlog items', async () => {
    render(<Wrapper><BrowserConsole /></Wrapper>);
    type('/add Fix login');
    expect(await screen.findByText(/Select a project first/)).toBeDefined();
    expect(apiClient.captureBacklogTask).not.toHaveBeenCalled();
  });

  it('selects a project and captures a backlog item through the real API', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('p1', 'Alpha')]);
    vi.mocked(apiClient.captureBacklogTask).mockResolvedValue({ task_id: 't9', title: 'Fix login' } as never);
    render(<Wrapper><BrowserConsole /></Wrapper>);

    type('/use Alpha');
    await screen.findByText(/Active project . Alpha/);

    type('/add Fix login :: it 500s');
    await waitFor(() =>
      expect(apiClient.captureBacklogTask).toHaveBeenCalledWith('p1', { title: 'Fix login', description: 'it 500s' }),
    );
    expect(await screen.findByText(/Captured "Fix login"/)).toBeDefined();
  });

  it('asks for clarification when a project name is ambiguous', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('p1', 'Web App'), makeProject('p2', 'Web App v2')]);
    render(<Wrapper><BrowserConsole /></Wrapper>);
    type('/use Web');
    expect(await screen.findByText(/matches 2 projects/)).toBeDefined();
  });

  it('starts an orchestration and preserves the confirmation gate (does not auto-confirm)', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('p1', 'Alpha')]);
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-77' } as never);
    render(<Wrapper><BrowserConsole /></Wrapper>);

    type('/use Alpha');
    await screen.findByText(/Active project . Alpha/);

    type('/orchestrate ship the feature');
    await waitFor(() => expect(apiClient.startOrchestration).toHaveBeenCalledWith('p1', 'ship the feature'));
    expect(await screen.findByText(/Outcome plan/i)).toBeDefined();
    expect(apiClient.confirmOutcomeSpec).not.toHaveBeenCalled();
    const link = await screen.findByRole('link', { name: /Open orchestration/ });
    expect(link.getAttribute('href')).toBe('/projects/p1/orchestrations/run-77');
  });

  it('does NOT auto-start work from free-form prose — it asks for explicit confirmation first', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('p1', 'Alpha')]);
    render(<Wrapper><BrowserConsole /></Wrapper>);

    type('/use Alpha');
    await screen.findByText(/Active project . Alpha/);

    type('please build a login page');
    expect(await screen.findByText(/No orchestration is bound/)).toBeDefined();
    expect(apiClient.startOrchestration).not.toHaveBeenCalled();

    // Explicit confirmation starts it.
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-9' } as never);
    type('yes');
    await waitFor(() => expect(apiClient.startOrchestration).toHaveBeenCalledWith('p1', 'please build a login page'));
  });
});
