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
  total_duration_ms: 42,
  checks: [
    { name: 'K8s API', status: 'healthy', message: 'Reachable', latencyMs: 5 },
    { name: 'PostgreSQL', status: 'healthy', message: 'Connected (8ms)', latencyMs: 8 },
    { name: 'GitHub Token', status: 'warning', message: 'Token expires in 2 days', latencyMs: 0 },
  ],
  active_agent_pods: [
    { claim_name: 'claim-abc123', pod_name: 'agent-abc123', run_id: 'run-001', status: 'ready', age_seconds: 60 },
    { claim_name: 'claim-def456', pod_name: 'agent-def456', run_id: 'run-002', status: 'ready', age_seconds: 120 },
  ],
  orphaned_agent_pods: [],
  pending_capacity_runs: [
    { subtask_id: 1, work_plan_id: 10, child_run_id: null, status: 'waiting', reason: 'Insufficient CPU', age_seconds: 30 },
  ],
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
      expect(screen.getByText('Health checks')).toBeDefined();
    });

    // KPI cards — "Active" removed (captured in Sandbox claims)
    expect(screen.queryByText('Active')).toBeNull();
    expect(screen.getByText('Orphaned')).toBeDefined();
    expect(screen.getByText('Pending capacity')).toBeDefined();
    expect(screen.getByText('Checks OK')).toBeDefined();

    // Health check rows
    expect(screen.getByText('K8s API')).toBeDefined();
    expect(screen.getByText('PostgreSQL')).toBeDefined();
    expect(screen.getByText('GitHub Token')).toBeDefined();

    // Active agent pods section removed
    expect(screen.queryByText(/Active agent pods/)).toBeNull();

    // Pending capacity section
    expect(screen.getByText('Pending capacity (1)')).toBeDefined();
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
