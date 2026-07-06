import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, screen } from '@testing-library/react';
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
    expect(screen.getByText('Dispatched subtask: Research multi-agent orchestration (Stark · Lead Researcher).')).toBeDefined();
    expect(screen.getByText('Subtask completed: Research multi-agent orchestration (Stark · Lead Researcher).')).toBeDefined();
    expect(screen.getByText('Collective assembly: RAI check started.')).toBeDefined();
    expect(screen.getByText('Child question from Research multi-agent orchestration (Stark · Lead Researcher): Which source should I use?')).toBeDefined();
    expect(screen.getByText('Tool approval required from Research multi-agent orchestration (Stark · Lead Researcher): web_fetch — Fetch docs')).toBeDefined();
    expect(screen.getByText('Tool Approval Required')).toBeDefined();
    // #122: system-prompt scaffolding is technical and hidden by default; the coordinator
    // instruction is high-signal and stays visible so the stream reads like a narrative.
    expect(screen.queryByText('System prompt')).toBeNull();
    expect(screen.getAllByText('Coordinator instruction')).toHaveLength(1);
    // Revealing technical details brings the collapsed system prompt back (collapsed, not deleted).
    await userEvent.click(screen.getByRole('switch', { name: 'Show technical details' }));
    await waitFor(() => expect(screen.getAllByText('System prompt')).toHaveLength(1));
    expect(screen.getAllByText('Coordinator instruction')).toHaveLength(1);
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
    expect(screen.getByText('Worker response')).toBeDefined();
    // #122: system prompt is technical and collapsed by default; the coordinator instruction stays.
    expect(screen.queryByText('System prompt')).toBeNull();
    expect(screen.getByText('Coordinator instruction')).toBeDefined();
    await userEvent.click(screen.getByRole('switch', { name: 'Show technical details' }));
    await waitFor(() => expect(screen.getByText('System prompt')).toBeDefined());
    expect(screen.getByText('Coordinator instruction')).toBeDefined();
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

    // Toggling technical details on reveals the collapsed plumbing (not deleted, just collapsed).
    await userEvent.click(screen.getByRole('switch', { name: 'Show technical details' }));
    await waitFor(() => expect(screen.queryByText(/Tool calls/)).not.toBeNull());
    expect(screen.getByText('Workspace file')).toBeDefined();
    // The narrative message remains visible alongside the revealed technical rows.
    expect(screen.getByText('Applying the requested fix.')).toBeDefined();
  });
});
