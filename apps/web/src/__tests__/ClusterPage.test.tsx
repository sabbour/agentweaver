import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { ClusterPage } from '../pages/ClusterPage';
import type { ClusterDiagnosticsDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getClusterDiagnostics: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
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
  warm_pool_ready: 3,
  warm_pool_total: 5,
  active_agent_pods: 2,
  pending_agent_pods: 1,
  claimed_agent_pods: 1,
  component_health: [
    { component: 'K8s API', status: 'ok', detail: 'Reachable' },
    { component: 'PostgreSQL', status: 'ok', detail: 'Connected (8ms)' },
    { component: 'GitHub Token', status: 'warning', detail: 'Token expires in 2 days' },
  ],
  active_pods: [
    { pod_name: 'agent-abc123', run_id: 'run-001', status: 'Running', started_at: new Date(Date.now() - 60000).toISOString() },
    { pod_name: 'agent-def456', run_id: 'run-002', status: 'Running', started_at: new Date(Date.now() - 120000).toISOString() },
  ],
  pending_pods: [
    { pod_name: 'agent-ghi789', run_id: 'run-003', reason: 'Insufficient CPU', retry_count: 2, pending_since: new Date(Date.now() - 30000).toISOString() },
  ],
  quota: {
    cpu_used: 18,
    cpu_limit: 24,
    memory_used_gi: 36,
    memory_limit_gi: 48,
  },
};

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
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

    expect(screen.getByText('Loading cluster diagnostics')).toBeDefined();
  });

  it('renders KPI cards and component health table on success', async () => {
    getClusterMock().mockResolvedValue(sampleData);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Component health')).toBeDefined();
    });

    // KPI cards
    expect(screen.getByText('Warm')).toBeDefined();
    expect(screen.getByText('Active')).toBeDefined();
    expect(screen.getByText('Pending')).toBeDefined();

    // Component health rows
    expect(screen.getByText('K8s API')).toBeDefined();
    expect(screen.getByText('PostgreSQL')).toBeDefined();
    expect(screen.getByText('GitHub Token')).toBeDefined();

    // Active pods
    expect(screen.getByText('Active agent pods (2)')).toBeDefined();
    expect(screen.getByText('agent-abc123')).toBeDefined();

    // Pending pods
    expect(screen.getByText('Pending agent pods (1)')).toBeDefined();
    expect(screen.getByText('Insufficient CPU')).toBeDefined();
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
});
