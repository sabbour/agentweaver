import {
  buildClusterTopology,
  ClusterTopologyGraph,
  initiallyExpandedTopologyIds,
  TOPOLOGY_EXPANDED_HEIGHT,
  TOPOLOGY_NODE_HEIGHT,
  TOPOLOGY_ROW_GAP,
} from '../components/ClusterTopologyGraph';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ComponentType, ReactNode } from 'react';
import type { ClusterDiagnosticsDto, WarmPoolInstanceDto } from '../api/types';

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
      className,
      nodes,
      nodeTypes,
    }: {
      className?: string;
      nodes: Array<{
        id: string;
        type?: string;
        data: Record<string, unknown>;
        position: { x: number; y: number };
        style?: { height?: string | number };
      }>;
      nodeTypes: Record<string, ComponentType<{ id: string; data: Record<string, unknown> }>>;
    }) => (
      <div className={className} data-testid="mock-reactflow">
        {nodes.map((node) => {
          const NodeComponent = nodeTypes[node.type ?? ''];
          return (
            <div
              key={node.id}
              data-testid={`flow-node-${node.id}`}
              data-position={`${node.position.x},${node.position.y}`}
              data-height={node.style?.height}
            >
              <NodeComponent id={node.id} data={node.data} />
            </div>
          );
        })}
      </div>
    ),
  };
});

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>{children}</MemoryRouter>
    </AzureFluentProvider>
  );
}

function createData(instances: WarmPoolInstanceDto[]): ClusterDiagnosticsDto {
  const claimedInstances = instances.filter((instance) => instance.claim_name);
  return {
    generated_utc: '2026-08-31T00:00:00.000Z',
    total_duration_ms: 42,
    checks: [
      { name: 'K8s API', status: 'healthy', message: 'Reachable', latencyMs: 5 },
      { name: 'PostgreSQL', status: 'healthy', message: 'Connected', latencyMs: 8 },
      { name: 'Key Vault', status: 'healthy', message: 'Loaded', latencyMs: 4 },
    ],
    active_agent_pods: claimedInstances.map((instance, index) => ({
      claim_name: instance.claim_name ?? `claim-${index + 1}`,
      pod_name: `agent-${String(index + 1).padStart(3, '0')}`,
      run_id: instance.run_id ?? null,
      status: 'ready',
      age_seconds: 60,
    })),
    orphaned_agent_pods: [],
    pending_capacity_runs: [],
    warm_pools: [
      {
        name: 'agentweaver-agent-host',
        desired_replicas: Math.max(instances.length, 1),
        ready_replicas: instances.filter((instance) => instance.status !== 'warming').length,
        available_replicas: instances.filter((instance) => instance.status === 'available').length,
        status: 'healthy',
        instances,
        age_seconds: 300,
      },
    ],
    sandbox_claims: claimedInstances.map((instance) => ({
      name: instance.claim_name!,
      phase: 'bound',
      ready: true,
      run_id: instance.run_id ?? null,
      bound_sandbox: instance.name,
      warm_pool: 'agentweaver-agent-host',
      age_seconds: 120,
    })),
  };
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('ClusterTopologyGraph', () => {
  it('renders a sized graph viewport and mixed claimed/unclaimed instance topology', () => {
    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={createData([
            {
              name: 'sandbox-available',
              status: 'available',
              claimed: false,
              age_seconds: 120,
            },
            {
              name: 'sandbox-claimed',
              status: 'claimed',
              claimed: true,
              claim_name: 'claim-001',
              run_id: 'run-001',
              project_id: 'proj-001',
              age_seconds: 180,
            },
          ])}
        />
      </Wrapper>,
    );

    expect(screen.getByTestId('cluster-topology-viewport')).toBeTruthy();
    expect(screen.getByTestId('mock-reactflow')).toBeTruthy();
    expect(screen.getByLabelText('Cluster: 3 / 3 checks healthy')).toBeTruthy();
    expect(screen.getByLabelText('agentweaver-agent-host: Warm pool · 2 / 2 ready')).toBeTruthy();
    expect(screen.getAllByLabelText('sandbox-available: Warm instance · available')).toHaveLength(2);
    expect(screen.getAllByLabelText('sandbox-claimed: Warm instance · claimed')).toHaveLength(2);
    expect(screen.getByLabelText('claim-001: Sandbox claim · bound')).toBeTruthy();
    expect(screen.getByLabelText('agent-001: Agent pod · ready')).toBeTruthy();
    expect(screen.getAllByRole('link', { name: 'run-001' })).toHaveLength(1);
    expect(screen.getAllByRole('link', { name: 'View run' })).toHaveLength(2);
    expect(screen.getByText('Unclaimed warm instance')).toBeTruthy();
  });

  it('initially expands resources that need attention plus claimed and bound relationships', () => {
    const data = createData([
      {
        name: 'sandbox-available',
        status: 'available',
        claimed: false,
        age_seconds: 120,
      },
      {
        name: 'sandbox-claimed',
        status: 'claimed',
        claimed: true,
        claim_name: 'claim-001',
        run_id: 'run-001',
        project_id: 'proj-001',
        age_seconds: 180,
      },
      {
        name: 'sandbox-unavailable',
        status: 'unavailable',
        claimed: false,
        age_seconds: 30,
      },
    ]);

    render(<Wrapper><ClusterTopologyGraph data={data} /></Wrapper>);

    expect(screen.getByTestId('cluster-topology-toggle-cluster').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('button', { name: /Expand sandbox-available/ }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('button', { name: /Collapse sandbox-claimed/ }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('button', { name: /Collapse sandbox-unavailable/ }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('button', { name: /Collapse claim-001/ }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('Not resolved')).toBeTruthy();
  });

  it('discloses the reason for unhealthy cluster checks on first render', () => {
    const data = createData([]);
    data.checks = [
      {
        name: 'Warm pool',
        status: 'critical',
        message: 'No available agent-host sandboxes',
        latencyMs: 14,
      },
    ];

    render(<Wrapper><ClusterTopologyGraph data={data} /></Wrapper>);

    expect(screen.getByRole('button', { name: /Collapse Cluster/ }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('No available agent-host sandboxes')).toBeTruthy();
  });

  it('toggles cards independently with mouse and keyboard while keeping multiple cards expanded', async () => {
    const user = userEvent.setup();
    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={createData([{
            name: 'sandbox-available',
            status: 'available',
            claimed: false,
            age_seconds: 120,
          }])}
        />
      </Wrapper>,
    );

    const cluster = screen.getByRole('button', { name: /Expand Cluster/ });
    const pool = screen.getByRole('button', { name: /Expand agentweaver-agent-host/ });
    await user.click(cluster);
    pool.focus();
    await user.keyboard('{Enter}');

    expect(cluster.getAttribute('aria-expanded')).toBe('true');
    expect(pool.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByTestId('cluster-topology-details-cluster')).toBeTruthy();
    expect(screen.getByText('Allocated')).toBeTruthy();

    await user.keyboard(' ');
    expect(pool.getAttribute('aria-expanded')).toBe('false');
    expect(cluster.getAttribute('aria-expanded')).toBe('true');
  });

  it('preserves user expansion state when polling replaces diagnostics objects', async () => {
    const user = userEvent.setup();
    const first = createData([{
      name: 'sandbox-available',
      status: 'available',
      claimed: false,
      age_seconds: 120,
    }]);
    const { rerender } = render(<Wrapper><ClusterTopologyGraph data={first} /></Wrapper>);
    await user.click(screen.getByRole('button', { name: /Expand sandbox-available/ }));

    rerender(
      <Wrapper>
        <ClusterTopologyGraph
          data={{
            ...first,
            generated_utc: '2026-08-31T00:00:30.000Z',
            warm_pools: first.warm_pools?.map((pool) => ({
              ...pool,
              instances: pool.instances?.map((instance) => ({ ...instance, age_seconds: 150 })),
            })),
          }}
        />
      </Wrapper>,
    );

    expect(screen.getByRole('button', { name: /Collapse sandbox-available/ }).getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('2m')).toBeTruthy();
  });

  it('reflows expanded cards in their own columns without overlap or unstable edges', () => {
    const data = createData([
      { name: 'one', status: 'claimed', claimed: true, claim_name: 'claim-one', age_seconds: 30 },
      { name: 'two', status: 'claimed', claimed: true, claim_name: 'claim-two', age_seconds: 40 },
      { name: 'three', status: 'available', claimed: false, age_seconds: 50 },
    ]);
    const expandedIds = initiallyExpandedTopologyIds(data);
    const model = buildClusterTopology(data, { expandedIds, onToggle: vi.fn() });
    const instances = model.nodes.filter((node) => node.id.startsWith('instance-'));

    expect(instances.map((node) => node.position.y)).toEqual([
      0,
      TOPOLOGY_EXPANDED_HEIGHT + TOPOLOGY_ROW_GAP,
      (TOPOLOGY_EXPANDED_HEIGHT + TOPOLOGY_ROW_GAP) * 2,
    ]);
    expect(instances[0].style?.height).toBe(TOPOLOGY_EXPANDED_HEIGHT);
    expect(instances[2].style?.height).toBe(TOPOLOGY_NODE_HEIGHT);
    expect(new Set(model.edges.map((edge) => edge.id)).size).toBe(model.edges.length);
    expect(model.edges.every((edge) =>
      model.nodes.some((node) => node.id === edge.source)
      && model.nodes.some((node) => node.id === edge.target))).toBe(true);
  });

  it('uses a narrow-viewport-safe responsive card width and stable disclosure markup', async () => {
    const user = userEvent.setup();
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 320 });
    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={createData([{
            name: 'sandbox-available',
            status: 'available',
            claimed: false,
            age_seconds: 120,
          }])}
        />
      </Wrapper>,
    );

    await user.click(screen.getByRole('button', { name: /Expand sandbox-available/ }));
    const card = screen.getByTestId(/cluster-topology-node-instance-/);
    expect(getComputedStyle(card).width).not.toBe('0px');
    expect(screen.getByTestId(/cluster-topology-details-instance-/).textContent).toMatchInlineSnapshot(
      `"StateavailableClaimUnclaimedRunNoneProjectNot resolvedAge2mPoolagentweaver-agent-host"`,
    );
  });

  it('preserves full long node names for hover and wrapping', () => {
    const longPoolName = 'agentweaver-agent-host-westus3-warm-pool-abcdef1234567890';
    const longInstanceName = 'agentweaver-agent-host-westus3-sandbox-available-abcdef1234567890';
    const longClaimName = 'sandboxclaim-agentweaver-agent-host-westus3-abcdef1234567890';
    const longPodName = 'agentweaver-agent-host-westus3-pod-abcdef1234567890';

    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={{
            generated_utc: '2026-08-31T00:00:00.000Z',
            total_duration_ms: 42,
            checks: [
              { name: 'K8s API', status: 'healthy', message: 'Reachable', latencyMs: 5 },
            ],
            active_agent_pods: [
              {
                claim_name: longClaimName,
                pod_name: longPodName,
                run_id: 'run-001',
                status: 'ready',
                age_seconds: 60,
              },
            ],
            orphaned_agent_pods: [],
            pending_capacity_runs: [],
            warm_pools: [
              {
                name: longPoolName,
                desired_replicas: 1,
                ready_replicas: 1,
                available_replicas: 0,
                status: 'healthy',
                instances: [
                  {
                    name: longInstanceName,
                    status: 'claimed',
                    claimed: true,
                    claim_name: longClaimName,
                    run_id: 'run-001',
                    project_id: 'proj-001',
                    age_seconds: 180,
                  },
                ],
                age_seconds: 300,
              },
            ],
            sandbox_claims: [
              {
                name: longClaimName,
                phase: 'bound',
                ready: true,
                run_id: 'run-001',
                bound_sandbox: longInstanceName,
                warm_pool: longPoolName,
                age_seconds: 120,
              },
            ],
          }}
        />
      </Wrapper>,
    );

    expect(screen.getAllByText(longPoolName).some((element) => element.getAttribute('title') === longPoolName)).toBe(true);
    expect(screen.getAllByText(longInstanceName).some((element) => element.getAttribute('title') === longInstanceName)).toBe(true);
    expect(screen.getAllByText(longClaimName).some((element) => element.getAttribute('title') === longClaimName)).toBe(true);
    expect(screen.getByText(longPodName).getAttribute('title')).toBe(longPodName);
  });

  it('renders correctly when a pool has zero unclaimed instances', () => {
    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={createData([
            {
              name: 'sandbox-claimed',
              status: 'claimed',
              claimed: true,
              claim_name: 'claim-001',
              run_id: 'run-001',
              project_id: 'proj-001',
              age_seconds: 180,
            },
          ])}
        />
      </Wrapper>,
    );

    expect(screen.getAllByLabelText('sandbox-claimed: Warm instance · claimed')).toHaveLength(2);
    expect(screen.queryByText('Unclaimed warm instance')).toBeNull();
    expect(screen.getByText('Claimed by claim-001')).toBeTruthy();
  });

  it('renders correctly when a pool has zero claimed instances', () => {
    render(
      <Wrapper>
        <ClusterTopologyGraph
          data={createData([
            {
              name: 'sandbox-available',
              status: 'available',
              claimed: false,
              age_seconds: 120,
            },
            {
              name: 'sandbox-warming',
              status: 'warming',
              claimed: false,
              age_seconds: 30,
            },
          ])}
        />
      </Wrapper>,
    );

    expect(screen.getAllByLabelText('sandbox-available: Warm instance · available')).toHaveLength(2);
    expect(screen.getAllByLabelText('sandbox-warming: Warm instance · warming')).toHaveLength(2);
    expect(screen.queryByRole('link', { name: 'run-001' })).toBeNull();
    expect(screen.getByText('Unclaimed warm instance')).toBeTruthy();
    expect(screen.getByText('Warming up')).toBeTruthy();
  });
});
