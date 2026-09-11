import { ClusterTopologyGraph } from '../components/ClusterTopologyGraph';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { findConnectorJunctions } from '../utils/dagLayout';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ComponentType, ReactNode } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { KubernetesTopologyDto } from '../api/types';

const flowCapture = vi.hoisted(() => ({
  edges: [] as Array<{ type?: string; sourceHandle?: string; targetHandle?: string }>,
  nodes: [] as Node[],
  edgeTypes: {} as Record<string, unknown>,
}));

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
      edges,
      nodeTypes,
      edgeTypes = {},
    }: {
      nodes: Node[];
      edges: Array<{ id: string; type?: string; sourceHandle?: string; targetHandle?: string; style?: { stroke?: string } }>;
      nodeTypes: Record<string, ComponentType<{ data: Record<string, unknown> }>>;
      edgeTypes?: Record<string, unknown>;
    }) => {
      flowCapture.edges = edges;
      flowCapture.nodes = nodes;
      flowCapture.edgeTypes = edgeTypes;
      return (
        <div data-testid="mock-reactflow">
          <output data-testid="topology-edges">{edges.map((edge) => edge.id).join('|')}</output>
          <output data-testid="topology-edge-styles">
            {edges.map((edge) => `${edge.id}:${edge.style?.stroke ?? ''}`).join('|')}
          </output>
          {nodes.map((node) => {
            const NodeComponent = nodeTypes[node.type ?? ''];
            return <NodeComponent key={node.id} data={node.data} />;
          })}
        </div>
      );
    },
  };
});

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

const topology: KubernetesTopologyDto = {
  generated_utc: '2026-09-11T00:00:00Z',
  namespace: 'agentweaver',
  requested_layers: ['runtime', 'networking', 'workloads', 'storage', 'autoscaling'],
  layers: [
    { name: 'runtime', status: 'available', resource_count: 5, message: 'available' },
    { name: 'networking', status: 'available', resource_count: 7, message: 'available' },
    { name: 'workloads', status: 'available', resource_count: 1, message: 'available' },
    { name: 'storage', status: 'available', resource_count: 2, message: 'available' },
    { name: 'autoscaling', status: 'available', resource_count: 1, message: 'available' },
    { name: 'availability', status: 'not_requested', resource_count: 0, message: 'not requested' },
  ],
  nodes: [
    {
      id: 'pod:agent-abc123',
      layer: 'runtime',
      type: 'Pod',
      api_version: 'v1',
      name: 'agent-abc123',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Pod · Running',
      details: {},
    },
    {
      id: 'template:agent-host',
      layer: 'runtime',
      type: 'SandboxTemplate',
      api_version: 'extensions.agents.x-k8s.io/v1beta1',
      name: 'agentweaver-agent-host',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'SandboxTemplate',
      details: {},
    },
    {
      id: 'pool:agent-host',
      layer: 'runtime',
      type: 'SandboxWarmPool',
      api_version: 'extensions.agents.x-k8s.io/v1beta1',
      name: 'agentweaver-agent-host',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'SandboxWarmPool · 2/2 ready',
      details: {},
    },
    {
      id: 'claim:agent-abc123',
      layer: 'runtime',
      type: 'SandboxClaim',
      api_version: 'extensions.agents.x-k8s.io/v1beta1',
      name: 'claim-abc123',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'SandboxClaim · ready',
      details: {},
    },
    {
      id: 'gateway:public',
      layer: 'networking',
      type: 'Gateway',
      api_version: 'gateway.networking.k8s.io/v1',
      name: 'agentweaver-gateway',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Gateway · 1 listener',
      details: {},
    },
    {
      id: 'service:api',
      layer: 'networking',
      type: 'Service',
      api_version: 'v1',
      name: 'agentweaver-api',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Service · ClusterIP',
      details: {},
    },
    {
      id: 'service:mcp',
      layer: 'networking',
      type: 'Service',
      api_version: 'v1',
      name: 'agentweaver-mcp',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Service · ClusterIP',
      details: {},
    },
    {
      id: 'policy:default-deny',
      layer: 'networking',
      type: 'NetworkPolicy',
      api_version: 'networking.k8s.io/v1',
      name: 'default-deny-ingress',
      namespace: 'agentweaver',
      health: 'unknown',
      summary: 'NetworkPolicy · ingress',
      details: {
        selector: 'app.kubernetes.io/part-of=agentweaver',
        direction: 'ingress',
        effect: 'deny',
        trafficImpact: 'default_deny_ingress',
      },
    },
    {
      id: 'policy:gateway-api',
      layer: 'networking',
      type: 'NetworkPolicy',
      api_version: 'networking.k8s.io/v1',
      name: 'allow-gateway-to-api',
      namespace: 'agentweaver',
      health: 'unknown',
      summary: 'NetworkPolicy · ingress',
      details: {
        selector: 'app=agentweaver-api',
        direction: 'ingress',
        effect: 'allow',
        trafficImpact: 'gateway_ingress',
      },
    },
    {
      id: 'deployment:api',
      layer: 'workloads',
      type: 'Deployment',
      api_version: 'apps/v1',
      name: 'agentweaver-api',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Deployment · 2/2 available',
      details: {},
    },
    {
      id: 'pvc:data',
      layer: 'storage',
      type: 'PersistentVolumeClaim',
      api_version: 'v1',
      name: 'agentweaver-data',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'PVC · Bound',
      details: {},
    },
    {
      id: 'pvc:workspace',
      layer: 'storage',
      type: 'PersistentVolumeClaim',
      api_version: 'v1',
      name: 'agentweaver-workspace',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'PVC · Bound',
      details: {},
    },
    {
      id: 'hpa:worker',
      layer: 'autoscaling',
      type: 'HorizontalPodAutoscaler',
      api_version: 'autoscaling/v2',
      name: 'agentweaver-worker',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'HPA · 2/3 current/desired',
      details: {},
    },
  ],
  edges: [],
  truncated: false,
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('ClusterTopologyGraph', () => {
  it('groups the live Kubernetes inventory into Agentweaver functions', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    expect(screen.getByTestId('cluster-topology-viewport')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-agent-execution')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-sandbox-template')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-sandbox-pool')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-sandbox-claim')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-sandbox')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-public-entry')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-service-targets')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-network-policies')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-control-plane')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-workload-pods')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-application-state')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-session-artifacts')).toBeTruthy();
    expect(screen.getByTestId('cluster-topology-node-capacity-scaling')).toBeTruthy();
    expect(screen.queryByText('ReplicaSet')).toBeNull();
    expect(screen.queryByText('agent-abc123')).toBeNull();
    expect(screen.queryByText('agentweaver-api')).toBeNull();
    expect(flowCapture.edges).not.toHaveLength(0);
    expect(flowCapture.edges.every((edge) => edge.type === 'spine')).toBe(true);
    expect(flowCapture.edges.every((edge) => edge.sourceHandle && edge.targetHandle)).toBe(true);
    expect(flowCapture.edgeTypes.spine).toBeTypeOf('function');

    for (const id of [
      'agent-execution',
      'sandbox-template',
      'sandbox-pool',
      'sandbox-claim',
      'sandbox',
      'public-entry',
      'service-targets',
      'network-policies',
      'control-plane',
      'application-state',
      'session-artifacts',
      'capacity-scaling',
    ]) {
      const icon = screen.getByTestId(`cluster-topology-icon-${id}`);
      expect(icon.getAttribute('data-icon-source')).toBe('iconcloud');
      expect(icon.getAttribute('src')).toMatch(/^(?:data:image\/svg\+xml,|.*\.svg$)/);
    }
  });

  it('marks only the actual Agent Execution split, not independent route elbows', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    const junctions = findConnectorJunctions(flowCapture.edges as Edge[], flowCapture.nodes);
    const points = [...junctions.values()].flat();

    expect(points).toHaveLength(1);
    expect(junctions.get('agent-execution->sandbox-claim')).toHaveLength(1);
    expect(junctions.has('control-plane->application-state')).toBe(false);
    expect(junctions.has('control-plane->workload-pods')).toBe(false);
  });

  it('keeps traffic concise while showing NetworkPolicy allow and deny impact', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    expect(screen.getByText('Public entry & gateway')).toBeTruthy();
    expect(screen.getByText('Agentweaver service targets')).toBeTruthy();
    expect(screen.getByText('Network policy guardrails')).toBeTruthy();
    expect(screen.getByText('1 gateway allow policy')).toBeTruthy();
    expect(screen.getByText('Other inbound traffic blocked')).toBeTruthy();
    expect(screen.getByTestId('topology-edge-styles').textContent)
      .toContain('public-entry->service-targets:var(--colorPaletteGreenBorder2)');

    fireEvent.click(screen.getByTestId('cluster-topology-node-network-policies'));
    const inspector = screen.getByLabelText('Network policy guardrails resource details');
    const policies = within(inspector).getByRole('list', { name: 'Ingress network policy details' });
    expect(within(policies).getByText('default-deny-ingress')).toBeTruthy();
    expect(within(policies).getByText('ingress · deny · default deny ingress')).toBeTruthy();
    expect(within(policies).getByText('allow-gateway-to-api')).toBeTruthy();
    expect(within(policies).getByText('Selector: app=agentweaver-api')).toBeTruthy();
  });

  it('uses only application persistence and workspace artifacts for storage', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    expect(screen.getByText('Application persistence')).toBeTruthy();
    expect(screen.getByText('Sandbox & session artifacts')).toBeTruthy();
    expect(screen.queryByText('PersistentVolume')).toBeNull();
    expect(screen.queryByText('StorageClass')).toBeNull();
    expect(screen.getByTestId('topology-edges').textContent).toContain('control-plane->application-state');
    expect(screen.getByTestId('topology-edges').textContent).toContain('agent-execution->session-artifacts');
  });
});
