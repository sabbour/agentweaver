import userEvent from '@testing-library/user-event';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR } from './fixtures/graphDescriptor';
import {
  act,
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
import type { RunStreamEvent } from '../api/sse';
import type { ReactNode } from 'react';
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

import type { GraphDescriptor } from '../api/types';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/projects/p1/orchestrations/coord-run-1']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations/:runId" element={children} />
        </Routes>
      </MemoryRouter>
    </AzureFluentProvider>
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
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
  vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
  vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: true });
  vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });
  vi.mocked(apiClient.retryRun).mockResolvedValue({ run_id: 'retry-run-1', retried_from: 'coord-run-1', status: 'in_progress' });
});

afterEach(() => cleanup());

describe('CoordinatorRunPage operator console redesign', () => {
  const graphWithBuildTest: GraphDescriptor = {
    ...COORDINATOR_GRAPH_DESCRIPTOR,
    nodes: [
      ...COORDINATOR_GRAPH_DESCRIPTOR.nodes.slice(0, 3),
      { id: 'planned:assembly-build-test', label: 'Build & Test', role: 'build_test', kind: 'planned', node_type: 'gate' },
      ...COORDINATOR_GRAPH_DESCRIPTOR.nodes.slice(3),
    ],
    edges: [
      { from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false },
      { from: 'coordinator', to: 'plan:subtask-2', cardinality: 'direct', loopback: false },
      { from: 'plan:subtask-1', to: 'planned:assembly-build-test', cardinality: 'fanin', loopback: false },
      { from: 'plan:subtask-2', to: 'planned:assembly-build-test', cardinality: 'fanin', loopback: false },
      { from: 'planned:assembly-build-test', to: 'planned:assembly-rai', cardinality: 'direct', loopback: false },
      { from: 'planned:assembly-rai', to: 'planned:assembly-review', cardinality: 'direct', loopback: false },
      { from: 'planned:assembly-review', to: 'planned:assembly-merge', cardinality: 'direct', loopback: false },
      { from: 'planned:assembly-merge', to: 'planned:assembly-scribe', cardinality: 'direct', loopback: false },
    ],
  };

  it('surfaces a durable ready preview on Build & Test and human review', async () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(graphWithBuildTest);
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'awaiting_review', coordinator_status: 'in_review' } as never);
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 10, type: 'sandbox.preview_ready', payload: { preview_url: 'https://preview.example.test', target_port: 5173 } },
      { sequence: 11, type: 'coordinator.assembly_review_requested', payload: { gateKind: 'human-review' } },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const topLevelPreview = await screen.findByTestId('compact-preview-run-action', undefined, { timeout: 4000 });
    expect(topLevelPreview.textContent).toContain('Open preview');
    fireEvent.click(topLevelPreview);
    expect(openSpy).toHaveBeenCalledWith('https://preview.example.test', '_blank', 'noopener,noreferrer');

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test:/i }, { timeout: 4000 });
    fireEvent.click(buildRow);
    const buildPreview = await screen.findByTestId('selected-build-preview-cta', undefined, { timeout: 4000 });
    expect(buildPreview.textContent).toContain('Preview from Build & Test is active');
    fireEvent.click(within(buildPreview).getByRole('button', { name: 'Open preview' }));
    expect(openSpy).toHaveBeenCalledWith('https://preview.example.test', '_blank', 'noopener,noreferrer');

    fireEvent.click(await screen.findByTestId('compact-primary-run-action', undefined, { timeout: 4000 }));
    const reviewPreview = await screen.findByTestId('human-review-preview-status', undefined, { timeout: 4000 });
    expect(reviewPreview.textContent).toContain('Preview from Build & Test is active');

    openSpy.mockRestore();
  });

  it('shows pending approval for the latest preview event', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(graphWithBuildTest);
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 20, type: 'sandbox.preview_pending', payload: { target_port: 5173 } },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test:/i }, { timeout: 4000 });
    fireEvent.click(buildRow);
    const buildPreview = await screen.findByTestId('selected-build-preview-cta', undefined, { timeout: 4000 });
    expect(buildPreview.textContent).toContain('Preview pending approval');
    expect(buildPreview.textContent).not.toContain('Open preview');
  });

  it('shows a non-blocking unavailable indication for failed previews', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(graphWithBuildTest);
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'awaiting_review', coordinator_status: 'in_review' } as never);
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 30, type: 'sandbox.preview_failed', payload: { reason: 'preview_not_requested', message: 'No preview was requested.' } },
      { sequence: 31, type: 'coordinator.assembly_review_requested', payload: { gateKind: 'human-review' } },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test:/i }, { timeout: 4000 });
    fireEvent.click(buildRow);
    const buildPreview = await screen.findByTestId('selected-build-preview-cta', undefined, { timeout: 4000 });
    expect(buildPreview.textContent).toContain('Preview unavailable');
    expect(buildPreview.textContent).toContain('preview not requested');
    expect(buildPreview.textContent).toContain('Human review can still proceed');

    fireEvent.click(await screen.findByTestId('compact-primary-run-action', undefined, { timeout: 4000 }));
    const reviewPreview = await screen.findByTestId('human-review-preview-status', undefined, { timeout: 4000 });
    expect(reviewPreview.textContent).toContain('Preview unavailable');
  });

  it('prioritizes the run tree and selected-task workspace while keeping topology on demand', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });

    const text = document.body.textContent ?? '';
    expect(text).toContain('Run tree');
    // The center is a single Messages thread — no Activity | Changes segmented control.
    expect(screen.queryByTestId('session-tab-activity')).toBeNull();
    expect(screen.queryByTestId('session-tab-changes')).toBeNull();
    expect(screen.queryByTestId('session-tab-files')).toBeNull();
    // No legacy center tabs or "Run actions" toolbar remain.
    expect(screen.queryByTestId('run-actions-toolbar')).toBeNull();
    expect(screen.queryByRole('toolbar', { name: 'Run actions' })).toBeNull();
    expect(screen.queryByTestId('center-tab-messages')).toBeNull();

    // Topology opens on demand from the left-rail minimap.
    expect(screen.getByTestId('open-topology-minimap')).toBeTruthy();
    expect(screen.queryByTestId('topology-inspector')).toBeNull();
    fireEvent.click(screen.getByTestId('open-topology-minimap'));
    const inspector = await screen.findByTestId('topology-inspector', undefined, { timeout: 4000 });
    expect(inspector.textContent).toContain('Select a node to focus its run messages, changes, and files.');
    await waitFor(() => expect(screen.getByRole('link', { name: 'Silver Pancake' })).toBeTruthy(), { timeout: 4000 });
    const topologyScroller = screen.getByTestId('topology-scroll-container');
    expect(topologyScroller.getAttribute('data-pan-enabled')).toBe('true');

    // The composer for steering the coordinator lives inline in the Messages surface and hosts the toggles.
    expect(screen.getByPlaceholderText('Message coordinator...')).toBeTruthy();
    const toggles = screen.getByTestId('composer-automation-toggles');
    expect(toggles.textContent).toContain('Autopilot');
    expect(toggles.textContent).toContain('Auto-approve');

    expect(text).not.toContain('Scoped risk mode');
    expect(text).not.toContain('Applies only to this orchestration and child runs.');
    expect(text).not.toContain('Transaction trace');
    expect(text).not.toContain('Agent token breakdown');
  });

  it('traps focus in the topology inspector and restores focus to the trigger on Escape', async () => {
    const user = userEvent.setup();
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const topologyButton = await screen.findByTestId('open-topology-minimap', undefined, { timeout: 4000 });
    await user.click(topologyButton);

    const dialog = await screen.findByRole('dialog', { name: 'Topology' }, { timeout: 4000 });
    await waitFor(() => expect(dialog.textContent).toContain('Coordinator'), { timeout: 4000 });

    const closeButton = within(dialog).getByRole('button', { name: 'Close panel' });
    await waitFor(() => expect(document.activeElement).toBe(closeButton), { timeout: 4000 });

    const outsideButton = screen.getByTestId('coordinator-retry-button');
    await user.tab();
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(outsideButton);

    await user.tab({ shift: true });
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(outsideButton);

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Topology' })).toBeNull();
      expect(document.activeElement).toBe(topologyButton);
    }, { timeout: 4000 });
  });

  it('keeps run identity in a compact protected header slot with details on demand', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const header = await screen.findByTestId('run-header', undefined, { timeout: 4000 });
    const summary = screen.getByTestId('run-summary');
    const title = screen.getByTestId('run-title');
    const progress = screen.getByTestId('run-progress-chips');

    expect(summary.parentElement).toBe(header);
    expect(screen.queryByTestId('run-actions-row')).toBeNull();
    expect(title.textContent).toBe('Orchestration');
    expect(progress.textContent).toContain('tasks');

    // The Details disclosure was removed: run id is in the breadcrumb, failure reason is in the rail.
    expect(screen.queryByTestId('run-chrome-toggle')).toBeNull();
    expect(screen.queryByTestId('run-metadata')).toBeNull();
    expect(screen.queryByTestId('run-status-details')).toBeNull();

    const titleStyle = getComputedStyle(title);
    expect(titleStyle.whiteSpace).toBe('nowrap');
    expect(titleStyle.textOverflow).toBe('ellipsis');

    // No legacy run-actions toolbar copy leaks into the compact header.
    expect(header.textContent).not.toContain('Run + children');
    expect(header.textContent).not.toContain('Retry after failure · Stop while running');
  });

  it('uses the run tree as task-structured navigation and scopes the composer to the selected task', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    fireEvent.click(screen.getByRole('treeitem', { name: /Subtask 1/i }));

    await waitFor(() => expect(document.body.textContent).toContain('Context: Subtask 1'), { timeout: 4000 });
    expect(document.body.textContent).toContain('Neo');
    expect(document.body.textContent).toContain('Researcher');
  });

  it('collapses and expands the left run-tree rail via the toggle control', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });

    const collapseBtn = screen.getByTestId('toggle-run-tree');
    expect(collapseBtn.getAttribute('aria-label')).toMatch(/collapse run tree/i);

    fireEvent.click(collapseBtn);
    await waitFor(() => {
      expect(screen.getByTestId('toggle-run-tree').getAttribute('aria-label')).toMatch(/expand run tree/i);
    });
    expect(screen.queryByRole('treeitem', { name: /Subtask 1/i })).toBeNull();

    fireEvent.click(screen.getByTestId('toggle-run-tree'));
    await waitFor(() => {
      expect(screen.getByTestId('toggle-run-tree').getAttribute('aria-label')).toMatch(/collapse run tree/i);
    });
    expect(screen.getByRole('treeitem', { name: /Subtask 1/i })).toBeTruthy();
  });

  it('orders out-of-order message deltas by sequence into one assistant message under its intent step', async () => {
    currentEvents = [
      { sequence: 4, type: 'agent.message.delta', payload: { messageId: 'm1', delta: 'world' } },
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.intent', payload: { intent: 'Greet the user' } },
      { sequence: 3, type: 'agent.message.delta', payload: { messageId: 'm1', delta: 'hello ' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const timeline = await screen.findByTestId('run-timeline');
    // Steps are expanded by default, so the assembled message is visible without expanding.

    // Deltas arriving out of order assemble in sequence order into a single message
    // ("hello " @seq3 then "world" @seq4), not two fragments and not duplicated.
    await waitFor(
      () => expect(within(timeline).getByTestId('timeline-message').textContent).toContain('hello world'),
      { timeout: 4000 },
    );
    expect((timeline.textContent?.match(/hello world/g) ?? [])).toHaveLength(1);
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

    // Workflow + status reason now live in the left rail (near the minimap), not the header.
    const indicator = await screen.findByTestId('rail-status-block', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Bug fix workflow'), { timeout: 4000 });
    expect(indicator.textContent).not.toContain('Task:');
    expect(indicator.textContent).toContain('The request needs code changes.');
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
    const indicator = await screen.findByTestId('rail-status-block', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Restored workflow'), { timeout: 4000 });
    expect(indicator.textContent).not.toContain('Task:');
    expect(indicator.textContent).toContain('Loaded from persisted event history.');
    const restoredRow = await screen.findByRole('treeitem', { name: /Select Restored task: Running/ }, { timeout: 4000 });
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

    const runningRow = await screen.findByRole('treeitem', { name: /Select Hydrated running task: Running/ }, { timeout: 4000 });
    const assemblyRow = await screen.findByRole('treeitem', { name: /Select Hydrated assembly task: Ready for assembly/ }, { timeout: 4000 });
    expect(within(runningRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('running');
    expect(within(assemblyRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('success');
    expect(runningRow.textContent).not.toMatch(/\bQueued\b/);
    expect(assemblyRow.textContent).not.toMatch(/\bQueued\b/);
    const coordinatorRow = await screen.findByRole('treeitem', { name: /Select Coordinator/i }, { timeout: 4000 });
    expect(coordinatorRow.textContent).toContain('1 running, 1 ready');
    const indicator = screen.getByTestId('rail-status-block');
    expect(indicator.textContent).not.toContain('Task:');
    expect(indicator.textContent).not.toMatch(/\bQueued\b/);
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

    const indicator = await screen.findByTestId('rail-status-block', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Failure workflow'), { timeout: 4000 });
    expect(indicator.textContent).toContain('Failed');
    expect(indicator.textContent).not.toContain('Last attempted:');
    // The short failure summary stays inline beside the workflow status; the ⓘ tooltip beside the
    // workflow NAME explains why THIS workflow was selected (not the failure).
    expect(indicator.textContent).toContain('Child run crashed after dispatch.');
    const reasonInfo = within(indicator).getByTestId('rail-status-reason-info');
    expect(reasonInfo.getAttribute('aria-label')).toBe('Why this workflow was selected');
    fireEvent.focus(reasonInfo);
    const tip = await screen.findByRole('tooltip', undefined, { timeout: 4000 });
    // Auto-selected with no explicit rationale ⇒ the "why selected" fallback, never the failure text.
    expect(tip.textContent).toContain('Automatically selected by the coordinator');
    expect(tip.textContent).not.toContain('Failure context');
    expect(indicator.textContent).not.toMatch(/\bExecuting\b/);
    expect(indicator.querySelector('[data-state-color="danger"]')).toBeTruthy();
  });

  it('shows the workflow-selection rationale (not the failure) in the workflow-name info tooltip', async () => {
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
        payload: {
          selectedName: 'Content Authoring',
          wasAutoSelected: true,
          rationale: 'The task asks to review error handling and propose improvements; the content-authoring workflow fits this review-and-propose process.',
        },
      },
    ] as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const indicator = await screen.findByTestId('rail-status-block', undefined, { timeout: 4000 });
    await waitFor(() => expect(indicator.textContent).toContain('Content Authoring'), { timeout: 4000 });
    const reasonInfo = within(indicator).getByTestId('rail-status-reason-info');
    fireEvent.focus(reasonInfo);
    const tip = await screen.findByRole('tooltip', undefined, { timeout: 4000 });
    expect(tip.textContent).toContain('review-and-propose process');
    expect(tip.textContent).not.toContain('Child run crashed after dispatch.');
    expect(tip.textContent).not.toContain('Failure context');
    // The failure reason still shows on the inline status line beneath the name.
    expect(indicator.textContent).toContain('Child run crashed after dispatch.');
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
      const row = await screen.findByRole('treeitem', { name: new RegExp(`Select ${label}:`) }, { timeout: 4000 });
      return within(row).getByTestId('run-tree-status-icon').getAttribute('data-state-color');
    };
    const statusIconFor = async (label: string) => {
      const row = await screen.findByRole('treeitem', { name: new RegExp(`Select ${label}:`) }, { timeout: 4000 });
      return within(row).getByTestId('run-tree-status-icon');
    };
    const rowFor = (label: string) => screen.getByRole('treeitem', { name: new RegExp(`Select ${label}:`) });

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

  it('declutters the run header and moves the AI-credits indicator beside the composer send button', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const header = await screen.findByTestId('run-header', undefined, { timeout: 4000 });
    // The topology button no longer lives in the header (it's in the left rail now).
    expect(within(header).queryByTestId('open-topology-panel')).toBeNull();
    // The AI-credits chip is gone from the header progress cluster.
    expect(header.textContent).not.toContain('AI credits');
    // The verbose 2nd execution line is gone from the header (moved to the rail).
    expect(within(header).queryByTestId('coordinator-execution-indicator')).toBeNull();

    // The credits affordance now sits by the composer send button, with a Session + USD popover.
    const credits = await screen.findByTestId('composer-credits', undefined, { timeout: 4000 });
    fireEvent.click(credits);
    await waitFor(() => {
      const body = document.body.textContent ?? '';
      expect(body).toContain('Session');
      expect(body).toContain('1 AIC = $0.01');
      expect(body).not.toContain('No usage limit');
    }, { timeout: 4000 });
  });

  it('renders a single Activity thread for the selected scope with no segmented control', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });
    // The center is a single thread — the Activity | Changes segmented control is gone.
    expect(screen.queryByTestId('session-tab-activity')).toBeNull();
    expect(screen.queryByTestId('session-tab-changes')).toBeNull();
    expect(screen.queryByTestId('session-tab-files')).toBeNull();
    expect(screen.queryByTestId('center-tab-messages')).toBeNull();
    expect(screen.queryByTestId('center-tab-plan')).toBeNull();
    expect(screen.queryByTestId('center-tab-artifacts')).toBeNull();
  });

  it('pins three run-wide chips (Goal, Changes, Files) above the composer and opens their overlays', async () => {
    // Real run-wide collective diff drives the Changes + Files chips' counts.
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([
      { path: 'src/app.ts', status: 'modified', added_lines: 40, removed_lines: 0, scope: 'merged' },
      { path: 'README.md', status: 'added', added_lines: 3, removed_lines: 1, scope: 'merged' },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });

    // The run-wide chip row is pinned above the composer for the coordinator scope.
    const chipRow = await screen.findByTestId('run-summary-chips', undefined, { timeout: 4000 });
    const goalChip = within(chipRow).getByTestId('run-summary-chip-goal');
    const changesChip = within(chipRow).getByTestId('run-summary-chip-changes');
    const filesChip = within(chipRow).getByTestId('run-summary-chip-files');

    // Goal has no numeric count.
    expect(goalChip.textContent).toContain('Goal');

    // Changes shows the file count + aggregate +added / −removed diff.
    expect(changesChip.textContent).toContain('Changes');
    expect(changesChip.textContent).toContain('2 files');
    expect(changesChip.textContent).toContain('+43');

    // Files shows just the produced-file count.
    expect(filesChip.textContent).toContain('Files');
    expect(filesChip.textContent).toContain('2');

    // The Subagents chip + overlay were dropped (the run tree already lists subagents task-first).
    expect(within(chipRow).queryByTestId('run-summary-chip-subagents')).toBeNull();
    expect(screen.queryByTestId('subagents-list')).toBeNull();

    // The legacy single Plan chip / header buttons are gone.
    expect(within(chipRow).queryByTestId('run-summary-chip-plan')).toBeNull();
    expect(within(chipRow).queryByTestId('run-summary-chip-task-plan')).toBeNull();
    expect(within(chipRow).queryByTestId('run-summary-chip-artifacts')).toBeNull();
    expect(screen.queryByTestId('open-plan-panel')).toBeNull();
    expect(screen.queryByTestId('open-artifacts-panel')).toBeNull();

    // Goal chip opens the Outcome plan overlay.
    fireEvent.click(within(screen.getByTestId('run-summary-chips')).getByTestId('run-summary-chip-goal'));
    await screen.findByRole('dialog', { name: 'Outcome plan' }, { timeout: 4000 });
    fireEvent.keyDown(document, { key: 'Escape' });

    // Changes chip opens the collective-diff overlay.
    fireEvent.click(within(screen.getByTestId('run-summary-chips')).getByTestId('run-summary-chip-changes'));
    await screen.findByRole('dialog', { name: 'Changes' }, { timeout: 4000 });
    fireEvent.keyDown(document, { key: 'Escape' });

    // Files chip opens the produced-files browser overlay (distinct destination from Changes).
    fireEvent.click(within(screen.getByTestId('run-summary-chips')).getByTestId('run-summary-chip-files'));
    await screen.findByRole('dialog', { name: 'Files' }, { timeout: 4000 });
  });

  it('shows disabled "None" Changes + Files chips when the run produced no assembly diff', async () => {
    // Failed-before-assembly run: getAssemblyFiles returns [] so there are no changes/files.
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const chipRow = await screen.findByTestId('run-summary-chips', undefined, { timeout: 4000 });

    // Goal is always present for the coordinator scope; Subagents chip was dropped.
    expect(within(chipRow).getByTestId('run-summary-chip-goal')).toBeTruthy();
    expect(within(chipRow).queryByTestId('run-summary-chip-subagents')).toBeNull();

    // Changes + Files are rendered but disabled ("· None") and are NOT interactive buttons.
    const changesChip = within(chipRow).getByTestId('run-summary-chip-changes');
    const filesChip = within(chipRow).getByTestId('run-summary-chip-files');
    expect(changesChip.textContent).toContain('None');
    expect(filesChip.textContent).toContain('None');
    expect(changesChip.getAttribute('aria-disabled')).toBe('true');
    expect(filesChip.getAttribute('aria-disabled')).toBe('true');
    expect(changesChip.tagName).toBe('SPAN');
    expect(filesChip.tagName).toBe('SPAN');
  });

  it('uses the Goal chip instead of a duplicate header button for outcome-plan review', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.outcome_spec',
        payload: { desiredOutcome: 'Polish the coordinator run detail UI.' },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const chipRow = await screen.findByTestId('run-summary-chips', undefined, { timeout: 4000 });
    expect(within(chipRow).getByTestId('run-summary-chip-goal')).toBeTruthy();
    expect(screen.queryByTestId('compact-primary-run-action')).toBeNull();
    expect(screen.queryByRole('button', { name: /review outcome plan/i })).toBeNull();
  });

  it('keeps all three run-wide chips pinned when a child agent scope is selected', async () => {
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([
      { path: 'src/app.ts', status: 'modified', added_lines: 5, removed_lines: 2, scope: 'merged' },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    // Coordinator scope shows the pinned chips.
    const coordChips = await screen.findByTestId('run-summary-chips', undefined, { timeout: 4000 });
    expect(within(coordChips).getByTestId('run-summary-chip-goal')).toBeTruthy();

    // Selecting a child agent keeps ALL three chips pinned (they always represent run-wide data).
    fireEvent.click(screen.getByRole('treeitem', { name: /Select Subtask 1/i }));
    await waitFor(() => {
      const chipRow = screen.getByTestId('run-summary-chips');
      expect(within(chipRow).getByTestId('run-summary-chip-goal')).toBeTruthy();
      expect(within(chipRow).getByTestId('run-summary-chip-changes')).toBeTruthy();
      expect(within(chipRow).getByTestId('run-summary-chip-files')).toBeTruthy();
      expect(within(chipRow).queryByTestId('run-summary-chip-subagents')).toBeNull();
    }, { timeout: 4000 });
  });

  it('keeps the run-wide chip data unchanged when a child agent scope is selected', async () => {
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([
      { path: 'HotelSearchForm.jsx', status: 'added', added_lines: 40, removed_lines: 0, scope: 'merged' },
    ]);
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

    // Run-wide Changes chip reflects the collective assembly diff regardless of selection.
    await waitFor(
      () => {
        const chipRow = screen.getByTestId('run-summary-chips');
        expect(within(chipRow).getByTestId('run-summary-chip-changes').textContent).toContain('1 file');
      },
      { timeout: 4000 },
    );

    // Selecting a child does not swap the chip to per-child data — it stays run-wide.
    fireEvent.click(screen.getByRole('treeitem', { name: /Select Subtask 1/i }));
    await waitFor(
      () => {
        const chipRow = screen.getByTestId('run-summary-chips');
        expect(within(chipRow).getByTestId('run-summary-chip-changes').textContent).toContain('1 file');
      },
      { timeout: 4000 },
    );
    expect(screen.queryByTestId('session-tab-changes')).toBeNull();

    // Clicking it opens the run-wide diff overlay.
    fireEvent.click(within(screen.getByTestId('run-summary-chips')).getByTestId('run-summary-chip-changes'));
    await screen.findByRole('dialog', { name: 'Changes' }, { timeout: 4000 });
  });

  it('re-fetches assembly files and refreshes the Changes/Files chips as new live events arrive, without a manual page refresh', async () => {
    // Run just started — no assembly diff exists yet, so the chips render disabled "· None".
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([]);
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
    ];

    const { rerender } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const chipRow = await screen.findByTestId('run-summary-chips', undefined, { timeout: 4000 });
    expect(within(chipRow).getByTestId('run-summary-chip-changes').textContent).toContain('None');

    // Assembly progresses and produces a diff, and a new live event lands on the SSE stream
    // (bumping artifactsLiveUpdateKey) — the chips must react to this on their own, the same
    // signal CoordinatorArtifactsPanel already reacts to, WITHOUT requiring a manual refresh.
    vi.mocked(apiClient.getAssemblyFiles).mockResolvedValue([
      { path: 'src/app.ts', status: 'modified', added_lines: 12, removed_lines: 4, scope: 'merged' },
    ]);
    currentEvents = [
      ...currentEvents,
      { sequence: 2, type: 'coordinator.assembly_completed', payload: {} },
    ];
    act(() => {
      rerender(<Wrapper><CoordinatorRunPage /></Wrapper>);
    });

    await waitFor(
      () => {
        const row = screen.getByTestId('run-summary-chips');
        expect(within(row).getByTestId('run-summary-chip-changes').textContent).toContain('1 file');
        expect(within(row).getByTestId('run-summary-chip-files').textContent).toContain('1');
      },
      { timeout: 4000 },
    );
  });

  it('renders the intent-driven Timeline as the default center content', async () => {
    // A realistic scope stream: two reported intents, each with its own tool calls
    // (one succeeds, one fails) and an agent message. The Timeline must group them.
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.intent', payload: { intent: 'Inspect the repository' } },
      { sequence: 3, type: 'tool.call', payload: { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } } },
      { sequence: 4, type: 'tool.result', payload: { callId: 'c1', content: 'export const x = 1;' } },
      { sequence: 5, type: 'agent.intent', payload: { intent: 'Run the build' } },
      { sequence: 6, type: 'tool.call', payload: { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm run build' } } },
      { sequence: 7, type: 'tool.error', payload: { callId: 'c2', errorMessage: 'command failed with exit code 1' } },
      { sequence: 8, type: 'agent.message', payload: { messageId: 'm1', content: 'The build failed on a type error.' } },
      { sequence: 9, type: 'agent.turn.end', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });

    // The primary center surface is the Timeline (renamed from Run activity), driven
    // by the agent's reported intents — NOT the run tree.
    expect(await screen.findByTestId('run-timeline')).toBeTruthy();
    const timeline = screen.getByTestId('run-timeline');
    // agent.intent events become the top-level steps.
    expect(timeline.textContent).toContain('Inspect the repository');
    expect(timeline.textContent).toContain('Run the build');

    // Steps are expanded by default (only the "Used N tools" groups stay collapsed).
    // Tool groups are collapsed by default; expand each to reveal its rows.
    for (const g of await within(timeline).findAllByTestId('timeline-tool-group', undefined, { timeout: 4000 })) {
      fireEvent.click(g);
    }

    // Tool calls render as rows under their owning intent, with success/error state.
    await waitFor(() => expect(within(timeline).getAllByTestId('timeline-tool-row').length).toBe(2), { timeout: 4000 });
    const toolRows = within(timeline).getAllByTestId('timeline-tool-row');
    expect(toolRows.some((r) => r.getAttribute('data-tool-status') === 'complete')).toBe(true);
    expect(toolRows.some((r) => r.getAttribute('data-tool-status') === 'error')).toBe(true);
    // Row anatomy: human primary title, muted secondary command, right-aligned result meta.
    expect(timeline.textContent).toContain('View src/app.ts');
    expect(timeline.textContent).toContain('1 line');
    expect(timeline.textContent).toContain('Run command');
    expect(timeline.textContent).toContain('npm run build');
    // The agent message appears in the expanded step.
    expect(timeline.textContent).toContain('The build failed on a type error.');

    // The Messages surface is the single default center thread; the composer for steering the
    // coordinator lives inline (no separate Chat/center tabs, no segmented control).
    expect(screen.queryByTestId('session-tab-activity')).toBeNull();
    expect(screen.queryByTestId('center-tab-messages')).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Chat' })).toBeNull();
    expect(await screen.findByPlaceholderText('Message coordinator...')).toBeTruthy();
  });

  it('expands an edit tool row into a read-only diff card', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.intent', payload: { intent: 'Apply the change' } },
      {
        sequence: 3,
        type: 'tool.call',
        payload: {
          callId: 'e1',
          toolName: 'str_replace_editor',
          arguments: {
            path: 'src/app.ts',
            old_str: 'const a = 1;',
            new_str: 'const a = 1;\nconst b = 2;',
          },
        },
      },
      { sequence: 4, type: 'tool.result', payload: { callId: 'e1', content: 'edited' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const timeline = await screen.findByTestId('run-timeline');
    // Steps are expanded by default; the tool group is collapsed by default, so expand it to
    // reveal the edit row.
    fireEvent.click(await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 }));

    // The edit row is a button (expandable) with a diff delta as its result meta.
    const editRow = await within(timeline).findByTestId('timeline-tool-row', undefined, { timeout: 4000 });
    expect(editRow.tagName.toLowerCase()).toBe('button');
    expect(editRow.textContent).toContain('Edit src/app.ts');
    expect(editRow.textContent).toContain('+2');

    // Expanding reveals the diff card with the added line.
    fireEvent.click(editRow);
    const diff = await within(timeline).findByTestId('timeline-tool-diff', undefined, { timeout: 4000 });
    expect(diff.textContent).toContain('const b = 2;');
  });

  it('keeps the composer read-only when viewing a child agent and steers other agents through the Coordinator', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });

    // Product decision: viewing a non-coordinator agent is read-only — you steer it
    // through the Coordinator, not by messaging it directly.
    fireEvent.click(screen.getByRole('treeitem', { name: /Select Subtask 1/i }));
    expect(await screen.findByText('Viewing Neo — steer via the Coordinator')).toBeTruthy();
    expect(screen.queryByPlaceholderText('Message coordinator...')).toBeNull();

    // Selecting the Coordinator root restores an interactive composer that steers the run.
    fireEvent.click(screen.getByRole('treeitem', { name: /Select Coordinator/i }));
    const input = await screen.findByPlaceholderText('Message coordinator...');
    fireEvent.change(input, { target: { value: 'Use the cached source' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalled(), { timeout: 4000 });
    expect(vi.mocked(apiClient.steerCoordinator).mock.calls[0][1]).toMatchObject({
      kind: 'send',
      instruction: 'Use the cached source',
    });
    // Coordinator-scoped messages steer the whole run, not a specific child.
    expect(vi.mocked(apiClient.steerCoordinator).mock.calls[0][1]).not.toHaveProperty('target_child_run_id');
    expect(await screen.findByText('Message sent to coordinator.')).toBeTruthy();
  });

  it('surfaces automation toggle failures instead of silently rolling back', async () => {
    vi.mocked(apiClient.setAutopilot).mockRejectedValue(new ApiError(409, '{"message":"run is not active"}'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

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
