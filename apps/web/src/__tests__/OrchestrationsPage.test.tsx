import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjectRuns: vi.fn(),
    getProject: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { OrchestrationsPage } from '../pages/OrchestrationsPage';

function renderPage() {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/orchestrations']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({ name: 'Demo' } as never);
});

afterEach(() => cleanup());

describe('OrchestrationsPage', () => {
  it('lists only coordinator runs', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([
      { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Coordinate squad', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
      { workflow_run_id: 'w1', execution_id: 'e2', agent_name: 'Ada', task: 'Solo task', status: 'in_progress', started_at: new Date().toISOString() },
    ] as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Coordinate squad')).toBeDefined());
    expect(screen.queryByText('Solo task')).toBeNull();
  });

  it('shows an empty state when there are no orchestrations', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([] as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('No orchestrations yet')).toBeDefined());
  });
});
