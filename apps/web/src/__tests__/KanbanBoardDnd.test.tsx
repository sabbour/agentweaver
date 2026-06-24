import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { makeBoard } from './fixtures/board';
import type { BacklogTaskDto } from '../api/types';

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

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

// A drag payload identical to what TaskCard writes onDragStart.
function dataTransferFor(taskId: string, sourceColumnId: string) {
  const store: Record<string, string> = {
    'application/agentweaver-task': JSON.stringify({ taskId, sourceColumnId }),
    'text/plain': taskId,
  };
  return {
    getData: (k: string) => store[k] ?? '',
    setData: (k: string, v: string) => { store[k] = v; },
    dropEffect: 'move',
    effectAllowed: 'move',
  };
}

const okTask: BacklogTaskDto = {
  task_id: 't1', project_id: 'proj-1', title: 'x', description: null, state: 'ready', order_key: 'a', captured_by: 'alice', created_at: '2026-01-01T00:00:00Z',
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());
  vi.mocked(apiClient.moveTaskToReady).mockResolvedValue(okTask);
  vi.mocked(apiClient.moveTaskToBacklog).mockResolvedValue(okTask);
  vi.mocked(apiClient.reorderBacklogTask).mockResolvedValue(okTask);
});

afterEach(() => {
  cleanup();
});

describe('KanbanBoard — Backlog<->Ready drag constraint (FR-018/018a)', () => {
  it('dragging Backlog -> Ready calls moveTaskToReady', async () => {
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);
    await waitFor(() => expect(screen.getByTestId('column-ready')).toBeTruthy());

    fireEvent.drop(screen.getByTestId('column-ready'), { dataTransfer: dataTransferFor('t1', 'backlog') });

    await waitFor(() => expect(vi.mocked(apiClient.moveTaskToReady)).toHaveBeenCalledWith('proj-1', 't1', expect.any(Number)));
    expect(vi.mocked(apiClient.moveTaskToBacklog)).not.toHaveBeenCalled();
  });

  it('dragging Ready -> Backlog calls moveTaskToBacklog', async () => {
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);
    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    fireEvent.drop(screen.getByTestId('column-backlog'), { dataTransfer: dataTransferFor('t3', 'ready') });

    await waitFor(() => expect(vi.mocked(apiClient.moveTaskToBacklog)).toHaveBeenCalledWith('proj-1', 't3', expect.any(Number)));
    expect(vi.mocked(apiClient.moveTaskToReady)).not.toHaveBeenCalled();
  });

  it('within-bucket reorder calls reorderBacklogTask with a target index (FR-018a)', async () => {
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);
    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    fireEvent.drop(screen.getByTestId('column-backlog'), { dataTransfer: dataTransferFor('t1', 'backlog') });

    await waitFor(() => expect(vi.mocked(apiClient.reorderBacklogTask)).toHaveBeenCalledWith('proj-1', 't1', expect.any(Number)));
  });

  it('dropping onto a workflow column is rejected: no move API call, MessageBar shown (FR-018)', async () => {
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);
    await waitFor(() => expect(screen.getByTestId('column-active')).toBeTruthy());

    fireEvent.drop(screen.getByTestId('column-active'), { dataTransfer: dataTransferFor('t1', 'backlog') });

    await waitFor(() => expect(screen.getByTestId('reject-message')).toBeTruthy());
    expect(vi.mocked(apiClient.moveTaskToReady)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.moveTaskToBacklog)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.reorderBacklogTask)).not.toHaveBeenCalled();
  });
});
