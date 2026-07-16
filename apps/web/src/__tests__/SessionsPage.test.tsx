import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SessionsPage } from '../pages/SessionsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listAssistantRuns: vi.fn(),
    deleteRun: vi.fn(),
  },
}));

function Wrapper({ children, initialEntry = '/sessions' }: { children: ReactNode; initialEntry?: string }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={[initialEntry]}>
        {children}
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location-probe">{`${location.pathname}${location.search}`}</div>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.listAssistantRuns).mockResolvedValue({
    runs: [
      {
        run_id: 'run-1',
        title: 'Inspect backlog',
        status: 'in_progress',
        created_at: '2026-07-15T16:00:00Z',
      },
    ],
  } as never);
});

afterEach(() => {
  cleanup();
});

describe('SessionsPage', () => {
  it('loads sessions without any assistant feature flag opt-in', async () => {
    render(
      <Wrapper>
        <SessionsPage />
      </Wrapper>,
    );

    await waitFor(() => expect(apiClient.listAssistantRuns).toHaveBeenCalledWith(50));
    expect(await screen.findByText('Inspect backlog')).toBeTruthy();
    expect(screen.getByText('Your assistant conversations across Agentweaver. Resume one, or start a new one.')).toBeTruthy();
  });

  it('preserves the project query when starting a new session', async () => {
    render(
      <Wrapper initialEntry="/sessions?project=proj-7">
        <Routes>
          <Route path="/sessions" element={<SessionsPage />} />
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </Wrapper>,
    );

    await screen.findByText('Inspect backlog');
    fireEvent.click(screen.getByTestId('sessions-new-button'));

    await waitFor(() => {
      expect(screen.getByTestId('location-probe').textContent).toBe('/assistant?project=proj-7');
    });
  });

  it('shows a confirmation dialog before deleting a session', async () => {
    render(
      <Wrapper>
        <SessionsPage />
      </Wrapper>,
    );

    await screen.findByText('Inspect backlog');
    fireEvent.click(screen.getByTestId('sessions-row-delete'));

    expect(await screen.findByText('Delete this conversation?')).toBeTruthy();
    expect(apiClient.deleteRun).not.toHaveBeenCalled();
  });

  it('deletes the session and removes it from the list on confirm', async () => {
    vi.mocked(apiClient.deleteRun).mockResolvedValue(undefined as never);

    render(
      <Wrapper>
        <SessionsPage />
      </Wrapper>,
    );

    await screen.findByText('Inspect backlog');
    fireEvent.click(screen.getByTestId('sessions-row-delete'));
    await screen.findByText('Delete this conversation?');

    fireEvent.click(screen.getByTestId('sessions-delete-confirm'));

    await waitFor(() => expect(apiClient.deleteRun).toHaveBeenCalledWith('run-1'));
    await waitFor(() => expect(screen.queryByText('Inspect backlog')).toBeNull());
  });

  it('shows an inline error and keeps the row if delete fails', async () => {
    vi.mocked(apiClient.deleteRun).mockRejectedValue(new Error('boom'));

    render(
      <Wrapper>
        <SessionsPage />
      </Wrapper>,
    );

    await screen.findByText('Inspect backlog');
    fireEvent.click(screen.getByTestId('sessions-row-delete'));
    await screen.findByText('Delete this conversation?');

    fireEvent.click(screen.getByTestId('sessions-delete-confirm'));

    expect(await screen.findByText('boom')).toBeTruthy();
    expect(screen.getByText('Inspect backlog')).toBeTruthy();
  });
});
