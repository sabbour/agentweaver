import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, within, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { makeBoard, makeBoardWithArchivedItems, makeBoardWorkflowUnavailable } from './fixtures/board';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getBoard: vi.fn(),
    getBacklogSettings: vi.fn(),
    setBacklogSettings: vi.fn(),
    captureBacklogTask: vi.fn(),
    editBacklogTask: vi.fn(),
    deleteBacklogTask: vi.fn(),
    deleteRun: vi.fn(),
    moveTaskToReady: vi.fn(),
    moveTaskToBacklog: vi.fn(),
    reorderBacklogTask: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => mockNavigate };
});

const getBoardMock = () => vi.mocked(apiClient.getBoard);

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

afterEach(() => {
  cleanup();
});

describe('KanbanBoard — fixed columns (FR-013/015/016/019)', () => {
  it('renders only the fixed six board columns', async () => {
    getBoardMock().mockResolvedValue(makeBoard());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    // Columns appear in the fixed product order, not the dynamic API stage order.
    const columns = screen.getAllByTestId(/^column-/);
    expect(columns.map((c) => c.getAttribute('data-testid'))).toEqual([
      'column-backlog',
      'column-ready',
      'column-problems',
      'column-human-review',
      'column-active',
      'column-done',
    ]);
    expect(screen.queryByTestId('column-coordinator')).toBeNull();
    expect(screen.queryByTestId('column-planned:assembly-custom')).toBeNull();
    expect(screen.queryByTestId('column-terminal')).toBeNull();

    // Backlog tasks render in priority (order_key) order as returned.
    const backlog = screen.getByTestId('column-backlog');
    expect(within(backlog).getByText('First backlog task')).toBeTruthy();
    expect(within(backlog).getByText('Second backlog task')).toBeTruthy();

    // Ready card present.
    expect(within(screen.getByTestId('column-ready')).getByText('Ready task')).toBeTruthy();
  });

  it('exposes a board zoom control (Ctrl+Scroll hint, +/- buttons, % readout)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    expect(screen.getByText('Ctrl + Scroll to zoom')).toBeTruthy();
    expect(screen.getByText('100%')).toBeTruthy();
    const zoomOut = screen.getByLabelText('Zoom out');
    const zoomIn = screen.getByLabelText('Zoom in') as HTMLButtonElement;

    // At 100% (max) zoom-in is disabled; zooming out lowers the readout.
    expect(zoomIn.disabled).toBe(true);
    fireEvent.click(zoomOut);
    expect(screen.getByText('90%')).toBeTruthy();
  });

  it('places an active run-backed card in the Active fixed column (FR-016)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-active')).toBeTruthy());
    const active = screen.getByTestId('column-active');
    const runCard = within(active).getByTestId('run-card-r1');
    expect(runCard).toBeTruthy();
    expect(within(active).getByText('Run-backed work')).toBeTruthy();

    // Coordinator-run detail pages are run_id-keyed for EVERY coordinator run, so the card must
    // navigate by run_id ('r1') — never by workflow_run_id ('wr1', distinct in this fixture).
    // Regression guard for the Feature 009 backlog-pickup 404 cascade.
    fireEvent.click(runCard);
    expect(mockNavigate).toHaveBeenCalledWith('/projects/proj-1/orchestrations/r1');
  });

  it('renders the workflow-unavailable warning without reintroducing dynamic columns (FR-019)', async () => {
    getBoardMock().mockResolvedValue(makeBoardWorkflowUnavailable());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());
    expect(screen.getByText(/Workflow columns are unavailable/i)).toBeTruthy();
    expect(screen.queryByTestId('column-coordinator')).toBeNull();
    expect(screen.getAllByTestId(/^column-/).map((c) => c.getAttribute('data-testid'))).toEqual([
      'column-backlog',
      'column-ready',
      'column-problems',
      'column-human-review',
      'column-active',
      'column-done',
    ]);
  });

  it('routes review and failed terminal cards into Human Review and Problems', async () => {
    const board = makeBoard({
      columns: [
        { id: 'backlog', kind: 'intake', label: 'Backlog', cards: [] },
        { id: 'ready', kind: 'intake', label: 'Ready', cards: [] },
        {
          id: 'planned:assembly-review',
          kind: 'workflow',
          label: 'Human Review',
          cards: [
            { kind: 'run', run_id: 'r-review', task: 'Needs review', status: 'awaiting_review', stage_id: 'planned:assembly-review', started_at: '2026-01-01T00:00:00Z' },
          ],
        },
        {
          id: 'terminal',
          kind: 'workflow',
          label: 'Done',
          cards: [
            { kind: 'run', run_id: 'r-failed', task: 'Failed work', status: 'failed', stage_id: 'terminal', started_at: '2026-01-01T00:01:00Z' },
          ],
        },
      ],
    });
    getBoardMock().mockResolvedValue(board);
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-human-review')).toBeTruthy());
    expect(within(screen.getByTestId('column-human-review')).getByText('Needs review')).toBeTruthy();
    expect(within(screen.getByTestId('column-problems')).getByText('Failed work')).toBeTruthy();
  });

  it('does not render archived cards or archive-only columns returned by the API', async () => {
    getBoardMock().mockResolvedValue(makeBoardWithArchivedItems());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());
    expect(screen.queryByText('Archived task')).toBeNull();
    expect(screen.queryByText('Archived run')).toBeNull();
    expect(screen.queryByTestId('task-card-archived-task')).toBeNull();
    expect(screen.queryByTestId('run-card-archived-run')).toBeNull();
  });

  it('terminal "Show older (N)" toggle refetches with include_terminal_history=true (FR-016a)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByText('Show older (5)')).toBeTruthy());
    fireEvent.click(screen.getByText('Show older (5)'));

    await waitFor(() => expect(getBoardMock()).toHaveBeenCalledWith('proj-1', true));
  });

  it('archives a task card using the existing off-board removal action and refetches', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    vi.mocked(apiClient.deleteBacklogTask).mockResolvedValue(undefined);

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());
    const callsBefore = getBoardMock().mock.calls.length;
    fireEvent.click(within(screen.getByTestId('task-card-t1')).getByLabelText('Archive task'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.deleteBacklogTask)).toHaveBeenCalledWith('proj-1', 't1'),
    );
    await waitFor(() =>
      expect(getBoardMock().mock.calls.length).toBeGreaterThan(callsBefore),
    );
  });
});
