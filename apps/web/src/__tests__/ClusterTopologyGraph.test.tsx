import { ClusterTopologyGraph } from '../components/ClusterTopologyGraph';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { cleanup, render, screen } from '@testing-library/react';
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
      nodes: Array<{ id: string; type?: string; data: Record<string, unknown> }>;
      nodeTypes: Record<string, ComponentType<{ data: Record<string, unknown> }>>;
    }) => (
      <div className={className} data-testid="mock-reactflow">
        {nodes.map((node) => {
          const NodeComponent = nodeTypes[node.type ?? ''];
          return <NodeComponent key={node.id} data={node.data} />;
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
    expect(screen.getByLabelText('sandbox-claimed: Warm instance · claimed · run-001')).toBeTruthy();
    expect(screen.getByLabelText('claim-001: Sandbox claim · bound')).toBeTruthy();
    expect(screen.getByLabelText('agent-001: Agent pod · ready')).toBeTruthy();
    expect(screen.getAllByRole('link', { name: 'run-001' })).toHaveLength(2);
    expect(screen.getByText('Unclaimed warm instance')).toBeTruthy();
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

    expect(screen.getByText(longPoolName).getAttribute('title')).toBe(longPoolName);
    expect(screen.getAllByText(longInstanceName).some((element) => element.getAttribute('title') === longInstanceName)).toBe(true);
    expect(screen.getByText(longClaimName).getAttribute('title')).toBe(longClaimName);
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

    expect(screen.getByLabelText('sandbox-claimed: Warm instance · claimed · run-001')).toBeTruthy();
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
