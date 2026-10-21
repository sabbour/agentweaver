import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createFakeGateway, NOW, project } from './__fixtures__/cosGateway';
import type { FakeGateway } from './__fixtures__/cosGateway';
import type { ChiefOfStaffConversation } from './contracts';

const hoisted = vi.hoisted(() => ({
  gateway: null as FakeGateway | null,
  // Binding passed to each apiCall, so tests can check which session each call used.
  bindings: [] as Array<unknown>,
  ensureSession: vi.fn(async () => true),
  authorize: vi.fn(async () => undefined),
}));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return {
    ...actual,
    gatewayClient: new actual.AgentweaverGatewayClient('https://gw.test/api/v1', (input, init) => hoisted.gateway!.fetch(input, init)),
  };
});

// Fake auth: the token is 'bound' for run-bound calls and 'unbound' otherwise.
// ensureSession and authorize are spies so tests can force the sign-in path.
vi.mock('./AuthContext', () => {
  const auth = {
    apiCall: (op: (token: string, tenant: null) => Promise<unknown>, binding: unknown) => {
      hoisted.bindings.push(binding);
      return op(binding ? 'bound' : 'unbound', null);
    },
    ensureSession: (...args: unknown[]) => hoisted.ensureSession(...(args as [])),
    authorize: (...args: unknown[]) => hoisted.authorize(...(args as [])),
  };
  return { useAuth: () => auth };
});

import { PENDING_CHAT_STORAGE_KEY } from './chiefOfStaff';
import { NewChatPage } from './ChiefOfStaffPanels';

/** Prints the current path so tests can assert navigation to the run page. */
function LocationProbe() {
  const location = useLocation();
  return <p data-testid="location">{location.pathname}</p>;
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/assistant']}>
      <Routes><Route path="*" element={<><NewChatPage /><LocationProbe /></>} /></Routes>
    </MemoryRouter>,
  );
}

function conversation(overrides: Partial<ChiefOfStaffConversation>): ChiefOfStaffConversation {
  return {
    conversationId: 'conv-1', access: 'available', projectId: 'p1', runId: 'run-1', rootSessionId: 'run-1-root',
    title: 'Ship the fix', requestState: 'started', runState: 'active', createdAt: NOW, ...overrides,
  };
}

async function typeAndSend(text: string) {
  const box = await screen.findByLabelText('Message');
  fireEvent.change(box, { target: { value: text } });
  return screen.getByRole('button', { name: 'Send' });
}

/** All create-run requests sent for project p1. */
const creates = () => hoisted.gateway!.requests.filter((request) => request.method === 'POST' && request.url.pathname === '/api/v1/projects/p1/runs');

beforeEach(() => {
  hoisted.gateway = createFakeGateway({ projects: [project('p1', 'Payments'), project('p2', 'Billing')] });
  hoisted.bindings = [];
  hoisted.ensureSession.mockReset().mockResolvedValue(true);
  hoisted.authorize.mockReset().mockResolvedValue(undefined);
  window.sessionStorage.clear();
});

afterEach(() => window.sessionStorage.clear());

describe('New chat', () => {
  it('creates one run for a repeated send, binds it, starts it, and opens the run page', async () => {
    renderPage();
    const send = await typeAndSend('Fix the flaky test');
    const form = send.closest('form')!;
    // Double submit in one tick must still create only one run.
    act(() => {
      fireEvent.submit(form);
      fireEvent.submit(form);
    });
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    expect(creates()).toHaveLength(1);
    expect(creates()[0].body).toEqual({ firstMessage: 'Fix the flaky test', expectedProjectConfigRevision: 3 });
    const writes = hoisted.gateway!.writes().map((request) => `${request.method} ${request.url.pathname}`);
    // The required order: create, bind to My conversations, then start.
    expect(writes).toEqual([
      'POST /api/v1/projects/p1/runs',
      'POST /api/v1/chief-of-staff/conversations',
      'POST /api/v1/projects/p1/runs/run-1/start',
    ]);
    expect(hoisted.ensureSession).toHaveBeenCalledWith({ projectId: 'p1', runId: 'run-1' }, false);
    // After a successful start nothing is left pending in storage.
    expect(window.sessionStorage.getItem(PENDING_CHAT_STORAGE_KEY)).toBeNull();
  });

  it('reuses the Idempotency-Key when the first response is lost', async () => {
    // The server creates the run but the client never sees the response.
    hoisted.gateway!.failNext('POST', '/api/v1/projects/p1/runs', 'lost');
    renderPage();
    const send = await typeAndSend('Fix the flaky test');
    fireEvent.click(send);
    expect((await screen.findByRole('alert')).textContent).toContain('Failed to fetch');
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    expect(creates()).toHaveLength(2);
    const [first, second] = creates().map((request) => request.headers.get('Idempotency-Key'));
    expect(first).toBeTruthy();
    expect(second).toBe(first);
    // The replayed key returned the same run, so only one run exists.
    expect(hoisted.gateway!.state.runs.p1).toHaveLength(1);
  });

  it('uses a new key when the picked project changes', async () => {
    hoisted.gateway!.failNext('POST', '/api/v1/projects/p1/runs', { status: 503, body: { error: 'service_unavailable' } });
    renderPage();
    const send = await typeAndSend('Fix the flaky test');
    fireEvent.click(send);
    await screen.findByRole('alert');
    fireEvent.change(screen.getByLabelText('Project'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p2/runs/run-1'));
    const keys = hoisted.gateway!.requests.filter((request) => request.method === 'POST' && request.url.pathname.endsWith('/runs'))
      .map((request) => request.headers.get('Idempotency-Key'));
    expect(keys).toHaveLength(2);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('keeps a created chat across sign-in and resumes it after reload without a second create', async () => {
    // The run token needs interactive sign-in, so start stops after create and bind.
    hoisted.ensureSession.mockResolvedValue(false);
    const first = renderPage();
    fireEvent.click(await typeAndSend('Fix the flaky test'));
    expect(await screen.findByText('The chat was created. Sign in for this run to start it.')).toBeTruthy();
    const stored = window.sessionStorage.getItem(PENDING_CHAT_STORAGE_KEY)!;
    // Storage holds only run identities, never the message text.
    expect(JSON.parse(stored)).toEqual([{ projectId: 'p1', runId: 'run-1', rootSessionId: 'run-1-root' }]);
    expect(stored).not.toContain('flaky');
    first.unmount();

    // "Reload" after sign-in: a new mount resumes the saved chat on its own.
    hoisted.ensureSession.mockResolvedValue(true);
    renderPage();
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    // No second create. Bind ran twice (once per attempt) but is idempotent, so one conversation exists.
    expect(creates()).toHaveLength(1);
    const binds = hoisted.gateway!.writes('/api/v1/chief-of-staff/conversations');
    expect(binds).toHaveLength(2);
    expect(binds.every((request) => request.body && (request.body as { runId: string }).runId === 'run-1')).toBe(true);
    expect(hoisted.gateway!.state.conversations).toHaveLength(1);
    expect(window.sessionStorage.getItem(PENDING_CHAT_STORAGE_KEY)).toBeNull();
  });

  it('starts after interactive sign-in from the pending notice', async () => {
    // Silent sign-in fails once; after the interactive sign-in it succeeds.
    hoisted.ensureSession.mockResolvedValueOnce(false).mockResolvedValue(true);
    renderPage();
    fireEvent.click(await typeAndSend('Fix the flaky test'));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign in and start' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    expect(hoisted.authorize).toHaveBeenCalledWith({ projectId: 'p1', runId: 'run-1' });
  });

  it('uses the unbound session for Chief of Staff and project reads and the run binding only for start', async () => {
    renderPage();
    fireEvent.click(await typeAndSend('Fix the flaky test'));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    const start = hoisted.gateway!.requests.find((request) => request.url.pathname.endsWith('/start'))!;
    const cos = hoisted.gateway!.requests.filter((request) => request.url.pathname.includes('/chief-of-staff/'));
    // Start uses the run-bound token; every Chief of Staff route uses the unbound one.
    expect(start.headers.get('Authorization')).toBe('Bearer bound');
    expect(cos.length).toBeGreaterThan(0);
    expect(cos.every((request) => request.headers.get('Authorization') === 'Bearer unbound')).toBe(true);
  });
});

describe('My conversations', () => {
  it('shows the same conversation after a reload and opens its run page', async () => {
    hoisted.gateway!.state.conversations = [conversation({})];
    const first = renderPage();
    const list = await screen.findByRole('list', { name: 'My conversations' });
    expect(within(list).getByText('Ship the fix')).toBeTruthy();
    expect(within(list).getByText('Working')).toBeTruthy();
    first.unmount();

    renderPage();
    const reloaded = await screen.findByRole('list', { name: 'My conversations' });
    expect(within(reloaded).getByText('Ship the fix')).toBeTruthy();
    expect(within(reloaded).getByText(/Payments/)).toBeTruthy();
    fireEvent.click(within(reloaded).getByRole('button', { name: 'Open Ship the fix' }));
    expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1');
  });

  it('offers Start for a chat that was created but not started', async () => {
    // Seed an idle run with a conversation still waiting for start.
    hoisted.gateway!.state.runs.p1 = [{ ...(await import('./__fixtures__/cosGateway')).run('run-1', 'idle', 'Ship the fix') }];
    hoisted.gateway!.state.conversations = [conversation({ requestState: 'awaiting_binding', runState: 'idle' })];
    renderPage();
    const list = await screen.findByRole('list', { name: 'My conversations' });
    expect(within(list).getByText('Not started')).toBeTruthy();
    fireEvent.click(within(list).getByRole('button', { name: 'Start Ship the fix' }));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/projects/p1/runs/run-1'));
    expect(hoisted.gateway!.writes().map((request) => request.url.pathname)).toContain('/api/v1/projects/p1/runs/run-1/start');
  });

  it('shows an unavailable conversation without project content', async () => {
    hoisted.gateway!.state.conversations = [conversation({
      access: 'unavailable', projectId: null, runId: null, rootSessionId: null, title: null, requestState: null, runState: null,
    })];
    renderPage();
    const list = await screen.findByRole('list', { name: 'My conversations' });
    // "Unavailable" appears as both the badge and the title. No project name and no action buttons.
    expect(within(list).getAllByText('Unavailable')).toHaveLength(2);
    expect(within(list).queryByText(/Payments/)).toBeNull();
    expect(within(list).queryByRole('button')).toBeNull();
  });

  it('shows cancelled chats as Cancelled', async () => {
    hoisted.gateway!.state.conversations = [conversation({ requestState: 'cancelled', runState: 'cancelled' })];
    renderPage();
    const list = await screen.findByRole('list', { name: 'My conversations' });
    expect(within(list).getByText('Cancelled')).toBeTruthy();
  });
});