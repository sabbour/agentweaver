import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { _resetRuntimeInfoCache } from '../hooks/useRuntimeInfo';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR, COORDINATOR_GRAPH_DESCRIPTOR_DELEGATED, COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR } from './fixtures/graphDescriptor';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { ReactNode } from 'react';
// ResizeObserver is required by @xyflow/react and absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const mockRunStreamState = vi.hoisted(() => ({
  current: {
    events: [] as Array<{ sequence: number; type: string; payload: Record<string, unknown> }>,
    droppedEventCount: 0,
    status: 'done',
    error: null as string | null,
    reconnect: vi.fn(),
  },
}));

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    steerCoordinator: vi.fn(),
    reviewAssembly: vi.fn(),
    getRun: vi.fn(),
    getProject: vi.fn(),
    getRunTokenBreakdown: vi.fn().mockResolvedValue({
      runId: 'coord-run-1',
      source: 'events',
      hasAgentData: false,
      totalTokens: 0,
      totalNanoAiu: 0,
      breakdown: [],
    }),
    getRunTraces: vi.fn().mockResolvedValue({ runId: 'coord-run-1', spans: [] }),
    getRunEvents: vi.fn().mockResolvedValue([]),
    // OutcomePlanPanel uses these — return empty/null to avoid noise.
    getOutcomeSpec: vi.fn(),
    getTeam: vi.fn().mockResolvedValue({ members: [] }),
    // Artifact browser (Changes/Files rail) — empty results in tests.
    getRunFiles: vi.fn().mockResolvedValue([]),
    getRunFileContent: vi.fn().mockResolvedValue({ path: 'file.txt', content: '', is_binary: false, language: 'text' }),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileDiff: vi.fn().mockResolvedValue(null),
    getAssemblyFiles: vi.fn().mockResolvedValue([]),
    getAssemblyWorkspace: vi.fn().mockResolvedValue([]),
    getAssemblyFileDiff: vi.fn().mockResolvedValue(null),
    listPortForwards: vi.fn().mockResolvedValue([]),
    startPortForward: vi.fn(),
    stopPortForward: vi.fn(),
    pingKeepalive: vi.fn().mockResolvedValue(undefined),
    retryPreviewApproval: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => mockRunStreamState.current,
}));

// OutcomePlanPanel performs its own fetch; stub it so it renders nothing.
vi.mock('../components/OutcomePlanPanel', () => ({
  OutcomePlanPanel: () => null,
}));

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
  mockRunStreamState.current = {
    events: [],
    droppedEventCount: 0,
    status: 'done',
    error: null,
    reconnect: vi.fn(),
  };
  _resetRuntimeInfoCache();
  vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: false, podName: null });
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'in_progress' } as never);
  vi.mocked(apiClient.getProject).mockResolvedValue({
    project_id: 'p1',
    name: 'Silver Pancake',
    origin: 'blank',
    source_repository: null,
    working_directory: '',
    default_branch: 'main',
    owner: 'tester',
    default_provider: 'github_copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    available: true,
    state: 'active',
    created_at: '2026-07-07T00:00:00.000Z',
    updated_at: '2026-07-07T00:00:00.000Z',
  } as never);
  vi.mocked(apiClient.getRunTokenBreakdown).mockResolvedValue({
    runId: 'coord-run-1',
    source: 'events',
    hasAgentData: false,
    totalTokens: 0,
    totalNanoAiu: 0,
    breakdown: [],
  });
  vi.mocked(apiClient.getRunTraces).mockResolvedValue({ runId: 'coord-run-1', spans: [] });
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
  vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

async function expandRunControls(): Promise<void> {
  // The topology entry point now lives in the left rail (minimap), not the header.
  await screen.findByTestId('open-topology-minimap', undefined, { timeout: 4000 });
}

async function openTopologyInspector(): Promise<HTMLElement> {
  const button = await screen.findByTestId('open-topology-minimap', undefined, { timeout: 4000 });
  fireEvent.click(button);
  return screen.findByTestId('topology-inspector', undefined, { timeout: 4000 });
}

describe('CoordinatorRunPage — unified coordinator graph view', () => {
  it('renders an explicit not-found state for a missing coordinator run', async () => {
    vi.mocked(apiClient.getRunGraph).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Run not found'),
      { timeout: 4000 },
    );
    expect(document.body.textContent).not.toContain('Running');
  });

  it('shows a failed run as Failed rather than falling back to Running', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'failed' } as never);
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Failed'),
      { timeout: 4000 },
    );
    await expandRunControls();
    expect((screen.getByRole('button', { name: /Stop run/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('treats assemble_ready as a terminal run status in the detail view', async () => {
    vi.mocked(apiClient.getRunGraph).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'assemble_ready' } as never);
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Finished'),
      { timeout: 4000 },
    );
    expect(document.body.textContent).toContain('Ready for assembly');
  });

  it('does not incorrectly terminalize blocked run status without a terminal orchestration phase', async () => {
    vi.mocked(apiClient.getRunGraph).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'blocked' } as never);
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator is still shaping the run'),
      { timeout: 4000 },
    );
    expect(document.body.textContent).not.toContain('Finished');
  });

  it('requires confirmation before stopping an active run', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const stopButton = await screen.findByRole('button', { name: 'Stop run' }, { timeout: 4000 });
    fireEvent.click(stopButton);

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Are you sure you want to stop this run?');
    expect(apiClient.steerCoordinator).not.toHaveBeenCalled();

    // The dialog surface is briefly aria-hidden while Fluent UI's tabster
    // focus trap finishes wiring up the modal (especially under the CPU
    // contention of a full-suite run), so poll for the button to become
    // accessible instead of querying it synchronously. A longer timeout
    // (matching the convention elsewhere in this file) gives the trap time
    // to settle even under heavy parallel-worker load.
    const stopConfirmButton = await within(dialog).findByRole('button', { name: 'Stop run' }, { timeout: 4000 });
    fireEvent.click(stopConfirmButton);
    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalledWith('coord-run-1', { kind: 'stop' }));
  });

  it('cancelling the stop confirmation dialog leaves the run running (true no-op)', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const stopButton = await screen.findByRole('button', { name: 'Stop run' }, { timeout: 4000 });
    fireEvent.click(stopButton);

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Are you sure you want to stop this run?');

    // See note above: wait for the modal's focus trap to settle before
    // querying its contents by role.
    const cancelButton = await within(dialog).findByRole('button', { name: 'Cancel' }, { timeout: 4000 });
    fireEvent.click(cancelButton);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiClient.steerCoordinator).not.toHaveBeenCalled();
  });

  it('surfaces stream errors and dropped events in a health banner', async () => {
    mockRunStreamState.current = {
      events: [],
      droppedEventCount: 2,
      status: 'error',
      error: 'connection lost',
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(screen.getByTestId('coordinator-stream-health')).toBeDefined(),
      { timeout: 4000 },
    );
    expect(document.body.textContent).toContain('connection lost');
    expect(document.body.textContent).toContain('2 events');
  });

  it('stops polling token breakdown after repeated failures', async () => {
    vi.useFakeTimers();
    vi.mocked(apiClient.getRunTokenBreakdown).mockRejectedValue(new Error('token breakdown failed'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await vi.waitFor(() => expect(apiClient.getRunTokenBreakdown).toHaveBeenCalledTimes(1));
    await vi.advanceTimersByTimeAsync(30_000);
    await vi.waitFor(() => expect(apiClient.getRunTokenBreakdown).toHaveBeenCalledTimes(2));
    await vi.advanceTimersByTimeAsync(30_000);
    await vi.waitFor(() => expect(apiClient.getRunTokenBreakdown).toHaveBeenCalledTimes(3));
    await vi.advanceTimersByTimeAsync(120_000);
    expect(apiClient.getRunTokenBreakdown).toHaveBeenCalledTimes(3);
  });

  it('renders coordinator node, subtask nodes, and planned assembly nodes', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    const text = inspector.textContent ?? '';
    // Coordinator orchestrator node
    expect(text).toContain('Coordinator');
    // Subtask nodes from fixture
    expect(text).toContain('Subtask 1');
    expect(text).toContain('Subtask 2');
    // Planned assembly nodes
    expect(text).toContain('RAI Review');
    expect(text).toContain('Human Review');
    expect(within(inspector).getByTestId('topology-toolbar')).toBeTruthy();
  });

  it('renders skipped assembly stages as "Delegated to backlog" on a delegated run (not Pending forever)', async () => {
    // A fully-promoted run: every story became an independent Board task, so the coordinator run is
    // terminal (delegated_to_backlog) and RAI / Human Review / Merge / Scribe are intentionally
    // skipped. They must render as a terminal "Delegated to backlog" state, never Pending forever.
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR_DELEGATED);
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'completed',
      coordinator_status: 'delegated',
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // All four skipped assembly stages read "Delegated to backlog" in the run tree.
    await screen.findByRole('treeitem', { name: /Select RAI Review: Delegated to backlog/i }, { timeout: 4000 });
    expect(screen.getByRole('treeitem', { name: /Select Human Review: Delegated to backlog/i })).toBeTruthy();
    expect(screen.getByRole('treeitem', { name: /Select Merge: Delegated to backlog/i })).toBeTruthy();
    expect(screen.getByRole('treeitem', { name: /Select Scribe: Delegated to backlog/i })).toBeTruthy();

    // None of the assembly stages linger as Pending.
    expect(screen.queryByRole('treeitem', { name: /Select Human Review: Pending/i })).toBeNull();
    expect(screen.queryByRole('treeitem', { name: /Select Scribe: Pending/i })).toBeNull();
  });

  it('drives the delegated state from coordinator_status even without a per-node server marker', async () => {
    // Server-marker path is covered above; this pins the frontend fallback: when the descriptor
    // nodes are plain "planned" (no status) but coordinator_status is "delegated", the assembly
    // stages still terminalize as "Delegated to backlog".
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'completed',
      coordinator_status: 'delegated',
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await screen.findByRole('treeitem', { name: /Select Human Review: Delegated to backlog/i }, { timeout: 4000 });
    expect(screen.getByRole('treeitem', { name: /Select Scribe: Delegated to backlog/i })).toBeTruthy();
    expect(screen.queryByRole('treeitem', { name: /Select Merge: Pending/i })).toBeNull();
  });

  it('renders planned assembly nodes with "Planned" badge (visually distinct)', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    // Planned state is now conveyed by the dashed pill + the node's aria-label (": Pending"),
    // not a visible "Planned" badge on the compact face.
    expect(screen.getByRole('article', { name: 'RAI Review: Pending' })).toBeTruthy();

    // Planned nodes carry data-node-type attributes in the rendered HTML.
    const html = inspector.innerHTML;
    expect(html).toContain('data-node-type="gate"');    // planned RAI Review + Human Review
    expect(html).toContain('data-node-type="action"');  // planned Merge + Scribe
  });

  it('highlights the human review tree node and reveals the review CTA when selected', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'awaiting_review',
      coordinator_status: 'in_review',
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const reviewRow = await screen.findByRole('treeitem', { name: /Select Human Review: Action needed/i }, { timeout: 4000 });
    expect(reviewRow.getAttribute('aria-label')).toContain('Select Human Review: Action needed');

    fireEvent.click(reviewRow);

    const approvalGate = await screen.findByLabelText('Approvals and gates', undefined, { timeout: 4000 });
    expect(approvalGate.textContent).toContain('Approve & merge');
    expect(approvalGate.textContent).toContain('You can request changes from the Artifacts tab.');
    expect(within(approvalGate).queryByRole('button', { name: /open outcome plan/i })).toBeNull();
    expect(within(approvalGate).queryByRole('button', { name: /open assembly artifacts/i })).toBeNull();
  });

  it('labels Build & Test as a build/test gate and surfaces the active preview there', async () => {
    mockRunStreamState.current = {
      events: [{ sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} }],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'build-test', label: 'Build & Test', role: 'review', kind: 'live', node_type: 'gate', status: 'running' },
      ],
      edges: [{ from: 'coordinator', to: 'build-test', cardinality: 'direct', loopback: false }],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 2, type: 'sandbox.preview_ready', payload: { preview_url: 'https://preview.example.test', target_port: 3000 } },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test: Running/i }, { timeout: 4000 });
    expect(buildRow.textContent).toContain('Build/test gate');
    expect(buildRow.textContent).not.toContain('Human Review');
    expect(buildRow.textContent).toContain('Preview');

    fireEvent.click(buildRow);
    const previewCta = await screen.findByTestId('selected-build-preview-cta', undefined, { timeout: 4000 });
    expect(previewCta.textContent).toContain('Preview from Build & Test is active');
    expect(previewCta.textContent).toContain('Open preview');
  });

  it('projects Build & Test running/completed from build-test gateKind events without arming human review', async () => {
    mockRunStreamState.current = {
      events: [
        { sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} },
        {
          sequence: 2,
          type: 'coordinator.assembly_review_requested',
          payload: { gateKind: 'build-test', timestamp_utc: '2026-07-08T00:00:00.000Z' },
        },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'build-test', label: 'Build & Test', role: 'review', kind: 'live', node_type: 'gate' },
        { id: 'planned:assembly-review', label: 'Human Review', role: 'review', kind: 'planned', node_type: 'gate' },
      ],
      edges: [
        { from: 'coordinator', to: 'build-test', cardinality: 'direct', loopback: false },
        { from: 'build-test', to: 'planned:assembly-review', cardinality: 'direct', loopback: false },
      ],
    });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test: Running/i }, { timeout: 4000 });
    expect(buildRow.textContent).toContain('Running');
    expect(buildRow.getAttribute('aria-label')).not.toContain('Operator action needed');
    expect(screen.queryByTestId('run-tree-review-cta')).toBeNull();

    cleanup();
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        ...mockRunStreamState.current.events,
        {
          sequence: 3,
          type: 'coordinator.assembly_review_approved',
          payload: { gateKind: 'build-test', timestamp_utc: '2026-07-08T00:01:00.000Z' },
        },
      ],
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const completedBuildRow = await screen.findByRole('treeitem', { name: /Select Build & Test: Completed/i }, { timeout: 4000 });
    expect(completedBuildRow.textContent).toContain('Completed');
    expect(screen.getByRole('treeitem', { name: /Select Human Review: Pending/i }).textContent).toContain('Pending');
  });

  it('converts live child and gate rows to failed after a terminal failed coordinator run', async () => {
    mockRunStreamState.current = {
      events: [{ sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} }],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'failed' } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'build-test', label: 'Build & Test', role: 'build_test', kind: 'live', node_type: 'gate', status: 'running' },
      ],
      edges: [{ from: 'coordinator', to: 'build-test', cardinality: 'direct', loopback: false }],
    });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole('treeitem', { name: /Select Build & Test: Failed/i }, { timeout: 4000 });
    expect(buildRow.textContent).toContain('Failed');
    expect(within(buildRow).getByTestId('run-tree-status-icon').getAttribute('data-state-color')).toBe('danger');
  });

  it('renders a RAI verdict on the selected RAI node instead of an empty-message fallback', async () => {
    mockRunStreamState.current = {
      events: [
        {
          sequence: 1,
          type: 'coordinator.assembly_rai_completed',
          payload: { timestamp_utc: '2026-07-08T00:00:00.000Z' },
        },
        {
          sequence: 2,
          type: 'rai.verdict',
          payload: { trafficLight: 'green', rationale: 'All checks passed.' },
        },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const raiRow = await screen.findByRole('treeitem', { name: /Select RAI Review:/i }, { timeout: 4000 });
    fireEvent.click(raiRow);

    await waitFor(() => expect(document.body.textContent).toContain('RAI verdict: 🟢 Green — All checks passed.'), { timeout: 4000 });
    expect(document.body.textContent).not.toContain('No streamed messages yet for this session.');
  });

  it('orders run tree rows by coordinator workflow stage instead of descriptor/event arrival order', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'planned:assembly-merge', label: 'Merge', role: 'merge', kind: 'planned', node_type: 'action' },
        { id: 'plan:subtask-2', label: 'Implement server.js and package.json', role: 'subtask', kind: 'live', node_type: 'subtask', child_run_id: 'child-run-2', agent: 'Trinity' },
        { id: 'planned:assembly-scribe', label: 'Scribe', role: 'scribe', kind: 'planned', node_type: 'action' },
        { id: 'build-test', label: 'Build & Test', role: 'build_test', kind: 'planned', node_type: 'gate' },
        { id: 'planned:assembly-rai', label: 'RAI Check', role: 'rai', kind: 'planned', node_type: 'gate' },
        { id: 'plan:subtask-1', label: 'Plan minimal preview app structure', role: 'subtask', kind: 'live', node_type: 'subtask', child_run_id: 'child-run-1', agent: 'Neo' },
        { id: 'planned:assembly-review', label: 'Review Gate', role: 'review', kind: 'planned', node_type: 'gate' },
      ],
      edges: [
        { from: 'coordinator', to: 'planned:assembly-merge', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:subtask-2', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'planned:assembly-scribe', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'build-test', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'planned:assembly-rai', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'planned:assembly-review', cardinality: 'direct', loopback: false },
      ],
    } as never);
    mockRunStreamState.current = {
      events: [
        { sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} },
        { sequence: 2, type: 'coordinator.work_plan', payload: {} },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await screen.findByRole('treeitem', { name: /Select Work plan:/i }, { timeout: 4000 });
    const labels = screen.getAllByRole('treeitem')
      .filter((item) => item.getAttribute('aria-label')?.startsWith('Select '))
      .map((button) => button.getAttribute('aria-label')?.replace(/^Select (.*): .+$/, '$1'))
      .filter((label): label is string => Boolean(label));

    expect(labels.slice(0, 10)).toEqual([
      'Coordinator',
      'Outcome plan',
      'Work plan',
      'Plan minimal preview app structure',
      'Implement server.js and package.json',
      'RAI Check',
      'Build & Test',
      'Review Gate',
      'Merge',
      'Scribe',
    ]);
  });

  it('surfaces assembly changes-requested as revising state on affected subtasks', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'plan:subtask-1', label: 'Plan minimal preview app structure', role: 'subtask', kind: 'live', node_type: 'subtask', child_run_id: 'child-run-3', agent: 'Neo' },
        { id: 'plan:subtask-2', label: 'Implement server.js and package.json', role: 'subtask', kind: 'live', node_type: 'subtask', child_run_id: 'child-run-2', agent: 'Trinity' },
        { id: 'planned:assembly-review', label: 'Review Gate', role: 'review', kind: 'planned', node_type: 'gate' },
      ],
      edges: [
        { from: 'coordinator', to: 'plan:subtask-1', cardinality: 'direct', loopback: false },
        { from: 'coordinator', to: 'plan:subtask-2', cardinality: 'direct', loopback: false },
        { from: 'plan:subtask-1', to: 'planned:assembly-review', cardinality: 'fanin', loopback: false },
        { from: 'plan:subtask-2', to: 'planned:assembly-review', cardinality: 'fanin', loopback: false },
      ],
    } as never);
    mockRunStreamState.current = {
      events: [
        { sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} },
        { sequence: 2, type: 'coordinator.work_plan', payload: {} },
        { sequence: 3, type: 'coordinator.assembly_review_requested', payload: { gateKind: 'rubberduck' } },
        {
          sequence: 4,
          type: 'coordinator.assembly_changes_requested',
          payload: {
            redispatchSubtaskIds: [1],
            redispatchedSubtaskIds: [1],
            feedback: 'Tighten the preview acceptance criteria.',
          },
        },
        { sequence: 5, type: 'subtask.dispatched', payload: { subtaskId: 1, childRunId: 'child-run-3' } },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const revisedRow = await screen.findByRole('treeitem', { name: /Select Plan minimal preview app structure: Changes requested — revising/i }, { timeout: 4000 });
    expect(revisedRow.textContent).toContain('Changes requested — revising');
    expect(screen.getByRole('treeitem', { name: /Select Implement server\.js and package\.json: Pending/i }).textContent).toContain('Pending');
    expect((await screen.findByTestId('run-status-chip', undefined, { timeout: 4000 })).textContent).toContain('Revising after Rubberduck feedback');
  });

  it('keeps coordinator messaging enabled during review when the backend marks the run steerable', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'awaiting_review',
      coordinator_status: 'in_review',
      coordinator_steerable: true,
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const input = await screen.findByPlaceholderText('Message coordinator...', undefined, { timeout: 4000 }) as HTMLInputElement;
    await waitFor(() => expect(input.disabled).toBe(false), { timeout: 4000 });
    expect(document.body.textContent).not.toContain('Messaging is unavailable because this coordinator run is not active.');
  });

  it('keeps never-run assembly gates planned after a pre-gate terminal failure', async () => {
    const reason = 'assembly_rearm_exhausted after 3 attempts';
    mockRunStreamState.current = {
      events: [
        {
          sequence: 10,
          type: 'coordinator.assembly_failed',
          payload: { reason, phase: 'assembly_blocked' },
        },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'failed',
      coordinator_status: 'failed',
      coordinator_status_reason: reason,
    } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: COORDINATOR_GRAPH_DESCRIPTOR.nodes.map((node) => (
        node.role === 'rai' || node.role === 'review' || node.role === 'merge' || node.role === 'scribe'
          ? { ...node, kind: 'planned', status: undefined, status_reason: undefined, terminal_stage: undefined }
          : node.id === 'coordinator'
            ? { ...node, status: 'assembly_failed', status_reason: reason, terminal_stage: undefined }
            : node
      )),
    });
    vi.mocked(apiClient.getWorkPlan).mockResolvedValue({
      workPlanId: 7,
      coordinatorRunId: 'coord-run-1',
      outcomeSpecId: 3,
      status: 'assembly_failed',
      assemblyStage: 'scribe',
      assemblyTerminalStage: null,
      statusReason: reason,
      subtasks: [],
      dependencies: [],
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await openTopologyInspector();
    await waitFor(
      () => expect(document.body.textContent).toContain(reason),
      { timeout: 4000 },
    );
    // Planned gates keep their ": Pending" aria-label (dashed pill conveys the planned state);
    // getByRole throws if the article is missing, so presence is the assertion.
    expect(screen.getByRole('article', { name: 'RAI Review: Pending' })).toBeTruthy();
    expect(screen.getByRole('article', { name: 'Human Review: Pending' })).toBeTruthy();
    expect(screen.getByRole('article', { name: 'Merge: Pending' })).toBeTruthy();
    expect(screen.getByRole('article', { name: 'Scribe: Pending' })).toBeTruthy();
    expect(screen.queryByRole('article', { name: 'Merge: Failed' })).toBeNull();
  });

  it('renders subtask nodes as data-node-type=subtask', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // SubtaskNode renders data-node-type="subtask" on its card div.
    const html = inspector.innerHTML;
    expect(html).toContain('data-node-type="subtask"');
  });

  it('surfaces coordinator model badges from the graph descriptor into the session panel header', async () => {
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: COORDINATOR_GRAPH_DESCRIPTOR.nodes.map((node) =>
        node.id === 'coordinator'
          ? { ...node, model: 'claude-sonnet-4.6' }
          : node),
    });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.querySelector('[title="claude-sonnet-4.6"]')?.textContent).toContain('Claude Sonnet 4.6'),
      { timeout: 4000 },
    );
  });

  it('removes background minimap graph nodes while the topology panel is open so the first subtask click targets the visible graph', async () => {
    const originalMatchMedia = window.matchMedia;
    window.matchMedia = ((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    try {
      render(<Wrapper><CoordinatorRunPage /></Wrapper>);

      const button = await screen.findByTestId('open-topology-minimap', undefined, { timeout: 4000 });
      await waitFor(() => expect(button.querySelectorAll('[data-node-type="subtask"]').length).toBeGreaterThan(0), { timeout: 4000 });

      const inspector = await openTopologyInspector();
      await waitFor(() => expect(inspector.textContent).toContain('Subtask 1'), { timeout: 4000 });
      expect(button.querySelector('[data-node-type="subtask"]')).toBeNull();

      const viewport = inspector.querySelector('.react-flow__viewport') as HTMLElement;
      const firstVisibleSubtask = document.querySelector('[data-node-type="subtask"]')!.closest('.react-flow__node') as HTMLElement;
      fireEvent.click(firstVisibleSubtask);

      await waitFor(() => expect(viewport.style.transform).toContain('scale(1.3)'));
    } finally {
      window.matchMedia = originalMatchMedia;
    }
  });

  it('renders subtask nodes without showing API pod chip when no executionPodName is set', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: true, podName: 'agentweaver-api-pod-1' });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // Nodes with no executionPodName must NOT show the API pod chip (no fallback).
    expect(document.body.querySelector('[aria-label^="Executing in pod agentweaver-api-pod-1"]')).toBeNull();
  });

  it('hides the header preview button until a preview lifecycle event exists', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: true, podName: 'agentweaver-api-pod-1' });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });
    expect(screen.queryByRole('button', { name: 'Preview Sandbox' })).toBeNull();
  });

  it('shows the header preview button once the preview lifecycle has started', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: true, podName: 'agentweaver-api-pod-1' });
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        { sequence: 1, type: 'sandbox.selected', payload: { backend: 'kubernetes-sandbox-claim' } },
        { sequence: 2, type: 'sandbox.preview_pending', payload: { target_port: 5173 } },
      ],
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    expect(await screen.findByRole('button', { name: 'Preview Sandbox' }, { timeout: 4000 })).toBeTruthy();
  });

  it('retries an expired preview approval from the Build & Test state', async () => {
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [{ sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} }],
    };
    vi.mocked(apiClient.getRunGraph).mockResolvedValue({
      ...COORDINATOR_GRAPH_DESCRIPTOR,
      nodes: [
        { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
        { id: 'build-test', label: 'Build & Test', role: 'review', kind: 'live', node_type: 'gate', status: 'running' },
      ],
      edges: [{ from: 'coordinator', to: 'build-test', cardinality: 'direct', loopback: false }],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 2,
        type: 'sandbox.preview_failed',
        payload: {
          reason: 'approval_timed_out',
          approval_request_id: 'expired-preview-request',
          retry_available: true,
          target_port: 5173,
        },
      },
    ]);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const buildRow = await screen.findByRole(
      'treeitem',
      { name: /Select Build & Test: Running/i },
      { timeout: 4000 },
    );
    fireEvent.click(buildRow);
    fireEvent.click(await screen.findByRole(
      'button',
      { name: 'Retry expired preview approval' },
      { timeout: 4000 },
    ));

    await waitFor(() => expect(apiClient.retryPreviewApproval)
      .toHaveBeenCalledWith('coord-run-1', 'expired-preview-request'));
  });

  it('renders the persistent coordinator composer inline in the Messages surface', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // The composer IS the chat — it is always present inline, with no separate
    // "Message coordinator" header button to reveal it.
    await waitFor(() => {
      const input = container.querySelector('textarea[placeholder="Message coordinator..."]') as HTMLTextAreaElement | null;
      expect(input).toBeTruthy();
    }, { timeout: 4000 });
    expect(container.querySelector('[data-testid="open-steer-panel"]')).toBeNull();
  });

  it('renders Ctrl+Scroll zoom controls on the orchestration graph', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(() => expect(inspector.textContent).toContain('Coordinator'), { timeout: 4000 });

    // The shared ZoomControls (Ctrl+Scroll hint via tooltip + +/- buttons + % readout)
    // render inside the topology inspector.
    const buttons = Array.from(inspector.querySelectorAll('button'));
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom in')).toBe(true);
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom out')).toBe(true);
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Fit to view')).toBe(true);
  });

  it('lays out the topology as a balanced flow rather than a strict vertical stack', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(() => expect(inspector.textContent).toContain('Scribe'), { timeout: 4000 });

    const graphNodes = Array.from(inspector.querySelectorAll('.react-flow__node')) as HTMLElement[];
    expect(graphNodes.length).toBeGreaterThan(4);
    const positions = graphNodes.map((node) => {
      const transform = node.style.transform;
      const match = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(transform);
      expect(match).not.toBeNull();
      return { x: Math.round(Number(match![1])), y: Math.round(Number(match![2])) };
    });
    const rows = new Set(positions.map((pos) => pos.y));
    const columns = new Set(positions.map((pos) => pos.x));

    // The staircase distributes the run across BOTH axes (not a single column stack, not a single
    // horizontal row): successive ranks step down-right, so there are multiple columns AND rows.
    expect(columns.size).toBeGreaterThan(1);
    expect(rows.size).toBeGreaterThan(1);
  });

  it('cinematically zooms the viewport onto a node on click (setCenter), in addition to selecting it', async () => {
    // Force reduced motion so setCenter applies instantly (duration 0) — deterministic in jsdom.
    const originalMatchMedia = window.matchMedia;
    window.matchMedia = ((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    try {
      render(<Wrapper><CoordinatorRunPage /></Wrapper>);

      const inspector = await openTopologyInspector();
      await waitFor(() => expect(inspector.textContent).toContain('Subtask 1'), { timeout: 4000 });

      const viewport = inspector.querySelector('.react-flow__viewport') as HTMLElement;
      const nodeEl = inspector
        .querySelector('[data-node-type="subtask"]')!
        .closest('.react-flow__node') as HTMLElement;
      expect(viewport).toBeTruthy();
      expect(nodeEl).toBeTruthy();

      fireEvent.click(nodeEl);

      // The viewport tweens to the cinematic target zoom (1.3). Click-select still runs alongside it.
      await waitFor(() => expect(viewport.style.transform).toContain('scale(1.3)'));
    } finally {
      window.matchMedia = originalMatchMedia;
    }
  });


  it('zooms back OUT toward the whole graph when the empty pane is clicked (onPaneClick → fit)', async () => {
    const originalMatchMedia = window.matchMedia;
    window.matchMedia = ((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    try {
      render(<Wrapper><CoordinatorRunPage /></Wrapper>);

      const inspector = await openTopologyInspector();
      await waitFor(() => expect(inspector.textContent).toContain('Subtask 1'), { timeout: 4000 });

      const viewport = inspector.querySelector('.react-flow__viewport') as HTMLElement;
      const nodeEl = inspector
        .querySelector('[data-node-type="subtask"]')!
        .closest('.react-flow__node') as HTMLElement;
      const pane = inspector.querySelector('.react-flow__pane') as HTMLElement;
      expect(pane).toBeTruthy();

      // Zoom in on a node first (scale 1.3)…
      fireEvent.click(nodeEl);
      await waitFor(() => expect(viewport.style.transform).toContain('scale(1.3)'));

      // …then click the empty pane. The onPaneClick handler fits the whole graph back out; in jsdom
      // fitView can't measure the 0-size container, so we assert the handler is wired and harmless
      // (no throw) and the graph/nodes remain intact rather than asserting an exact fit transform.
      expect(() => fireEvent.click(pane)).not.toThrow();
      expect(inspector.querySelectorAll('.react-flow__node').length).toBeGreaterThan(4);
    } finally {
      window.matchMedia = originalMatchMedia;
    }
  });


  it('keeps the run tree order completely stable when the graph orientation changes (LR ⇄ TB)', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(() => expect(inspector.textContent).toContain('Scribe'), { timeout: 4000 });

    const treeOrder = () =>
      screen.getAllByRole('treeitem').map((el) => el.getAttribute('aria-label') ?? el.textContent ?? '');
    const before = treeOrder();
    expect(before.length).toBeGreaterThan(2);

    // Switch to vertical (TB): rank now advances on Y, siblings on X. The run tree is derived from
    // dependency/emission order, so its order/structure must be byte-identical.
    const switchBtn = screen.getByRole('button', { name: /Switch orientation/i });
    fireEvent.click(switchBtn);
    expect(treeOrder()).toEqual(before);

    // Back to horizontal (LR) — still identical.
    fireEvent.click(switchBtn);
    expect(treeOrder()).toEqual(before);
  });

  it('renders from REST descriptor even when SSE stream is done (finished coordinator runs)', async () => {
    // Stream is already 'done' in the mock (simulates a finished coordinator run with closed SSE).
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    // Graph must render from REST seed even though SSE stream is done.
    const text = inspector.textContent ?? '';
    expect(text).toContain('Subtask 1');
    expect(text).toContain('RAI Review');
  });
});

describe('CoordinatorRunPage — graph during outcome-plan drafting', () => {
  it('projects drafting events as active planning instead of pending', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({ run_id: 'coord-run-1', status: 'pending' } as never);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR);
    mockRunStreamState.current = {
      events: [
        {
          sequence: 1,
          type: 'coordinator.outcome_spec.drafting',
          payload: { message: 'Drafting the outcome plan', timestamp_utc: '2026-07-07T00:00:00.000Z' },
        },
      ],
      droppedEventCount: 0,
      status: 'streaming',
      error: null,
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const statusChip = await screen.findByTestId('run-status-chip', undefined, { timeout: 4000 });
    expect(statusChip.textContent).toContain('Drafting outcome plan');
    expect(await screen.findByLabelText('Select Outcome plan: Drafting outcome plan', undefined, { timeout: 4000 })).toBeDefined();
    expect(screen.queryByLabelText('Select Outcome plan: Pending')).toBeNull();
  });

  it('hides the assembly pipeline stages and shows a caption while drafting the spec', async () => {
    // Drafting state: coordinator + planned assembly stages, no subtasks, no confirmed spec.
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    const text = inspector.textContent ?? '';
    // Coordinator node still renders live.
    expect(text).toContain('Coordinator');
    // The calm caption explains why the pipeline is absent.
    expect(text).toContain('The execution pipeline appears once you confirm the Outcome plan.');
    // Assembly stages must NOT be presented as committed planned work yet.
    expect(text).not.toContain('RAI Review');
    expect(text).not.toContain('Human Review');
    expect(text).not.toContain('Merge');
    expect(text).not.toContain('Scribe');
  });

  it('renders the full pipeline (and drops the caption) once subtasks exist', async () => {
    // The standard coordinator fixture has subtask nodes → hasSubtaskNodes flips inSpecAuthoring off.
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    const text = inspector.textContent ?? '';
    // Full assembly pipeline renders.
    expect(text).toContain('RAI Review');
    expect(text).toContain('Human Review');
    expect(text).toContain('Scribe');
    // No drafting caption once the plan exists.
    expect(text).not.toContain('The execution pipeline appears once you confirm the Outcome plan.');
  });
});

describe('CoordinatorRunPage — work-plan 404 (no plan yet / stuck run)', () => {
  it('renders an explicit graph-not-emitted state instead of a fake running graph', async () => {
    // No graph descriptor and a 404 work-plan: an early run should not invent a successful graph.
    vi.mocked(apiClient.getRunGraph).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'in_progress' } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('Graph has not been emitted yet'),
      { timeout: 4000 },
    );

    expect(document.body.textContent).not.toContain('The execution pipeline appears once you confirm the Outcome plan.');
  });

  it('keeps retrying getWorkPlan after a 404 while the coordinator run is in progress', async () => {
    // Coordinator run: work-plan returns 404, but run is still in_progress.
    // The poll must keep running (to track coordinator_status) but skip getWorkPlan.
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'in_progress' } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // Wait for the first poll tick to fire and record the 404.
    await waitFor(
      () => expect(vi.mocked(apiClient.getRun)).toHaveBeenCalled(),
      { timeout: 2000 },
    );

    const afterFirstTick = vi.mocked(apiClient.getWorkPlan).mock.calls.length;

    // The first render calls work-plan from the seed and the lifecycle poll. The important
    // regression guard is that 404 is not permanently suppressed while the run is active.
    await new Promise((resolve) => setTimeout(resolve, 4200));

    const afterDelay = vi.mocked(apiClient.getWorkPlan).mock.calls.length;

    expect(afterDelay).toBeGreaterThan(afterFirstTick);
  });
});

describe('CoordinatorRunPage — child run (non-coordinator) skips coordinator artifacts', () => {
  it('does not call getWorkPlan for a child run (parent_run_id is set)', async () => {
    // A child run has parent_run_id set. The work-plan and outcome-plan endpoints do not exist
    // for child runs; calling them produces expected 404s that add noise without value.
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'child-run-1',
      status: 'in_progress',
      parent_run_id: 'coordinator-run-1',
    } as never);
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // Wait for getRun to be called (confirms effects have fired).
    await waitFor(
      () => expect(vi.mocked(apiClient.getRun)).toHaveBeenCalled(),
      { timeout: 2000 },
    );

    // Allow any pending async work to settle before asserting call counts.
    await new Promise((resolve) => setTimeout(resolve, 100));

    // getWorkPlan must not be called for a child run — it is a coordinator-only artifact.
    expect(vi.mocked(apiClient.getWorkPlan)).not.toHaveBeenCalled();
  });

  it('does not render the Outcome plan panel for a child run', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'child-run-1',
      status: 'in_progress',
      parent_run_id: 'coordinator-run-1',
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // Wait for the run type to resolve.
    await waitFor(
      () => expect(vi.mocked(apiClient.getRun)).toHaveBeenCalled(),
      { timeout: 2000 },
    );
    await new Promise((resolve) => setTimeout(resolve, 100));

    // The OutcomePlanPanel is stubbed to return null, so getOutcomeSpec must not be called.
    // (OutcomePlanPanel is mocked at the module level in this file.)
    expect(vi.mocked(apiClient.getOutcomeSpec)).not.toHaveBeenCalled();
  });

  it('stops polling after run-level terminal status even when coordinator_status is absent', async () => {
    // A run that is terminal at the run level but has no coordinator_status field set.
    // The lifecycle poll must stop after the first tick, not keep retrying.
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'failed' } as never);
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(vi.mocked(apiClient.getRun)).toHaveBeenCalled(),
      { timeout: 2000 },
    );
    // Let any scheduled timers fire.
    await new Promise((resolve) => setTimeout(resolve, 200));

    // getRun should only be called once or twice (seed + first poll tick); the poll stops
    // because the run-level status is terminal.
    const runCalls = vi.mocked(apiClient.getRun).mock.calls.length;
    expect(runCalls).toBeLessThan(4);
  });

  // #97 — an assembly_blocked run must name WHICH subtasks blocked assembly (id/title/status) and a
  // readable reason, never the opaque `ineligible_subtasks [ids]` code / "could not complete" fallback.
  it('surfaces structured ineligible subtasks when assembly is blocked', async () => {
    mockRunStreamState.current = {
      events: [
        { sequence: 1, type: 'coordinator.outcome_spec.confirmed', payload: {} },
        {
          sequence: 2,
          type: 'coordinator.assembly_blocked',
          payload: {
            reason: 'ineligible_subtasks',
            ineligibleSubtaskIds: [59, 60],
            ineligibleSubtasks: [
              { id: 59, title: 'Auth API', status: 'failed', agent: 'morpheus' },
              { id: 60, title: 'DB layer', status: 'running', agent: 'trinity' },
            ],
            timestamp_utc: '2026-07-08T00:00:00.000Z',
          },
        },
      ],
      droppedEventCount: 0,
      status: 'done',
      error: null,
      reconnect: vi.fn(),
    };

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const panel = await screen.findByTestId('assembly-ineligible-subtasks', undefined, { timeout: 4000 });
    // Readable, normalized reason — not the raw `ineligible_subtasks [59,60]` code.
    expect(panel.textContent).toContain("Waiting on 2 subtasks that aren't ready to assemble");
    expect(panel.textContent).not.toContain('ineligible_subtasks [');
    // Names WHICH subtasks blocked, with their actual status.
    const rows = screen.getAllByTestId('assembly-ineligible-subtask');
    expect(rows).toHaveLength(2);
    expect(panel.textContent).toContain('#59');
    expect(panel.textContent).toContain('Auth API');
    expect(panel.textContent).toContain('failed');
    expect(panel.textContent).toContain('#60');
    expect(panel.textContent).toContain('DB layer');
  });

  // #97 — fallback: after a reload the enriched event may be gone, leaving only the persisted
  // status/reason field. The reason must STILL normalize and the ids must still surface.
  it('normalizes the blocked reason from the persisted status field after reload', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1',
      status: 'in_progress',
      coordinator_status: 'assembly_blocked',
      coordinator_status_reason: 'assembly_blocked: ineligible_subtasks [59,60,61,62]',
    } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const panel = await screen.findByTestId('assembly-ineligible-subtasks', undefined, { timeout: 4000 });
    expect(panel.textContent).toContain("Waiting on 4 subtasks that aren't ready to assemble");
    expect(panel.textContent).toContain('#61');
    expect(document.body.textContent).not.toContain('The collective assembly could not complete');
  });
});
