import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getBoard: vi.fn(),
    getProject: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { FlowPage } from '../pages/FlowPage';

function renderPage() {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/flow']}>
        <Routes>
          <Route path="/projects/:projectId/flow" element={<FlowPage />} />
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

describe('FlowPage', () => {
  it('lists active agents and what they are working on', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [
        { agent_name: 'Ada', active: 1, queued: 2, blocked: 0, done: 3, run_ids: ['r1'], sample_titles: ['Implement login'] },
      ],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Ada')).toBeDefined());
    expect(screen.getByText('1 active')).toBeDefined();
    expect(screen.getByText('Implement login')).toBeDefined();
  });

  it('shows an empty state when no agents are active', async () => {
    vi.mocked(apiClient.getBoard).mockResolvedValue({
      project_id: 'p1',
      workflow_stages_available: false,
      columns: [],
      agent_queues: [],
    } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('No active agents')).toBeDefined());
  });
});
