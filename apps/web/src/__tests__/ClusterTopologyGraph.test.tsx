import { ClusterTopologyGraph } from '../components/ClusterTopologyGraph';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ComponentType, ReactNode } from 'react';
import type { KubernetesTopologyDto } from '../api/types';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('@xyflow/react', async (importActual) => {
  const actual = await importActual<typeof import('@xyflow/react')>();
  return {
    ...actual,
    Handle: () => null,
    ReactFlow: ({
      nodes,
      nodeTypes,
      onNodeClick,
    }: {
      nodes: Array<{ id: string; type?: string; data: Record<string, unknown> }>;
      nodeTypes: Record<string, ComponentType<{ data: Record<string, unknown> }>>;
      onNodeClick?: (event: unknown, node: { data: Record<string, unknown> }) => void;
    }) => (
      <div data-testid="mock-reactflow">
        {nodes.map((node) => {
          const NodeComponent = nodeTypes[node.type ?? ''];
          return (
            <button key={node.id} onClick={() => onNodeClick?.({}, node)}>
              <NodeComponent data={node.data} />
            </button>
          );
        })}
      </div>
    ),
  };
});

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

const topology: KubernetesTopologyDto = {
  generated_utc: '2026-09-10T00:00:00Z',
  namespace: 'agentweaver',
  requested_layers: ['runtime', 'networking'],
  layers: [
    { name: 'runtime', status: 'available', resource_count: 1, message: 'available' },
    { name: 'networking', status: 'partial', resource_count: 1, message: 'Gateway API unavailable' },
    { name: 'workloads', status: 'not_requested', resource_count: 0, message: 'not requested' },
    { name: 'storage', status: 'not_requested', resource_count: 0, message: 'not requested' },
    { name: 'autoscaling', status: 'not_requested', resource_count: 0, message: 'not requested' },
    { name: 'availability', status: 'not_requested', resource_count: 0, message: 'not requested' },
  ],
  nodes: [
    {
      id: 'v1:Pod:agentweaver:web-1',
      layer: 'runtime',
      type: 'Pod',
      api_version: 'v1',
      name: 'web-1',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Pod · Running',
      details: { phase: 'Running', serviceAccount: 'web' },
    },
    {
      id: 'v1:Service:agentweaver:web',
      layer: 'networking',
      type: 'Service',
      api_version: 'v1',
      name: 'web',
      namespace: 'agentweaver',
      health: 'unknown',
      summary: 'Service · ClusterIP · 1 ports',
      details: { type: 'ClusterIP' },
    },
  ],
  edges: [{
    id: 'service-selects-pod',
    source: 'v1:Service:agentweaver:web',
    target: 'v1:Pod:agentweaver:web-1',
    type: 'selects',
    inferred: true,
    summary: 'Selector match',
  }],
  truncated: false,
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('ClusterTopologyGraph', () => {
  it('renders layer status and typed resource nodes', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    expect(screen.getByTestId('cluster-topology-viewport')).toBeTruthy();
    expect(screen.getByText('runtime: 1')).toBeTruthy();
    expect(screen.getByText('networking: 1')).toBeTruthy();
    expect(screen.getByLabelText('web-1: Pod · Running')).toBeTruthy();
    expect(screen.getByLabelText('web: Service · ClusterIP · 1 ports')).toBeTruthy();
  });

  it('opens a safe resource drill-down card when a node is selected', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    fireEvent.click(screen.getByLabelText('web-1: Pod · Running'));

    const card = screen.getByLabelText('web-1 resource details');
    expect(card).toBeTruthy();
    expect(within(card).getByText('serviceAccount')).toBeTruthy();
    expect(within(card).getByText('web')).toBeTruthy();
    expect(screen.queryByText(/manifest/i)).toBeNull();
  });
});
