import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getBoard: vi.fn(),
    getProject: vi.fn(),
    getProjectRuns: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { FlowPage } from '../pages/FlowPage';

function renderPage(initialEntry = '/projects/p1/flow') {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/projects/:projectId/flow" element={<FlowPage />} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({ name: 'Demo' } as never);
  vi.mocked(apiClient.getProjectRuns).mockResolvedValue([]);
});

afterEach(() => cleanup());

describe('FlowPage', () => {
  it('lists active agents and what they are working on', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [
        { agent_name: 'Ada', active: 1, queued: 2, blocked: 0, done: 3, run_ids: ['r1'], sample_titles: ['Implement login'] },
      ],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getAllByText('Ada').length).toBeGreaterThan(0));
    expect(screen.getByText('1 active')).toBeDefined();
    expect(screen.getByText('Implement login')).toBeDefined();
  });

  it('shows an empty state when no agents are active', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('No active agents')).toBeDefined());
  });

  it('renders one labeled group per orchestration with its own titles and View-orchestration link', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [
        {
          agent_name: 'Ada', active: 2, queued: 1, blocked: 0, done: 1,
          run_ids: ['run-aaaa1111', 'run-bbbb2222'], sample_titles: ['x'],
          orchestrations: [
            { run_id: 'run-aaaa1111', title: 'Login epic', active: 1, queued: 1, blocked: 0, done: 0, sample_titles: ['Implement login'] },
            { run_id: 'run-bbbb2222', title: 'Billing epic', active: 1, queued: 0, blocked: 0, done: 1, sample_titles: ['Add invoices'] },
          ],
        },
      ],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getAllByText('Ada').length).toBeGreaterThan(0));
    // Two orchestration group headings, each with its own work.
    expect(screen.getByText('Login epic')).toBeDefined();
    expect(screen.getByText('Billing epic')).toBeDefined();
    expect(screen.getByText('Implement login')).toBeDefined();
    expect(screen.getByText('Add invoices')).toBeDefined();

    // Two distinct View-orchestration links pointing at the two run ids.
    const links = screen.getAllByRole('link', { name: 'View orchestration' });
    const hrefs = links.map((l) => l.getAttribute('href'));
    expect(hrefs).toContain('/projects/p1/orchestrations/run-aaaa1111');
    expect(hrefs).toContain('/projects/p1/orchestrations/run-bbbb2222');
  });

  it('falls back to a short-id label when an orchestration title is null', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [
        {
          agent_name: 'Neo', active: 1, queued: 0, blocked: 0, done: 0,
          run_ids: ['run-cccc3333dddd'], sample_titles: ['y'],
          orchestrations: [
            { run_id: 'run-cccc3333dddd', title: null, active: 1, queued: 0, blocked: 0, done: 0, sample_titles: ['Refactor parser'] },
          ],
        },
      ],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Neo')).toBeDefined());
    // run_id.slice(0, 8) => 'run-cccc'
    expect(screen.getByText('Orchestration run-cccc')).toBeDefined();
    expect(screen.getByText('Refactor parser')).toBeDefined();
  });

  it('filters by the agent query param and renders terminal-run archive history', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [
        { agent_name: 'Ada', active: 1, queued: 0, blocked: 0, done: 0, run_ids: ['r1'], sample_titles: ['Implement login'] },
        { agent_name: 'Neo', active: 1, queued: 0, blocked: 0, done: 0, run_ids: ['r2'], sample_titles: ['Refactor parser'] },
      ],
    } as never);
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
      {
        workflow_run_id: 'wr-1',
        execution_id: 'child-1',
        task: 'Implement login',
        status: 'completed',
        agent_name: 'Ada',
        started_at: '2026-06-23T01:00:00Z',
        ended_at: '2026-06-23T01:10:00Z',
        model_id: 'gpt-4o',
      },
    ] as never);

    renderPage('/projects/p1/flow?agent=Ada');

    await waitFor(() => expect(screen.getByText('Ada')).toBeDefined());
    expect(screen.queryByText('Neo')).toBeNull();
    expect(screen.getByLabelText('Previous work archive')).toBeDefined();
    await waitFor(() => expect(screen.getAllByText('Implement login').length).toBeGreaterThan(0));
    expect(screen.getByRole('link', { name: 'Implement login' }).getAttribute('href'))
      .toBe('/projects/p1/runs/wr-1/execution/child-1');
    expect(apiClient.getProjectRuns).toHaveBeenCalledWith('p1', {
      agentName: 'Ada',
      terminalOnly: true,
      includeChildren: true,
      limit: 20,
    });
  });
});
