import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import type { OverviewDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getOverview: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { OverviewPage } from '../pages/OverviewPage';

const dto: OverviewDto = {
  generated_utc: new Date().toISOString(),
  at_a_glance: { in_flight: 2, queued_work: 5, done_today: 3, active_projects: 1, health: 'healthy' },
  live_sessions: [
    { project_id: 'p1', project_name: 'Demo', agent: 'Ada', status: 'in_progress', started_utc: new Date().toISOString(), last_activity_utc: new Date().toISOString() },
  ],
  active_workflow_runs: [
    { project_id: 'p1', project_name: 'Demo', trigger: 'interactive', status: 'in_progress', started_utc: new Date().toISOString() },
  ],
  active_projects: [
    { project_id: 'p1', project_name: 'Demo', active_count: 2, queued_count: 1, last_activity_utc: new Date().toISOString() },
  ],
  recent_activity: [
    { project_id: 'p1', project_name: 'Demo', label: 'Run completed', kind: 'completed', timestamp_utc: new Date().toISOString() },
    { project_id: 'p1', project_name: 'Demo', label: 'Run started', kind: 'in_progress', timestamp_utc: new Date().toISOString() },
  ],
};

function renderPage() {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/overview']}>
        <OverviewPage />
      </MemoryRouter>
    </FluentProvider>,
  );
}

beforeEach(() => vi.clearAllMocks());
afterEach(() => cleanup());

describe('OverviewPage', () => {
  it('renders at-a-glance cards and the live tables', async () => {
    vi.mocked(apiClient.getOverview).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(screen.getByText('In flight')).toBeDefined());
    expect(screen.getByText('Queued work')).toBeDefined();
    expect(screen.getByText('Live sessions')).toBeDefined();
    expect(screen.getByText('Active workflow runs')).toBeDefined();
    expect(screen.getByText('Recent activity')).toBeDefined();
    expect(screen.getByText('Run completed', { exact: false })).toBeDefined();
    // Raw status kinds are humanized for the activity status chip.
    expect(screen.getByText('In progress')).toBeDefined();
    expect(screen.getByText('healthy')).toBeDefined();
    // Liveness: the header shows a countdown to the next auto-refresh.
    expect(screen.getByText(/Next refresh in/)).toBeDefined();
  });
});
