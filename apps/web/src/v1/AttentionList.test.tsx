import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AttentionBell, AttentionList } from './AttentionList';
import type { AttentionItem, RunAttention } from './runAttention';

const items: AttentionItem[] = [
  { projectId: 'p1', runId: 'r1', sessionId: 's1', requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?' },
  { projectId: 'p1', runId: 'r1', sessionId: 's2', requestId: 'a1', kind: 'awaitingPlanApproval', prompt: null },
];

function attention(overrides: Partial<RunAttention> = {}): RunAttention {
  return { binding: { projectId: 'p1', runId: 'r1' }, items: [], loading: false, error: null, reload: vi.fn(), ...overrides };
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
  it('asks the user to open a run when no run is bound', () => {
    renderWithRouter(<AttentionList attention={attention({ binding: null })} />);
    expect(screen.getByText('Open a run to see what needs your input.')).toBeTruthy();
  });

  it('shows an error before the loading state', () => {
    renderWithRouter(<AttentionList attention={attention({ error: new Error('Boom'), loading: true })} />);
    expect(screen.getByRole('alert').textContent).toBe('Could not check the open run. Boom');
  });

  it('shows the loading and empty states', () => {
    const { unmount } = renderWithRouter(<AttentionList attention={attention({ loading: true })} />);
    expect(screen.getByText('Checking the open run…')).toBeTruthy();
    unmount();
    renderWithRouter(<AttentionList attention={attention()} />);
    expect(screen.getByText('Nothing needs your input in the open run.')).toBeTruthy();
  });

  it('lists each item with its title and prompt and opens the Approvals view', () => {
    const onNavigate = vi.fn();
    renderWithRouter(<AttentionList attention={attention({ items })} onNavigate={onNavigate} />);
    const list = screen.getByRole('list', { name: 'Needs your input' });
    const entries = within(list).getAllByRole('listitem');
    expect(entries).toHaveLength(2);
    expect(within(entries[0]).getByText('Question')).toBeTruthy();
    expect(within(entries[0]).getByText('Which branch?')).toBeTruthy();
    expect(within(entries[1]).getByText('Approve the work plan')).toBeTruthy();
    expect(within(entries[1]).getByText('Run r1')).toBeTruthy();
    fireEvent.click(entries[1]);
    expect(onNavigate).toHaveBeenCalled();
    expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/r1?view=approvals');
  });
});

describe('AttentionBell', () => {
  it('labels the bell with the number of pending items', () => {
    const { unmount } = renderWithRouter(<AttentionBell attention={attention({ items })} />);
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label')).toBe('Notifications, 2 need your input');
    unmount();
    renderWithRouter(<AttentionBell attention={attention({ items: items.slice(0, 1) })} />);
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label')).toBe('Notifications, 1 needs your input');
  });

  it('uses a plain label without pending items and opens the bound run', async () => {
    renderWithRouter(<AttentionBell attention={attention()} />);
    const bell = screen.getByTestId('notification-bell');
    expect(bell.getAttribute('aria-label')).toBe('Notifications');
    fireEvent.click(bell);
    expect(await screen.findByText('Nothing needs your input in the open run.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Open run' }));
    expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/r1');
  });
});
