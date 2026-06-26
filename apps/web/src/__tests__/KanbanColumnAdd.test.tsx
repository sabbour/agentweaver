import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, within, cleanup, fireEvent } from '@testing-library/react';
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
    archiveBacklogTask: vi.fn(),
    moveTaskToReady: vi.fn(),
    moveTaskToBacklog: vi.fn(),
    reorderBacklogTask: vi.fn(),
    sendAllBacklogToReady: vi.fn(),
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

function makeTask(taskId: string): BacklogTaskDto {
  return {
    task_id: taskId,
    project_id: 'proj-1',
    title: 'A new task',
    description: null,
    state: 'backlog',
  } as BacklogTaskDto;
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('KanbanColumn — per-column Add (+) button', () => {
  it('opens an input popover and captures a Backlog task, then refetches the board', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());
    vi.mocked(apiClient.captureBacklogTask).mockResolvedValue(makeTask('new-1'));

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    const backlog = screen.getByTestId('column-backlog');
    fireEvent.click(within(backlog).getByLabelText('Add to Backlog'));

    const surface = await screen.findByLabelText('Add task to Backlog');
    const input = within(surface).getByLabelText('New task title for Backlog');
    fireEvent.change(input, { target: { value: 'A new task' } });

    const callsBefore = vi.mocked(apiClient.getBoard).mock.calls.length;
    fireEvent.click(within(surface).getByRole('button', { name: 'Add' }));

    await waitFor(() =>
      expect(vi.mocked(apiClient.captureBacklogTask)).toHaveBeenCalledWith('proj-1', { title: 'A new task' }),
    );
    expect(vi.mocked(apiClient.moveTaskToReady)).not.toHaveBeenCalled();
    await waitFor(() =>
      expect(vi.mocked(apiClient.getBoard).mock.calls.length).toBeGreaterThan(callsBefore),
    );
  });

  it('captures into Backlog then promotes to Ready when the Ready + is used', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());
    vi.mocked(apiClient.captureBacklogTask).mockResolvedValue(makeTask('new-2'));
    vi.mocked(apiClient.moveTaskToReady).mockResolvedValue(makeTask('new-2'));

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-ready')).toBeTruthy());

    const ready = screen.getByTestId('column-ready');
    fireEvent.click(within(ready).getByLabelText('Add to Ready'));

    const surface = await screen.findByLabelText('Add task to Ready');
    const input = within(surface).getByLabelText('New task title for Ready');
    fireEvent.change(input, { target: { value: 'Ready task' } });
    fireEvent.click(within(surface).getByRole('button', { name: 'Add' }));

    await waitFor(() =>
      expect(vi.mocked(apiClient.captureBacklogTask)).toHaveBeenCalledWith('proj-1', { title: 'Ready task' }),
    );
    await waitFor(() =>
      expect(vi.mocked(apiClient.moveTaskToReady)).toHaveBeenCalledWith('proj-1', 'new-2'),
    );
  });

  it('does not call the API when the title is empty', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    const backlog = screen.getByTestId('column-backlog');
    fireEvent.click(within(backlog).getByLabelText('Add to Backlog'));

    const surface = await screen.findByLabelText('Add task to Backlog');

    // Add button is disabled for an empty title; clicking does nothing.
    const addBtn = within(surface).getByRole('button', { name: 'Add' });
    expect(addBtn).toHaveProperty('disabled', true);
    fireEvent.click(addBtn);

    expect(vi.mocked(apiClient.captureBacklogTask)).not.toHaveBeenCalled();
  });
});
