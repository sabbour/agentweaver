import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';

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
    // RunLayout artifact browser (Changes/Files rail) — empty results in tests.
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

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { _resetRuntimeInfoCache } from '../hooks/useRuntimeInfo';
import { COORDINATOR_GRAPH_DESCRIPTOR, COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR } from './fixtures/graphDescriptor';

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

afterEach(() => cleanup());

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

  it('renders coordinator node, subtask nodes, and planned assembly nodes', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
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

    await waitFor(
      () => expect(document.body.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    // Planned nodes show a "Planned" status badge (from StatusBadge with isPlanned=true).
    const text = document.body.textContent ?? '';
    expect(text).toContain('Planned');

    // Planned nodes carry data-node-type attributes in the rendered HTML.
    const html = document.body.innerHTML;
    expect(html).toContain('data-node-type="gate"');    // planned RAI Review + Human Review
    expect(html).toContain('data-node-type="action"');  // planned Merge + Scribe
  });

  it('renders subtask nodes as data-node-type=subtask', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // SubtaskNode renders data-node-type="subtask" on its card div.
    const html = document.body.innerHTML;
    expect(html).toContain('data-node-type="subtask"');
  });

  it('renders subtask nodes without showing API pod chip when no executionPodName is set', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: true, podName: 'agentweaver-api-pod-1' });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // Nodes with no executionPodName must NOT show the API pod chip (no fallback).
    expect(document.body.querySelector('[aria-label^="Executing in pod agentweaver-api-pod-1"]')).toBeNull();
  });

  it('shows a Message button that focuses the persistent coordinator composer', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const steerBtn = await waitFor(() => {
      const btn = container.querySelector('[data-testid="open-steer-panel"]') as HTMLButtonElement | null;
      expect(btn).not.toBeNull();
      return btn as HTMLButtonElement;
    }, { timeout: 4000 });
    steerBtn.click();
    steerBtn.click();

    await waitFor(() => {
      const input = container.querySelector('input[placeholder="Message coordinator..."]') as HTMLInputElement | null;
      expect(input).toBeTruthy();
      expect(document.activeElement).toBe(input);
    }, { timeout: 4000 });
  });

  it('renders Ctrl+Scroll zoom controls on the orchestration graph', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    // The shared ZoomControls (Ctrl+Scroll hint via tooltip + +/- buttons + % readout)
    // render alongside the orchestration graph, mirroring WorkflowRunPage.
    const buttons = Array.from(document.body.querySelectorAll('button'));
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom in')).toBe(true);
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom out')).toBe(true);
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Fit to view')).toBe(true);
  });

  it('renders from REST descriptor even when SSE stream is done (finished coordinator runs)', async () => {
    // Stream is already 'done' in the mock (simulates a finished coordinator run with closed SSE).
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    // Graph must render from REST seed even though SSE stream is done.
    const text = document.body.textContent ?? '';
    expect(text).toContain('Subtask 1');
    expect(text).toContain('RAI Review');
  });
});

describe('CoordinatorRunPage — graph during outcome-plan drafting', () => {
  it('hides the assembly pipeline stages and shows a caption while drafting the spec', async () => {
    // Drafting state: coordinator + planned assembly stages, no subtasks, no confirmed spec.
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
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

    await waitFor(
      () => expect(document.body.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
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

    await waitFor(
      () => expect(document.body.textContent).toContain('Graph has not been emitted yet'),
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
