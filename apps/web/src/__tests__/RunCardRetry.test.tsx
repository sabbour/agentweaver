import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { type ReactNode } from 'react';
import { RunCard } from '../components/board/RunCard';
import type { RunCardDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    retryRun: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => mockNavigate };
});

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/proj-1/board']}>
        <Routes>
          <Route path="/projects/:projectId/board" element={<>{children}</>} />
          <Route path="/projects/:projectId/orchestrations/:runId" element={<div>Run detail</div>} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

function makeCard(overrides: Partial<RunCardDto> = {}): RunCardDto {
  return {
    kind: 'run',
    run_id: 'run-123',
    task: 'Do something',
    status: 'in_progress',
    stage_id: 'coordinator',
    started_at: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

afterEach(() => {
  cleanup();
});

describe('RunCard — Retry button', () => {
  it('renders Retry button when status is "failed"', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.getByTestId('run-card-retry')).toBeTruthy();
  });

  it('renders Retry button when status is "merge_failed"', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'merge_failed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.getByTestId('run-card-retry')).toBeTruthy();
  });

  it('does NOT render Retry button for in_progress status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'in_progress' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('does NOT render Retry button for completed status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'completed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('does NOT render Retry button for declined status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'declined' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('clicking Retry calls apiClient.retryRun with the run_id and navigates to the new run', async () => {
    vi.mocked(apiClient.retryRun).mockResolvedValue({
      run_id: 'new-run-456',
      retried_from: 'run-123',
      status: 'in_progress',
    });

    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed' })} projectId="proj-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('run-card-retry'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.retryRun)).toHaveBeenCalledWith('run-123'),
    );

    await waitFor(() =>
      expect(mockNavigate).toHaveBeenCalledWith('/projects/proj-1/orchestrations/new-run-456'),
    );
  });
});
