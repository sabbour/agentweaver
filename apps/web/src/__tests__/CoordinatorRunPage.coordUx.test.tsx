import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, fireEvent, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { RunStreamEvent } from '../api/sse';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    steerCoordinator: vi.fn(),
    reviewAssembly: vi.fn(),
    answerQuestion: vi.fn(),
    getRun: vi.fn(),
    getOutcomeSpec: vi.fn(),
    getTeam: vi.fn().mockResolvedValue({ members: [{ name: 'Neo', role_title: 'Researcher' }] }),
    setAutopilot: vi.fn(),
    setAutoApprove: vi.fn(),
    retryRun: vi.fn(),
    getRunTokenBreakdown: vi.fn().mockResolvedValue({
      runId: 'coord-run-1',
      source: 'events',
      hasAgentData: true,
      totalTokens: 1200,
      totalNanoAiu: 15990000000,
      breakdown: [{ agentName: 'Neo', totalTokens: 1200, totalNanoAiu: 15990000000 }],
    }),
    getRunEvents: vi.fn().mockResolvedValue([]),
    getRunFiles: vi.fn().mockResolvedValue([
      { path: 'src/app.ts', status: 'modified', added_lines: 3, removed_lines: 1 },
    ]),
    getRunFileContent: vi.fn().mockResolvedValue({ path: 'src/app.ts', content: 'export {}', is_binary: false, language: 'typescript' }),
    getRunWorkspace: vi.fn().mockResolvedValue([]),
    getRunFileDiff: vi.fn().mockResolvedValue({ path: 'src/app.ts', diff: 'diff --git a/src/app.ts b/src/app.ts' }),
    getAssemblyFiles: vi.fn().mockResolvedValue([]),
    getAssemblyWorkspace: vi.fn().mockResolvedValue([]),
    getAssemblyFileDiff: vi.fn().mockResolvedValue(null),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, status: 'done', error: null, reconnect: vi.fn() }),
}));

vi.mock('../components/OutcomePlanPanel', () => ({
  OutcomePlanPanel: () => null,
}));

import { apiClient } from '../api/apiClient';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR } from './fixtures/graphDescriptor';

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
  vi.mocked(apiClient.getRun).mockResolvedValue({ status: 'in_progress', autopilot: false, auto_approve_tools: false } as never);
  vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
  vi.mocked(apiClient.setAutopilot).mockResolvedValue({ run_id: 'coord-run-1', autopilot: true });
  vi.mocked(apiClient.setAutoApprove).mockResolvedValue({ run_id: 'coord-run-1', auto_approve_tools: true });
  vi.mocked(apiClient.retryRun).mockResolvedValue({ run_id: 'retry-run-1', retried_from: 'coord-run-1', status: 'in_progress' });
});

afterEach(() => cleanup());

describe('CoordinatorRunPage operator console redesign', () => {
  it('renders the four persistent zones and removes debug-dashboard cards from the body', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(screen.getByTestId('run-operator-console')).toBeTruthy(), { timeout: 4000 });

    const text = document.body.textContent ?? '';
    expect(text).toContain('Run tree');
    expect(text).toContain('Graph');
    expect(text).toContain('Messages');
    expect(text).toContain('Changes');
    expect(text).toContain('Files');
    expect(screen.getByPlaceholderText('Message coordinator...')).toBeTruthy();
    expect(text).toContain('Autopilot');
    expect(text).toContain('Auto-approve');
    expect(text).toContain('Retry failed');
    expect(text).toContain('Stop run');
    expect(text).not.toContain('Transaction trace');
    expect(text).not.toContain('Agent token breakdown');
  });

  it('uses the run tree as task-structured navigation and scopes the composer to the selected task', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    fireEvent.click(screen.getByRole('button', { name: /Subtask 1/i }));

    await waitFor(() => expect(document.body.textContent).toContain('Context: Subtask 1'), { timeout: 4000 });
    expect(document.body.textContent).toContain('Neo');
    expect(document.body.textContent).toContain('Researcher');
  });

  it('orders and dedupes stream events by sequence and groups message deltas into one assistant bubble', async () => {
    currentEvents = [
      { sequence: 3, type: 'agent.message.delta', payload: { delta: 'world' } },
      { sequence: 1, type: 'agent.turn.start', payload: { turnId: 't1' } },
      { sequence: 2, type: 'agent.message.delta', payload: { delta: 'hello ' } },
      { sequence: 3, type: 'agent.message.delta', payload: { delta: 'world' } },
      { sequence: 4, type: 'agent.turn.end', payload: {} },
      { sequence: 5, type: 'tool.call', payload: { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } } },
    ];

    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('hello world'), { timeout: 4000 });
    expect((document.body.textContent?.match(/hello world/g) ?? [])).toHaveLength(1);
    expect(document.body.textContent).toContain('Tool calls');
  });

  it('keeps Changes and Files as the only artifact tabs beside Messages', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    const changesTab = await screen.findByTestId('session-tab-changes', undefined, { timeout: 4000 });
    expect(screen.getByTestId('session-tab-messages')).toBeTruthy();
    expect(changesTab).toBeTruthy();
    expect(screen.getByTestId('session-tab-files')).toBeTruthy();
    expect(document.body.textContent).not.toContain('Tools');
  });

  it('routes the single composer through coordinator steering and targets a selected child run', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(() => expect(document.body.textContent).toContain('Subtask 1'), { timeout: 4000 });
    fireEvent.click(screen.getByRole('button', { name: /Subtask 1/i }));
    const input = screen.getByPlaceholderText('Message coordinator...');
    fireEvent.change(input, { target: { value: 'Use the cached source' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send message' }));

    await waitFor(() => expect(apiClient.steerCoordinator).toHaveBeenCalled(), { timeout: 4000 });
    expect(vi.mocked(apiClient.steerCoordinator).mock.calls[0][1]).toMatchObject({
      kind: 'send',
      instruction: 'Use the cached source',
      target_child_run_id: 'child-run-1',
    });
  });
});
