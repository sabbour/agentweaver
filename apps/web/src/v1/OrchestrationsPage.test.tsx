import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { OrchestrationsPage } from './OrchestrationsPage';
import type { ProjectRunListOptions, RunListData, RunListItem } from './runListData';

const mocks = vi.hoisted(() => ({
  lists: {} as Record<string, RunListData>,
  useProjectRunList: vi.fn(),
}));

vi.mock('./runListData', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./runListData')>();
  return {
    ...actual,
    useProjectRunList: (projectId: string, options?: ProjectRunListOptions) => {
      mocks.useProjectRunList(projectId, options);
      return mocks.lists[options?.states?.join(',') ?? ''];
    },
  };
});

vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return {
    ...actual,
    PageHeading: ({ title, actions }: { title: string; actions?: React.ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
  };
});

function item(runId: string, executionState: string): RunListItem {
  return { projectId: 'p1', runId, label: runId, executionState, time: { label: 'Started', value: '2026-01-01T00:00:00Z' } };
}

function list(items: RunListItem[] = [], overrides: Partial<RunListData> = {}): RunListData {
  return { items, loading: false, error: null, hasMore: false, loadingMore: false, loadMore: vi.fn(), reload: vi.fn(), ...overrides };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/projects/p1/orchestrations']}>
      <Routes><Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} /></Routes>
    </MemoryRouter>,
  );
}

describe('OrchestrationsPage', () => {
  beforeEach(() => {
    mocks.useProjectRunList.mockClear();
    mocks.lists = { open: list(), terminal: list() };
  });

  it('reads open and finished runs for the route project and shows the empty state', () => {
    renderPage();
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { states: ['open'] });
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { states: ['terminal'] });
    expect(screen.getByRole('heading', { name: 'Orchestrations' })).toBeTruthy();
    expect(screen.getByText('No orchestrations yet')).toBeTruthy();
    expect(screen.queryByLabelText('Orchestration status summary')).toBeNull();
    expect(screen.queryByText(/service update/)).toBeNull();
  });

  it('shows a loader while the first page loads', () => {
    mocks.lists.open = list([], { loading: true });
    renderPage();
    expect(screen.getByText('Loading orchestrations')).toBeTruthy();
    expect(screen.queryByText('No orchestrations yet')).toBeNull();
  });

  it('shows Active and Recent sections with a state summary', () => {
    mocks.lists.open = list([item('r1', 'blocked'), item('r2', 'active'), item('r5', 'blocked')], { hasMore: true });
    mocks.lists.terminal = list([item('r3', 'completed'), item('r4', 'cancelled')]);
    renderPage();
    const active = screen.getByRole('region', { name: 'Active' });
    const recent = screen.getByRole('region', { name: 'Recent' });
    expect(within(active).getAllByRole('link', { name: /^Open run/ }).map((link) => link.getAttribute('href')))
      .toEqual(['/projects/p1/runs/r1', '/projects/p1/runs/r2', '/projects/p1/runs/r5']);
    expect(within(recent).getAllByRole('link', { name: /^Open run/ }).map((link) => link.getAttribute('aria-label')))
      .toEqual(['Open run r3', 'Open run r4']);
    const summary = screen.getByLabelText('Orchestration status summary');
    expect(summary.textContent).toBe('3+ active2 recent2 Needs input1 Working1 Done1 Cancelled');
  });

  it('loads more runs in one section', () => {
    mocks.lists.open = list([item('r1', 'active')], { hasMore: true });
    renderPage();
    fireEvent.click(within(screen.getByRole('region', { name: 'Active' })).getByRole('button', { name: 'Load more' }));
    expect(mocks.lists.open.loadMore).toHaveBeenCalled();
  });

  it('omits an empty section', () => {
    mocks.lists.open = list([item('r1', 'idle')]);
    renderPage();
    expect(screen.getByRole('region', { name: 'Active' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Recent' })).toBeNull();
  });

  it('explains a missing read permission and retries', () => {
    const error = new GatewayError(403, { code: 'forbidden' }, 'Forbidden');
    mocks.lists.open = list([], { error });
    mocks.lists.terminal = list([], { error });
    renderPage();
    expect(screen.getByRole('alert').textContent).toContain('You do not have permission to read runs in this project.');
    expect(screen.queryByText('No orchestrations yet')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(mocks.lists.open.reload).toHaveBeenCalled();
    expect(mocks.lists.terminal.reload).toHaveBeenCalled();
  });

  it('reloads both lists on Refresh', () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(mocks.lists.open.reload).toHaveBeenCalled();
    expect(mocks.lists.terminal.reload).toHaveBeenCalled();
  });
});
