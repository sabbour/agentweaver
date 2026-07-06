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
});
