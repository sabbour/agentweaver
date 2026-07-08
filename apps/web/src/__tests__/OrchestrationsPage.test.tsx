import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent, within } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjectRuns: vi.fn(),
    getProject: vi.fn(),
    cancelRun: vi.fn(),
    deleteRun: vi.fn(),
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

  it('labels the automatic assembly handoff without presenting it as a user-waiting stage', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([
      { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Assemble squad output', status: 'in_progress', coordinator_status: 'awaiting_assembly', started_at: new Date().toISOString() },
    ] as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Assemble squad output')).toBeDefined());
    expect(document.body.textContent).toContain('Preparing assembly');
    expect(document.body.textContent).not.toContain('Awaiting assembly');
  });

  it('shows an empty state when there are no orchestrations', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([] as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('No orchestrations yet')).toBeDefined());
  });

  it('disables Stop for terminal orchestrations but enables Delete', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([
      { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Done work', status: 'merged', coordinator_status: 'complete', started_at: new Date().toISOString() },
    ] as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Done work')).toBeDefined());
    expect(screen.getByRole('button', { name: 'Stop orchestration' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Delete orchestration' })).toHaveProperty('disabled', false);
  });

  it('stops a running orchestration after confirmation and refreshes the list', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([
      { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Running work', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
    ] as never);
    vi.mocked(apiClient.cancelRun).mockResolvedValue({ run_id: 'c1', status: 'failed', cancelled: true, already_terminal: false } as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Running work')).toBeDefined());
    const stopBtn = screen.getByRole('button', { name: 'Stop orchestration' });
    expect(stopBtn).toHaveProperty('disabled', false);

    fireEvent.click(stopBtn);

    expect(confirmSpy).toHaveBeenCalled();
    await waitFor(() => expect(apiClient.cancelRun).toHaveBeenCalledWith('c1'));
    // list is refreshed (initial load + refresh after cancel)
    await waitFor(() => expect(vi.mocked(apiClient.listProjectRuns).mock.calls.length).toBeGreaterThanOrEqual(2));

    confirmSpy.mockRestore();
  });

  it('deletes an orchestration via the confirm dialog and removes it from the list', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue([
      { workflow_run_id: 'c1', execution_id: 'e1', agent_name: 'Coordinator', task: 'Delete me', status: 'in_progress', coordinator_status: 'dispatching', started_at: new Date().toISOString() },
    ] as never);
    vi.mocked(apiClient.deleteRun).mockResolvedValue(undefined as never);

    renderPage();

    await waitFor(() => expect(screen.getByText('Delete me')).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: 'Delete orchestration' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(apiClient.deleteRun).toHaveBeenCalledWith('c1'));
    await waitFor(() => expect(screen.queryByText('Delete me')).toBeNull());
  });
});
