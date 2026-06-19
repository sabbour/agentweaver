import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { RunStreamEvent } from '../api/sse';

// ResizeObserver is required by @xyflow/react and absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

// Mutable event list so each test can drive the coordinator stream.
let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    steerCoordinator: vi.fn(),
    reviewAssembly: vi.fn(),
    answerQuestion: vi.fn(),
    getRun: vi.fn(),
    getOutcomeSpec: vi.fn(),
    setAutopilot: vi.fn(),
    setAutoApprove: vi.fn(),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, status: 'done', error: null, reconnect: vi.fn() }),
}));

// OutcomeSpecPanel performs its own fetch; stub it so it renders nothing.
vi.mock('../components/OutcomeSpecPanel', () => ({
  OutcomeSpecPanel: () => null,
}));

import { apiClient } from '../api/apiClient';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { coordinatorLoopbackLabel } from '../components/WorkflowGraphPanel';
import { COORDINATOR_GRAPH_DESCRIPTOR, COORDINATOR_GRAPH_DESCRIPTOR_LOOPBACKS } from './fixtures/graphDescriptor';

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
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getRun).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
  vi.mocked(apiClient.answerQuestion).mockResolvedValue({ run_id: 'child-run-2', request_id: 'q-1', answered: true });
  vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: true });
  vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });
});

afterEach(() => cleanup());

describe('CoordinatorRunPage — session timeline (issue 6)', () => {
  it('renders chronological milestones from the coordinator event stream', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.started', payload: { goal: 'Build it', timestamp_utc: '2026-06-18T15:00:00Z' } },
      { sequence: 2, type: 'coordinator.work_plan', payload: { subtasks: [{}, {}], timestamp_utc: '2026-06-18T15:00:05Z' } },
      { sequence: 3, type: 'subtask.dispatched', payload: { subtaskId: 1, timestamp_utc: '2026-06-18T15:00:10Z' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator session'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Coordinator started');
    expect(text).toContain('Work plan ready');
    expect(text).toContain('Subtask 1 dispatched');
    // Steering chat box is visible on the page (not only in the dialog).
    expect(text).toContain('Steer the coordinator');
  });
});

describe('CoordinatorRunPage — assembly review affordance (issues 3 & 4)', () => {
  it('shows the assembling spinner state', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_assembling', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Assembling collective output'),
      { timeout: 4000 },
    );
  });

  it('shows the Approve / Request changes / Decline panel when review is requested', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_review_requested', payload: { diff: '--- a\n+++ b\n+integration' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Assembly review'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Approve');
    expect(text).toContain('Request changes');
    expect(text).toContain('Decline');
    // The integration diff/summary is surfaced.
    expect(text).toContain('+integration');
  });

  it('shows a human-readable reason (not a bare Failed) when assembly fails', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_failed', payload: { reason: 'merge conflict in src/app.ts' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('merge conflict in src/app.ts'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('parked');
  });
});

describe('CoordinatorRunPage — parent aggregate elapsed (issue 2)', () => {
  it('shows the sum of child pipeline step durations on the expanded subtask card', async () => {
    // Child stream events (same mock feeds every useRunStream): agent 30s + rai 10s = 40s.
    currentEvents = [
      { sequence: 1, type: 'workflow.step', payload: { step: 'agent', status: 'started', timestamp_utc: '2026-06-18T15:00:10Z' } },
      { sequence: 2, type: 'workflow.step', payload: { step: 'agent', status: 'completed', timestamp_utc: '2026-06-18T15:00:40Z' } },
      { sequence: 3, type: 'workflow.step', payload: { step: 'rai', status: 'started', timestamp_utc: '2026-06-18T15:00:40Z' } },
      { sequence: 4, type: 'workflow.step', payload: { step: 'rai', status: 'completed', timestamp_utc: '2026-06-18T15:00:50Z' } },
    ];

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Expand pipeline'),
      { timeout: 4000 },
    );

    const expandBtn = Array.from(container.querySelectorAll('button')).find(
      (btn) => btn.textContent?.includes('Expand pipeline'),
    );
    expect(expandBtn).toBeTruthy();
    expandBtn!.click();

    await waitFor(
      () => expect(document.body.querySelector('[aria-label="Total child elapsed"]')).toBeTruthy(),
      { timeout: 4000 },
    );

    const aggregate = document.body.querySelector('[aria-label="Total child elapsed"]');
    expect(aggregate?.textContent).toContain('40s');
  });
});

describe('CoordinatorRunPage — coordinator topology loopback labels', () => {
  it('derives role-based labels for the coordinator loopback back-edges', () => {
    // The renderer derives a loopback label from the SOURCE node's role, robust across id schemes.
    expect(coordinatorLoopbackLabel('rai', 'planned:assembly-rai')).toBe('RAI flags');
    expect(coordinatorLoopbackLabel('review', 'planned:assembly-review')).toBe('Request changes');
    // Id-based fallback when role is absent/generic.
    expect(coordinatorLoopbackLabel(undefined, 'rai-gate')).toBe('RAI flags');
    expect(coordinatorLoopbackLabel('', 'human-review-node')).toBe('Request changes');
    // Unknown source → generic label (never empty), so the back-edge is still visibly labelled.
    expect(coordinatorLoopbackLabel('coordinator', 'coordinator')).toBe('Rework');
  });

  it('builds the two labelled loopback edges from the coordinator descriptor when present', () => {
    const labels = COORDINATOR_GRAPH_DESCRIPTOR_LOOPBACKS.edges
      .filter((e) => e.loopback)
      .map((e) => {
        const role = COORDINATOR_GRAPH_DESCRIPTOR_LOOPBACKS.nodes.find((n) => n.id === e.from)?.role;
        return coordinatorLoopbackLabel(role, e.from);
      });
    expect(labels).toEqual(['RAI flags', 'Request changes']);
  });

  it('produces no loopback edges when the descriptor has zero loopbacks (older runs)', () => {
    const loopbacks = COORDINATOR_GRAPH_DESCRIPTOR.edges.filter((e) => e.loopback);
    expect(loopbacks).toHaveLength(0);
  });
});

describe('CoordinatorRunPage — bubbled child questions & approvals', () => {
  it('renders a child question answer box and routes the answer to the childRunId', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.child_question',
        payload: { childRunId: 'child-run-2', subtaskId: '2', requestId: 'q-1', question: 'Which database should I target?' },
      },
    ];

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Which database should I target?'),
      { timeout: 4000 },
    );
    // Provenance label identifies the source subtask.
    expect(document.body.textContent).toContain('Subtask 2');

    const textarea = container.querySelector('textarea[placeholder="Type your answer…"]') as HTMLTextAreaElement;
    expect(textarea).toBeTruthy();
    fireEvent.change(textarea, { target: { value: 'Use Postgres' } });

    const submit = Array.from(container.querySelectorAll('button')).find((b) => b.textContent?.includes('Submit answer'));
    expect(submit).toBeTruthy();
    submit!.click();

    await waitFor(
      () => expect(vi.mocked(apiClient.answerQuestion)).toHaveBeenCalled(),
      { timeout: 4000 },
    );
    // The answer is POSTed against the CHILD run id, not the coordinator run id.
    expect(vi.mocked(apiClient.answerQuestion)).toHaveBeenCalledWith('child-run-2', 'q-1', 'Use Postgres');
  });

  it('collapses an already-answered child question to a muted state', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.child_question',
        payload: { childRunId: 'child-run-2', subtaskId: '2', requestId: 'q-1', question: 'Which database?' },
      },
      {
        sequence: 2,
        type: 'agent.question_answered',
        payload: { requestId: 'q-1', answer: 'Postgres', timedOut: false },
      },
    ];

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Question answered'),
      { timeout: 4000 },
    );
    // Collapsed: no answer input remains.
    expect(container.querySelector('textarea[placeholder="Type your answer…"]')).toBeNull();
    expect(document.body.textContent).toContain('Postgres');
  });

  it('renders a child approval request as a tool-approval card scoped to the child', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.child_approval_required',
        payload: { childRunId: 'child-run-1', subtaskId: '1', requestId: 'a-1', toolName: 'fetch_url', url: 'https://example.com', message: 'Worker wants to fetch a URL.' },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Tool Approval Required'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Subtask 1');
    expect(text).toContain('fetch_url');
    expect(text).toContain('Allow once');
  });
});

describe('CoordinatorRunPage — automation toggles (autopilot + auto-approve)', () => {
  it('seeds both toggles from the coordinator run and flips them via the right endpoints', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'running', parent_run_id: null,
      autopilot: true, auto_approve_tools: false,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
    vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: false });
    vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // Autopilot (first switch) seeded checked, auto-approve (second switch) seeded unchecked.
    await waitFor(() => {
      const boxes = Array.from(container.querySelectorAll('input[type=checkbox]')) as HTMLInputElement[];
      expect(boxes.length).toBeGreaterThanOrEqual(2);
      expect(boxes[0].checked).toBe(true);
      expect(boxes[1].checked).toBe(false);
    }, { timeout: 4000 });

    const boxes = () => Array.from(container.querySelectorAll('input[type=checkbox]')) as HTMLInputElement[];

    // Flip autopilot (first) off.
    fireEvent.click(boxes()[0]);
    await waitFor(
      () => expect(vi.mocked(apiClient.setAutopilot)).toHaveBeenCalledWith('coord-run-1', false),
      { timeout: 4000 },
    );

    // Flip auto-approve (second) on.
    fireEvent.click(boxes()[1]);
    await waitFor(
      () => expect(vi.mocked(apiClient.setAutoApprove)).toHaveBeenCalledWith('coord-run-1', true),
      { timeout: 4000 },
    );
  });
});

describe('CoordinatorRunPage — automation audit lines', () => {
  it('renders muted audit lines for tool.auto_approved and coordinator.autopilot_answered', async () => {
    currentEvents = [
      { sequence: 1, type: 'tool.auto_approved', payload: { requestId: 'a-1', toolName: 'fetch_url', url: 'https://example.com' } },
      { sequence: 2, type: 'coordinator.autopilot_answered', payload: { runId: 'coord-run-1', childRunId: 'child-run-2', requestId: 'q-1', question: 'Which region?', answer: 'westus2' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text).toContain('Tool auto-approved: fetch_url');
      expect(text).toContain('Autopilot answered');
      expect(text).toContain('Which region?');
      expect(text).toContain('westus2');
    }, { timeout: 4000 });
  });
});


