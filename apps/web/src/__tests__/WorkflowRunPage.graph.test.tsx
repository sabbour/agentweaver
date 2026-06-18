import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';

// ResizeObserver is required by @xyflow/react and absent in happy-dom.
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
    getRunGraph: vi.fn(),
  },
}));

// useRunStream is driven by SSE/fetch; stub it so the page reads from the REST seed only.
vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: [], status: 'done', error: null, reconnect: vi.fn() }),
}));

import { apiClient } from '../api/apiClient';
import { WorkflowRunPage } from '../pages/WorkflowRunPage';
import { FULL_GRAPH_DESCRIPTOR, CHILD_GRAPH_DESCRIPTOR } from './fixtures/graphDescriptor';

function Wrapper({ children, runId = 'run-1' }: { children: ReactNode; runId?: string }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[`/projects/p1/runs/${runId}/workflow`]}>
        <Routes>
          <Route path="/projects/:projectId/runs/:runId/workflow" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

function baseRunMock() {
  vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
    {
      workflow_run_id: 'run-1',
      execution_id: 'exec-1',
      task: 'Some task',
      status: 'completed',
      agent_name: 'Neo',
      started_at: '2026-06-17T00:00:00Z',
    },
  ]);
  vi.mocked(apiClient.getTeam).mockResolvedValue({
    project_id: 'p1',
    project_name: 'Demo',
    members: [],
  } as unknown as Awaited<ReturnType<typeof apiClient.getTeam>>);
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
}

beforeEach(() => {
  vi.clearAllMocks();
  baseRunMock();
});

afterEach(() => cleanup());

describe('WorkflowRunPage — descriptor-driven graph renderer', () => {
  it('renders full-variant node labels (Agent/Rai/Human Review/Merge/Scribe) from fixture descriptor', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'exec-1',
      status: 'completed',
      parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(FULL_GRAPH_DESCRIPTOR);

    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Human Review'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Agent');
    expect(text).toContain('Rai');
    expect(text).toContain('Human Review');
    expect(text).toContain('Merge');
    expect(text).toContain('Scribe');
    // Child-only node must NOT appear in the full graph
    expect(text).not.toContain('Assemble-ready');
  });

  it('fixture has correct node_type values: agent=agent, merge/scribe=action (action nodes are visually smaller)', async () => {
    // The DOM rendering of node_type is tested directly in WorkflowGraphPanel.test.tsx.
    // Here we verify the fixture node_type values that drive shape+size in the renderer.
    const agentNode = FULL_GRAPH_DESCRIPTOR.nodes.find(n => n.id === 'agent');
    const raiNode   = FULL_GRAPH_DESCRIPTOR.nodes.find(n => n.id === 'rai');
    const mergeNode = FULL_GRAPH_DESCRIPTOR.nodes.find(n => n.id === 'merge');
    const scribeNode = FULL_GRAPH_DESCRIPTOR.nodes.find(n => n.id === 'scribe');
    expect(agentNode?.node_type).toBe('agent');  // primary/largest card
    expect(raiNode?.node_type).toBe('gate');     // gate/decision shape
    expect(mergeNode?.node_type).toBe('action'); // visually smaller secondary node
    expect(scribeNode?.node_type).toBe('action'); // visually smaller secondary node
  });

  it('renders child-variant node labels (Agent/Rai/Assemble-ready) and excludes Human Review/Merge/Scribe', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'exec-1',
      status: 'parked',
      parent_run_id: 'coord-1',
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(CHILD_GRAPH_DESCRIPTOR);

    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Assemble-ready'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Agent');
    expect(text).toContain('Rai');
    expect(text).toContain('Assemble-ready');
    // Full-pipeline nodes must NOT appear in the child graph
    expect(text).not.toContain('Human Review');
    expect(text).not.toContain('Merge Coordinator');
    expect(text).not.toContain('Session Logger');
    // Verify the fixture has correct node_type for the terminal node
    const terminalNode = CHILD_GRAPH_DESCRIPTOR.nodes.find(n => n.id === 'assemble-ready');
    expect(terminalNode?.node_type).toBe('terminal');
  });

  it('falls back to hardcoded graph when getRunGraph returns null (404)', async () => {
    vi.mocked(apiClient.getRun).mockResolvedValue({
      run_id: 'exec-1',
      status: 'completed',
      parent_run_id: null,
    } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
    // null simulates a 404 — the page must fall back to the hardcoded full-graph EXECUTORS.
    vi.mocked(apiClient.getRunGraph).mockResolvedValue(null);

    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    // Hardcoded full graph has 'Rai' as a label — wait for it to appear.
    await waitFor(
      () => expect(document.body.textContent).toContain('Rai'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // All 5 hardcoded executor labels must appear
    expect(text).toContain('Agent');
    expect(text).toContain('Rai');
    // Hardcoded roleDescription for review, merge, scribe
    expect(text).toContain('Human Review');
    expect(text).toContain('Merge Coordinator');
    expect(text).toContain('Session Logger');
  });
});
