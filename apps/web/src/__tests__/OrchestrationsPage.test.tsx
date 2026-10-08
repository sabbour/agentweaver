import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { OrchestrationsPage } from '../pages/OrchestrationsPage';
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectRuns: vi.fn(),
    getProject: vi.fn(),
    cancelRun: vi.fn(),
    deleteRun: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjectRuns`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function runsPage(items: unknown[], page = 1, pageSize = 100, totalCount = items.length) {
  return {
    items,
    page,
    page_size: pageSize,
    total_count: totalCount,
    total_pages: Math.max(1, Math.ceil(totalCount / Math.max(1, pageSize))),
  } as never;
}

function renderPage() {
  return render(
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/projects/p1/orchestrations']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} />
        </Routes>
      </MemoryRouter>
    </AzureFluentProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({ name: 'Demo' } as never);
});

afterEach(() => cleanup());

describe('OrchestrationsPage', () => {
  it('lists only coordinator runs', async () => {
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) return runsPage([]);
      return runsPage([
        { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Coordinate squad', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
        { workflow_run_id: 'w1', execution_id: 'e2', agent_name: 'Ada', task: 'Solo task', status: 'in_progress', started_at: new Date().toISOString() },
      ]);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Coordinate squad')).toBeDefined());
    expect(screen.queryByText('Solo task')).toBeNull();
  });

  it('labels the automatic assembly handoff without presenting it as a user-waiting stage', async () => {
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) return runsPage([]);
      return runsPage([
        { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Assemble squad output', status: 'in_progress', coordinator_status: 'awaiting_assembly', started_at: new Date().toISOString() },
      ]);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Assemble squad output')).toBeDefined());
    expect(document.body.textContent).toContain('Preparing assembly');
    expect(document.body.textContent).not.toContain('Awaiting assembly');
  });

  it('shows an empty state when there are no orchestrations', async () => {
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue(runsPage([]));

    renderPage();

    await waitFor(() => expect(screen.getByText('No orchestrations yet')).toBeDefined());
  });

  it('disables Stop for terminal orchestrations but enables Delete', async () => {
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) {
        return runsPage([
          { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Done work', status: 'merged', coordinator_status: 'complete', started_at: new Date().toISOString() },
        ], options?.page ?? 1, options?.pageSize ?? 10);
      }
      return runsPage([
        { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Done work', status: 'merged', coordinator_status: 'complete', started_at: new Date().toISOString() },
      ]);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Done work')).toBeDefined());
    expect(screen.getByRole('button', { name: 'Stop orchestration' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Delete orchestration' })).toHaveProperty('disabled', false);
  });

  it('treats assemble_ready runs as recent-only terminal history', async () => {
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      const assembleReadyRun = {
        workflow_run_id: 'ready-1',
        execution_id: 'ready-1',
        agent_name: 'Coordinator',
        task: 'Assembly ready handoff',
        status: 'assemble_ready',
        coordinator_status: 'complete',
        started_at: new Date().toISOString(),
      };

      if (options?.terminalOnly) {
        return runsPage([assembleReadyRun], options?.page ?? 1, options?.pageSize ?? 10);
      }

      return runsPage([assembleReadyRun]);
    });

    renderPage();

    const recentSection = await screen.findByRole('region', { name: 'Recent orchestrations' });
    expect(within(recentSection).getByText('Assembly ready handoff')).toBeDefined();
    expect(screen.queryByRole('region', { name: 'Active orchestrations' })).toBeNull();

    const stopButton = within(recentSection).getByRole('button', { name: 'Stop orchestration' });
    expect(stopButton).toHaveProperty('disabled', true);
    expect(within(recentSection).getByRole('button', { name: 'Delete orchestration' })).toHaveProperty('disabled', false);
  });

  it('stops a running orchestration after confirmation and refreshes the list', async () => {
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) return runsPage([]);
      return runsPage([
        { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Running work', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
      ]);
    });
    vi.mocked(apiClient.cancelRun).mockResolvedValue({ run_id: 'c1', status: 'failed', cancelled: true, already_terminal: false } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Running work')).toBeDefined());
    const stopBtn = screen.getByRole('button', { name: 'Stop orchestration' });
    expect(stopBtn).toHaveProperty('disabled', false);

    fireEvent.click(stopBtn);

    // Stop now uses a Dialog instead of window.confirm — click the confirm button inside the dialog
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Stop' }));

    await waitFor(() => expect(apiClient.cancelRun).toHaveBeenCalledWith('c1'));
    await waitFor(() => expect(vi.mocked(apiClient.getProjectRuns).mock.calls.length).toBeGreaterThanOrEqual(4));
  });

  it('deletes an orchestration via the confirm dialog and removes it from the list', async () => {
    let deleted = false;
    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (deleted) return runsPage([]);
      if (options?.terminalOnly) return runsPage([]);
      return runsPage([
        { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Delete me', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
      ]);
    });
    vi.mocked(apiClient.deleteRun).mockImplementation(async () => {
      deleted = true;
      return undefined as never;
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Delete me')).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: 'Delete orchestration' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(apiClient.deleteRun).toHaveBeenCalledWith('c1'));
    await waitFor(() => expect(screen.queryByText('Delete me')).toBeNull());
  });

  it('keeps active runs visible beyond the first 100 items and pages terminal history from the server', async () => {
    const terminalRuns = Array.from({ length: 100 }, (_, i) => ({
      workflow_run_id: `r${i}`,
      execution_id: `e${i}`,
      agent_name: 'Coordinator',
      task: `Terminal run ${i}`,
      status: 'merged',
      coordinator_status: 'complete',
      started_at: new Date(Date.now() - i * 1000).toISOString(),
    }));
    const activeRun = {
      workflow_run_id: 'active-101',
      execution_id: 'active-101',
      agent_name: 'Coordinator',
      task: 'Active run beyond 100',
      status: 'in_progress',
      coordinator_status: 'dispatching',
      started_at: new Date('2026-01-01T00:00:00Z').toISOString(),
    };

    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) {
        const pageNumber = options?.page ?? 1;
        const pageSize = options?.pageSize ?? 10;
        const start = (pageNumber - 1) * pageSize;
        return runsPage(
          terminalRuns.slice(start, start + pageSize),
          pageNumber,
          pageSize,
          terminalRuns.length,
        );
      }

      const allRuns = [...terminalRuns, activeRun];
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 100;
      const start = (pageNumber - 1) * pageSize;
      return runsPage(allRuns.slice(start, start + pageSize), pageNumber, pageSize, allRuns.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Active run beyond 100')).toBeDefined());
    expect(screen.getByText('Terminal run 0')).toBeDefined();
    expect(screen.getByText('Terminal run 9')).toBeDefined();
    expect(screen.queryByText('Terminal run 10')).toBeNull();

    const nextButtons = screen.getAllByRole('button', { name: 'Next' });
    fireEvent.click(nextButtons[nextButtons.length - 1]);

    await waitFor(() => expect(screen.getByText('Terminal run 10')).toBeDefined());
    expect(screen.queryByText('Terminal run 0')).toBeNull();
    expect(
      vi.mocked(apiClient.getProjectRuns).mock.calls.some(([, options]) => options?.terminalOnly && options.page === 2),
    ).toBe(true);
  });

  it('resets to page 1 and refetches the Recent section when its page size changes', async () => {
    const terminalRuns = Array.from({ length: 30 }, (_, i) => ({
      workflow_run_id: `r${i}`,
      execution_id: `e${i}`,
      agent_name: 'Coordinator',
      task: `Terminal run ${i}`,
      status: 'merged',
      coordinator_status: 'complete',
      started_at: new Date(Date.now() - i * 1000).toISOString(),
    }));

    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) {
        const pageNumber = options?.page ?? 1;
        const pageSize = options?.pageSize ?? 10;
        const start = (pageNumber - 1) * pageSize;
        return runsPage(terminalRuns.slice(start, start + pageSize), pageNumber, pageSize, terminalRuns.length);
      }
      // The non-terminal-only call is the full coordinator-run scan (via collectPagedItems) —
      // it must include these same runs too, matching real `/runs` semantics, or the page
      // falls back to its "No orchestrations yet" empty state regardless of the Recent section.
      return runsPage(terminalRuns, 1, 100, terminalRuns.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Terminal run 0')).toBeDefined());

    fireEvent.click(screen.getByRole('combobox', { name: 'Rows per page' }));
    fireEvent.click(await screen.findByRole('option', { name: '25 / page' }));

    await waitFor(() =>
      expect(
        vi.mocked(apiClient.getProjectRuns).mock.calls.some(([, options]) => options?.terminalOnly && options.page === 1 && options.pageSize === 25),
      ).toBe(true),
    );
    await waitFor(() => expect(screen.getByText('Terminal run 24')).toBeDefined());
    expect(screen.queryByText('Terminal run 25')).toBeNull();
  });

  it('disables Next once the Recent section reaches its last real page (boundary, not an out-of-range fetch)', async () => {
    const terminalRuns = Array.from({ length: 12 }, (_, i) => ({
      workflow_run_id: `r${i}`,
      execution_id: `e${i}`,
      agent_name: 'Coordinator',
      task: `Terminal run ${i}`,
      status: 'merged',
      coordinator_status: 'complete',
      started_at: new Date(Date.now() - i * 1000).toISOString(),
    }));

    vi.mocked(apiClient.getProjectRuns).mockImplementation(async (_projectId, options) => {
      if (options?.terminalOnly) {
        const pageNumber = options?.page ?? 1;
        const pageSize = options?.pageSize ?? 10;
        const start = (pageNumber - 1) * pageSize;
        return runsPage(terminalRuns.slice(start, start + pageSize), pageNumber, pageSize, terminalRuns.length);
      }
      return runsPage(terminalRuns, 1, 100, terminalRuns.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Terminal run 0')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    // Only 2 items on the final page (12 total, page size 10) — Next should now be disabled
    // rather than the UI issuing a page=3 request that would come back empty.
    await waitFor(() => expect(screen.getByText('Terminal run 10')).toBeDefined());
    expect(screen.getByText('Terminal run 11')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Next' })).toHaveProperty('disabled', true);
  });
});
