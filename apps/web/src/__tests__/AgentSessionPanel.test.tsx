import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { RunStreamEvent } from '../api/sse';
import { AgentSessionPanel, type RunSessionTree } from '../components/AgentSessionPanel';

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

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>
        {children}
      </MemoryRouter>
    </FluentProvider>
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

  it('renders coordinator lifecycle, subtask, assembly, and child activity while collapsing repeated prompt scaffolding', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.system_prompt', payload: { prompt: 'Coordinator system prompt v1' } },
      { sequence: 3, type: 'agent.task', payload: { task: 'Coordinate the requested work' } },
      { sequence: 4, type: 'agent.turn.end', payload: {} },
      { sequence: 5, type: 'agent.turn.start', payload: { turnId: 't2' } },
      { sequence: 6, type: 'agent.system_prompt', payload: { prompt: 'Coordinator system prompt v2' } },
      { sequence: 7, type: 'agent.task', payload: { task: 'Dispatch the same work again' } },
      { sequence: 8, type: 'agent.turn.end', payload: {} },
      {
        sequence: 9,
        type: 'coordinator.work_plan',
        payload: {
          subtasks: [
            { id: '1', title: 'Research multi-agent orchestration', assignedAgent: 'Stark', roleTitle: 'Lead Researcher' },
          ],
        },
      },
      {
        sequence: 10,
        type: 'subtask.dispatched',
        payload: { subtaskId: '1', childRunId: 'child-run-1', status: 'dispatched' },
      },
      {
        sequence: 11,
        type: 'subtask.completed',
        payload: { subtaskId: '1', childRunId: 'child-run-1', status: 'completed' },
      },
      { sequence: 12, type: 'coordinator.assembly_rai_started', payload: {} },
      {
        sequence: 13,
        type: 'coordinator.child_question',
        payload: { subtaskId: '1', childRunId: 'child-run-1', requestId: 'q-1', question: 'Which source should I use?' },
      },
      {
        sequence: 14,
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

    await waitFor(() => expect(screen.getByText('Coordinator created a work plan with 1 subtasks.')).toBeDefined(), { timeout: 4000 });
    expect(screen.getByText('Dispatched subtask: Research multi-agent orchestration — Stark (Lead Researcher).')).toBeDefined();
    expect(screen.getByText('Subtask completed: Research multi-agent orchestration — Stark (Lead Researcher).')).toBeDefined();
    expect(screen.getByText('Collective assembly: RAI check started.')).toBeDefined();
    expect(screen.getByText('Child question from Research multi-agent orchestration — Stark (Lead Researcher): Which source should I use?')).toBeDefined();
    expect(screen.getByText('Tool approval required from Research multi-agent orchestration — Stark (Lead Researcher): web_fetch — Fetch docs')).toBeDefined();
    expect(screen.getByText('Tool Approval Required')).toBeDefined();
    expect(screen.getAllByTestId('session-activity-row').length).toBeGreaterThan(0);
    expect(screen.getAllByTestId('session-activity-rail').length).toBeGreaterThan(0);
    expect(getComputedStyle(screen.getAllByTestId('session-activity-row')[0] as HTMLElement).listStyleType).not.toBe('disc');
    expect(screen.queryByText('Activity')).toBeNull();
    // #122: system-prompt scaffolding is technical and hidden by default; the coordinator
    // instruction is high-signal and stays visible inline so the stream reads like a narrative.
    expect(screen.queryByText('System prompt')).toBeNull();
    expect(screen.getByText('Coordinate the requested work')).toBeDefined();
    // Revealing technical details brings system prompt scaffolding back while keeping activity
    // collapsed behind an explicit control until the operator asks for it.
    await userEvent.click(screen.getByRole('switch', { name: 'Technical details hidden' }));
    await waitFor(() => expect(screen.getAllByTestId('activity-details-summary').length).toBeGreaterThan(0));
    expect(screen.queryByText('Coordinator created a work plan with 1 subtasks.')).toBeNull();
    await waitFor(() => expect(screen.getAllByText('System prompt')).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: /Expand activity details/i }));
    expect(screen.getByText('Coordinator created a work plan with 1 subtasks.')).toBeDefined();
    expect(screen.getByText('Coordinate the requested work')).toBeDefined();
  });

  it('defaults docked coordinator panels to technical details while folding long workspace context', async () => {
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

    const technicalToggle = await screen.findByRole('switch', { name: 'Technical details shown' }, { timeout: 4000 });
    expect((technicalToggle as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('System prompt')).toBeDefined();
    const contextDisclosure = screen.getByRole('button', { name: /Coordinator context/i });
    expect(contextDisclosure.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByText('This verbose workspace sync prose should stay folded until the operator opens it.')).toBeNull();
    await userEvent.click(technicalToggle);
    const hiddenToggle = screen.getByRole('switch', { name: 'Technical details hidden' });
    expect((hiddenToggle as HTMLInputElement).checked).toBe(false);
    expect(screen.queryByText('System prompt')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: /Coordinator context/i }));
    expect(screen.getByRole('button', { name: /Coordinator context/i }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('This verbose workspace sync prose should stay folded until the operator opens it.')).toBeDefined();
  });

  it('keeps docked tool activity and file artifacts collapsed until expanded', async () => {
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

    const summaries = await screen.findAllByTestId('activity-details-summary', undefined, { timeout: 4000 });
    const summary = summaries.find((element) => element.textContent?.includes('1 artifact')) ?? summaries[0];
    expect(summary.textContent).toContain('Activity collapsed');
    expect(summary.textContent).toContain('1 update');
    expect(summary.textContent).toContain('1 tool call');
    expect(summary.textContent).toContain('1 artifact');
    expect(screen.queryByTestId('session-activity-row')).toBeNull();
    expect(screen.queryByTestId('session-file-row')).toBeNull();

    await userEvent.click(screen.getByTestId('toggle-activity-details'));
    await waitFor(() => expect(screen.getByTestId('session-activity-row')).toBeDefined(), { timeout: 4000 });
    const fileRow = screen.getByTestId('session-file-row');
    expect(fileRow.textContent).toContain('hotel-booking-design.md');
    expect(fileRow.textContent).toContain('Workspace file');
    expect(getComputedStyle(fileRow).display).toBe('grid');
  });

  it('projects workspace file references from messages into Files without inventing Changes', async () => {
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

    const changesTab = await screen.findByTestId('session-tab-changes', undefined, { timeout: 4000 });
    expect(changesTab.textContent).toContain('Changes (0)');
    const filesTab = screen.getByTestId('session-tab-files');
    expect(filesTab.textContent).toContain('Files (1)');

    await userEvent.click(filesTab);
    await waitFor(() => expect(screen.getByText('HotelBooking.css')).toBeDefined(), { timeout: 4000 });
    await userEvent.click(screen.getByText('HotelBooking.css'));

    expect(vi.mocked(apiClient.getRunFileDiff)).not.toHaveBeenCalled();
  });

  it('keeps the normal agent-message conversation view for non-coordinator sessions', async () => {
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

    await waitFor(() => expect(screen.getByText('I found the implementation details.')).toBeDefined(), { timeout: 4000 });
    expect(screen.getAllByText('Worker (Researcher)').length).toBeGreaterThan(0);
    expect(screen.getByText('response')).toBeDefined();
    // #122: system prompt is technical and hidden by default; the user instruction stays inline.
    expect(screen.queryByText('System prompt')).toBeNull();
    expect(screen.getByText('Do worker task')).toBeDefined();
    await userEvent.click(screen.getByRole('switch', { name: 'Technical details hidden' }));
    await waitFor(() => expect(screen.getByText('System prompt')).toBeDefined());
    expect(screen.getByText('Do worker task')).toBeDefined();
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

    await waitFor(() => expect(screen.getByText('I am working on it.')).toBeDefined(), { timeout: 4000 });
    const messageRow = screen.getByText('I am working on it.').closest('[data-testid="session-message-row"]') as HTMLElement;
    expect(within(messageRow).getByText('Assistant (Implementer)')).toBeDefined();
    expect(within(messageRow).queryByText('Agent')).toBeNull();
  });

  it('collapses low-signal technical tool plumbing by default and reveals it via the toggle (#122)', async () => {
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

    // The high-signal agent message reads as a clean narrative by default.
    await waitFor(() => expect(screen.getByText('Applying the requested fix.')).toBeDefined(), { timeout: 4000 });
    // Tool-call plumbing and file-write rows are hidden by default.
    expect(screen.queryByText(/Tool calls/)).toBeNull();
    expect(screen.queryByText('Workspace file')).toBeNull();

    // Toggling technical details on reveals the collapsed plumbing summary first.
    await userEvent.click(screen.getByRole('switch', { name: 'Technical details hidden' }));
    await waitFor(() => expect(screen.getAllByTestId('activity-details-summary').length).toBeGreaterThan(0));
    expect(screen.queryByText(/Tool calls/)).toBeNull();
    expect(screen.queryByText('Workspace file')).toBeNull();

    // Expanding activity details reveals compact tool/artifact rows without turning them into cards.
    await userEvent.click(screen.getByRole('button', { name: /Expand activity details/i }));
    expect(screen.queryByText(/Tool calls/)).not.toBeNull();
    expect(screen.getByText('Workspace file')).toBeDefined();
    const fileRow = screen.getByTestId('session-file-row');
    const fileRowStyle = getComputedStyle(fileRow);
    expect(fileRowStyle.backgroundColor === 'transparent' || fileRowStyle.backgroundColor === 'rgba(0, 0, 0, 0)').toBe(true);
    expect(fileRowStyle.display).toBe('grid');
    expect(fileRowStyle.minHeight).toBe('24px');
    // The narrative message remains visible alongside the revealed technical rows.
    expect(screen.getByText('Applying the requested fix.')).toBeDefined();
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
          selectedNodeId="subtask-1"
          onSelectNode={vi.fn()}
          coordinatorRunId="coord-run-1"
          projectId="p1"
          coordinatorActive
        />
      </Wrapper>,
    );

    const input = await screen.findByPlaceholderText('Message coordinator...', undefined, { timeout: 4000 });
    await user.type(input, 'Check the compact view');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalledWith(
      'coord-run-1',
      expect.objectContaining({
        kind: 'send',
        instruction: 'Check the compact view',
        target_child_run_id: 'child-run-1',
      }),
    ));
    expect(await screen.findByText('Message sent to coordinator.')).toBeDefined();

    vi.mocked(apiClient.steerCoordinator).mockRejectedValueOnce(new Error('message bus unavailable'));
    await user.type(input, 'Try again');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    expect(await screen.findByText(/message bus unavailable/i)).toBeDefined();
    cleanup();

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
          coordinatorActive={false}
        />
      </Wrapper>,
    );

    expect(await screen.findByText('Messaging is unavailable because this coordinator run is not active.')).toBeDefined();
    expect(screen.getByPlaceholderText('Message coordinator...')).toHaveProperty('disabled', true);
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
    await userEvent.click(screen.getByTestId('session-tab-files'));
    expect(screen.getByTestId('planned-node-file-guard')).toBeDefined();
    expect(vi.mocked(apiClient.getRun)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunFiles)).not.toHaveBeenCalled();
    expect(vi.mocked(apiClient.getRunWorkspace)).not.toHaveBeenCalled();
  });
});
