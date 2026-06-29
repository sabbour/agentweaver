import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { DiagnosticsPage } from '../pages/DiagnosticsPage';
import type { ProjectDiagnosticsDto, SystemDiagnosticsDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getDiagnostics: vi.fn(),
    getProjectDiagnostics: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/diagnostics`]}>
        <Routes>
          <Route path="/projects/:projectId/diagnostics" element={<DiagnosticsPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getDiagnosticsMock = () => vi.mocked(apiClient.getDiagnostics);
const getProjectDiagnosticsMock = () => vi.mocked(apiClient.getProjectDiagnostics);

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const sampleData: SystemDiagnosticsDto = {
  api_version: '1.2.3.4',
  process_started_utc: '2026-06-22T10:00:00Z',
  uptime_seconds: 3661,
  total_projects: 5,
  total_runs: 42,
  active_runs: 2,
  generated_utc: '2026-06-22T11:00:00Z',
  total_duration_ms: 18,
  checks: [
    { name: 'Database', status: 'pass', detail: 'Connected', duration_ms: 4 },
    { name: 'Disk space', status: 'warn', detail: 'Low headroom', duration_ms: 6 },
    { name: 'Queue', status: 'fail', detail: 'Backed up', duration_ms: 8 },
  ],
};

const projectData: ProjectDiagnosticsDto = {
  project_id: 'proj-001',
  project_name: 'Demo Project',
  generated_utc: '2026-06-22T11:01:00Z',
  total_duration_ms: 9,
  checks: [
    { name: 'Repo link', status: 'pass', detail: 'Linked', duration_ms: 3 },
    { name: 'Sandbox policy', status: 'pass', detail: 'Valid', duration_ms: 6 },
  ],
};

describe('DiagnosticsPage', () => {
  it('renders global diagnostics summary and checks', async () => {
    getDiagnosticsMock().mockResolvedValue(sampleData);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Diagnostics')).toBeDefined();
    });

    await waitFor(() => {
      expect(screen.getByText('1.2.3.4')).toBeDefined();
    });

    expect(screen.getByText('1h 1m 1s')).toBeDefined();
    expect(screen.getByText('Database')).toBeDefined();
    expect(screen.getByText('Disk space')).toBeDefined();
    expect(screen.getByText('Queue')).toBeDefined();
    expect(screen.getByText('Connected')).toBeDefined();
  });

  it('switches to the project tab and renders project checks', async () => {
    getDiagnosticsMock().mockResolvedValue(sampleData);
    getProjectDiagnosticsMock().mockResolvedValue(projectData);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Database')).toBeDefined();
    });

    fireEvent.click(screen.getByRole('tab', { name: 'This project' }));

    await waitFor(() => {
      expect(getProjectDiagnosticsMock()).toHaveBeenCalledWith('proj-001');
    });

    await waitFor(() => {
      expect(screen.getByText('Demo Project')).toBeDefined();
    });
    expect(screen.getByText('Repo link')).toBeDefined();
    expect(screen.getByText('Sandbox policy')).toBeDefined();
  });

  it('renders error state when fetch fails', async () => {
    const { ApiError } = await import('../api/client');
    getDiagnosticsMock().mockRejectedValue(new ApiError(503, 'Service unavailable'));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/API error 503/)).toBeDefined();
    });
  });
});
