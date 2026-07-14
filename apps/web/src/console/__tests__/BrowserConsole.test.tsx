import { apiClient } from '../../api/apiClient';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { BrowserConsole } from '../BrowserConsole';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { AgentweaverConsoleResponse, Project } from '../../api/types';
import type { ReactNode } from 'react';
vi.mock('../../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    getProject: vi.fn(),
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
    sendConsoleMessage: vi.fn(),
  },
}));

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
    working_directory: 'C:\\repo\\x',
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
  } as Project;
}

function Wrapper({ children, initialPath = '/overview' }: { children: ReactNode; initialPath?: string }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[initialPath]}>{children}</MemoryRouter>
    </FluentProvider>
  );
}

function RouteHarness() {
  const navigate = useNavigate();
  return (
    <>
      <BrowserConsole />
      <button type="button" onClick={() => navigate('/projects/p1/orchestrations/run-1')}>Go run</button>
      <button type="button" onClick={() => navigate('/overview')}>Go global</button>
    </>
  );
}

function submit(text: string) {
  fireEvent.change(screen.getByRole('textbox', { name: 'Ask Agentweaver' }), { target: { value: text } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
}

function response(partial: Partial<AgentweaverConsoleResponse>): AgentweaverConsoleResponse {
  return {
    conversation_id: 'c1',
    role: 'assistant',
    status: 'completed',
    message: 'Done',
    ...partial,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue(makeProject('p1', 'Alpha'));
});
afterEach(() => cleanup());

describe('BrowserConsole operator dock', () => {
  it('keeps a singleton transcript while route context changes', async () => {
    vi.mocked(apiClient.sendConsoleMessage).mockResolvedValue(response({ message: 'I can help from here.' }));

    render(<Wrapper><RouteHarness /></Wrapper>);
    submit('hello console');

    expect(await screen.findByText('I can help from here.')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Go run' }));

    expect(screen.getByText('hello console')).toBeDefined();
    expect(await screen.findByText(/Run · run-1/)).toBeDefined();
    expect(await screen.findByText(/Project · Alpha/)).toBeDefined();
  });

  it('submits natural language to the facade seam with global context', async () => {
    vi.mocked(apiClient.sendConsoleMessage).mockResolvedValue(response({
      conversation_id: 'c1',
      message: 'The project list is healthy.',
      tools: [{ label: 'List projects', status: 'completed', detail: '2 active projects' }],
      links: [{ label: 'Open Projects', to: '/projects' }],
    }));

    render(<Wrapper><BrowserConsole /></Wrapper>);
    submit('show me active work');

    await waitFor(() => expect(apiClient.sendConsoleMessage).toHaveBeenCalledWith(expect.objectContaining({
      conversation_id: null,
      message: 'show me active work',
      text: 'show me active work',
      context: expect.objectContaining({ scope: 'global', project_id: null, run_id: null, route: '/overview' }),
    })));
    expect(await screen.findByText('The project list is healthy.')).toBeDefined();
    expect(screen.getByText(/List projects/)).toBeDefined();
    expect(screen.getByRole('link', { name: /Open Projects/ }).getAttribute('href')).toBe('/projects');
  });

  it('uses project and run route bindings when calling the facade', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue(makeProject('p1', 'Alpha'));
    vi.mocked(apiClient.sendConsoleMessage).mockResolvedValue(response({ message: 'Run summary ready.' }));

    render(<Wrapper initialPath="/projects/p1/orchestrations/run-77"><BrowserConsole /></Wrapper>);
    expect(await screen.findByText(/Project · Alpha/)).toBeDefined();
    expect(screen.getByText(/Run · run-77/)).toBeDefined();

    submit('summarize this run');

    await waitFor(() => expect(apiClient.sendConsoleMessage).toHaveBeenCalledWith(expect.objectContaining({
      message: 'summarize this run',
      text: 'summarize this run',
      context: expect.objectContaining({
        scope: 'run',
        project_id: 'p1',
        run_id: 'run-77',
        route: '/projects/p1/orchestrations/run-77',
      }),
    })));
  });

  it('preserves log semantics and Enter versus Shift+Enter composer behavior', async () => {
    vi.mocked(apiClient.sendConsoleMessage).mockResolvedValue(response({ message: 'Keyboard request received.' }));

    render(<Wrapper><BrowserConsole /></Wrapper>);
    expect(screen.getByRole('log', { name: 'Console responses' })).toBeDefined();

    const textbox = screen.getByRole('textbox', { name: 'Ask Agentweaver' });
    fireEvent.change(textbox, { target: { value: 'line one' } });
    fireEvent.keyDown(textbox, { key: 'Enter', code: 'Enter', shiftKey: true });
    expect(apiClient.sendConsoleMessage).not.toHaveBeenCalled();

    fireEvent.change(textbox, { target: { value: 'line one\nline two' } });
    fireEvent.keyDown(textbox, { key: 'Enter', code: 'Enter' });

    await waitFor(() => expect(apiClient.sendConsoleMessage).toHaveBeenCalledWith(expect.objectContaining({
      message: 'line one\nline two',
      text: 'line one\nline two',
    })));
  });

  it('renders clarification, gate-required, and error facade states', async () => {
    vi.mocked(apiClient.sendConsoleMessage)
      .mockResolvedValueOnce(response({ kind: 'clarification', message: 'Which project should I inspect?' }))
      .mockResolvedValueOnce(response({ kind: 'gate_required', message: 'Review the outcome plan before dispatch.', gate: { kind: 'outcome', title: 'Outcome plan review' } }))
      .mockResolvedValueOnce(response({ status: 'blocked', message: 'The facade agent could not reach the run service.' }));

    render(<Wrapper><BrowserConsole /></Wrapper>);

    submit('inspect it');
    expect(await screen.findByText('Clarification needed')).toBeDefined();
    expect(screen.getByText('Which project should I inspect?')).toBeDefined();

    submit('start the plan');
    expect(await screen.findByText('Outcome plan review')).toBeDefined();
    expect(screen.getByText('Review the outcome plan before dispatch.')).toBeDefined();

    submit('try again');
    expect(await screen.findByText('Blocked')).toBeDefined();
    expect(screen.getByText('The facade agent could not reach the run service.')).toBeDefined();
  });

  it('keeps slash commands as secondary shortcuts and does not present gate-bypass copy', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([makeProject('p1', 'Alpha')]));

    render(<Wrapper><BrowserConsole /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: '/projects' }));
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText(/1 project/)).toBeDefined();
    expect(screen.getByRole('link', { name: /Alpha \(p1\)/ }).getAttribute('href')).toBe('/projects/p1');
    expect(screen.queryByText(/bypass|skip gate|auto-confirm/i)).toBeNull();
  });
});
