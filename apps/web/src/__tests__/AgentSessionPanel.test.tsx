import userEvent from '@testing-library/user-event';
import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AgentSessionPanel } from '../components/AgentSessionPanel';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { RunStreamEvent } from '../api/sse';
import type { RunDetail } from '../api/types';
import type { RunSessionTree } from '../components/AgentSessionPanel';
import type { ReactNode } from 'react';
let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRun: vi.fn().mockResolvedValue({
      run_id: 'child-run-1',
      status: 'in_progress',
      started_at: new Date().toISOString(),
    }),
    getRunEvents: vi.fn().mockResolvedValue([]),
    getRunFiles: vi.fn().mockResolvedValue([]),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileContent: vi.fn().mockResolvedValue({ path: 'file.txt', content: '', is_binary: false, language: 'text' }),
    getRunFileDiff: vi.fn().mockResolvedValue(null),
    getOutcomeSpec: vi.fn().mockResolvedValue({
      status: 'confirmed',
      desiredOutcome: 'Ship the preview polish updates.',
      scope: ['UI-only fixes'],
      confirmedBy: 'Ahmed',
    }),
    confirmOutcomeSpec: vi.fn(),
    reviseOutcomeSpec: vi.fn(),
    decomposeSpec: vi.fn(),
    steerCoordinator: vi.fn().mockResolvedValue({ status: 'applied' }),
    approveTool: vi.fn().mockResolvedValue(undefined),
    denyTool: vi.fn().mockResolvedValue(undefined),
    approveShell: vi.fn().mockResolvedValue(undefined),
    denyShell: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, status: 'done', error: null, reconnect: vi.fn() }),
}));

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        {children}
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

const tree: RunSessionTree[] = [
  {
    nodeId: 'coordinator',
    label: 'Coordinator',
    status: 'running',
    depth: 0,
    children: [
      {
        nodeId: 'subtask-1',
        label: 'Subtask 1',
        agentName: 'Worker',
        agentRole: 'Researcher',
        status: 'running',
        childRunId: 'child-run-1',
        depth: 1,
        children: [],
      },
    ],
  },
];

beforeEach(() => {
  vi.clearAllMocks();
  currentEvents = [];
});

afterEach(() => cleanup());

describe('AgentSessionPanel', () => {
  it('renders a child run tool approval card in the session slide-up and posts to the child run', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'tool.approval_required',
        payload: { requestId: 'approval-1', toolName: 'web_fetch', url: 'https://example.com' },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Tool Approval Required')).toBeDefined(), { timeout: 4000 });
    // Approvals render via the native ApprovalGate primitive (components/ui/agentic),
    // not the legacy lifecycle card.
    expect(screen.getByTestId('session-approval-gate')).toBeDefined();
    expect(screen.getByRole('region', { name: 'Approval required' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Allow for session' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Always allow' })).toBeDefined();
    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));

    expect(vi.mocked(apiClient.approveTool)).toHaveBeenCalledWith('child-run-1', 'approval-1', 'once');
  });

  it('does not expose a standalone run page opener in the selected task header', async () => {
    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getAllByText('Subtask 1').length).toBeGreaterThan(0), { timeout: 4000 });
    expect(screen.queryByRole('button', { name: /open full run page/i })).toBeNull();
  });

  it('shows a completed platform gate status instead of an empty stream placeholder', async () => {
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'rai',
            label: 'Rai',
            agentName: 'Coordinator',
            agentRole: 'Risk gate',
            status: 'completed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(document.body.textContent).toContain('Rai completed'), { timeout: 4000 });
    expect(document.body.textContent).toContain('No chat messages were emitted for this completed platform gate.');
    expect(document.body.textContent).not.toContain('No streamed messages yet for this session.');
  });

  it('does not show coordinator decomposition messages as the RAI gate response', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'decompose' } },
      {
        sequence: 2,
        type: 'agent.message',
        payload: { content: '[{"title":"Raw decomposed task","scope":"Implement the task","role":"Engineer","depends_on":[]}]' },
      },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
      {
        sequence: 4,
        type: 'coordinator.work_plan',
        payload: { subtasks: [{ id: '1', title: 'Raw decomposed task', scope: 'Implement the task' }] },
      },
      { sequence: 5, type: 'coordinator.assembly_rai_started', payload: {} },
    ];
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'planned:assembly-rai',
            label: 'RAI Review',
            agentName: 'Coordinator',
            agentRole: 'Risk gate',
            status: 'running',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="planned:assembly-rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(document.body.textContent).toContain('Collective assembly: RAI check started.'), { timeout: 4000 });
    expect(document.body.textContent).not.toContain('Raw decomposed task');
    expect(document.body.textContent).not.toContain('Implement the task');
  });

  it('renders a red RAI verdict as an error state for a completed RAI gate', async () => {
    currentEvents = [
      {
        sequence: 10,
        type: 'rai.verdict',
        payload: {
          verdict: 'red',
          runId: 'coord-run-1',
          rationale: 'Safety policy blocked this output.',
        },
      },
    ];
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'planned:assembly-rai',
            label: 'RAI Review',
            agentName: 'Coordinator',
            agentRole: 'Risk gate',
            status: 'completed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="planned:assembly-rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    const verdictCard = await screen.findByTestId('rai-verdict-card', undefined, { timeout: 4000 });
    expect(verdictCard.getAttribute('data-intent')).toBe('error');
    expect(document.body.textContent).toContain('RAI verdict: 🔴 Red');
    expect(document.body.textContent).toContain('Safety policy blocked this output.');
    expect(document.body.textContent).not.toContain('Green');
    expect(document.body.textContent).not.toContain('success');
    expect(document.body.textContent).not.toContain('No streamed messages yet for this session.');
  });

  it('renders a revise RAI verdict as a warning state', async () => {
    currentEvents = [
      {
        sequence: 10,
        type: 'rai.verdict',
        payload: {
          verdict: 'revise',
          runId: 'coord-run-1',
          rationale: 'Revise the assembled response before continuing.',
        },
      },
    ];
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'planned:assembly-rai',
            label: 'RAI Review',
            agentName: 'Coordinator',
            agentRole: 'Risk gate',
            status: 'completed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="planned:assembly-rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    const verdictCard = await screen.findByTestId('rai-verdict-card', undefined, { timeout: 4000 });
    expect(verdictCard.getAttribute('data-intent')).toBe('warning');
    expect(document.body.textContent).toContain('RAI verdict: 🟡 Revise');
    expect(document.body.textContent).toContain('Revise the assembled response before continuing.');
    expect(document.body.textContent).not.toContain('No streamed messages yet for this session.');
  });

  it('omits empty RAI verdict rationale placeholders and hides system prompts on assembly gates', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'rai-turn' } },
      { sequence: 2, type: 'agent.system_prompt', payload: { prompt: 'Internal RAI reviewer system prompt' } },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
      {
        sequence: 4,
        type: 'rai.verdict',
        payload: { trafficLight: 'yellow', rationale: '---' },
      },
    ];
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'planned:assembly-rai',
            label: 'RAI Review',
            agentName: 'Coordinator',
            agentRole: 'RAI Reviewer',
            status: 'completed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          variant="docked"
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="planned:assembly-rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(document.body.textContent).toContain('RAI verdict: 🟡 Yellow'), { timeout: 4000 });
    expect(document.body.textContent).not.toContain('RAI verdict: 🟡 Yellow — ---');
    expect(document.body.textContent).not.toContain('System (Prompt)');
    expect(document.body.textContent).not.toContain('Internal RAI reviewer system prompt');
  });

  it('renders outcome-spec JSON as an outcome-plan message instead of a RAI reviewer response', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'outcome-turn' } },
      {
        sequence: 2,
        type: 'agent.message',
        payload: {
          content: JSON.stringify({
            desired_outcome: 'Ship a minimal preview app',
            scope: 'Implement only the web preview path.',
          }),
        },
      },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
    ];
    const gateTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'planned:assembly-rai',
            label: 'RAI Review',
            agentName: 'Coordinator',
            agentRole: 'RAI Reviewer',
            status: 'completed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={gateTree}
          selectedNodeId="planned:assembly-rai"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Outcome plan')).toBeDefined(), { timeout: 4000 });
    expect(screen.getByText(/Desired outcome/i)).toBeDefined();
    expect(screen.getByText(/Ship a minimal preview app/i)).toBeDefined();
    expect(screen.getByText(/Scope/i)).toBeDefined();
    expect(screen.getByText(/Implement only the web preview path/i)).toBeDefined();
    expect(document.body.textContent).not.toContain('desired_outcome');
    const timelineMessage = screen.getByText(/Ship a minimal preview app/i).closest('[data-testid="timeline-message"]') as HTMLElement;
    expect(timelineMessage).toBeTruthy();
    expect(timelineMessage.textContent).not.toContain('Coordinator (RAI Reviewer)');
  });

  it('explains assembly-requested revision cycles in the coordinator timeline with feedback', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.assembly_review_requested',
        payload: { gateKind: 'build-test' },
      },
      {
        sequence: 2,
        type: 'coordinator.assembly_changes_requested',
        payload: {
          redispatchSubtaskIds: [1, 2],
          redispatchedSubtaskIds: [1, 2],
          feedback: 'The preview server did not start.',
        },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(document.body.textContent).toContain('🔁 Build & Test requested changes → revising 2 subtasks'), { timeout: 4000 });
    expect(document.body.textContent).toContain('Feedback: The preview server did not start.');
  });

  it('removes duplicate outer chrome when the outcome-plan scope is selected', async () => {
    const outcomeTree: RunSessionTree[] = [
      {
        ...tree[0],
        children: [
          {
            nodeId: 'outcome-plan',
            label: 'Outcome plan',
            agentName: 'Coordinator',
            agentRole: 'Planning gate',
            roleKey: 'outcome_plan',
            status: 'confirmed',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={outcomeTree}
          selectedNodeId="outcome-plan"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Outcome plan confirmed by Ahmed. Dispatch is unblocked.')).toBeDefined(), { timeout: 4000 });
    expect(screen.queryByText('Outcome plan (Planning gate)')).toBeNull();
    expect(screen.queryByText('Context: Outcome plan')).toBeNull();
  });

  it('does not invent a just-started timestamp when restored run metadata lacks timing', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValueOnce({
      run_id: 'child-run-1',
      status: 'in_progress',
      started_at: null,
    } as never);

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Start time unavailable')).toBeDefined(), { timeout: 4000 });
    expect(document.body.textContent).not.toContain('Started just now');
  });

  it('provides a working jump-to-latest affordance for session messages', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.message.delta', payload: { delta: 'First message' } },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
      { sequence: 4, type: 'agent.turn.start', payload: { turnId: 't2' } },
      { sequence: 5, type: 'agent.message.delta', payload: { delta: 'Latest message' } },
      { sequence: 6, type: 'agent.turn.end', payload: {} },
    ];
    const originalScrollIntoView = Element.prototype.scrollIntoView;
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;

    try {
      render(
        <Wrapper>
          <AgentSessionPanel
            open
            onClose={vi.fn()}
            tree={tree}
            selectedNodeId="subtask-1"
            onSelectNode={vi.fn()}
            coordinatorRunId="coord-run-1"
            projectId="p1"
          />
        </Wrapper>,
      );

      const button = await screen.findByTestId('jump-to-latest-messages', undefined, { timeout: 4000 });
      await userEvent.click(button);
      expect(scrollIntoView).toHaveBeenCalledWith({ block: 'end', behavior: 'smooth' });
    } finally {
      Element.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  // Regression for #196: from the COORDINATOR view a child subtask raises a tool approval that
  // is bubbled as coordinator.child_approval_required. Approve/Deny must post to the child
  // subtask run id (carried in the event payload), NOT the coordinator run id — otherwise the
  // backend returns 404 "No approval request found for this request_id on this run".
  it('routes a bubbled child approval to the child run id when the coordinator is selected', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.child_approval_required',
        payload: {
          childRunId: 'child-run-1',
          subtaskId: 1,
          requestId: 'toolu_01abcdef',
          toolName: 'web_fetch',
          url: 'https://api.github.com/search/issues',
        },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Tool Approval Required')).toBeDefined(), { timeout: 4000 });
    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));

    // Must target the child subtask run id, never the coordinator run id.
    expect(vi.mocked(apiClient.approveTool)).toHaveBeenCalledWith('child-run-1', 'toolu_01abcdef', 'once');
    expect(vi.mocked(apiClient.approveTool)).not.toHaveBeenCalledWith(
      'coord-run-1', expect.anything(), expect.anything(),
    );
  });

  it('routes a bubbled child approval DENY to the child run id when the coordinator is selected', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'coordinator.child_approval_required',
        payload: {
          childRunId: 'child-run-1',
          subtaskId: 1,
          requestId: 'toolu_01abcdef',
          toolName: 'web_fetch',
          url: 'https://api.github.com/search/issues',
        },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Tool Approval Required')).toBeDefined(), { timeout: 4000 });
    await userEvent.click(screen.getByRole('button', { name: 'Deny' }));

    expect(vi.mocked(apiClient.denyTool)).toHaveBeenCalledWith('child-run-1', 'toolu_01abcdef');
    expect(vi.mocked(apiClient.denyTool)).not.toHaveBeenCalledWith('coord-run-1', expect.anything());
  });

  it('surfaces a direct coordinator tool approval and targets the coordinator run', async () => {
    currentEvents = [
      {
        sequence: 1,
        type: 'tool.approval_required',
        payload: { requestId: 'req-direct-1', toolName: 'web_fetch', message: 'Fetch changelog' },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // A coordinator-owned approval (no childRunId) must still surface as an actionable
    // ApprovalGate in the coordinator view and resolve against the coordinator run.
    await waitFor(() => expect(screen.getByText('Tool Approval Required')).toBeDefined(), { timeout: 4000 });
    expect(screen.getByTestId('session-approval-gate')).toBeDefined();
    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));

    expect(vi.mocked(apiClient.approveTool)).toHaveBeenCalledWith('coord-run-1', 'req-direct-1', 'once');
  });

  it('surfaces a coordinator child approval inline and never dumps raw system-prompt scaffolding into the thread', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.system_prompt', payload: { prompt: 'Coordinator system prompt v1' } },
      { sequence: 3, type: 'agent.task', payload: { task: 'Coordinate the requested work' } },
      { sequence: 4, type: 'agent.turn.end', payload: {} },
      {
        sequence: 5,
        type: 'coordinator.work_plan',
        payload: {
          subtasks: [
            { id: '1', title: 'Research multi-agent orchestration', assignedAgent: 'Stark', roleTitle: 'Lead Researcher' },
          ],
        },
      },
      { sequence: 6, type: 'subtask.dispatched', payload: { subtaskId: '1', childRunId: 'child-run-1', status: 'dispatched' } },
      {
        sequence: 7,
        type: 'coordinator.child_approval_required',
        payload: { subtaskId: '1', childRunId: 'child-run-1', requestId: 'approval-2', toolName: 'web_fetch', message: 'Fetch docs' },
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // The pending child approval is surfaced inline and is actionable.
    await waitFor(() => expect(screen.getByTestId('session-approval-gate')).toBeDefined(), { timeout: 4000 });
    expect(screen.getByText('Tool Approval Required')).toBeDefined();

    // The intent-driven Timeline is the conversation surface — the banned activity
    // side-stripe and the old grouped-activity affordance never render.
    expect(screen.getByTestId('run-timeline')).toBeDefined();
    expect(screen.queryByTestId('session-activity-rail')).toBeNull();
    expect(screen.queryByTestId('activity-details-summary')).toBeNull();

    // Raw system-prompt scaffolding is technical and is never dumped as a visible bubble.
    expect(screen.queryByText('Coordinator system prompt v1')).toBeNull();
    expect(screen.queryByText('System prompt')).toBeNull();
  });

  it('renders reported intents as ordered Timeline steps for a coordinator scope', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.intent', payload: { intent: 'Draft the outcome plan' } },
      { sequence: 2, type: 'agent.intent', payload: { intent: 'Dispatch the work' } },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          variant="docked"
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // Each agent.intent becomes a top-level Timeline step — no wall of per-turn
    // "Activity collapsed" affordances and no banned left side-stripe rail.
    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    expect(within(timeline).getByText('Draft the outcome plan')).toBeDefined();
    expect(within(timeline).getByText('Dispatch the work')).toBeDefined();
    expect(screen.queryByTestId('session-activity-rail')).toBeNull();
    expect(screen.queryByTestId('activity-details-summary')).toBeNull();
  });

  it('never dumps long workspace system-prompt context into the Messages thread', async () => {
    const longContext = [
      'workspace-sync coordinator context',
      'TEAM_ROOT: C:\\Users\\asabbour\\Git\\agentweaver\\.squad',
      'WORKTREE_PATH: C:\\Users\\asabbour\\Git\\agentweaver',
      'CURRENT_DATETIME: 2026-07-07T12:57:00.000-07:00',
      'Requested by: Ahmed Sabbour',
      '',
      'This verbose workspace sync prose should stay folded until the operator opens it.',
    ].join('\n');
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'coordinator-turn' } },
      { sequence: 2, type: 'agent.system_prompt', payload: { prompt: 'Coordinator system prompt' } },
      { sequence: 3, type: 'agent.task', payload: { task: longContext } },
      { sequence: 4, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          variant="docked"
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // The Messages surface is the intent-driven Timeline — system-prompt / workspace
    // scaffolding is not part of the conversation and never leaks into it, and there
    // is no legacy "Technical details" switch on this surface.
    await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    expect(screen.queryByRole('switch', { name: /Technical details/i })).toBeNull();
    expect(screen.queryByText('System prompt')).toBeNull();
    expect(screen.queryByText('This verbose workspace sync prose should stay folded until the operator opens it.')).toBeNull();
  });

  it('nests a tool call under its reported intent step in the Timeline', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'Writing the design artifact.' } },
      { sequence: 3, type: 'agent.intent', payload: { intent: 'Writing hotel booking design doc' } },
      {
        sequence: 4,
        type: 'tool.call',
        payload: {
          callId: 'tool-1',
          toolName: 'write_file',
          arguments: { path: 'hotel-booking-design.md' },
        },
      },
      { sequence: 5, type: 'tool.result', payload: { callId: 'tool-1' } },
      { sequence: 6, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          variant="docked"
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // Steps are expanded by default, so the intent's tool group is visible immediately.

    const group = await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 });
    expect(group.textContent).toContain('Used 1 tool');
    // Tool group is collapsed by default; expand to reveal the rows.
    fireEvent.click(group);
    const rows = within(timeline).getAllByTestId('timeline-tool-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-tool-status')).toBe('complete');
  });

  it('renders scope tool activity in a single thread without Activity/Changes/Files tabs', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'Creating the booking page components.' } },
      {
        sequence: 3,
        type: 'tool.call',
        payload: {
          callId: 'tool-1',
          toolName: 'write_file',
          arguments: { path: 'HotelBooking.css' },
        },
      },
      { sequence: 4, type: 'tool.result', payload: { callId: 'tool-1' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          variant="docked"
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // The surface is a single thread — the Activity | Changes segmented control (and old Files tab) are gone.
    expect(screen.queryByTestId('session-tab-activity')).toBeNull();
    expect(screen.queryByTestId('session-tab-changes')).toBeNull();
    expect(screen.queryByTestId('session-tab-files')).toBeNull();

    // The write_file tool surfaces as a Timeline activity row, not a run-tree/Files artifact.
    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // With no agent.intent, activity nests under the synthetic "Working" step, which is expanded
    // by default. Tool group is collapsed by default; expand it to reveal the rows.
    fireEvent.click(await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 }));
    const rows = await within(timeline).findAllByTestId('timeline-tool-row', undefined, { timeout: 4000 });
    expect(rows.length).toBeGreaterThan(0);
    expect(vi.mocked(apiClient.getRunFileDiff)).not.toHaveBeenCalled();
  });

  it('renders a non-coordinator agent message as markdown inside its Timeline step', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.system_prompt', payload: { prompt: 'Worker system prompt' } },
      { sequence: 3, type: 'agent.task', payload: { task: 'Do worker task' } },
      { sequence: 4, type: 'agent.message', payload: { content: 'I found the implementation details.' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    // A message with no preceding intent opens a synthetic "Working" step, expanded by default,
    // revealing the agent's actual output rendered as a message.
    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    await waitFor(
      () => expect(within(timeline).getByTestId('timeline-message').textContent).toContain('I found the implementation details.'),
      { timeout: 4000 },
    );

    // System-prompt scaffolding is never dumped into the conversation.
    expect(screen.queryByText('Worker system prompt')).toBeNull();
    expect(screen.queryByText('System prompt')).toBeNull();
  });

  it('uses a non-generic fallback instead of bare Agent for agent-authored rows', async () => {
    const genericTree: RunSessionTree[] = [
      {
        nodeId: 'coordinator',
        label: 'Coordinator',
        status: 'running',
        depth: 0,
        children: [
          {
            nodeId: 'generic-agent',
            label: 'Agent',
            agentName: 'Agent',
            agentRole: 'Implementer',
            status: 'running',
            childRunId: 'child-run-1',
            depth: 1,
            children: [],
          },
        ],
      },
    ];
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'generic-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'I am working on it.' } },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={genericTree}
          selectedNodeId="generic-agent"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // The scope header names the agent by its role fallback, never the bare generic
    // literal "Agent".
    expect(screen.getAllByText('Assistant (Implementer)').length).toBeGreaterThan(0);
    expect(screen.queryByText('Agent (Implementer)')).toBeNull();
  });

  it('nests tool calls and the agent message together under one Timeline step (#122)', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'Applying the requested fix.' } },
      { sequence: 3, type: 'tool.call', payload: { callId: 'c1', toolName: 'edit_file', arguments: { path: 'apps/web/src/App.tsx' } } },
      { sequence: 4, type: 'tool.result', payload: { callId: 'c1' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // Message + tool with no preceding intent group under a single synthetic step, expanded by default.

    const group = await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 });
    expect(group.textContent).toContain('Used 1 tool');
    // Tool group is collapsed by default; expand to reveal the rows.
    fireEvent.click(group);
    const rows = within(timeline).getAllByTestId('timeline-tool-row');
    expect(rows[0].getAttribute('data-tool-status')).toBe('complete');
    // The agent's narrative message sits in the same step, not a separate loud bubble.
    expect(within(timeline).getByTestId('timeline-message').textContent).toContain('Applying the requested fix.');
  });

  it('derives a settled Timeline step to complete while an open step stays running', async () => {
    // Two turns: the first is closed (agent.turn.end), the second is still open
    // (agent.turn.start with no matching end). The run is live (getRun → in_progress).
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.message', payload: { messageId: 'm1', content: 'First response is finished.' } },
      { sequence: 3, type: 'agent.turn.end', payload: {} },
      { sequence: 4, type: 'agent.turn.start', payload: { turnId: 't2' } },
      { sequence: 5, type: 'agent.message', payload: { messageId: 'm2', content: 'Second response still streaming.' } },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive
        />
      </Wrapper>,
    );

    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // The first message's step is closed by agent.turn.end → Complete status icon (no
    // perpetual spinner). The still-open second step derives to Running.
    await waitFor(() => expect(within(timeline).getAllByLabelText('Complete').length).toBeGreaterThan(0), { timeout: 4000 });
    expect(within(timeline).getAllByLabelText('Running').length).toBeGreaterThan(0);
  });

  it('marks a failed tool call with an error status in the Timeline, not a success check', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 'worker-turn' } },
      { sequence: 2, type: 'agent.message', payload: { content: 'Trying a tool.' } },
      { sequence: 3, type: 'tool.call', payload: { callId: 'c1', toolName: 'web_fetch', arguments: { url: 'https://example.com' } } },
      { sequence: 4, type: 'tool.error', payload: { callId: 'c1', errorMessage: 'boom' } },
      { sequence: 5, type: 'agent.turn.end', payload: {} },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // The synthetic step is expanded by default; the tool group is collapsed by default, so expand it.
    fireEvent.click(await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 }));

    const rows = await within(timeline).findAllByTestId('timeline-tool-row', undefined, { timeout: 4000 });
    expect(rows[0].getAttribute('data-tool-status')).toBe('error');
    expect(rows.some((r) => r.getAttribute('data-tool-status') === 'complete')).toBe(false);
  });

  it('shows coordinator messaging availability, success, and failure feedback', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.steerCoordinator).mockResolvedValueOnce({ status: 'applied' });
    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive
        />
      </Wrapper>,
    );

    const input = await screen.findByPlaceholderText('Message coordinator...', undefined, { timeout: 4000 });
    await user.type(input, 'Check the compact view');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalledWith(
      'coord-run-1',
      expect.objectContaining({
        kind: 'send',
        instruction: 'Check the compact view',
      }),
    ));
    expect(await screen.findByText('Message sent to coordinator.')).toBeDefined();

    vi.mocked(apiClient.steerCoordinator).mockRejectedValueOnce(new Error('message bus unavailable'));
    await user.type(input, 'Try again');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText(/message bus unavailable/i)).toBeDefined();
    cleanup();

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive={false}
        />
      </Wrapper>,
    );

    expect(await screen.findByText('Messaging is unavailable because this coordinator run is not active.')).toBeDefined();
    expect(screen.getByPlaceholderText('Message coordinator...')).toHaveProperty('disabled', true);
  });

  it('seeds and refreshes persisted coordinator steering messages for active runs', async () => {
    const user = userEvent.setup();
    let persistedEvents: RunStreamEvent[] = [
      {
        sequence: 1,
        type: 'coordinator.steering',
        payload: { instruction: 'Seeded before the panel mounted.' },
      },
    ];
    vi.mocked(apiClient.getRunEvents).mockImplementation(() => Promise.resolve(persistedEvents));
    vi.mocked(apiClient.steerCoordinator).mockImplementation(async () => {
      persistedEvents = [
        ...persistedEvents,
        {
          sequence: 2,
          type: 'coordinator.steering',
          payload: { instruction: 'Refresh from durable events after send.' },
        },
      ];
      return { status: 'applied' };
    });

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="coordinator"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive
        />
      </Wrapper>,
    );

    expect(await screen.findByText('Coordinator steering applied: Seeded before the panel mounted.')).toBeDefined();

    const input = await screen.findByPlaceholderText('Message coordinator...', undefined, { timeout: 4000 });
    await user.type(input, 'Refresh from durable events after send.');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText('Coordinator steering applied: Refresh from durable events after send.')).toBeDefined();
  });

  it('makes the composer read-only when viewing a non-coordinator agent (steer via the Coordinator)', async () => {
    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={tree}
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive
        />
      </Wrapper>,
    );

    // Viewing a child agent shows a read-only notice, not a message box — you steer
    // other agents through the Coordinator.
    expect(await screen.findByText('Viewing Worker — steer via the Coordinator')).toBeDefined();
    expect(screen.queryByPlaceholderText('Message coordinator...')).toBeNull();
  });

  it('guards planned subtasks without childRunId from run/workspace API calls', async () => {
    const plannedTree: RunSessionTree[] = [
      {
        nodeId: 'coordinator',
        label: 'Coordinator',
        status: 'running',
        depth: 0,
        children: [
          {
            nodeId: 'planned-subtask',
            label: 'Planned subtask',
            agentName: 'Worker',
            agentRole: 'Researcher',
            status: 'pending',
            depth: 1,
            children: [],
          },
        ],
      },
    ];

    render(
      <Wrapper>
        <AgentSessionPanel
          open
          onClose={vi.fn()}
          tree={plannedTree}
          selectedNodeId="planned-subtask"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
        />
      </Wrapper>,
    );

    expect(screen.getByText(/has not been dispatched yet/i)).toBeDefined();
    // No segmented Changes tab anymore; the panel must still avoid run/workspace API calls for
    // an undispatched planned node.
    expect(vi.mocked(apiClient.getRun)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunFiles)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunWorkspace)).not.toHaveBeenCalled();
  });

  it('shows a loading state, not the prior run events, on a first-time run selection', async () => {
    const twoRunTree: RunSessionTree[] = [{
      ...tree[0],
      children: [
        tree[0].children[0],
        {
          nodeId: 'subtask-2',
          label: 'Subtask 2',
          agentName: 'Worker',
          agentRole: 'Researcher',
          status: 'running',
          childRunId: 'child-run-2',
          depth: 1,
          children: [],
        },
      ],
    }];
    const makeRunDetail = (runId: string, status: RunDetail['status']): RunDetail => ({
      run_id: runId,
      status,
      model_source: 'github-copilot',
      started_at: new Date().toISOString(),
      ended_at: null,
      result: null,
      diff: null,
      step_count: 0,
      tree_hash: null,
    });
    let resolveSecondRun: (value: RunDetail) => void;
    const secondRun = new Promise<RunDetail>((resolve) => {
      resolveSecondRun = resolve;
    });
    vi.mocked(apiClient.getRun).mockImplementation((runId) => (
      runId === 'child-run-2'
        ? secondRun
        : Promise.resolve(makeRunDetail('child-run-1', 'completed'))
    ));
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 1, type: 'agent.message', payload: { content: 'First run only' } },
    ]);

    const props = {
      open: true,
      onClose: vi.fn(),
      tree: twoRunTree,
      onSelectNode: vi.fn(),
      coordinatorRunId: 'coord-run-1',
      projectId: 'p1',
    };
    const { rerender } = render(
      <Wrapper><AgentSessionPanel {...props} selectedNodeId="subtask-1" /></Wrapper>,
    );
    expect(await screen.findByText('First run only')).toBeDefined();

    currentEvents = [];
    rerender(<Wrapper><AgentSessionPanel {...props} selectedNodeId="subtask-2" /></Wrapper>);

    expect(await screen.findByText('Loading session details...')).toBeDefined();
    expect(screen.queryByText('First run only')).toBeNull();
    resolveSecondRun!(makeRunDetail('child-run-2', 'in_progress'));
  });

  it('stops status polling when a child run reaches assemble_ready', async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(apiClient.getRun).mockResolvedValue({
        run_id: 'child-run-1',
        status: 'assemble_ready',
        model_source: 'github-copilot',
        started_at: new Date().toISOString(),
        ended_at: null,
        result: null,
        diff: null,
        step_count: 0,
        tree_hash: null,
      });

      render(
        <Wrapper>
          <AgentSessionPanel
            open
            onClose={vi.fn()}
            tree={tree}
            selectedNodeId="subtask-1"
            onSelectNode={vi.fn()}
            coordinatorRunId="coord-run-1"
            projectId="p1"
          />
        </Wrapper>,
      );

      await vi.advanceTimersByTimeAsync(4000);
      expect(vi.mocked(apiClient.getRun)).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(8000);
      expect(vi.mocked(apiClient.getRun)).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('reuses cached session data when revisiting a previously opened child run', async () => {
    const twoRunTree: RunSessionTree[] = [{
      ...tree[0],
      children: [
        tree[0].children[0],
        {
          nodeId: 'subtask-2',
          label: 'Subtask 2',
          agentName: 'Worker',
          agentRole: 'Researcher',
          status: 'running',
          childRunId: 'child-run-2',
          depth: 1,
          children: [],
        },
      ],
    }];
    const makeRunDetail = (runId: string, status: RunDetail['status']): RunDetail => ({
      run_id: runId,
      status,
      model_source: 'github-copilot',
      started_at: new Date().toISOString(),
      ended_at: null,
      result: null,
      diff: null,
      step_count: 0,
      tree_hash: null,
    });
    vi.mocked(apiClient.getRun).mockImplementation((runId) => Promise.resolve(
      runId === 'child-run-2'
        ? makeRunDetail('child-run-2', 'completed')
        : makeRunDetail('child-run-1', 'completed'),
    ));
    vi.mocked(apiClient.getRunEvents).mockImplementation((runId) => Promise.resolve(
      runId === 'child-run-2'
        ? [{ sequence: 2, type: 'agent.message', payload: { content: 'Second run only' } }]
        : [{ sequence: 1, type: 'agent.message', payload: { content: 'First run only' } }],
    ));

    const props = {
      open: true,
      onClose: vi.fn(),
      tree: twoRunTree,
      onSelectNode: vi.fn(),
      coordinatorRunId: 'coord-run-1',
      projectId: 'p1',
    };
    const { rerender } = render(
      <Wrapper><AgentSessionPanel {...props} selectedNodeId="subtask-1" /></Wrapper>,
    );
    expect(await screen.findByText('First run only')).toBeDefined();

    rerender(<Wrapper><AgentSessionPanel {...props} selectedNodeId="subtask-2" /></Wrapper>);
    expect(await screen.findByText('Second run only')).toBeDefined();

    vi.mocked(apiClient.getRun).mockClear();
    vi.mocked(apiClient.getRunEvents).mockClear();

    rerender(<Wrapper><AgentSessionPanel {...props} selectedNodeId="subtask-1" /></Wrapper>);

    expect(await screen.findByText('First run only')).toBeDefined();
    expect(screen.queryByText('Loading session details...')).toBeNull();
    expect(vi.mocked(apiClient.getRunEvents)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRun)).not.toHaveBeenCalled();
  });
});
