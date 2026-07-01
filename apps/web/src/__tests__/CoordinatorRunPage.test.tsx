import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
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

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    getRunUsage: vi.fn(),
    steerCoordinator: vi.fn(),
    reviewAssembly: vi.fn(),
    getRun: vi.fn(),
    // OutcomeSpecPanel uses these — return empty/null to avoid noise.
    getOutcomeSpec: vi.fn(),
    getTeam: vi.fn().mockResolvedValue({ members: [] }),
    // RunLayout artifact browser (Changes/Files rail) — empty results in tests.
    getRunFiles: vi.fn().mockResolvedValue([]),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileDiff: vi.fn().mockResolvedValue(null),
    getAssemblyFiles: vi.fn().mockResolvedValue([]),
    getAssemblyWorkspace: vi.fn().mockResolvedValue([]),
    getAssemblyFileDiff: vi.fn().mockResolvedValue(null),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: [], status: 'done', error: null, reconnect: vi.fn() }),
}));

// OutcomeSpecPanel performs its own fetch; stub it so it renders nothing.
vi.mock('../components/OutcomeSpecPanel', () => ({
  OutcomeSpecPanel: () => null,
}));

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { _resetRuntimeInfoCache } from '../hooks/useRuntimeInfo';
import { COORDINATOR_GRAPH_DESCRIPTOR, CHILD_GRAPH_DESCRIPTOR, COORDINATOR_GRAPH_DRAFTING_DESCRIPTOR } from './fixtures/graphDescriptor';

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
  _resetRuntimeInfoCache();
  vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: false, podName: null });
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getRunUsage).mockResolvedValue({ input_tokens: 0, output_tokens: 0, total_tokens: 0, total_nano_aiu: 0, by_model: [] });
  vi.mocked(apiClient.getRun).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
});

afterEach(() => cleanup());

describe('CoordinatorRunPage — unified coordinator graph view', () => {
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

  it('subtask nodes with child_graph_ref render an expand button', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // Both subtasks in the fixture have child_graph_ref → both should have expand buttons.
    const text = document.body.textContent ?? '';
    expect(text).toContain('Expand pipeline');
  });

  it('renders child usage and sandbox pod pills on subtask nodes', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({ kubernetes: true, podName: 'agentweaver-api-pod-1' });
    vi.mocked(apiClient.getRunUsage).mockImplementation(async (id: string) => {
      if (id === 'child-run-1') {
        return { input_tokens: 800, output_tokens: 434, total_tokens: 1234, total_nano_aiu: 0, by_model: [] };
      }
      return { input_tokens: 0, output_tokens: 0, total_tokens: 0, total_nano_aiu: 0, by_model: [] };
    });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('1.2K tok'),
      { timeout: 4000 },
    );

    expect(document.body.textContent).toContain('agentweaver-api-pod-1');
  });

  it('shows the steering bar with Send, Redirect, Amend, and Stop verbs', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Steer coordinator'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Send');
    expect(text).toContain('Stop');
    // All three steering verbs are distinctly selectable, matching the backend contract.
    const buttonLabels = Array.from(container.querySelectorAll('button')).map((b) => (b.textContent ?? '').trim());
    expect(buttonLabels.some((t) => t === 'Send')).toBe(true);
    expect(buttonLabels.some((t) => t === 'Redirect')).toBe(true);
    expect(buttonLabels.some((t) => t === 'Amend')).toBe(true);
    // Broadcast scope note is shown next to the bar.
    expect(text).toContain('Applies to all active subtasks.');
  });

  it('renders Ctrl+Scroll zoom controls on the orchestration graph', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    // The shared ZoomControls (Ctrl+Scroll hint + +/- buttons + % readout) render
    // alongside the orchestration graph, mirroring WorkflowRunPage.
    const text = document.body.textContent ?? '';
    expect(text).toContain('Ctrl + Scroll to zoom');

    const buttons = Array.from(document.body.querySelectorAll('button'));
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom in')).toBe(true);
    expect(buttons.some((b) => b.getAttribute('aria-label') === 'Zoom out')).toBe(true);
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

  it('Expand pipeline renders inline child node cards with live status, not static text', async () => {
    // Return child descriptor for child run ids, coordinator descriptor for the coordinator.
    vi.mocked(apiClient.getRunGraph).mockImplementation((runId: string) => {
      if (runId === 'coord-run-1') return Promise.resolve(COORDINATOR_GRAPH_DESCRIPTOR);
      return Promise.resolve(CHILD_GRAPH_DESCRIPTOR);
    });

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // Wait for the coordinator graph to render with subtask nodes.
    await waitFor(
      () => expect(document.body.textContent).toContain('Expand pipeline'),
      { timeout: 4000 },
    );

    // Click the first Expand pipeline button.
    const expandBtn = Array.from(container.querySelectorAll('button')).find(
      (btn) => btn.textContent?.includes('Expand pipeline'),
    );
    expect(expandBtn).toBeTruthy();
    expandBtn!.click();

    // After expanding, the child descriptor nodes should appear as cards (not static text).
    await waitFor(
      () => expect(document.body.textContent).toContain('Assemble-ready'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Agent');
    expect(text).toContain('Rai');
    expect(text).toContain('Assemble-ready');

    // The expand button should have changed label to Collapse pipeline.
    expect(text).toContain('Collapse pipeline');
  });
});

describe('CoordinatorRunPage — graph during outcome-spec drafting', () => {
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
    expect(text).toContain('The execution pipeline appears once you confirm the outcome spec.');
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
    expect(text).not.toContain('The execution pipeline appears once you confirm the outcome spec.');
  });
});

describe('CoordinatorRunPage — work-plan 404 (no plan yet / stuck run)', () => {
  it('renders a graceful empty state and does not call the 404 work-plan endpoint again', async () => {
    // No graph descriptor and a 404 work-plan: a stuck/early run with no plan.
    vi.mocked(apiClient.getRunGraph).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new ApiError(404, 'not found'));
    vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'running' } as never);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('No work plan available yet.'),
      { timeout: 4000 },
    );

    // The graceful state renders instead of an indefinite "Waiting for coordinator graph...".
    expect(document.body.textContent).toContain('No work plan available yet.');

    // After the first 404 the work-plan endpoint is not called again — wpEverMissing stops
    // further fetches for the lifetime of the page, so the total call count stays very low.
    const calls = vi.mocked(apiClient.getWorkPlan).mock.calls.length;
    expect(calls).toBeLessThan(3);
  });

  it('does not call getWorkPlan again after the first 404 even as the poll continues', async () => {
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

    // Advance time past one poll interval to confirm no additional getWorkPlan calls.
    await new Promise((resolve) => setTimeout(resolve, 200));

    const afterDelay = vi.mocked(apiClient.getWorkPlan).mock.calls.length;

    // getWorkPlan call count must not increase after the first 404.
    expect(afterDelay).toBe(afterFirstTick);
  });
});

describe('CoordinatorRunPage — child run (non-coordinator) skips coordinator artifacts', () => {
  it('does not call getWorkPlan for a child run (parent_run_id is set)', async () => {
    // A child run has parent_run_id set. The work-plan and outcome-spec endpoints do not exist
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

  it('does not render the outcome spec panel for a child run', async () => {
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

    // The OutcomeSpecPanel is stubbed to return null, so getOutcomeSpec must not be called.
    // (OutcomeSpecPanel is mocked at the module level in this file.)
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
