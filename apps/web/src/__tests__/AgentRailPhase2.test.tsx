import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { fromDto } from '../api/agentQueues';
import { makeBoard, makeAgentQueueDto } from './fixtures/board';
import type { AgentQueueDto } from '../api/types';

// ---- mocks ----
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
  vi.mocked(apiClient.getBacklogSettings).mockResolvedValue({
    max_ready_per_heartbeat: 3,
    pickup_autopilot: false,
    pickup_auto_approve_tools: false,
  });
});
afterEach(() => {
  cleanup();
});

// ---------------------------------------------------------------------------
// fromDto — snake→camel mapping
// ---------------------------------------------------------------------------
describe('fromDto', () => {
  it('maps snake_case AgentQueueDto to camelCase AgentQueueItem', () => {
    const dto: AgentQueueDto = {
      agent_name:    'Trinity',
      active:        3,
      queued:        2,
      blocked:       1,
      done:          5,
      run_ids:       ['run-1', 'run-2'],
      sample_titles: ['Fix auth', 'Add tests'],
      orchestrations: [],
    };
    const item = fromDto(dto);
    expect(item.agentName).toBe('Trinity');
    expect(item.active).toBe(3);
    expect(item.queued).toBe(2);
    expect(item.blocked).toBe(1);
    expect(item.done).toBe(5);
    expect(item.runIds).toEqual(['run-1', 'run-2']);
    expect(item.sampleTitles).toEqual(['Fix auth', 'Add tests']);
    expect(item.orchestrations).toEqual([]);
  });

  it('maps zero-value counts correctly', () => {
    const dto = makeAgentQueueDto({ active: 0, queued: 0, blocked: 0, done: 0 });
    const item = fromDto(dto);
    expect(item.active).toBe(0);
    expect(item.queued).toBe(0);
    expect(item.blocked).toBe(0);
    expect(item.done).toBe(0);
  });
});

// ---------------------------------------------------------------------------
// KanbanBoard — the live Agent rail no longer lives on the Board (board-dedupe).
// Active agent activity is owned by the Flow page; the board focuses on the Kanban.
// ---------------------------------------------------------------------------
describe('KanbanBoard agent rail removal (board-dedupe)', () => {
  it('does not render the AgentRail even when agent_queues is present', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(
      makeBoard({ agent_queues: [makeAgentQueueDto({ agent_name: 'Neo', active: 2, queued: 1, done: 3 })] }),
    );
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    // Board columns still render.
    await waitFor(() => expect(screen.getByTestId('column-backlog')).toBeTruthy());

    // The agent rail and its empty-state copy must be absent on the board.
    expect(screen.queryByTestId('agent-rail')).toBeNull();
    expect(screen.queryByText('No active agents')).toBeNull();
    expect(screen.queryByText('Neo')).toBeNull();
  });
});
