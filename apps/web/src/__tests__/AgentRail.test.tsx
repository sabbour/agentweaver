import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { AgentRail } from '../components/AgentRail';
import { RunCard } from '../components/board/RunCard';
import { deriveAgentQueues, subtaskStatusToBucket } from '../api/agentQueues';
import type { AgentQueueItem } from '../api/agentQueues';
import type { RunCardDto, WorkPlanResponse, WorkPlanSubtaskResponse, CoordinatorChildResponse } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    retryRun: vi.fn(),
  },
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn() };
});

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>
        {children}
      </MemoryRouter>
    </FluentProvider>
  );
}

function makeCard(overrides: Partial<RunCardDto> = {}): RunCardDto {
  return {
    kind: 'run',
    run_id: 'run-abc',
    task: 'Test task',
    status: 'in_progress',
    stage_id: 'coordinator',
    started_at: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function makeAgents(overrides: Partial<AgentQueueItem>[] = []): AgentQueueItem[] {
  return overrides.map((o, i) => ({
    agentName: `Agent${i}`,
    active: 0,
    queued: 0,
    blocked: 0,
    done: 0,
    orchestrations: [],
    ...o,
  }));
}

beforeEach(() => {
  vi.clearAllMocks();
});
afterEach(() => {
  cleanup();
});

// ---------------------------------------------------------------------------
// AgentRail — rendering
// ---------------------------------------------------------------------------
describe('AgentRail', () => {
  it('renders a row per agent with correct load chips', () => {
    const agents = makeAgents([
      { agentName: 'Neo', active: 2, queued: 1, blocked: 0, done: 3 },
      { agentName: 'Trinity', active: 0, queued: 3, blocked: 1, done: 0 },
    ]);
    render(<Wrapper><AgentRail agents={agents} /></Wrapper>);

    expect(screen.getByTestId('agent-rail-row-Neo')).toBeTruthy();
    expect(screen.getByTestId('agent-rail-row-Trinity')).toBeTruthy();

    // Active / queued / done chips for Neo
    expect(screen.getByText('2 active')).toBeTruthy();
    expect(screen.getByText('1 queued')).toBeTruthy();
    expect(screen.getByText('3 done')).toBeTruthy();

    // Queued / blocked for Trinity
    expect(screen.getByText('3 queued')).toBeTruthy();
    expect(screen.getByText('1 blocked')).toBeTruthy();
  });

  it('renders the empty state when agents is empty', () => {
    render(<Wrapper><AgentRail agents={[]} /></Wrapper>);
    expect(screen.getByText('No active agents')).toBeTruthy();
  });

  it('renders a custom title', () => {
    render(<Wrapper><AgentRail agents={[]} title="My Agents" /></Wrapper>);
    expect(screen.getByText('My Agents')).toBeTruthy();
  });

  it('calls onSelectAgent with agent name when a selectable row is clicked', () => {
    const onSelect = vi.fn();
    const agents = makeAgents([{ agentName: 'Morpheus', active: 1 }]);
    render(<Wrapper><AgentRail agents={agents} onSelectAgent={onSelect} /></Wrapper>);
    fireEvent.click(screen.getByTestId('agent-rail-row-Morpheus'));
    expect(onSelect).toHaveBeenCalledWith('Morpheus');
  });

  it('calls onSelectAgent with null when the already-selected row is clicked (toggle off)', () => {
    const onSelect = vi.fn();
    const agents = makeAgents([{ agentName: 'Morpheus', active: 1 }]);
    render(
      <Wrapper>
        <AgentRail agents={agents} selectedAgent="Morpheus" onSelectAgent={onSelect} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByTestId('agent-rail-row-Morpheus'));
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  it('does not call onSelectAgent when no handler is provided (read-only)', () => {
    const agents = makeAgents([{ agentName: 'Morpheus', active: 1 }]);
    // Should not throw even without handler
    render(<Wrapper><AgentRail agents={agents} /></Wrapper>);
    fireEvent.click(screen.getByTestId('agent-rail-row-Morpheus'));
    // No assertion needed — no onSelectAgent, click is a no-op
  });
});

// ---------------------------------------------------------------------------
// subtaskStatusToBucket — unit tests for the status→bucket mapping
// ---------------------------------------------------------------------------
describe('subtaskStatusToBucket', () => {
  it.each([
    ['dispatched', 'active'],
    ['running', 'active'],
    ['in_progress', 'active'],
    ['completed', 'done'],
    ['assemble_ready', 'done'],
    ['merged', 'done'],
    ['failed', 'blocked'],
    ['rai_flagged', 'blocked'],
    ['pending', 'queued'],
    ['unknown_status', 'queued'],
  ] as const)('maps %s → %s', (status, expected) => {
    expect(subtaskStatusToBucket(status)).toBe(expected);
  });
});

// ---------------------------------------------------------------------------
// deriveAgentQueues — derive helper
// ---------------------------------------------------------------------------
function makeWorkPlan(subtasks: WorkPlanResponse['subtasks']): WorkPlanResponse {
  return {
    workPlanId: 1,
    coordinatorRunId: 'run-1',
    outcomeSpecId: 1,
    status: 'in_progress',
    subtasks,
    dependencies: [],
  };
}

function makeSub(overrides: Partial<WorkPlanSubtaskResponse> & Pick<WorkPlanSubtaskResponse, 'subtaskId' | 'title' | 'assignedAgent' | 'status'>): WorkPlanSubtaskResponse {
  return {
    scope: 'feature',
    selectedModelId: 'gpt-4',
    phase: 'coding',
    isolation: 'branch',
    ...overrides,
  };
}

describe('deriveAgentQueues', () => {
  it('groups subtasks by assignedAgent into correct buckets', () => {
    const workPlan = makeWorkPlan([
      makeSub({ subtaskId: 1, assignedAgent: 'Neo', status: 'running', title: 'Task A' }),
      makeSub({ subtaskId: 2, assignedAgent: 'Neo', status: 'pending', title: 'Task B' }),
      makeSub({ subtaskId: 3, assignedAgent: 'Neo', status: 'completed', title: 'Task C' }),
      makeSub({ subtaskId: 4, assignedAgent: 'Trinity', status: 'failed', title: 'Task D' }),
      makeSub({ subtaskId: 5, assignedAgent: 'Trinity', status: 'rai_flagged', title: 'Task E' }),
    ]);

    const result = deriveAgentQueues(workPlan, [], 'run-1');
    const neo = result.find(r => r.agentName === 'Neo');
    const trinity = result.find(r => r.agentName === 'Trinity');

    expect(neo).toBeDefined();
    expect(neo!.active).toBe(1);
    expect(neo!.queued).toBe(1);
    expect(neo!.done).toBe(1);
    expect(neo!.blocked).toBe(0);

    expect(trinity).toBeDefined();
    expect(trinity!.blocked).toBe(2);
    expect(trinity!.active).toBe(0);
  });

  it('overlays children status on top of workplan status', () => {
    const workPlan = makeWorkPlan([
      makeSub({ subtaskId: 1, assignedAgent: 'Neo', status: 'pending', title: 'Task A' }),
    ]);
    const children: CoordinatorChildResponse[] = [
      { subtaskId: 1, subtaskStatus: 'running' } as CoordinatorChildResponse,
    ];

    const result = deriveAgentQueues(workPlan, children, 'run-1');
    const neo = result.find(r => r.agentName === 'Neo')!;
    // Children overlay: pending → running → active
    expect(neo.active).toBe(1);
    expect(neo.queued).toBe(0);
  });

  it('populates runIds and sampleTitles', () => {
    const workPlan = makeWorkPlan([
      makeSub({ subtaskId: 1, assignedAgent: 'Neo', status: 'running', title: 'Task A' }),
      makeSub({ subtaskId: 2, assignedAgent: 'Neo', status: 'running', title: 'Task B' }),
    ]);
    const result = deriveAgentQueues(workPlan, [], 'run-xyz');
    const neo = result.find(r => r.agentName === 'Neo')!;
    expect(neo.runIds).toContain('run-xyz');
    expect(neo.sampleTitles).toContain('Task A');
    expect(neo.sampleTitles).toContain('Task B');
  });

  it('uses "Unassigned" for subtasks with no assignedAgent', () => {
    const workPlan = makeWorkPlan([
      makeSub({ subtaskId: 1, assignedAgent: '' as string, status: 'pending', title: 'X' }),
    ]);
    const result = deriveAgentQueues(workPlan, [], 'run-1');
    expect(result.find(r => r.agentName === 'Unassigned')).toBeDefined();
  });
});

// ---------------------------------------------------------------------------
// RunCard — agent chip
// ---------------------------------------------------------------------------
describe('RunCard agent chip', () => {
  it('renders data-testid="run-card-agent" with avatar and name when agent_name is present', () => {
    const card = makeCard({ agent_name: 'Trinity' });
    render(<Wrapper><RunCard card={card} projectId="proj-1" /></Wrapper>);
    const chip = screen.getByTestId('run-card-agent');
    expect(chip).toBeTruthy();
    expect(chip.textContent).toContain('Trinity');
  });

  it('does not render the agent chip when agent_name is absent', () => {
    const card = makeCard({ agent_name: undefined });
    render(<Wrapper><RunCard card={card} projectId="proj-1" /></Wrapper>);
    expect(screen.queryByTestId('run-card-agent')).toBeNull();
    // Falls back to "Coordinator" text
    expect(screen.getByText('Coordinator')).toBeTruthy();
  });
});
