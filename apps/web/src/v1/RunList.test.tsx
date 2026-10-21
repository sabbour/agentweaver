import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RunList } from './RunList';
import type { RunListData, RunListItem } from './runListData';

function item(runId: string, executionState: string | null, projectId = 'p1', label = `Task ${runId}`): RunListItem {
  return { projectId, runId, label, executionState, time: { label: 'Started', value: '2026-01-01T00:00:00Z' } };
}

function data(items: RunListItem[], overrides: Partial<RunListData> = {}): RunListData {
  return { items, loading: false, error: null, hasMore: false, loadingMore: false, loadMore: vi.fn(), reload: vi.fn(), ...overrides };
}

function LocationProbe() {
  const location = useLocation();
  return <p data-testid="location">{location.pathname}</p>;
}

function renderList(props: Partial<Parameters<typeof RunList>[0]> & { data: RunListData }) {
  return render(
    <MemoryRouter initialEntries={['/start']}>
      <Routes>
        <Route path="*" element={<><RunList title="Runs" emptyText="Nothing here yet." {...props} /><LocationProbe /></>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('RunList', () => {
  it('shows the empty text and no count when there are no runs', () => {
    renderList({ data: data([]) });
    expect(screen.getByText('Nothing here yet.')).toBeTruthy();
    expect(screen.queryByText(/runs$/)).toBeNull();
    expect(screen.getByRole('region', { name: 'Runs' })).toBeTruthy();
  });

  it('renders a row per run with its label, state, and the total count', () => {
    renderList({ data: data([item('r1', 'blocked'), item('r2', 'completed'), item('r3', null)]), description: 'Recent runs' });
    expect(screen.getByText('Recent runs')).toBeTruthy();
    expect(screen.getByText('3 runs')).toBeTruthy();
    expect(screen.getByText('Task r1')).toBeTruthy();
    expect(screen.getByText('Needs input')).toBeTruthy();
    expect(screen.getByText('Done')).toBeTruthy();
    expect(screen.getByText('Unavailable')).toBeTruthy();
    expect(screen.getAllByText('Live run')).toHaveLength(2);
    expect(screen.getByText('Finished')).toBeTruthy();
  });

  it('links each row to the run page with encoded IDs', () => {
    renderList({ data: data([item('r/1', 'active', 'p 1', 'Fix the build')]) });
    const link = screen.getByRole('link', { name: 'Open run Fix the build' });
    expect(link.getAttribute('href')).toBe('/projects/p%201/runs/r%2F1');
    fireEvent.click(link);
    expect(screen.getByTestId('location').textContent).toBe('/projects/p%201/runs/r%2F1');
  });

  it('shows Load more only when the service has another page', () => {
    const loadMore = vi.fn();
    const { unmount } = renderList({ data: data([item('r1', 'idle')]) });
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
    unmount();
    renderList({ data: data([item('r1', 'idle')], { hasMore: true, loadMore }) });
    expect(screen.getByText('1+ runs')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(loadMore).toHaveBeenCalledTimes(1);
  });

  it('disables Load more while the next page loads', () => {
    renderList({ data: data([item('r1', 'idle')], { hasMore: true, loadingMore: true }) });
    expect((screen.getByRole('button', { name: 'Loading more runs\u2026' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('limits rows, still counts every loaded run, and hides Load more', () => {
    renderList({ data: data([item('r1', 'idle'), item('r2', 'idle'), item('r3', 'idle')], { hasMore: true }), limit: 2 });
    expect(screen.getByText('3+ runs')).toBeTruthy();
    expect(screen.getAllByRole('link', { name: /^Open run/ })).toHaveLength(2);
    expect(screen.queryByText('Task r3')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('adds the project name when given project names', () => {
    renderList({ data: data([item('r1', 'idle', 'p1'), item('r2', 'idle', 'p2')]), projectNames: new Map([['p1', 'Alpha']]) });
    expect(screen.getByText(/^Alpha \u00b7 Started/)).toBeTruthy();
    expect(screen.getByText(/^p2 \u00b7 Started/)).toBeTruthy();
  });
});
