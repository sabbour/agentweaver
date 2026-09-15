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
      details: {
        ready: '2/2',
        restartCount: '0',
        ageSeconds: '120',
        nodeName: 'aks-katapool-12319583-vmss00004d',
        runtimeClassName: 'kata-vm-isolation',
        containers: 'agentweaver-agent-host, agentweaver-exec',
        imageTags: 'v0.32.2',
        deployment: 'agentweaver-agent-host',
      },
    },
    {
      id: 'sandbox:agent-abc123',
      layer: 'runtime',
      type: 'Sandbox',
      api_version: 'agents.x-k8s.io/v1beta1',
      name: 'agent-abc123',
      namespace: 'agentweaver',
      health: 'healthy',
      summary: 'Sandbox · Ready',
      details: {
        podName: 'agent-abc123',
        nodeName: 'aks-katapool-12319583-vmss00004d',
        runtimeClassName: 'kata-vm-isolation',
        isolationBackend: 'Kata VM isolation',
        containers: 'agentweaver-agent-host, agentweaver-exec',
        status: 'available',
      },
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
      details: {
        imageTags: 'v0.32.2',
        runtimeClassName: 'kata-vm-isolation',
        resourceRequests: 'cpu 300m, memory 1Gi',
        resourceLimits: 'cpu 800m, memory 2Gi',
        mounts: '/workspace, /local-workspace',
        policy: 'env injection Allowed; network Managed',
      },
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
      details: {
        replicas: '2',
        readyReplicas: '2',
        availableReplicas: '1',
        template: 'agentweaver-agent-host',
        minReplicas: '1',
        maxReplicas: '4',
        lastScaleEvent: 'Not reported by controller',
      },
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
      details: {
        runId: 'run-abc123',
        boundSandbox: 'agent-abc123',
        warmPool: 'agentweaver-agent-host',
        phase: 'bound',
        ageSeconds: '90',
      },
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
      details: {
        replicas: '2',
        readyReplicas: '2',
        availableReplicas: '2',
        imageTags: 'v0.32.2',
        lastRolloutUtc: '2026-09-10T12:00:00Z',
      },
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

  it('marks the Agent Execution split without duplicating separated lane stubs or independent route elbows', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    const junctions = findConnectorJunctions(flowCapture.edges as Edge[], flowCapture.nodes);
    const points = [...junctions.values()].flat();

    expect(points).toHaveLength(1);
    expect(junctions.get('agent-execution->sandbox-claim')).toEqual([{ x: 566, y: 456 }]);
    expect(junctions.has('agent-execution->session-artifacts')).toBe(false);
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

  it('renders enriched calm detail for healthy workload pods without the generic Function row', () => {
    render(<Wrapper><ClusterTopologyGraph topology={topology} /></Wrapper>);

    fireEvent.click(screen.getByTestId('cluster-topology-node-workload-pods'));
    const inspector = screen.getByLabelText('Agentweaver workload pods resource details');

    expect(within(inspector).getByText('Last updated')).toBeTruthy();
    expect(within(inspector).getByText('agentweaver-agent-host')).toBeTruthy();
    expect(within(inspector).getByText('agent-abc123')).toBeTruthy();
    expect(within(inspector).getByText('2/2')).toBeTruthy();
    expect(within(inspector).getByText('0')).toBeTruthy();
    expect(within(inspector).getByText('aks-katapool-12319583-vmss00004d')).toBeTruthy();
    expect(within(inspector).getByText('v0.32.2')).toBeTruthy();
    expect(within(inspector).queryByText('Function')).toBeNull();
    expect(within(inspector).queryByRole('alert')).toBeNull();
  });

  it('sorts unhealthy workload pods first and makes restart counts prominent', () => {
    const mixedTopology: KubernetesTopologyDto = {
      ...topology,
      nodes: [
        ...topology.nodes,
        {
          id: 'pod:agent-bad456',
          layer: 'runtime',
          type: 'Pod',
          api_version: 'v1',
          name: 'agent-bad456',
          namespace: 'agentweaver',
          health: 'attention',
          summary: 'Pod · Running',
          details: {
            ready: '0/1',
            restartCount: '7',
            ageSeconds: '360',
            nodeName: 'aks-apppool-17502699-vmss000012',
            containers: 'worker',
            imageTags: 'v0.32.2',
            deployment: 'agentweaver-worker',
          },
        },
      ],
    };

    render(<Wrapper><ClusterTopologyGraph topology={mixedTopology} /></Wrapper>);

    fireEvent.click(screen.getByTestId('cluster-topology-node-workload-pods'));
    const inspector = screen.getByLabelText('Agentweaver workload pods resource details');
    const rows = within(inspector).getAllByRole('row');

    expect(rows[1].textContent).toContain('agent-bad456');
    expect(within(inspector).getByRole('alert').textContent).toContain('agent-bad456');
    expect(within(inspector).getByText('7 restarts')).toBeTruthy();
  });

  it('renders zero sandbox claims as a clear calm empty state', () => {
    const idleTopology: KubernetesTopologyDto = {
      ...topology,
      nodes: topology.nodes.filter((node) => node.type !== 'SandboxClaim'),
    };

    render(<Wrapper><ClusterTopologyGraph topology={idleTopology} /></Wrapper>);

    fireEvent.click(screen.getByTestId('cluster-topology-node-sandbox-claim'));
    const inspector = screen.getByLabelText('Sandbox claims resource details');

    expect(within(inspector).getByText('No sandbox claims are active')).toBeTruthy();
    expect(within(inspector).getByText(/legitimate idle state/i)).toBeTruthy();
    expect(within(inspector).queryByRole('alert')).toBeNull();
  });

  it('names a partial detail fetch while still rendering available detail', () => {
    const partialTopology: KubernetesTopologyDto = {
      ...topology,
      layers: topology.layers.map((layer) =>
        layer.name === 'runtime'
          ? { ...layer, status: 'partial', message: 'Pod detail read timed out after 8s.' }
          : layer),
    };

    render(<Wrapper><ClusterTopologyGraph topology={partialTopology} /></Wrapper>);

    fireEvent.click(screen.getByTestId('cluster-topology-node-workload-pods'));
    const inspector = screen.getByLabelText('Agentweaver workload pods resource details');

    expect(within(inspector).getByRole('alert').textContent)
      .toContain('Runtime detail fetch partially failed: Pod detail read timed out after 8s.');
    expect(within(inspector).getByText('agent-abc123')).toBeTruthy();
  });
});
