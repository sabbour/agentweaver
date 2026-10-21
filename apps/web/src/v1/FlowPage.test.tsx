import { render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createFakeGateway, notification, project, run } from './__fixtures__/cosGateway';
import type { FakeGateway } from './__fixtures__/cosGateway';

const hoisted = vi.hoisted(() => ({ gateway: null as FakeGateway | null, bindings: [] as unknown[] }));

// Route the real Gateway and notifications clients through an in-memory fake Gateway,
// so the test covers real request building and response parsing.
vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return { ...actual, gatewayClient: new actual.AgentweaverGatewayClient('https://gw.test/api/v1', (i, init) => hoisted.gateway!.fetch(i, init)) };
});

vi.mock('./notificationsClient', async () => {
  const actual = await vi.importActual<typeof import('./notificationsClient')>('./notificationsClient');
  return { ...actual, notificationsClient: new actual.NotificationsClient('https://gw.test/api/v1', (i, init) => hoisted.gateway!.fetch(i, init)) };
});

vi.mock('./AuthContext', () => {
  // Record the binding of every call, so a test can prove Flow never asks for a run-bound token.
  const auth = {
    session: { accessToken: 'unbound' },
    apiCall: (op: (token: string, tenant: null) => Promise<unknown>, binding: unknown) => {
      hoisted.bindings.push(binding);
      return op(binding ? 'bound' : 'unbound', null);
    },
  };
  return { useAuth: () => auth };
});

import { FlowPage } from './FlowPage';
import { PULL_REQUEST_NOTE, flowGroup } from './flowGroups';

function renderFlow() {
  return render(
    <MemoryRouter initialEntries={['/projects/p1/flow']}>
      <Routes><Route path="/projects/:projectId/flow" element={<FlowPage />} /></Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  hoisted.bindings = [];
  hoisted.gateway = createFakeGateway({
    projects: [project('p1', 'Payments')],
    // One run for every known state, so each Flow group is exercised.
    runs: {
      p1: [
        run('r-idle', 'idle', 'Draft the plan'),
        run('r-active', 'active', 'Write the code'),
        run('r-blocked', 'blocked', 'Pick a branch'),
        run('r-done', 'completed', 'Ship it'),
        run('r-failed', 'failed', 'Broken build'),
        run('r-cancelled', 'cancelled', 'Stopped early'),
        run('r-unknown', 'indeterminate', 'Lost track'),
      ],
    },
    // A pending gate, a dismissed gate, a gate in another project, and a non-gate notice.
    notifications: [
      notification('n-open', { prompt: 'Which branch?' }),
      notification('n-dismissed', { prompt: 'Approve the plan?', requestId: 'req-2', dismissed: true }),
      notification('n-other', { projectId: 'p2', prompt: 'Other project' }),
      notification('n-promo', {
        source: 'backlogPromoted', promotionId: 'promo-1', taskCount: 3,
        requestId: null, kind: null, gateKind: null, stateVersion: null, prompt: 'Backlog promoted',
      }),
    ],
  });
});

describe('flowGroup', () => {
  it('maps run states to Open, Working, and Done', () => {
    expect(flowGroup('idle')).toBe('open');
    expect(flowGroup('active')).toBe('working');
    expect(flowGroup('blocked')).toBe('working');
    for (const state of ['completed', 'failed', 'cancelled', 'indeterminate']) expect(flowGroup(state)).toBe('done');
    expect(flowGroup('later')).toBeNull();
  });
});

describe('FlowPage', () => {
  it('groups runs and keeps failed, cancelled, and unknown outcomes distinct in Done', async () => {
    renderFlow();
    const open = await screen.findByRole('region', { name: 'Open' });
    await waitFor(() => expect(within(open).getByText('Draft the plan')).toBeTruthy());
    const working = screen.getByRole('region', { name: 'Working' });
    expect(within(working).getByText('Write the code')).toBeTruthy();
    expect(within(working).getByText('Pick a branch')).toBeTruthy();
    const done = screen.getByRole('region', { name: 'Done' });
    // Walk up from each Open button to its row, to read the row's full text.
    const rows = within(done).getAllByRole('button', { name: /^Open run / }).map((button) => button.parentElement?.parentElement?.textContent ?? '');
    expect(rows).toHaveLength(4);
    for (const [title, label] of [['Ship it', 'Done'], ['Broken build', 'Failed'], ['Stopped early', 'Cancelled'], ['Lost track', 'Outcome unknown']]) {
      expect(rows.find((row) => row.includes(title))).toContain(label);
    }
    expect(within(done).queryByText('Write the code')).toBeNull();
  });

  it('shows pull request state as Unavailable and never as Open or Merged', async () => {
    renderFlow();
    await screen.findByText('Draft the plan');
    // One note per run (seven runs).
    expect(screen.getAllByText(new RegExp(PULL_REQUEST_NOTE))).toHaveLength(7);
    expect(screen.queryByText(/Merged/)).toBeNull();
  });

  it('lists dismissed gate notifications under Needs input and skips promotions', async () => {
    renderFlow();
    const needs = screen.getByRole('heading', { name: 'Needs input' }).closest('section')!;
    await waitFor(() => expect(within(needs).getByText(/Which branch\?/)).toBeTruthy());
    expect(within(needs).getByText(/Approve the plan\?/)).toBeTruthy();
    expect(within(needs).queryByText(/Other project/)).toBeNull();
    expect(within(needs).queryByText(/Backlog promoted/)).toBeNull();
    // Flow includes dismissed items, so it offers no Dismiss action.
    expect(within(needs).queryByRole('button', { name: /Dismiss/ })).toBeNull();
  });

  it('reads notifications and runs with the unbound session and never writes', async () => {
    renderFlow();
    await screen.findByText('Draft the plan');
    const reads = hoisted.gateway!.requests;
    const notifications = reads.find((request) => request.url.pathname === '/api/v1/notifications')!;
    expect(notifications.url.searchParams.get('projectId')).toBe('p1');
    expect(notifications.url.searchParams.get('includeDismissed')).toBe('true');
    // Every request used the unbound token, and Flow is read-only.
    expect(reads.every((request) => request.headers.get('Authorization') === 'Bearer unbound')).toBe(true);
    expect(hoisted.bindings.every((binding) => binding === null)).toBe(true);
    expect(hoisted.gateway!.writes()).toHaveLength(0);
  });
});