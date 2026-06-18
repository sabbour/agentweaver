import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';

// ResizeObserver is required by @xyflow/react and is absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectRuns: vi.fn(),
    getTeam: vi.fn(),
    getRun: vi.fn(),
    getRunEvents: vi.fn(),
  },
}));

// useRunStream is driven by SSE/fetch; stub it so the page reads from the REST seed only.
vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: [], status: 'done', error: null, reconnect: vi.fn() }),
}));

import { apiClient } from '../api/apiClient';
import { WorkflowRunPage } from '../pages/WorkflowRunPage';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/runs/child-1/workflow']}>
        <Routes>
          <Route path="/projects/:projectId/runs/:runId/workflow" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
    {
      workflow_run_id: 'child-1',
      execution_id: 'exec-1',
      task: 'Subtask work',
      status: 'parked',
      agent_name: 'Trinity',
      started_at: '2026-06-17T00:00:00Z',
    },
  ]);
  vi.mocked(apiClient.getTeam).mockResolvedValue({
    project_id: 'p1',
    project_name: 'Demo',
    members: [],
  } as unknown as Awaited<ReturnType<typeof apiClient.getTeam>>);
  // Non-null parent_run_id ⇒ this run is a coordinator child.
  vi.mocked(apiClient.getRun).mockResolvedValue({
    run_id: 'exec-1',
    status: 'parked',
    parent_run_id: 'coord-1',
    subtask_id: '3',
  } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([
    { sequence: 1, type: 'workflow.step', payload: { step: 'agent', status: 'completed', agent_name: 'Trinity' } },
    { sequence: 2, type: 'workflow.step', payload: { step: 'rai', status: 'completed' } },
    { sequence: 3, type: 'run.assemble_ready', payload: {} },
  ]);
});

afterEach(() => cleanup());

describe('WorkflowRunPage child run graph', () => {
  it('renders only the trimmed agent -> RAI -> assemble-ready pipeline for a coordinator child', async () => {
    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Assemble-ready'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Awaiting collective assembly');
    expect(text).not.toContain('Merge Coordinator');
    expect(text).not.toContain('Human Review');
    expect(text).not.toContain('Session Logger');
  });
});
