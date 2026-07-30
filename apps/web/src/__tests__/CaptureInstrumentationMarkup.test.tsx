import { apiClient } from '../api/apiClient';
import { CoordinatorTopologyGraph } from '../components/CoordinatorTopologyGraph';
import { TransactionTracePanel } from '../components/runs/TransactionTracePanel';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ComponentType, ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunTraces: vi.fn(),
    steerCoordinator: vi.fn(),
  },
}));

vi.mock('@xyflow/react', async (importActual) => {
  const actual = await importActual<typeof import('@xyflow/react')>();
  return {
    ...actual,
    Handle: () => null,
    Panel: ({ children }: { children: ReactNode }) => <>{children}</>,
    ReactFlow: ({
      nodes,
      nodeTypes,
      children,
    }: {
      nodes: Array<{ id: string; type?: string; data: Record<string, unknown> }>;
      nodeTypes: Record<string, ComponentType<{ data: Record<string, unknown> }>>;
      children: ReactNode;
    }) => (
      <div>
        {nodes.map((node) => {
          const NodeComponent = nodeTypes[node.type ?? ''];
          return <NodeComponent key={node.id} data={node.data} />;
        })}
        {children}
      </div>
    ),
    useReactFlow: () => ({
      fitView: vi.fn(),
      getNode: vi.fn(),
      setCenter: vi.fn(),
      zoomIn: vi.fn(),
      zoomOut: vi.fn(),
      zoomTo: vi.fn(),
    }),
    useStore: (selector: (state: { transform: [number, number, number] }) => unknown) =>
      selector({ transform: [0, 0, 1] }),
  };
});

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

afterEach(() => {
  vi.clearAllMocks();
});

describe('capture instrumentation markup', () => {
  it('exposes stable topology graph, identity, kind, and raw status attributes', () => {
    render(
      <Wrapper>
        <CoordinatorTopologyGraph
          projectId="project-1"
          coordinatorRunId="run-1"
          nodes={[
            { id: 'coordinator', kind: 'coordinator', title: 'Coordinator', status: 'running' },
            { id: 'task-1', kind: 'subtask', title: 'Build preview', status: 'completed' },
          ]}
          edges={[]}
        />
      </Wrapper>,
    );

    expect(screen.getByTestId('coordinator-topology-graph')).toBeTruthy();
    const nodes = screen.getAllByTestId('topology-node');
    expect(nodes[0].getAttribute('data-node-id')).toBe('coordinator');
    expect(nodes[0].getAttribute('data-node-kind')).toBe('coordinator');
    expect(nodes[0].getAttribute('data-node-status')).toBe('running');
    expect(nodes[1].getAttribute('data-node-status')).toBe('completed');
  });

  it('exposes stable trace tree/span attributes and selected state', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-1',
      spans: [{
        id: 'span-1',
        name: 'Coordinator',
        spanType: 'invoke-agent',
        timestamp: '2026-07-30T00:00:00Z',
        durationMs: 1200,
        success: true,
        agentName: 'Coordinator',
      }],
    });

    render(
      <Wrapper>
        <TransactionTracePanel runId="run-1" />
      </Wrapper>,
    );
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByTestId('transaction-trace-panel')).toBeTruthy();
    expect(screen.getByTestId('trace-tree')).toBeTruthy();
    const span = screen.getByTestId('trace-span');
    expect(span.getAttribute('data-span-key')).toBe('span-1');
    expect(span.getAttribute('data-span-type')).toBe('invoke-agent');
    expect(span.getAttribute('data-selected')).toBe('false');

    fireEvent.click(span);
    expect(span.getAttribute('data-selected')).toBe('true');
    expect(span.getAttribute('aria-pressed')).toBe('true');
  });
});
