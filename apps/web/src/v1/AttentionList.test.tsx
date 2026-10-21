import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AttentionBell, AttentionList } from './AttentionList';
import type { AttentionItem, RunAttention } from './runAttention';

const createdAt = new Date(Date.now() - 5 * 60_000).toISOString();

const items: AttentionItem[] = [
  { notificationId: 'n1', source: 'pendingGate', projectId: 'p1', runId: 'r1', sessionId: 's1', createdAt, requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?' },
  { notificationId: 'n2', source: 'pendingGate', projectId: 'p2', runId: 'r2', sessionId: 's2', createdAt, requestId: 'a1', kind: 'awaitingPlanApproval', prompt: null },
];

function attention(overrides: Partial<RunAttention> = {}): RunAttention {
  return {
    items: [], count: 0, loading: false, error: null, reload: vi.fn(), dismiss: vi.fn(() => Promise.resolve()), dismissError: null,
    ...overrides,
  };
}

function LocationProbe() {
  const location = useLocation();
  return <p data-testid="location">{`${location.pathname}${location.search}`}</p>;
}

function renderWithRouter(element: React.ReactElement) {
  return render(
    <MemoryRouter initialEntries={['/start']}>
      <Routes><Route path="*" element={<>{element}<LocationProbe /></>} /></Routes>
    </MemoryRouter>,
  );
}

describe('AttentionList', () => {
  it('shows an error before the loading state', () => {
    renderWithRouter(<AttentionList attention={attention({ error: new Error('Boom'), loading: true })} />);
    expect(screen.getByRole('alert').textContent).toBe('Could not load notifications. Boom');
  });

  it('shows the loading and empty states', () => {
    const { unmount } = renderWithRouter(<AttentionList attention={attention({ loading: true })} />);
    expect(screen.getByText('Loading notifications…')).toBeTruthy();
    unmount();
    renderWithRouter(<AttentionList attention={attention()} />);
    expect(screen.getByText('Nothing needs your attention.')).toBeTruthy();
  });

  it('lists items from several projects with names, prompts, and time, and opens the Approvals view', () => {
    const onNavigate = vi.fn();
    renderWithRouter(
      <AttentionList attention={attention({ items, count: 2 })} projectNames={new Map([['p1', 'Alpha']])} onNavigate={onNavigate} />,
    );
    const entries = within(screen.getByRole('list', { name: 'Needs your attention' })).getAllByRole('listitem');
    expect(entries).toHaveLength(2);
    expect(within(entries[0]).getByText('Question')).toBeTruthy();
    expect(within(entries[0]).getByText('Which branch?')).toBeTruthy();
    expect(within(entries[0]).getByText('Alpha · Run r1')).toBeTruthy();
    expect(within(entries[0]).getByText('5m ago')).toBeTruthy();
    expect(within(entries[1]).getByText('Approve the work plan')).toBeTruthy();
    // Without a known name the project ID is shown.
    expect(within(entries[1]).getByText('p2 · Run r2')).toBeTruthy();
    fireEvent.click(within(entries[1]).getByRole('link'));
    expect(onNavigate).toHaveBeenCalled();
    expect(screen.getByTestId('location').textContent).toBe('/projects/p2/runs/r2?view=approvals');
  });

  it('shows backlog promotions as created subtasks that open the project Board', () => {
    const promotions: AttentionItem[] = [
      { notificationId: 'm1', source: 'backlogPromoted', projectId: 'p 1', runId: 'r1', sessionId: 's1', createdAt, promotionId: 'm1', taskCount: 1 },
      { notificationId: 'm2', source: 'backlogPromoted', projectId: 'p 1', runId: 'r2', sessionId: 's2', createdAt, promotionId: 'm2', taskCount: 3 },
    ];
    renderWithRouter(<AttentionList attention={attention({ items: promotions, count: 2 })} />);
    const entries = within(screen.getByRole('list', { name: 'Needs your attention' })).getAllByRole('listitem');
    expect(within(entries[0]).getByText('1 subtask created')).toBeTruthy();
    expect(within(entries[1]).getByText('3 subtasks created')).toBeTruthy();
    fireEvent.click(within(entries[1]).getByRole('link'));
    expect(screen.getByTestId('location').textContent).toBe('/projects/p%201/board');
  });

  it('dismisses one item without following its link', () => {
    const dismiss = vi.fn(() => Promise.resolve());
    renderWithRouter(<AttentionList attention={attention({ items, count: 2, dismiss })} />);
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification: Question' }));
    expect(dismiss).toHaveBeenCalledWith('n1');
    expect(screen.getByTestId('location').textContent).toBe('/start');
  });

  it('shows a dismiss failure and notes when more items exist than shown', () => {
    renderWithRouter(<AttentionList attention={attention({ items, count: 9, dismissError: new Error('Denied') })} />);
    expect(screen.getByRole('alert').textContent).toBe('Could not dismiss the notification. Denied');
    expect(screen.getByText('Showing the newest 2 of 9.')).toBeTruthy();
  });
});

describe('AttentionBell', () => {
  it('labels the bell with the service count, not the page length', () => {
    const { unmount } = renderWithRouter(<AttentionBell attention={attention({ items, count: 12 })} />);
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label')).toBe('Notifications, 12 need your attention');
    expect(screen.getByText('12')).toBeTruthy();
    unmount();
    renderWithRouter(<AttentionBell attention={attention({ items: items.slice(0, 1), count: 1 })} />);
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label')).toBe('Notifications, 1 needs your attention');
  });

  it('hides the badge on error and shows the error in the panel', async () => {
    renderWithRouter(<AttentionBell attention={attention({ count: 3, error: new Error('Boom') })} />);
    const bell = screen.getByTestId('notification-bell');
    expect(bell.getAttribute('aria-label')).toBe('Notifications');
    expect(screen.queryByText('3')).toBeNull();
    fireEvent.click(bell);
    expect((await screen.findByRole('alert')).textContent).toBe('Could not load notifications. Boom');
  });

  it('closes the panel when an item link is followed', async () => {
    renderWithRouter(<AttentionBell attention={attention({ items, count: 2 })} projectNames={new Map([['p1', 'Alpha']])} />);
    fireEvent.click(screen.getByTestId('notification-bell'));
    expect(await screen.findByText('Alpha · Run r1')).toBeTruthy();
    fireEvent.click(screen.getByText('Which branch?'));
    expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/r1?view=approvals');
    expect(screen.queryByText('Alpha · Run r1')).toBeNull();
  });
});
