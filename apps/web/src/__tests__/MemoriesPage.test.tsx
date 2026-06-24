import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { MemoriesPage } from '../pages/MemoriesPage';
import type { DecisionDto, DecisionInboxEntryDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getDecisions: vi.fn(),
    getDecisionsInbox: vi.fn(),
    getProjectMemory: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/memories`]}>
        <Routes>
          <Route path="/projects/:projectId/memories" element={<MemoriesPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

function makeActive(id: string): DecisionDto {
  return {
    id,
    agent_name: 'Architect',
    type: 'architecture',
    status: 'merged',
    title: 'Active decision',
    content: 'Active content',
    rationale: 'Active rationale',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  };
}

function makePending(id: string): DecisionInboxEntryDto {
  return {
    id,
    agent_name: 'Architect',
    slug: 'scope-cut',
    type: 'scope',
    title: 'Cut the export feature',
    content: 'Defer CSV export to a later milestone.',
    rationale: 'Keeps the milestone shippable.',
    status: 'pending',
    created_at: '2026-02-02T00:00:00Z',
    updated_at: '2026-02-02T00:00:00Z',
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('MemoriesPage — Decisions tab', () => {
  it('renders a Proposed section for pending inbox entries', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue([makeActive('d1')]);
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue([makePending('p1')]);

    renderPage();

    await waitFor(() => expect(screen.getByText('Proposed — awaiting Coordinator')).toBeTruthy());
    expect(screen.getByText('Cut the export feature')).toBeTruthy();
    expect(screen.getByText('Proposed')).toBeTruthy();
    expect(screen.getByText(/promotes these proposals into active Team Memory/)).toBeTruthy();
    // Active decision still renders as the primary list.
    expect(screen.getByText('Active decision')).toBeTruthy();
  });

  it('does not render a Proposed section when the inbox is empty', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue([makeActive('d1')]);
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue([]);

    renderPage();

    await waitFor(() => expect(screen.getByText('Active decision')).toBeTruthy());
    expect(screen.queryByText('Proposed — awaiting Coordinator')).toBeNull();
  });

  it('ignores non-pending inbox entries', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue([makeActive('d1')]);
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue([
      { ...makePending('p1'), status: 'merged' },
    ]);

    renderPage();

    await waitFor(() => expect(screen.getByText('Active decision')).toBeTruthy());
    expect(screen.queryByText('Proposed — awaiting Coordinator')).toBeNull();
  });

  it('shows the combined empty state when both active and pending are empty', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue([]);
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue([]);

    renderPage();

    await waitFor(() => expect(screen.getByText('No decisions recorded yet.')).toBeTruthy());
  });
});
