import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, within, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { makeBoard } from './fixtures/board';
import type { BoardDto } from '../api/types';

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

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

function makeBoardEmptyBacklog(): BoardDto {
  const board = makeBoard();
  return {
    ...board,
    columns: board.columns.map((col) =>
      col.id === 'backlog' ? { ...col, cards: [] } : col,
    ),
  };
}

describe('KanbanColumn — Send all to Ready button', () => {
  it('renders the "Send all to Ready" button on the Backlog column when cards exist', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    const backlog = screen.getByTestId('column-backlog');
    expect(within(backlog).getByText('Send all to Ready')).toBeTruthy();
  });

  it('does NOT render the button on the Backlog column when it is empty', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoardEmptyBacklog());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    const backlog = screen.getByTestId('column-backlog');
    expect(within(backlog).queryByText('Send all to Ready')).toBeNull();
  });

  it('does NOT render the button on the Ready column', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-ready')).toBeTruthy());

    const ready = screen.getByTestId('column-ready');
    expect(within(ready).queryByText('Send all to Ready')).toBeNull();
  });

  it('calls sendAllBacklogToReady with projectId and triggers board refresh on click', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());
    vi.mocked(apiClient.sendAllBacklogToReady).mockResolvedValue({ moved: 2 });

    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    const backlog = screen.getByTestId('column-backlog');
    const btn = within(backlog).getByText('Send all to Ready');

    const callsBefore = vi.mocked(apiClient.getBoard).mock.calls.length;

    fireEvent.click(btn);

    await waitFor(() =>
      expect(vi.mocked(apiClient.sendAllBacklogToReady)).toHaveBeenCalledWith('proj-1'),
    );

    // Board should be refetched after the mutation.
    await waitFor(() =>
      expect(vi.mocked(apiClient.getBoard).mock.calls.length).toBeGreaterThan(callsBefore),
    );
  });
});
