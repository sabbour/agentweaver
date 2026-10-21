import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { OrchestrationsPage } from './OrchestrationsPage';
import type { RunListData, RunListItem } from './runListData';

const mocks = vi.hoisted(() => ({
  runs: null as RunListData | null,
  useBrowserRunList: vi.fn(),
}));

vi.mock('./runListData', () => ({
  // Record the project id the page asks for, then return the test's runs.
  useBrowserRunList: (projectId?: string) => {
    mocks.useBrowserRunList(projectId);
    return mocks.runs;
  },
}));

vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return {
    ...actual,
    PageHeading: ({ title, actions }: { title: string; actions?: React.ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
  };
});

function item(runId: string, executionState: string | null): RunListItem {
  return { projectId: 'p1', runId, executionState, time: { label: 'Last opened', value: '2026-01-01T00:00:00Z' } };
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
    mocks.useBrowserRunList.mockClear();
    mocks.runs = { items: [], reload: vi.fn(), remove: vi.fn() };
  });

  it('reads the route project and shows the empty list', () => {
    renderPage();
    expect(mocks.useBrowserRunList).toHaveBeenCalledWith('p1');
    expect(screen.getByRole('heading', { name: 'Orchestrations' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Recent runs in this browser' })).toBeTruthy();
    expect(screen.getByText('Runs you open appear here.')).toBeTruthy();
    expect(screen.queryByLabelText('Run status summary')).toBeNull();
  });

  it('groups runs into In progress and Finished with a state summary', () => {
    mocks.runs!.items = [item('r1', 'blocked'), item('r2', 'active'), item('r3', 'completed'), item('r4', 'failed'), item('r5', 'blocked')];
    renderPage();
    const progress = screen.getByRole('region', { name: 'In progress' });
    const finished = screen.getByRole('region', { name: 'Finished' });
    expect(within(progress).getAllByRole('button', { name: /^Open run/ }).map((button) => button.getAttribute('aria-label')))
      .toEqual(['Open run r1', 'Open run r2', 'Open run r5']);
    expect(within(finished).getAllByRole('button', { name: /^Open run/ }).map((button) => button.getAttribute('aria-label')))
      .toEqual(['Open run r3', 'Open run r4']);
    const summary = screen.getByLabelText('Run status summary');
    // Pills list each state once with its count, in first-seen order.
    expect(summary.textContent).toBe('2Needs input1Working1Done1Failed');
  });

  it('omits an empty group', () => {
    mocks.runs!.items = [item('r1', 'idle')];
    renderPage();
    expect(screen.getByRole('region', { name: 'In progress' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Finished' })).toBeNull();
  });

  it('reloads the list on Refresh', () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(mocks.runs!.reload).toHaveBeenCalled();
  });
});
