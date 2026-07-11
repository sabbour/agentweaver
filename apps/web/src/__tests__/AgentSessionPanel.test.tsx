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
    // Steps are collapsed by default; expand the intent that ran the tool.
    fireEvent.click(within(timeline).getByText('Writing hotel booking design doc'));

    const group = await within(timeline).findByTestId('timeline-tool-group', undefined, { timeout: 4000 });
    expect(group.textContent).toContain('Used 1 tool');
    // Tool group is collapsed by default; expand to reveal the rows.
    fireEvent.click(group);
    const rows = within(timeline).getAllByTestId('timeline-tool-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-tool-status')).toBe('complete');
  });

  it('renders scope tool activity and exposes Activity/Changes without a Files tab', async () => {
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

    // The surface uses a single Activity | Changes segmented control — the old Files tab is gone.
    expect(await screen.findByTestId('session-tab-activity', undefined, { timeout: 4000 })).toBeDefined();
    expect(screen.getByTestId('session-tab-changes')).toBeDefined();
    expect(screen.queryByTestId('session-tab-files')).toBeNull();

    // The write_file tool surfaces as a Timeline activity row, not a run-tree/Files artifact.
    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    // With no agent.intent, activity nests under the synthetic "Working" step; expand it.
    fireEvent.click(within(timeline).getByText('Working'));
    // Tool group is collapsed by default; expand it to reveal the rows.
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

    // A message with no preceding intent opens a synthetic "Working" step; expand it
    // to reveal the agent's actual output rendered as a message.
    const timeline = await screen.findByTestId('run-timeline', undefined, { timeout: 4000 });
    fireEvent.click(within(timeline).getByText('Working'));
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
    // Message + tool with no preceding intent group under a single synthetic step.
    fireEvent.click(within(timeline).getByText('Working'));

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
    fireEvent.click(within(timeline).getByText('Working'));
    // Tool group is collapsed by default; expand it to reveal the rows.
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
    await userEvent.click(screen.getByTestId('session-tab-changes'));
    expect(screen.getByTestId('planned-node-artifact-guard')).toBeDefined();
    expect(vi.mocked(apiClient.getRun)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunFiles)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunWorkspace)).not.toHaveBeenCalled();
  });
});
