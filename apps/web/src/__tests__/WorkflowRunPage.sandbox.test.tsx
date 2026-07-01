import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
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

let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
    getProjectRuns: vi.fn(),
    getTeam: vi.fn(),
    getRun: vi.fn(),
    getRunEvents: vi.fn(),
    getRunGraph: vi.fn(),
    getRunUsage: vi.fn().mockResolvedValue(null),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, status: 'done', error: null, reconnect: vi.fn() }),
}));

import { apiClient } from '../api/apiClient';
import { WorkflowRunPage } from '../pages/WorkflowRunPage';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/runs/run-1/workflow']}>
        <Routes>
          <Route path="/projects/:projectId/runs/:runId/workflow" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  currentEvents = [];
  vi.mocked(apiClient.getTeam).mockResolvedValue({
    project_id: 'p1', project_name: 'Demo', members: [],
  } as unknown as Awaited<ReturnType<typeof apiClient.getTeam>>);
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(null);
});

afterEach(() => cleanup());

describe('WorkflowRunPage — preview sandbox button visibility', () => {
  it('shows Preview button for a completed kubernetes-sandbox-claim run (bug #99)', async () => {
    // Completed run: status=completed means runActive=false
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
      {
        workflow_run_id: 'run-1',
        execution_id: 'run-1',
        task: 'Deploy something',
        status: 'completed',
        agent_name: 'Neo',
        started_at: '2026-06-18T00:00:00Z',
      },
    ]);
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'run-1', status: 'completed', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    // sandbox.selected event with kubernetes backend — run is already done, no previewSession
    currentEvents = [
      { sequence: 1, type: 'sandbox.selected', payload: { backend: 'kubernetes-sandbox-claim' } },
    ];

    const { container } = render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => {
        const buttons = Array.from(container.querySelectorAll('button'));
        return buttons.some((b) => b.textContent?.includes('Preview'));
      },
      { timeout: 4000 },
    );

    const previewBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent?.includes('Preview'),
    );
    expect(previewBtn).toBeTruthy();
  });

  it('shows Preview button for an active kubernetes-sandbox-claim run', async () => {
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
      {
        workflow_run_id: 'run-1',
        execution_id: 'run-1',
        task: 'Deploy something',
        status: 'running',
        agent_name: 'Neo',
        started_at: '2026-06-18T00:00:00Z',
      },
    ]);
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'run-1', status: 'running', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    currentEvents = [
      { sequence: 1, type: 'sandbox.selected', payload: { backend: 'kubernetes-sandbox-claim' } },
    ];

    const { container } = render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => {
        const buttons = Array.from(container.querySelectorAll('button'));
        return buttons.some((b) => b.textContent?.includes('Preview'));
      },
      { timeout: 4000 },
    );

    const previewBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent?.includes('Preview'),
    );
    expect(previewBtn).toBeTruthy();
  });

  it('does NOT show Preview button when sandbox backend is not kubernetes-sandbox-claim', async () => {
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
      {
        workflow_run_id: 'run-1',
        execution_id: 'run-1',
        task: 'Deploy something',
        status: 'completed',
        agent_name: 'Neo',
        started_at: '2026-06-18T00:00:00Z',
      },
    ]);
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'run-1', status: 'completed', parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);

    // No sandbox.selected event — sandboxBackend stays undefined
    currentEvents = [];

    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    // Wait for the page to settle (run id label renders)
    await waitFor(
      () => expect(document.body.textContent).toContain('run-1'),
      { timeout: 4000 },
    );

    const buttons = Array.from(document.querySelectorAll('button'));
    expect(buttons.every((b) => !b.textContent?.includes('Preview'))).toBe(true);
  });
});
