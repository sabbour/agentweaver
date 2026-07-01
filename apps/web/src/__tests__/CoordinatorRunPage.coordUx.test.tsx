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
    getTeam: vi.fn().mockResolvedValue({ members: [] }),
    setAutopilot: vi.fn(),
    setAutoApprove: vi.fn(),
    retryRun: vi.fn(),
    getRunFiles: vi.fn().mockResolvedValue([]),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileDiff: vi.fn().mockResolvedValue(null),
    getAssemblyFiles: vi.fn().mockResolvedValue([]),
    getAssemblyWorkspace: vi.fn().mockResolvedValue([]),
    getAssemblyFileDiff: vi.fn().mockResolvedValue(null),
  },
}));

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => mockNavigate };
});

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
  mockNavigate.mockReset();
  currentEvents = [];
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getRun).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
  vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
  vi.mocked(apiClient.answerQuestion).mockResolvedValue({ run_id: 'child-run-2', request_id: 'q-1', answered: true });
  vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: true });
  vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });
});

afterEach(() => cleanup());

describe('CoordinatorRunPage — session run (issue 6)', () => {
  it('renders the coordinator event stream using the standard run timeline', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.started', payload: { goal: 'Build it', timestamp_utc: '2026-06-18T15:00:00Z' } },
      { sequence: 2, type: 'agent.message', payload: { messageId: 'm1', content: 'Decomposing the outcome into subtasks.', timestamp_utc: '2026-06-18T15:00:05Z' } },
      { sequence: 3, type: 'subtask.dispatched', payload: { subtaskId: 1, timestamp_utc: '2026-06-18T15:00:10Z' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Decomposing the outcome into subtasks.'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // The session now reuses the standard rich run Timeline over the coordinator's own stream,
    // so the coordinator's agent messages render like every other agent's "view run".
    expect(text).toContain('Decomposing the outcome into subtasks.');
    // Steering lives in the graph toolbar (collapsed to a single Send action + Stop).
    expect(text).toContain('Steer coordinator:');
    // Compact automation toolbar (no oversized session panel/heading).
    expect(text).toContain('Autopilot');
  });

  it('hides the steering toolbar once the orchestration is complete (no Stop on a finished run)', async () => {
    // Regression: a terminal orchestration cannot be steered or stopped, so the whole "Steer
    // coordinator:" toolbar (incl. the Stop button) must disappear once the run completes.
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_completed', payload: { timestamp_utc: new Date().toISOString() } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator Graph'),
      { timeout: 4000 },
    );
    const text = document.body.textContent ?? '';
    expect(text).not.toContain('Steer coordinator:');
  });
});

describe('CoordinatorRunPage — work-plan rendering (no raw JSON dump)', () => {
  it('suppresses the decompose agent serialized work-plan JSON message but keeps the structured chip', async () => {
    // The decompose agent streams its final assistant message (the SERIALIZED work plan) onto the
    // coordinator stream, and the orchestrator emits coordinator.work_plan. The raw JSON array must
    // NOT be dumped verbatim into the timeline; only the structured "Decomposed into N subtasks"
    // chip (+ work-plan panel/graph) should surface.
    const planJson = JSON.stringify([
      { title: 'Audit the repo', scope: 'Read src/ and list issues', role: 'Repo Auditor', depends_on: [] },
      { title: 'Apply fixes', scope: 'Patch the flagged files', role: 'Engineer', depends_on: [1] },
    ]);

    currentEvents = [
      { sequence: 1, type: 'coordinator.started', payload: { goal: 'Build it', timestamp_utc: '2026-06-18T15:00:00Z' } },
      { sequence: 2, type: 'agent.turn.start', payload: { turnId: 't1', timestamp_utc: '2026-06-18T15:00:01Z' } },
      { sequence: 3, type: 'agent.message', payload: { messageId: 'plan-msg', content: planJson, timestamp_utc: '2026-06-18T15:00:05Z' } },
      { sequence: 4, type: 'agent.turn.end', payload: { turnId: 't1', timestamp_utc: '2026-06-18T15:00:06Z' } },
      { sequence: 5, type: 'coordinator.work_plan', payload: { workPlanId: 1, subtasks: [{}, {}], timestamp_utc: '2026-06-18T15:00:07Z' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // The structured affordance is present...
    await waitFor(
      () => expect(document.body.textContent).toContain('Decomposed into 2 subtasks'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // ...and the raw JSON blob is NOT rendered verbatim anywhere in the timeline.
    expect(text).not.toContain('"depends_on"');
    expect(text).not.toContain('"scope"');
    expect(text).not.toContain(planJson);
  });

  it('still renders ordinary coordinator agent prose (only the serialized plan is suppressed)', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.started', payload: { goal: 'Build it', timestamp_utc: '2026-06-18T15:00:00Z' } },
      { sequence: 2, type: 'agent.turn.start', payload: { turnId: 't1', timestamp_utc: '2026-06-18T15:00:01Z' } },
      { sequence: 3, type: 'agent.message', payload: { messageId: 'm1', content: 'Decomposing the outcome into subtasks.', timestamp_utc: '2026-06-18T15:00:05Z' } },
      { sequence: 4, type: 'agent.turn.end', payload: { turnId: 't1', timestamp_utc: '2026-06-18T15:00:06Z' } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Decomposing the outcome into subtasks.'),
      { timeout: 4000 },
    );
  });
});

describe('CoordinatorRunPage — assembly review affordance (issues 3 & 4)', () => {
  it('shows the assembling spinner state', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_started', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Assembling collective output'),
      { timeout: 4000 },
    );
  });

  it('reuses the standard review bar (Commit and Merge / Change / Decline) when review is requested', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_review_requested', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Your review is pending'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // The collective output is reviewed through the SAME artifact-browser review bar as a normal run.
    expect(text).toContain('Commit and Merge');
    expect(text).toContain('Change');
    expect(text).toContain('Decline');
  });

  it('marks the Human Review topology gate as action-required ("Awaiting your review") when review is requested', async () => {
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_review_requested', payload: {} },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Awaiting your review'),
      { timeout: 4000 },
    );
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
    expect(text).toContain('Use the controls below to redirect the coordinator');
  });

  it('explains an integration_conflict block and lists the conflicting files', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_blocked',
        payload: { reason: 'integration_conflict', conflictingFiles: ['src/a.txt', 'src/shared/util.ts'] },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('changed the same lines'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // The blocked card replaces the raw reason code with a human explanation and surfaces the files.
    expect(text).toContain('could not be combined automatically');
    expect(text).toContain('src/a.txt');
    expect(text).toContain('src/shared/util.ts');
  });

  it('lists the blocking subtasks (title, agent, status badge) for an ineligible_subtasks block', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_blocked',
        payload: {
          reason: 'ineligible_subtasks',
          ineligibleSubtasks: [
            { id: 42, title: 'Validate implementation', status: 'rai_flagged', agent: 'Cobb' },
          ],
        },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Validate implementation'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // The all-or-nothing explanation replaces the bare reason code.
    expect(text).toContain('there is no partial assembly');
    // The blocking subtask row surfaces title, agent, status badge, and a per-status hint.
    expect(text).toContain('Validate implementation');
    expect(text).toContain('Cobb');
    expect(text).toContain('RAI-flagged');
    expect(text).toContain('RAI flagged this subtask');
    // The bare reason code is not shown verbatim.
    expect(text).not.toContain('assembly_blocked: ineligible_subtasks');
  });

  it('normalizes the run-result reason prefix ("assembly_blocked: ineligible_subtasks")', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_blocked',
        payload: {
          reason: 'assembly_blocked: ineligible_subtasks',
          ineligibleSubtasks: [
            { id: 7, title: 'Build the API', status: 'failed', agent: 'Mal' },
          ],
        },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Build the API'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('there is no partial assembly');
    expect(text).toContain('Failed');
    expect(text).toContain('This subtask failed');
  });

  it('falls back to the single-message blocked panel when ineligibleSubtasks is absent', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_blocked',
        payload: { reason: 'ineligible_subtasks' },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('there is no partial assembly'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // The panel still renders with the steer controls and does not crash.
    expect(text).toContain('Use the controls below to redirect the coordinator');
  });

  it('shows an immediate waiting indicator after sending steering from the blocked panel', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_blocked',
        payload: { reason: 'integration_conflict', conflictingFiles: ['src/a.txt'] },
      },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('could not be combined automatically'),
      { timeout: 4000 },
    );

    fireEvent.click(document.body.querySelector('[data-testid="steer-panel-send"]') as Element);

    await waitFor(
      () => expect(document.body.textContent).toContain('Message sent — waiting for coordinator response.'),
      { timeout: 4000 },
    );
  });

  it('shows a live count-up timer on the Merge stage once it has started', async () => {
    // The Merge stage has no distinct orchestration phase, so its timer must come from the
    // assembly_merge_started timestamp_utc. Started ~2m5s ago → "2m 5s".
    const startedIso = new Date(Date.now() - 125_000).toISOString();
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_merge_started', payload: { workPlanId: 1, timestamp_utc: startedIso } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toMatch(/2m \d+s/),
      { timeout: 4000 },
    );
  });

  it('clears the Human Review "Awaiting your review" state once the review is approved and merge has begun', async () => {
    // Regression: the orchestration phase lingers on `in_review` after the user approves, while
    // merge/scribe run. Timing (review_approved → review.completedAt) must win so the gate no longer
    // shows action-required. The review node should read "Reviewed", not "Awaiting your review".
    const t0 = new Date(Date.now() - 120_000).toISOString();
    const t1 = new Date(Date.now() - 90_000).toISOString();
    const t2 = new Date(Date.now() - 60_000).toISOString();
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_review_requested', payload: { timestamp_utc: t0 } },
      { sequence: 2, type: 'coordinator.assembly_review_approved', payload: { timestamp_utc: t1 } },
      { sequence: 3, type: 'coordinator.assembly_merge_started', payload: { workPlanId: 1, timestamp_utc: t2 } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Reviewed'),
      { timeout: 4000 },
    );
    expect(document.body.textContent ?? '').not.toContain('Awaiting your review');
  });

  it('opens the Scribe sub-run stream in a dialog from the assembly "View execution" button', async () => {
    // Regression: assembly Scribe/RAI own a real persisted sub-run stream (`${runId}-scribe`),
    // so "View execution" must open it in the RunWatcher dialog (surfacing the actual memory work),
    // not merely scroll to the high-level coordinator timeline.
    const startedIso = new Date(Date.now() - 30_000).toISOString();
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_scribe_started', payload: { workPlanId: 1, timestamp_utc: startedIso } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const btn = await waitFor(() => {
      const els = Array.from(document.querySelectorAll('button'))
        .filter(b => b.textContent === 'View execution');
      expect(els.length).toBeGreaterThan(0);
      return els[els.length - 1];
    }, { timeout: 4000 });

    fireEvent.click(btn);

    await waitFor(
      () => expect(document.body.textContent).toContain('Scribe documentation (collective assembly)'),
      { timeout: 4000 },
    );
  });

  it('Merge "Browse files" routes to Workspace with the integration branch selected', async () => {
    // Regression: "Browse files" must leave the orchestration page and land in the project Workspace
    // with enough context to preserve refresh/back behavior for the assembled integration branch.
    const t0 = new Date(Date.now() - 60_000).toISOString();
    currentEvents = [
      { sequence: 1, type: 'coordinator.assembly_merge_started', payload: { workPlanId: 1, timestamp_utc: t0 } },
      { sequence: 2, type: 'coordinator.assembly_merge_completed', payload: { workPlanId: 1, timestamp_utc: new Date().toISOString() } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const btn = await waitFor(() => {
      const el = Array.from(document.querySelectorAll('button'))
        .find(b => b.textContent?.includes('Browse files'));
      expect(el).toBeTruthy();
      return el as HTMLButtonElement;
    }, { timeout: 4000 });

    fireEvent.click(btn);

    expect(mockNavigate).toHaveBeenCalledWith(
      '/projects/p1/workspace?run=coord-run-1&ref=agentweaver%2Fintegration%2Fcoord-run-1',
    );
    expect(apiClient.getAssemblyWorkspace).not.toHaveBeenCalled();
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

  it('exposes a visible info affordance explaining what Autopilot does', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'running', parent_run_id: null,
      autopilot: true, auto_approve_tools: false,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    // The (i) info button is rendered next to the toggle (not a hover-only tooltip).
    const infoButton = await waitFor(() => {
      const btn = container.querySelector('button[aria-label="About Autopilot"]') as HTMLButtonElement | null;
      expect(btn).not.toBeNull();
      return btn as HTMLButtonElement;
    }, { timeout: 4000 });

    // Opening it reveals the explanation copy.
    fireEvent.click(infoButton);
    await waitFor(
      () => expect(document.body.textContent).toContain("Auto-answers the coordinator"),
      { timeout: 4000 },
    );
  });
});

describe('CoordinatorRunPage — collapsed steer bar', () => {
  it('exposes Send, Redirect, and Amend actions plus Stop and the broadcast scope note', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Steer coordinator:'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Applies to all active subtasks.');

    const buttons = Array.from(container.querySelectorAll('button')).map((b) => (b.textContent ?? '').trim());
    // All three steering verbs are distinctly selectable, plus Stop.
    expect(buttons.some((t) => t === 'Send')).toBe(true);
    expect(buttons.some((t) => t === 'Redirect')).toBe(true);
    expect(buttons.some((t) => t === 'Amend')).toBe(true);
    expect(buttons.some((t) => t.includes('Stop'))).toBe(true);
  });

  it('Send sends with kind=send and surfaces the queued/applied confirmation', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'queued' });

    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Steer coordinator:'),
      { timeout: 4000 },
    );

    const input = container.querySelector('input[type=text]') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'Target the v2 API instead.' } });

    const sendBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => (b.textContent ?? '').trim() === 'Send',
    ) as HTMLButtonElement;
    fireEvent.click(sendBtn);

    await waitFor(
      () => expect(vi.mocked(apiClient.steerCoordinator)).toHaveBeenCalledWith(
        'coord-run-1',
        { kind: 'send', instruction: 'Target the v2 API instead.' },
      ),
      { timeout: 4000 },
    );
    await waitFor(
      () => expect(document.body.textContent).toContain('Queued — applies at the next step.'),
      { timeout: 4000 },
    );
  });

  it('exposes a visible info affordance explaining what Send does', async () => {
    const { container } = render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const infoButton = await waitFor(() => {
      const btn = container.querySelector('button[aria-label="About steering the coordinator"]') as HTMLButtonElement | null;
      expect(btn).not.toBeNull();
      return btn as HTMLButtonElement;
    }, { timeout: 4000 });

    fireEvent.click(infoButton);
    await waitFor(
      () => expect(document.body.textContent).toContain('Sends a course-correction to the coordinator'),
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

describe('CoordinatorRunPage — Retry button (retrigger)', () => {
  it('shows Retry button when runLevelStatus is failed', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'failed', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.querySelector('[data-testid="coordinator-retry-button"]')).toBeTruthy(),
      { timeout: 4000 },
    );
  });

  it('shows Retry button when runLevelStatus is merge_failed', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'merge_failed', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.querySelector('[data-testid="coordinator-retry-button"]')).toBeTruthy(),
      { timeout: 4000 },
    );
  });

  it('does NOT show Retry button for in_progress runs', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'in_progress', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Orchestration'),
      { timeout: 4000 },
    );
    expect(document.body.querySelector('[data-testid="coordinator-retry-button"]')).toBeNull();
  });

  it('clicking Retry calls apiClient.retryRun and navigates to the new run', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'coord-run-1', status: 'failed', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
    vi.mocked(apiClient.retryRun).mockResolvedValue({
      run_id: 'new-coord-run-2',
      retried_from: 'coord-run-1',
      status: 'in_progress',
    });

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const btn = await waitFor(
      () => {
        const el = document.body.querySelector('[data-testid="coordinator-retry-button"]');
        expect(el).toBeTruthy();
        return el as HTMLButtonElement;
      },
      { timeout: 4000 },
    );

    fireEvent.click(btn);

    await waitFor(
      () => expect(vi.mocked(apiClient.retryRun)).toHaveBeenCalledWith('coord-run-1'),
      { timeout: 4000 },
    );
    await waitFor(
      () => expect(mockNavigate).toHaveBeenCalledWith('/projects/p1/orchestrations/new-coord-run-2'),
      { timeout: 4000 },
    );
  });
});
