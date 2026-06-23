import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, within, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { makeBoard, makeBoardWorkflowUnavailable } from './fixtures/board';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getBoard: vi.fn(),
    getBacklogSettings: vi.fn(),
    setBacklogSettings: vi.fn(),
    captureBacklogTask: vi.fn(),
    editBacklogTask: vi.fn(),
    deleteBacklogTask: vi.fn(),
    moveTaskToReady: vi.fn(),
    moveTaskToBacklog: vi.fn(),
    reorderBacklogTask: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

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
});

afterEach(() => {
  cleanup();
});

describe('KanbanBoard — dynamic columns (FR-013/015/016/019)', () => {
  it('renders Backlog + Ready first, then the server-ordered workflow columns', async () => {
    getBoardMock().mockResolvedValue(makeBoard());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    // Columns appear in the exact order returned by the API.
    const columns = screen.getAllByTestId(/^column-/);
    expect(columns.map((c) => c.getAttribute('data-testid'))).toEqual([
      'column-backlog',
      'column-ready',
      'column-coordinator',
      'column-planned:assembly-custom',
      'column-terminal',
    ]);

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

  it('renders an extra/changed workflow stage as an extra column without code changes (FR-015/SC-004)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-planned:assembly-custom')).toBeTruthy());
    expect(screen.getByText('Custom Stage')).toBeTruthy();
  });

  it('places a run-backed card in the workflow-stage column matching its stage_id (FR-016)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-coordinator')).toBeTruthy());
    const coordinator = screen.getByTestId('column-coordinator');
    const runCard = within(coordinator).getByTestId('run-card-r1');
    expect(runCard).toBeTruthy();
    expect(within(coordinator).getByText('Run-backed work')).toBeTruthy();

    // Coordinator-run detail pages are run_id-keyed for EVERY coordinator run, so the card must
    // navigate by run_id ('r1') — never by workflow_run_id ('wr1', distinct in this fixture).
    // Regression guard for the Feature 009 backlog-pickup 404 cascade.
    expect(runCard.getAttribute('href')).toBe('/projects/proj-1/orchestrations/r1');
  });

  it('renders the workflow-unavailable fallback with only intake columns (FR-019)', async () => {
    getBoardMock().mockResolvedValue(makeBoardWorkflowUnavailable());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());
    expect(screen.getByText(/Workflow columns are unavailable/i)).toBeTruthy();
    expect(screen.queryByTestId('column-coordinator')).toBeNull();
    expect(screen.getByTestId('column-backlog')).toBeTruthy();
    expect(screen.getByTestId('column-ready')).toBeTruthy();
  });

  it('terminal "Show older (N)" toggle refetches with include_terminal_history=true (FR-016a)', async () => {
    getBoardMock().mockResolvedValue(makeBoard());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByText('Show older (5)')).toBeTruthy());
    fireEvent.click(screen.getByText('Show older (5)'));

    await waitFor(() => expect(getBoardMock()).toHaveBeenCalledWith('proj-1', true));
  });
});
