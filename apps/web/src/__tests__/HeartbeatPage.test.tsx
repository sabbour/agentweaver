import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { HeartbeatPage } from '../pages/HeartbeatPage';
import type { HeartbeatStatusDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getHeartbeatStatus: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/heartbeat`]}>
        <Routes>
          <Route path="/projects/:projectId/heartbeat" element={<HeartbeatPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getHeartbeatMock = () => vi.mocked(apiClient.getHeartbeatStatus);

function sampleData(overrides: Partial<HeartbeatStatusDto> = {}): HeartbeatStatusDto {
  return {
    enabled: true,
    interval_seconds: 60,
    last_tick_utc: new Date(Date.now() - 45000).toISOString(),
    service_status: 'running',
    last_error: null,
    recent_activity: [
      {
        timestamp_utc: new Date(Date.now() - 45000).toISOString(),
        acted_count: 3,
        error_count: 0,
        duration_ms: 120,
        error: null,
        automation_name: 'Coordinator Heartbeat',
      },
      {
        timestamp_utc: new Date(Date.now() - 105000).toISOString(),
        acted_count: 0,
        error_count: 1,
        duration_ms: 88,
        error: 'boom',
        automation_name: 'Checkpoint GC',
      },
    ],
    automations: [
      {
        name: 'Coordinator Heartbeat',
        description: 'Advances active coordinator runs.',
        cadence_seconds: 60,
        last_run_utc: new Date(Date.now() - 45000).toISOString(),
        last_acted_count: 3,
        status: 'idle',
      },
      {
        name: 'Checkpoint GC',
        description: 'Cleans up stale checkpoints.',
        cadence_seconds: 3600,
        last_run_utc: null,
        last_acted_count: null,
        status: 'waiting_first_tick',
      },
    ],
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('HeartbeatPage', () => {
  it('renders service status and interval', async () => {
    getHeartbeatMock().mockResolvedValue(sampleData());

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Heartbeat')).toBeDefined();
    });

    await waitFor(() => {
      expect(screen.getByText('running')).toBeDefined();
    });

    expect(screen.getByText(/interval 60s/)).toBeDefined();
  });

  it('renders the two real automations', async () => {
    getHeartbeatMock().mockResolvedValue(sampleData());

    renderPage();

    await waitFor(() => {
      expect(screen.getAllByText('Coordinator Heartbeat').length).toBeGreaterThan(0);
    });
    expect(screen.getAllByText('Checkpoint GC').length).toBeGreaterThan(0);
    expect(screen.getByText('Advances active coordinator runs.')).toBeDefined();
  });

  it('renders the recent activity tick timeline', async () => {
    getHeartbeatMock().mockResolvedValue(sampleData());

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Recent activity')).toBeDefined();
    });
    expect(screen.getByText('120 ms')).toBeDefined();
    expect(screen.getByText('boom')).toBeDefined();
  });

  it('shows the last error when present', async () => {
    getHeartbeatMock().mockResolvedValue(sampleData({ last_error: 'service crashed' }));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/service crashed/)).toBeDefined();
    });
  });

  it('renders error state when fetch fails', async () => {
    const { ApiError } = await import('../api/client');
    getHeartbeatMock().mockRejectedValue(new ApiError(500, 'Internal server error'));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/API error 500/)).toBeDefined();
    });
  });
});
