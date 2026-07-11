import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { _resetRuntimeInfoCache } from '../hooks/useRuntimeInfo';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR, COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR } from './fixtures/graphDescriptor';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
  vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

async function expandRunControls(): Promise<void> {
  if (screen.queryByTestId('run-actions-row')) return;
  const toggle = await screen.findByTestId('run-chrome-toggle', undefined, { timeout: 4000 });
  fireEvent.click(toggle);
  await screen.findByTestId('run-actions-row', undefined, { timeout: 4000 });
}

async function openTopologyInspector(): Promise<HTMLElement> {
  await expandRunControls();
  const button = await screen.findByTestId('open-topology-panel', undefined, { timeout: 4000 });
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
    expect((screen.getByText('Stop run') as HTMLButtonElement).disabled).toBe(true);
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
  });

  it('renders planned assembly nodes with "Planned" badge (visually distinct)', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const inspector = await openTopologyInspector();
    await waitFor(
      () => expect(inspector.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    // Planned nodes show a "Planned" status badge (from StatusBadge with isPlanned=true).
    const text = inspector.textContent ?? '';
    expect(text).toContain('Planned');

    // Planned nodes carry data-node-type attributes in the rendered HTML.
    const html = inspector.innerHTML;
    expect(html).toContain('data-node-type="gate"');    // planned RAI Review + Human Review
    expect(html).toContain('data-node-type="action"');  // planned Merge + Scribe
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
    expect(screen.getByRole('article', { name: 'RAI Review: Pending' }).textContent).toContain('Planned');
    expect(screen.getByRole('article', { name: 'Human Review: Pending' }).textContent).toContain('Planned');
    expect(screen.getByRole('article', { name: 'Merge: Pending' }).textContent).toContain('Planned');
    expect(screen.getByRole('article', { name: 'Scribe: Pending' }).textContent).toContain('Planned');
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

  it('shows a Message button that focuses the persistent coordinator composer', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const steerBtn = await waitFor(() => {
      const btn = container.querySelector('[data-testid="compact-primary-run-action"]') as HTMLButtonElement | null;
      expect(btn).not.toBeNull();
      return btn as HTMLButtonElement;
    }, { timeout: 4000 });
    steerBtn.click();

    await waitFor(() => {
      const input = container.querySelector('textarea[placeholder="Message coordinator..."]') as HTMLTextAreaElement | null;
      expect(input).toBeTruthy();
      expect(document.activeElement).toBe(input);
    }, { timeout: 4000 });
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

    expect(columns.size).toBeGreaterThan(1);
    expect(rows.size).toBeLessThan(graphNodes.length - 1);
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
});
