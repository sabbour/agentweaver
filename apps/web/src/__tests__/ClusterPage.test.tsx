import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ClusterPage } from '../pages/ClusterPage';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { ClusterDiagnosticsDto } from '../api/types';
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
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getClusterMock = () => vi.mocked(apiClient.getClusterDiagnostics);

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
      age_seconds: 300,
    },
  ],
  sandbox_claims: [
    {
      name: 'claim-abc123',
      phase: 'bound',
      ready: true,
      warm_pool: 'default-pool',
      age_seconds: 60,
    },
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
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
    expect(screen.getByLabelText('Cluster: 3 / 3 checks healthy')).toBeDefined();
    expect(screen.getByLabelText('default-pool: Warm pool · 2 / 2 ready')).toBeDefined();
    expect(screen.getByLabelText('claim-abc123: Sandbox claim · bound')).toBeDefined();
    expect(screen.getByLabelText('agent-abc123: Agent pod · ready')).toBeDefined();

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
});
