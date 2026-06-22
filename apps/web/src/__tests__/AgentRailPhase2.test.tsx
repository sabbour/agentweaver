import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent, cleanup } from '@testing-library/react';
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
    };
    const item = fromDto(dto);
    expect(item.agentName).toBe('Trinity');
    expect(item.active).toBe(3);
    expect(item.queued).toBe(2);
    expect(item.blocked).toBe(1);
    expect(item.done).toBe(5);
    expect(item.runIds).toEqual(['run-1', 'run-2']);
    expect(item.sampleTitles).toEqual(['Fix auth', 'Add tests']);
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
// KanbanBoard — AgentRail rendered from board.agent_queues
// ---------------------------------------------------------------------------
describe('KanbanBoard AgentRail (Phase 2)', () => {
  it('renders the AgentRail when board.agent_queues is non-empty', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(
      makeBoard({ agent_queues: [makeAgentQueueDto({ agent_name: 'Neo', active: 2, queued: 1, done: 3 })] }),
    );
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail')).toBeTruthy());
    expect(screen.getByText('Neo')).toBeTruthy();
    expect(screen.getByText('2 active')).toBeTruthy();
  });

  it('renders the AgentRail with empty state when agent_queues is absent', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard({ agent_queues: undefined }));
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail')).toBeTruthy());
    expect(screen.getByText('No active agents')).toBeTruthy();
    // Board columns still render beneath the rail
    expect(screen.getByTestId('column-backlog')).toBeTruthy();
  });

  it('renders the AgentRail with empty state when agent_queues is an empty array', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard({ agent_queues: [] }));
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail')).toBeTruthy());
    expect(screen.getByText('No active agents')).toBeTruthy();
    // Board columns still render beneath the rail
    expect(screen.getByTestId('column-backlog')).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Click-to-filter
// ---------------------------------------------------------------------------
describe('KanbanBoard agent filter', () => {
  function makeBoardWithTwoAgents() {
    return makeBoard({
      agent_queues: [
        makeAgentQueueDto({ agent_name: 'Neo',     active: 1, run_ids: ['r1'] }),
        makeAgentQueueDto({ agent_name: 'Trinity', active: 1, run_ids: ['r2'] }),
      ],
      columns: [
        {
          id: 'coordinator',
          kind: 'workflow',
          label: 'Coordinator',
          cards: [
            { kind: 'run', run_id: 'r1', task: 'Neo task', status: 'in_progress', stage_id: 'coordinator', started_at: '2026-01-01T00:00:00Z' },
            { kind: 'run', run_id: 'r2', task: 'Trinity task', status: 'in_progress', stage_id: 'coordinator', started_at: '2026-01-01T00:01:00Z' },
          ],
        },
      ],
    });
  }

  it('selecting an agent filters cards to only that agent\'s run_ids', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoardWithTwoAgents());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail-row-Neo')).toBeTruthy());

    // Before filter: both cards visible
    expect(screen.getByText('Neo task')).toBeTruthy();
    expect(screen.getByText('Trinity task')).toBeTruthy();

    // Select Neo
    fireEvent.click(screen.getByTestId('agent-rail-row-Neo'));

    await waitFor(() => {
      expect(screen.getByText('Neo task')).toBeTruthy();
      expect(screen.queryByText('Trinity task')).toBeNull();
    });

    // Filter-active sentinel is present
    expect(screen.getByTestId('agent-rail-filter-active')).toBeTruthy();
  });

  it('re-selecting the active agent clears the filter', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoardWithTwoAgents());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail-row-Neo')).toBeTruthy());

    // Select then deselect Neo
    fireEvent.click(screen.getByTestId('agent-rail-row-Neo'));
    await waitFor(() => expect(screen.queryByText('Trinity task')).toBeNull());

    fireEvent.click(screen.getByTestId('agent-rail-row-Neo'));
    await waitFor(() => {
      expect(screen.getByText('Neo task')).toBeTruthy();
      expect(screen.getByText('Trinity task')).toBeTruthy();
    });

    // Filter sentinel gone
    expect(screen.queryByTestId('agent-rail-filter-active')).toBeNull();
  });

  it('switching selection from one agent to another updates the filter', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoardWithTwoAgents());
    render(<Wrapper><KanbanBoard projectId="proj-1" pollIntervalMs={100000} /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('agent-rail-row-Neo')).toBeTruthy());

    fireEvent.click(screen.getByTestId('agent-rail-row-Neo'));
    await waitFor(() => expect(screen.queryByText('Trinity task')).toBeNull());

    fireEvent.click(screen.getByTestId('agent-rail-row-Trinity'));
    await waitFor(() => {
      expect(screen.getByText('Trinity task')).toBeTruthy();
      expect(screen.queryByText('Neo task')).toBeNull();
    });
  });
});
