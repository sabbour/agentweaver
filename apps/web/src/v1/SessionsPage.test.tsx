import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRunPage, ProjectSummary } from './contracts';
import { SessionsPage } from './SessionsPage';

const mocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  listProjectRuns: vi.fn(),
  listChiefOfStaffConversations: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
  // The real auth context returns a stable apiCall, so the mock does too.
  apiCall: <T,>(operation: (token: string, tenant: string | null) => Promise<T>) => operation('token', 'tenant'),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: mocks.apiCall }),
}));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    listProjects: mocks.listProjects, listProjectRuns: mocks.listProjectRuns,
    listChiefOfStaffConversations: mocks.listChiefOfStaffConversations,
  },
}));
vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return {
    ...actual,
    PageHeading: ({ title, actions }: { title: string; actions?: React.ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
  };
});

import { GatewayError } from './api';

function project(projectId: string, name: string): ProjectSummary {
  return { projectId, name, state: 'active', revision: 1, configurationRevision: 1, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', effectiveRoles: [] };
}

function runs(projectId: string, entries: Array<[string, string, string | null]>, nextCursor: string | null = null): ProjectRunPage {
  return {
    projectId,
    nextCursor,
    items: entries.map(([runId, createdAt, title]) => ({
      runId, rootSessionId: null, title, goal: null, state: 'active', createdAt, updatedAt: createdAt, completedAt: null,
      startedBy: { issuer: 'https://issuer.test', subject: 'user' },
    })),
  };
}

function renderPage() {
  return render(<MemoryRouter initialEntries={['/sessions']}><SessionsPage /></MemoryRouter>);
}

describe('SessionsPage', () => {
  beforeEach(() => {
    mocks.listProjects.mockReset().mockResolvedValue([project('p1', 'Alpha'), project('p2', 'Beta')]);
    mocks.listProjectRuns.mockReset().mockImplementation(async (_token: string, projectId: string, options: { cursor: string | null }) => {
      if (projectId === 'p1' && options.cursor === 'more') return runs('p1', [['a0', '2026-01-01T00:00:00Z', 'Oldest']]);
      if (projectId === 'p1') return runs('p1', [['a1', '2026-01-03T00:00:00Z', 'Alpha task']], 'more');
      return runs('p2', [['b1', '2026-01-02T00:00:00Z', null]]);
    });
  });

  it('lists runs across projects newest first with project names and run links', async () => {
    renderPage();
    expect(screen.getByText('Loading sessions')).toBeTruthy();
    const list = await screen.findByRole('region', { name: 'All sessions' });
    const links = within(list).getAllByRole('link', { name: /^Open run/ });
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/projects/p1/runs/a1', '/projects/p2/runs/b1']);
    expect(within(list).getByText('Alpha task')).toBeTruthy();
    // An untitled run with no goal shows "Run" and its short ID.
    expect(within(list).getByText('Run b1')).toBeTruthy();
    expect(within(list).getByText(/^Beta \u00b7 Started/)).toBeTruthy();
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { cursor: null, pageSize: 10 }, 'tenant');
  });

  it('loads more runs from projects with another page', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Oldest')).toBeTruthy();
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { cursor: 'more', pageSize: 10 }, 'tenant');
    expect(mocks.listProjectRuns).not.toHaveBeenCalledWith('token', 'p2', expect.objectContaining({ cursor: expect.any(String) }), 'tenant');
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('shows the empty state when there are no runs', async () => {
    mocks.listProjectRuns.mockImplementation(async (_token: string, projectId: string) => runs(projectId, []));
    renderPage();
    expect(await screen.findByText('No sessions yet')).toBeTruthy();
    expect(screen.getByText('Runs started in your projects appear here.')).toBeTruthy();
  });

  it('warns about projects whose runs cannot be read', async () => {
    mocks.listProjectRuns.mockImplementation(async (_token: string, projectId: string) => {
      if (projectId === 'p2') throw new GatewayError(403, { code: 'forbidden' }, 'Forbidden');
      return runs('p1', [['a1', '2026-01-03T00:00:00Z', 'Alpha task']]);
    });
    renderPage();
    expect(await screen.findByText('Runs from one project could not be read: Beta.')).toBeTruthy();
    expect(screen.getByText('Alpha task')).toBeTruthy();
  });

  it('shows an error and retries when the project list fails', async () => {
    mocks.listProjects.mockRejectedValueOnce(new Error('Network down'));
    renderPage();
    expect((await screen.findByRole('alert')).textContent).toContain('Network down');
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Alpha task')).toBeTruthy();
  });
});
