import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ClusterPage } from '../pages/ClusterPage';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { ClusterDiagnosticsDto, KubernetesTopologyDto } from '../api/types';
import type { ReactNode } from 'react';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getClusterDiagnostics: vi.fn(),
    getClusterTopology: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/cluster`]}>
        <Routes>
          <Route path="/projects/:projectId/cluster" element={<ClusterPage />} />
          <Route path="/projects/:projectId/orchestrations/:runId" element={<div>Run detail</div>} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getClusterMock = () => vi.mocked(apiClient.getClusterDiagnostics);
const getTopologyMock = () => vi.mocked(apiClient.getClusterTopology);

const sampleTopology: KubernetesTopologyDto = {
  generated_utc: new Date().toISOString(),
  namespace: 'agentweaver',
  requested_layers: ['runtime'],
  layers: [
    { name: 'runtime' as const, status: 'available' as const, resource_count: 1, message: 'available' },
    { name: 'networking' as const, status: 'not_requested' as const, resource_count: 0, message: 'not requested' },
    { name: 'workloads' as const, status: 'not_requested' as const, resource_count: 0, message: 'not requested' },
    { name: 'storage' as const, status: 'not_requested' as const, resource_count: 0, message: 'not requested' },
    { name: 'autoscaling' as const, status: 'not_requested' as const, resource_count: 0, message: 'not requested' },
    { name: 'availability' as const, status: 'not_requested' as const, resource_count: 0, message: 'not requested' },
  ],
  nodes: [{
    id: 'v1:Pod:agentweaver:agent-abc123',
    layer: 'runtime' as const,
    type: 'Pod',
    api_version: 'v1',
    name: 'agent-abc123',
    namespace: 'agentweaver',
    health: 'healthy' as const,
    summary: 'Pod · Running',
    details: { phase: 'Running' },
  }],
  edges: [],
  truncated: false,
};

const sampleData: ClusterDiagnosticsDto = {
  generated_utc: new Date().toISOString(),
  total_duration_ms: 42,
  checks: [
    { name: 'K8s API', status: 'healthy', message: 'Reachable', latencyMs: 5 },
    { name: 'PostgreSQL', status: 'healthy', message: 'Connected (8ms)', latencyMs: 8 },
    { name: 'Key Vault', status: 'healthy', message: 'Signing key loaded', latencyMs: 4 },
  ],
  active_agent_pods: [
    { claim_name: 'claim-abc123', pod_name: 'agent-abc123', run_id: 'run-001', status: 'ready', age_seconds: 60 },
    { claim_name: 'claim-def456', pod_name: 'agent-def456', run_id: 'run-002', status: 'ready', age_seconds: 120 },
  ],
  orphaned_agent_pods: [],
  pending_capacity_runs: [
    { subtask_id: 1, work_plan_id: 10, child_run_id: null, status: 'waiting', reason: 'Insufficient CPU', age_seconds: 30 },
  ],
  warm_pools: [
    {
      name: 'default-pool',
      desired_replicas: 2,
      ready_replicas: 2,
      available_replicas: 1,
      status: 'healthy',
      instances: [
        {
          name: 'agentweaver-sandbox-unclaimed',
          status: 'available',
          claimed: false,
          age_seconds: 120,
        },
        {
          name: 'agentweaver-sandbox-claimed',
          status: 'claimed',
          claimed: true,
          claim_name: 'claim-abc123',
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
      name: 'claim-abc123',
      phase: 'bound',
      ready: true,
      run_id: 'run-001',
      bound_sandbox: 'agentweaver-sandbox-claimed',
      warm_pool: 'default-pool',
      age_seconds: 60,
    },
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
  getTopologyMock().mockResolvedValue(sampleTopology);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('ClusterPage', () => {
  it('renders "Cluster" heading', async () => {
    getClusterMock().mockResolvedValue(sampleData);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Cluster')).toBeDefined();
    });
  });

  it('renders spinner while loading', () => {
    getClusterMock().mockReturnValue(new Promise(() => { /* never resolves */ }));

    renderPage();

    expect(screen.getByRole('status', { name: 'Loading cluster diagnostics' })).toBeDefined();
  });

  it('renders KPI cards and component health table on success', async () => {
    getClusterMock().mockResolvedValue(sampleData);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Health checks')).toBeDefined();
    });

    // KPI cards — "Active" removed (captured in Sandbox claims)
    expect(screen.queryByText('Active')).toBeNull();
    expect(screen.getByText('Orphaned pods')).toBeDefined();
    expect(screen.getByText('Pending capacity')).toBeDefined();
    expect(screen.getByText('Checks healthy')).toBeDefined();

    expect(screen.getByText('Resource topology')).toBeDefined();
    expect(screen.getByTestId('cluster-topology-graph')).toBeDefined();
    expect(screen.getByTestId('cluster-topology-viewport')).toBeDefined();
    expect(screen.getByLabelText('agent-abc123: Pod · Running')).toBeDefined();
    expect((screen.getByRole('checkbox', { name: 'Runtime' }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('checkbox', { name: 'Networking' }) as HTMLInputElement).checked).toBe(false);

    // Health check rows
    expect(screen.getByText('K8s API')).toBeDefined();
    expect(screen.getByText('PostgreSQL')).toBeDefined();
    expect(screen.getByText('Key Vault')).toBeDefined();

    // Active agent pods section removed
    expect(screen.queryByText(/Active agent pods/)).toBeNull();

    // Pending capacity section
    expect(screen.getByText('Pending capacity (1)')).toBeDefined();
    expect(screen.getByText(/couldn't get a sandbox immediately/i)).toBeDefined();
    expect(screen.getByText('Insufficient CPU')).toBeDefined();
  });

  it('explains empty pending capacity state', async () => {
    getClusterMock().mockResolvedValue({
      ...sampleData,
      pending_capacity_runs: [],
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('No pending capacity runs')).toBeDefined();
    });

    expect(screen.getByText(/every run is getting a sandbox immediately/i)).toBeDefined();
  });

  it('renders "Not available" bar when API returns 404 (null)', async () => {
    getClusterMock().mockResolvedValue(null);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/not available in this environment/i)).toBeDefined();
    });
  });

  it('renders error state when fetch throws', async () => {
    getClusterMock().mockRejectedValue(Object.assign(new Error('Internal server error'), { status: 500, body: 'Internal server error' }));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/API error 500|Internal server error/)).toBeDefined();
    });
  });

  it('enables auto-refresh by default', async () => {
    getClusterMock().mockResolvedValue(sampleData);

    renderPage();

    await waitFor(() => {
      expect((screen.getByRole('switch', { name: 'Auto-refresh' }) as HTMLInputElement).checked).toBe(true);
    });
  });

  it('loads an opt-in topology layer without changing the default', async () => {
    const user = userEvent.setup();
    getClusterMock().mockResolvedValue(sampleData);

    renderPage();

    await user.click(await screen.findByRole('checkbox', { name: 'Networking' }));

    await waitFor(() => {
      expect(getTopologyMock()).toHaveBeenLastCalledWith(['runtime', 'networking']);
    });
  });
});
