import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { RunStreamEvent } from '../api/sse';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    steerCoordinator: vi.fn(),
    reviewAssembly: vi.fn(),
    answerQuestion: vi.fn(),
    getRun: vi.fn(),
    getProject: vi.fn(),
    getOutcomeSpec: vi.fn(),
    getTeam: vi.fn().mockResolvedValue({ members: [{ name: 'Neo', role_title: 'Researcher' }] }),
    setAutopilot: vi.fn(),
    setAutoApprove: vi.fn(),
    retryRun: vi.fn(),
    getRunTokenBreakdown: vi.fn().mockResolvedValue({
      runId: 'coord-run-1',
      source: 'events',
      hasAgentData: true,
      totalTokens: 1200,
      totalNanoAiu: 15990000000,
      breakdown: [{ agentName: 'Neo', totalTokens: 1200, totalNanoAiu: 15990000000 }],
    }),
    getRunEvents: vi.fn().mockResolvedValue([]),
    getRunFiles: vi.fn().mockResolvedValue([
      { path: 'src/app.ts', status: 'modified', added_lines: 3, removed_lines: 1 },
    ]),
    getRunFileContent: vi.fn().mockResolvedValue({ path: 'src/app.ts', content: 'export {}', is_binary: false, language: 'typescript' }),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileDiff: vi.fn().mockResolvedValue({ path: 'src/app.ts', diff: 'diff --git a/src/app.ts b/src/app.ts' }),
    getAssemblyFiles: vi.fn().mockResolvedValue([]),
    getAssemblyWorkspace: vi.fn().mockResolvedValue([]),
    getAssemblyFileDiff: vi.fn().mockResolvedValue(null),
    listPortForwards: vi.fn().mockResolvedValue([]),
    startPortForward: vi.fn(),
    stopPortForward: vi.fn(),
    pingKeepalive: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, droppedEventCount: 0, status: 'done', error: null, reconnect: vi.fn() }),
}));

vi.mock('../components/OutcomePlanPanel', () => ({
  OutcomePlanPanel: () => null,
}));

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR } from './fixtures/graphDescriptor';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/orchestrations/coord-run-1']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations/:runId" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  currentEvents = [];
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'in_progress', autopilot: false, auto_approve_tools: false } as never);
  vi.mocked(apiClient.getProject).mockResolvedValue({
    project_id: 'p1',
    name: 'Silver Pancake',
    origin: 'blank',
    state: 'active',
    created_at: '2026-07-07T00:00:00.000Z',
    updated_at: '2026-07-07T00:00:00.000Z',
  } as never);
  vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
  vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: true });
  vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });
  vi.mocked(apiClient.retryRun).mockResolvedValue({ run_id: 'retry-run-1', retried_from: 'coord-run-1', status: 'in_progress' });
});

afterEach(() => cleanup());

describe('CoordinatorRunPage operator console redesign', () => {
  it('prioritizes the run tree and selected-task workspace while keeping topology on demand', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });

    const text = document.body.textContent ?? '';
    expect(text).toContain('Run tree');
    expect(text).toContain('Selected task');
    expect(text).toContain('Messages');
    expect(text).toContain('Changes');
    expect(text).toContain('Files');
    expect(screen.getByTestId('compact-primary-run-action')).toBeTruthy();
    expect(screen.queryByTestId('run-actions-toolbar')).toBeNull();
    fireEvent.click(screen.getByTestId('run-chrome-toggle'));
    expect(screen.getByTestId('open-topology-panel')).toBeTruthy();
    expect(screen.queryByTestId('topology-inspector')).toBeNull();
    fireEvent.click(screen.getByTestId('open-topology-panel'));
    const inspector = await screen.findByTestId('topology-inspector', undefined, { timeout: 4000 });
    expect(inspector.textContent).toContain('Select a node to focus its run messages, changes, and files.');
    await waitFor(() => expect(screen.getByRole('link', { name: 'Silver Pancake' })).toBeTruthy(), { timeout: 4000 });
    const topologyScroller = screen.getByTestId('topology-scroll-container');
    expect(topologyScroller.getAttribute('data-pan-enabled')).toBe('true');
    expect(topologyScroller.getAttribute('data-scroll-mode')).toBe('auto');
    expect(screen.getByPlaceholderText('Message coordinator...')).toBeTruthy();
    const expandedText = document.body.textContent ?? '';
    expect(expandedText).toContain('Autopilot');
    expect(expandedText).toContain('Auto-approve');
    expect(expandedText).toContain('Retry failed');
    expect(expandedText).toContain('Stop run');
    const toolbar = screen.getByRole('toolbar', { name: 'Run actions' });
    expect(toolbar.textContent).toContain('Risk');
    expect(toolbar.textContent).not.toContain('Run + children');
    expect(text).not.toContain('Scoped risk mode');
    expect(text).not.toContain('Applies only to this orchestration and child runs.');
    expect(text).not.toContain('Transaction trace');
    expect(text).not.toContain('Agent token breakdown');
  });

  it('traps focus in the topology inspector and restores focus to the trigger on Escape', async () => {
    const user = userEvent.setup();
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    fireEvent.click(await screen.findByTestId('run-chrome-toggle', undefined, { timeout: 4000 }));
    const topologyButton = await screen.findByTestId('open-topology-panel', undefined, { timeout: 4000 });
    await user.click(topologyButton);

    const dialog = await screen.findByRole('dialog', { name: 'Topology' }, { timeout: 4000 });
    await waitFor(() => expect(dialog.textContent).toContain('Coordinator'), { timeout: 4000 });

    const closeButton = within(dialog).getByRole('button', { name: 'Close panel' });
    await waitFor(() => expect(document.activeElement).toBe(closeButton), { timeout: 4000 });

    const planButton = screen.getByTestId('open-plan-panel');
    await user.tab();
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(planButton);

    await user.tab({ shift: true });
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(planButton);

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Topology' })).toBeNull();
      expect(document.activeElement).toBe(topologyButton);
    }, { timeout: 4000 });
  });

  it('keeps run identity in a compact protected header slot and lets actions wrap', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const header = await screen.findByTestId('run-header', undefined, { timeout: 4000 });
    const summary = screen.getByTestId('run-summary');
    const title = screen.getByTestId('run-title');
    const progress = screen.getByTestId('run-progress-chips');

    expect(summary.parentElement).toBe(header);
    expect(screen.queryByTestId('run-actions-row')).toBeNull();
    expect(screen.queryByTestId('run-metadata')).toBeNull();
    expect(screen.queryByTestId('run-status-details')).toBeNull();
    expect(screen.getByTestId('compact-primary-run-action')).toBeTruthy();
    fireEvent.click(screen.getByTestId('run-chrome-toggle'));
    const actionsRow = screen.getByTestId('run-actions-row');
    const toolbar = screen.getByRole('toolbar', { name: 'Run actions' });
    const metadata = screen.getByTestId('run-metadata');
    expect(actionsRow.parentElement).toBe(header);
    expect(Array.from(header.children).indexOf(summary)).toBeLessThan(Array.from(header.children).indexOf(actionsRow));
    expect(title.textContent).toBe('Orchestration');
    expect(metadata.textContent).toContain('Run');
    expect(metadata.textContent).toContain('Status source:');
    expect(progress.textContent).toContain('tasks');
    expect(screen.getByTestId('run-status-details')).toBeTruthy();

    const toolbarStyle = getComputedStyle(toolbar);
    expect(toolbarStyle.flexWrap).toBe('wrap');
    expect(toolbarStyle.width).toBe('100%');
    expect(toolbarStyle.maxWidth).toBe('100%');
    expect(toolbarStyle.borderTopStyle).toBe('none');
    const titleStyle = getComputedStyle(title);
    expect(titleStyle.whiteSpace).toBe('nowrap');
    expect(titleStyle.textOverflow).toBe('ellipsis');

    const cssText = Array.from(document.styleSheets)
      .flatMap((sheet) => {
        try {
          return Array.from(sheet.cssRules).map((rule) => rule.cssText);
        } catch {
          return [];
        }
      })
      .join('\n');
    expect(cssText).toMatch(/grid-template-areas:\s*"identity"\s+"actions"/);
    expect(cssText).toContain('grid-area: identity');
    expect(cssText).toContain('grid-area: actions');
    expect(cssText).toContain('border-top');
    expect(toolbar.textContent).not.toContain('Run + children');
    expect(toolbar.textContent).not.toContain('Retry after failure · Stop while running');
  });

  it('uses the run tree as task-structured navigation and scopes the composer to the selected task', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    fireEvent.click(screen.getByRole('button', { name: /Subtask 1/i }));

    await waitFor(() => expect(document.body.textContent).toContain('Context: Subtask 1'), { timeout: 4000 });
    expect(document.body.textContent).toContain('Neo');
    expect(document.body.textContent).toContain('Researcher');
  });

  it('orders and dedupes stream events by sequence and groups message deltas into one assistant bubble', async () => {
    currentEvents = [
      { sequence: 3, type: 'agent.message.delta', payload: { delta: 'world' } },
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.message.delta', payload: { delta: 'hello ' } },
      { sequence: 3, type: 'agent.message.delta', payload: { delta: 'world' } },
      { sequence: 4, type: 'agent.turn.end', payload: {} },
      { sequence: 5, type: 'tool.call', payload: { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('hello world'), { timeout: 4000 });
    expect((document.body.textContent?.match(/hello world/g) ?? [])).toHaveLength(1);
    // Docked coordinator panels default technical details on, with low-signal activity collapsed until requested.
    await waitFor(() => expect(document.body.textContent).toContain('Activity collapsed'), { timeout: 4000 });
    await userEvent.click(screen.getByTestId('toggle-activity-details'));
    await waitFor(() => expect(document.body.textContent).toContain('Tool calls'), { timeout: 4000 });
    expect((document.body.textContent?.match(/hello world/g) ?? [])).toHaveLength(1);
  });

  it('shows the executing workflow, active task, and why in the run header', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.workflow_selected',
        payload: {
          selectedName: 'Bug fix workflow',
          wasAutoSelected: true,
          rationale: 'The request needs code changes.',
        },
      },
      {
        sequence: 2,
        type: 'coordinator.topology',
        payload: {
          version: 1,
          seq: 1,
          nodes: [
            { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'running' },
            { id: 'subtask-1', kind: 'subtask', label: 'Subtask 1', status: 'running', agent: 'Neo', childRunId: 'child-run-1' },
          ],
          edges: [],
        },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const indicator = await screen.findByTestId('coordinator-execution-indicator', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Workflow: Bug fix workflow'), { timeout: 4000 });
    expect(indicator.textContent).toContain('Task: Subtask 1 (Running)');
    expect(indicator.textContent).toContain('Why: The request needs code changes.');
  });

  it('restores coordinator indicators from persisted event history for parked or terminal runs', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'completed', autopilot: false, auto_approve_tools: false } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'plan:subtask-1', label: 'Restored task', role: 'subtask', kind: 'live', node_type: 'subtask' },
      ],
      edges: [{ from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false }],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 1,
        type: 'coordinator.workflow_selected',
        payload: {
          selectedName: 'Restored workflow',
          wasAutoSelected: true,
          rationale: 'Loaded from persisted event history.',
        },
      },
      {
        sequence: 2,
        type: 'coordinator.topology',
        payload: {
          version: 1,
          seq: 1,
          nodes: [
            { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'completed' },
            { id: 'subtask-1', kind: 'subtask', label: 'Restored task', status: 'running', agent: 'Neo', childRunId: 'child-run-1' },
          ],
          edges: [],
        },
      },
      {
        sequence: 3,
        type: 'subtask.running',
        payload: {
          subtaskId: 1,
          childRunId: 'child-run-1',
          assignedAgent: 'Neo',
          selectedModelId: 'gpt-5',
          status: 'running',
          timestamp_utc: '2026-07-07T20:00:00.000Z',
        },
      },
    ] as never);
    currentEvents = [];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(apiClient.getRunEvents).toHaveBeenCalledWith('coord-run-1'), { timeout: 4000 });
    const indicator = await screen.findByTestId('coordinator-execution-indicator', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Workflow: Restored workflow'), { timeout: 4000 });
    expect(indicator.textContent).toContain('Task: Restored task (Running)');
    expect(indicator.textContent).toContain('Why: Loaded from persisted event history.');
    const restoredRow = await screen.findByRole('button', { name: /Select Restored task: Running/ }, { timeout: 4000 });
    expect(within(restoredRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('running');
  });

  it('restores current task states from persisted work-plan and child-run projections without defaulting rows to queued', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      status: 'in_progress',
      coordinator_status: 'dispatching',
      autopilot: false,
      auto_approve_tools: false,
    } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'plan:subtask-1', label: 'Hydrated running task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:subtask-2', label: 'Hydrated assembly task', role: 'subtask', kind: 'live', node_type: 'subtask' },
      ],
      edges: [
        { from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:subtask-2', cardinality: 'direct', loopback: false },
      ],
    });
    vi.mocked(apiClient.getWorkPlan).mockResolvedValue({
      workPlanId: 7,
      coordinatorRunId: 'coord-run-1',
      outcomeSpecId: 3,
      status: 'dispatching',
      statusReason: null,
      subtasks: [
        {
          subtaskId: 1,
          title: 'Hydrated running task',
          scope: 'Run restored task',
          assignedAgent: 'Neo',
          selectedModelId: 'gpt-5',
          phase: 'build',
          isolation: 'shared',
          status: 'pending',
          childRunId: 'child-run-1',
        },
        {
          subtaskId: 2,
          title: 'Hydrated assembly task',
          scope: 'Assemble restored task',
          assignedAgent: 'Trinity',
          selectedModelId: 'gpt-5',
          phase: 'assemble',
          isolation: 'shared',
          status: 'pending',
          childRunId: 'child-run-2',
        },
      ],
      dependencies: [],
    } as never);
    vi.mocked(apiClient.getCoordinatorChildren).mockResolvedValue([
      {
        subtaskId: 1,
        childRunId: 'child-run-1',
        subtaskStatus: 'running',
        assignedAgent: 'Neo',
        selectedModelId: 'gpt-5',
        childRunStatus: 'in_progress',
        stepCount: 4,
      },
      {
        subtaskId: 2,
        childRunId: 'child-run-2',
        subtaskStatus: 'assemble_ready',
        assignedAgent: 'Trinity',
        selectedModelId: 'gpt-5',
        childRunStatus: 'assemble_ready',
        stepCount: 8,
      },
    ] as never);
    currentEvents = [];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const runningRow = await screen.findByRole('button', { name: /Select Hydrated running task: Running/ }, { timeout: 4000 });
    const assemblyRow = await screen.findByRole('button', { name: /Select Hydrated assembly task: Ready for assembly/ }, { timeout: 4000 });
    expect(within(runningRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('running');
    expect(within(assemblyRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('success');
    expect(runningRow.textContent).not.toMatch(/\bQueued\b/);
    expect(assemblyRow.textContent).not.toMatch(/\bQueued\b/);
    const indicator = screen.getByTestId('coordinator-execution-indicator');
    expect(indicator.textContent).toContain('Task: Hydrated running task (Running)');
  });

  it('treats a terminal failed run as failed instead of actively executing stale restored state', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      status: 'failed',
      coordinator_status: 'failed',
      coordinator_status_reason: 'Child run crashed after dispatch.',
      autopilot: false,
      auto_approve_tools: false,
    } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'plan:subtask-1', label: 'Last restored task', role: 'subtask', kind: 'live', node_type: 'subtask' },
      ],
      edges: [{ from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false }],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 1,
        type: 'coordinator.workflow_selected',
        payload: { selectedName: 'Failure workflow', wasAutoSelected: true },
      },
      {
        sequence: 2,
        type: 'coordinator.topology',
        payload: {
          version: 1,
          seq: 1,
          nodes: [
            { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'failed' },
            { id: 'subtask-1', kind: 'subtask', label: 'Last restored task', status: 'running', agent: 'Neo', childRunId: 'child-run-1' },
          ],
          edges: [],
        },
      },
    ] as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const indicator = await screen.findByTestId('coordinator-execution-indicator', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Workflow: Failure workflow'), { timeout: 4000 });
    expect(indicator.textContent).toContain('Failed');
    expect(indicator.textContent).toContain('Last attempted: Last restored task');
    expect(indicator.textContent).toContain('Failure context: Child run crashed after dispatch.');
    expect(indicator.textContent).not.toMatch(/\bExecuting\b/);
    expect(indicator.querySelector('[data-state-color="danger"]')).toBeTruthy();
  });

  it('color codes run tree states with restrained semantic affordances', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'plan:running-task', label: 'Running task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:done-task', label: 'Done task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:failed-task', label: 'Failed task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:review-task', label: 'Review task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:pending-task', label: 'Pending task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:assembly-ready-task', label: 'Assembly-ready task', role: 'subtask', kind: 'live', node_type: 'subtask' },
        { id: 'plan:assembly-handoff-task', label: 'Assembly handoff task', role: 'subtask', kind: 'live', node_type: 'subtask' },
      ],
      edges: [
        { from: 'coordinator', to: 'plan:running-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:done-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:failed-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:review-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:pending-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:assembly-ready-task', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:assembly-handoff-task', cardinality: 'direct', loopback: false },
      ],
    });
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.topology',
        payload: {
          version: 1,
          seq: 1,
          nodes: [
            { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'running' },
            { id: 'running-task', kind: 'subtask', label: 'Running task', status: 'running' },
            { id: 'done-task', kind: 'subtask', label: 'Done task', status: 'completed' },
            { id: 'failed-task', kind: 'subtask', label: 'Failed task', status: 'failed' },
            { id: 'review-task', kind: 'subtask', label: 'Review task', status: 'awaiting_confirmation' },
            { id: 'pending-task', kind: 'subtask', label: 'Pending task', status: 'pending' },
            { id: 'assembly-ready-task', kind: 'subtask', label: 'Assembly-ready task', status: 'assemble_ready' },
            { id: 'assembly-handoff-task', kind: 'subtask', label: 'Assembly handoff task', status: 'awaiting_assembly' },
          ],
          edges: [],
        },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const statusColorFor = async (label: string) => {
      const row = await screen.findByRole('button', { name: new RegExp(`Select ${label}:`) }, { timeout: 4000 });
      return within(row).getByTestId('run-tree-status-icon').getAttribute('data-state-color');
    };
    const statusIconFor = async (label: string) => {
      const row = await screen.findByRole('button', { name: new RegExp(`Select ${label}:`) }, { timeout: 4000 });
      return within(row).getByTestId('run-tree-status-icon');
    };
    const rowFor = (label: string) => screen.getByRole('button', { name: new RegExp(`Select ${label}:`) });

    expect((await screen.findByTestId('run-status-chip', undefined, { timeout: 4000 })).getAttribute('data-state-color')).toBe('running');
    expect(await statusColorFor('Running task')).toBe('running');
    expect(await statusColorFor('Done task')).toBe('success');
    expect(await statusColorFor('Failed task')).toBe('danger');
    expect(await statusColorFor('Review task')).toBe('input');
    expect(await statusColorFor('Pending task')).toBe('queued');
    expect(await statusColorFor('Assembly-ready task')).toBe('success');
    expect(await statusColorFor('Assembly handoff task')).toBe('running');
    expect(rowFor('Pending task').textContent).toContain('Pending');
    expect(rowFor('Assembly-ready task').textContent).toContain('Ready for assembly');
    expect(rowFor('Assembly handoff task').textContent).toContain('Preparing assembly');
    expect(rowFor('Assembly handoff task').textContent).not.toContain('Awaiting assembly');
    for (const label of ['Running task', 'Done task', 'Failed task', 'Review task', 'Pending task', 'Assembly-ready task', 'Assembly handoff task']) {
      const style = getComputedStyle(await statusIconFor(label));
      expect(style.backgroundColor === 'transparent' || style.backgroundColor === 'rgba(0, 0, 0, 0)').toBe(true);
      expect(style.borderTopStyle).toBe('none');
    }
    for (const label of ['Done task', 'Failed task', 'Review task', 'Pending task', 'Assembly-ready task', 'Assembly handoff task']) {
      expect(rowFor(label).textContent).not.toMatch(/\bQueued\b/);
    }
  });

  it('keeps Changes and Files as the only artifact tabs beside Messages', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const changesTab = await screen.findByTestId('session-tab-changes', undefined, { timeout: 4000 });
    expect(screen.getByTestId('session-tab-messages')).toBeTruthy();
    expect(changesTab).toBeTruthy();
    expect(screen.getByTestId('session-tab-files')).toBeTruthy();
    expect(document.body.textContent).not.toContain('Tools');
  });

  it('surfaces selected-task message file references in Files while Changes stays diff-only', async () => {
    vi.mocked(apiClient.getRunFiles).mockResolvedValue([]);
    vi.mocked(apiClient.getRunWorkspace).mockResolvedValue([]);
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'Implementing hotel booking page React components.' } },
      {
        sequence: 3,
        type: 'tool.call',
        payload: {
          callId: 'tool-1',
          toolName: 'write_file',
          arguments: { path: 'HotelSearchForm.jsx' },
        },
      },
      { sequence: 4, type: 'tool.result', payload: { callId: 'tool-1' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });
    fireEvent.click(screen.getByRole('button', { name: /Select Subtask 1/i }));

    await waitFor(
      () => expect(screen.getByTestId('session-tab-files').textContent).toContain('Files (1)'),
      { timeout: 4000 },
    );
    expect(screen.getByTestId('session-tab-changes').textContent).toContain('Changes (0)');

    fireEvent.click(screen.getByTestId('session-tab-files'));
    await waitFor(() => expect(screen.getByText('HotelSearchForm.jsx')).toBeTruthy(), { timeout: 4000 });
  });

  it('routes the single composer through coordinator steering and targets a selected child run', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    fireEvent.click(screen.getByRole('button', { name: /Subtask 1/i }));
    const input = screen.getByPlaceholderText('Message coordinator...');
    fireEvent.change(input, { target: { value: 'Use the cached source' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send message' }));

    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalled(), { timeout: 4000 });
    expect(vi.mocked(apiClient.steerCoordinator).mock.calls[0][1]).toMatchObject({
      kind: 'send',
      instruction: 'Use the cached source',
      target_child_run_id: 'child-run-1',
    });
    expect(await screen.findByText('Message sent to coordinator.')).toBeTruthy();
  });

  it('surfaces automation toggle failures instead of silently rolling back', async () => {
    vi.mocked(apiClient.setAutopilot).mockRejectedValue(new ApiError(409, '{"message":"run is not active"}'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    fireEvent.click(await screen.findByTestId('run-chrome-toggle', undefined, { timeout: 4000 }));
    const autopilot = await screen.findByRole('switch', { name: /Autopilot/i }, { timeout: 4000 });
    fireEvent.click(autopilot);

    await waitFor(
      () => expect(document.body.textContent).toContain('Autopilot update failed'),
      { timeout: 4000 },
    );
    expect(document.body.textContent).toContain('run is not active');
  });

  it('surfaces pending capacity, blocked, and needs-resolution states explicitly', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.topology',
        payload: {
          version: 1,
          seq: 1,
          nodes: [
            { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'needs_resolution' },
            { id: 'subtask-1', kind: 'subtask', label: 'Subtask 1', status: 'pending_capacity', agent: 'Neo' },
            { id: 'subtask-2', kind: 'subtask', label: 'Subtask 2', status: 'blocked' },
          ],
          edges: [],
        },
      },
      {
        sequence: 2,
        type: 'merge.conflicted',
        payload: { reason: 'integration_conflict', conflictingFiles: ['src/app.ts'] },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Waiting for capacity'), { timeout: 4000 });
    expect(document.body.textContent).toContain('Blocked');
    expect(document.body.textContent).toContain('Needs resolution');
  });
});
