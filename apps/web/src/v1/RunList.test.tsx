import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RunList } from './RunList';
import type { RunListData, RunListItem } from './runListData';

function item(runId: string, executionState: string | null, projectId = 'p1'): RunListItem {
  return { projectId, runId, executionState, time: { label: 'Last opened', value: '2026-01-01T00:00:00Z' } };
}

function data(items: RunListItem[], remove?: RunListData['remove']): RunListData {
  return { items, remove, reload: vi.fn() };
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

  it('renders a row per run with its state and the total count', () => {
    renderList({ data: data([item('r1', 'blocked'), item('r2', null)]), description: 'Recent runs' });
    expect(screen.getByText('Recent runs')).toBeTruthy();
    expect(screen.getByText('2 runs')).toBeTruthy();
    expect(screen.getByText('Run r1')).toBeTruthy();
    expect(screen.getByText('Needs input')).toBeTruthy();
    expect(screen.getByText('State when last opened')).toBeTruthy();
    expect(screen.getByText('Unavailable')).toBeTruthy();
    expect(screen.getByText('State not read yet')).toBeTruthy();
  });

  it('opens the run page with encoded IDs', () => {
    renderList({ data: data([item('r/1', 'active', 'p 1')]) });
    fireEvent.click(screen.getByRole('button', { name: 'Open run r/1' }));
    expect(screen.getByTestId('location').textContent).toBe('/projects/p%201/runs/r%2F1');
  });

  it('removes a row through the data adapter', () => {
    const remove = vi.fn();
    const run = item('r1', 'completed');
    renderList({ data: data([run], remove) });
    fireEvent.click(screen.getByRole('button', { name: 'Remove run r1 from this list' }));
    expect(remove).toHaveBeenCalledWith(run);
  });

  it('hides Remove when the adapter cannot remove', () => {
    renderList({ data: data([item('r1', 'completed')]) });
    expect(screen.queryByRole('button', { name: /Remove run/ })).toBeNull();
  });

  it('limits rows but still counts every run', () => {
    renderList({ data: data([item('r1', 'idle'), item('r2', 'idle'), item('r3', 'idle')]), limit: 2 });
    expect(screen.getByText('3 runs')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /^Open run/ })).toHaveLength(2);
    expect(screen.queryByText('Run r3')).toBeNull();
  });

  it('adds the project name when given project names', () => {
    renderList({ data: data([item('r1', 'idle', 'p1'), item('r2', 'idle', 'p2')]), projectNames: new Map([['p1', 'Alpha']]) });
    expect(screen.getByText(/^Alpha · Last opened/)).toBeTruthy();
    expect(screen.getByText(/^p2 · Last opened/)).toBeTruthy();
  });
});
