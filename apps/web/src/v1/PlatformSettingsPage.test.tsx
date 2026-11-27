import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { PlatformComponentArea, PlatformComponentAreaReport, PlatformEgressDestinationPage } from './contracts';
import { PlatformSettingsPage } from './PlatformSettingsPage';

const api = vi.hoisted(() => ({ getPlatformComponents: vi.fn(), listPlatformEgressDestinations: vi.fn() }));
const calls = vi.hoisted(() => ({ bindings: [] as unknown[] }));
// One stable apiCall, like the real provider. It records the binding used for each call.
const auth = vi.hoisted(() => ({
  session: null as { accessToken: string; binding: null } | null,
  authSessionEpoch: 0,
  authSessionGeneration: 0,
  unboundSessionGeneration: 0,
  authorizationContext: null as { issuer: string; actorId: string; tenantId: string } | null,
  isActiveSession: (session: unknown) => session === auth.session,
  apiCall: (operation: (token: string, tenant: string) => unknown, binding: unknown) => {
    calls.bindings.push(binding);
    return operation(auth.session?.accessToken ?? 'token', auth.authorizationContext?.tenantId ?? 'tenant-1');
  },
}));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));
vi.mock('./PlatformModelProviders', () => ({ PlatformModelProviders: () => <h2>Model providers</h2> }));
vi.mock('./pageParts', async (importActual) => ({
  ...(await importActual<typeof import('./pageParts')>()),
  PageHeading: ({ title, actions }: { title: string; actions?: ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
}));

const AREAS: PlatformComponentArea[] = [
  'sessions', 'snapshots', 'sandbox', 'storage', 'memory', 'policy', 'guardrails', 'networkPolicy',
  'cost', 'applicationHosting', 'canvas', 'secrets', 'sourceControl', 'telemetry', 'messaging', 'objectStore',
];

function area(name: PlatformComponentArea, overrides: Partial<PlatformComponentAreaReport> = {}): PlatformComponentAreaReport {
  return {
    area: name, purpose: `${name} purpose`, owner: 'environment', cardinality: 'exclusive',
    readiness: 'unknown', reason: 'ownerUnavailable', active: [], available: [], ...overrides,
  };
}

beforeEach(() => {
  api.getPlatformComponents.mockReset().mockResolvedValue({ observedAt: '2026-01-01T00:00:00Z', areas: [area('sessions')] });
  api.listPlatformEgressDestinations.mockReset().mockResolvedValue({ destinations: [], nextCursor: null });
  auth.session = null;
  auth.authorizationContext = null;
  auth.authSessionGeneration = 0;
  calls.bindings = [];
});

const queueTitle = 'Platform-wide provider host requests';
const emptyQueue = 'No provider host requests were returned.';

function signIn(tenant = 'tenant-1') {
  auth.authSessionGeneration += 1;
  auth.session = { accessToken: `token-${tenant}`, binding: null };
  auth.authorizationContext = { issuer: 'issuer', actorId: 'actor', tenantId: tenant };
}

function page(ids: string[], nextCursor: string | null = null): PlatformEgressDestinationPage {
  return {
    destinations: ids.map((id) => ({
      destinationId: `destination-${id}`, requestId: `request-${id}`, host: `host-${id}.example.test`,
      state: 'requested', origin: 'modelProvider', scope: 'project', tenantId: `tenant-${id}`,
      projectId: `project-${id}`, providerId: `provider-${id}`, providerRevision: 3,
      port: 443, requestedBy: 'actor', requestedAt: '2026-01-01T00:00:00Z',
    })),
    nextCursor,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function CurrentPath() {
  const location = useLocation();
  return <output aria-label="Current path">{location.pathname}</output>;
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/platform-settings']}>
      <Routes>
        <Route path="/platform-settings" element={<PlatformSettingsPage />} />
        <Route path="*" element={null} />
      </Routes>
      <CurrentPath />
    </MemoryRouter>,
  );
}

describe('Platform-wide provider host requests', () => {
  it('opens Policies with Enter after a successful empty list', async () => {
    signIn();
    const user = userEvent.setup();
    renderPage();
    await screen.findByText(emptyQueue);
    const link = screen.getByRole('link', { name: 'Open Policies' });
    link.focus();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('status', { name: 'Current path' }).textContent).toBe('/policies');
  });

  it('does not show Open Policies when access is denied', async () => {
    signIn();
    api.listPlatformEgressDestinations.mockRejectedValue(
      new GatewayError(403, {}, 'Forbidden'));
    renderPage();
    await screen.findByText('Only platform administrators can view provider host requests.');
    expect(screen.queryByRole('link', { name: 'Open Policies' })).toBeNull();
  });

  it('restores queue heading focus when session renewal removes a focused Open Policies link', async () => {
    signIn();
    const view = renderPage();
    const link = await screen.findByRole('link', { name: 'Open Policies' });
    link.focus();
    signIn();
    view.rerender(
      <MemoryRouter initialEntries={['/platform-settings']}>
        <Routes>
          <Route path="/platform-settings" element={<PlatformSettingsPage />} />
          <Route path="*" element={null} />
        </Routes>
        <CurrentPath />
      </MemoryRouter>,
    );
    expect(await screen.findByRole('link', { name: 'Open Policies' })).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByText(queueTitle));
  });

  it('shows both tenants and their project IDs in server order', async () => {
    signIn();
    api.listPlatformEgressDestinations.mockResolvedValue(page(['2', '1']));
    renderPage();
    const table = await screen.findByRole('table', { name: queueTitle });
    const [, first, second] = within(table).getAllByRole('row');
    expect(within(first).getByRole('cell', { name: 'tenant-2' })).toBeTruthy();
    expect(within(first).getByRole('cell', { name: 'project-2' })).toBeTruthy();
    expect(within(second).getByRole('cell', { name: 'tenant-1' })).toBeTruthy();
    expect(within(second).getByRole('cell', { name: 'project-1' })).toBeTruthy();
    expect(api.listPlatformEgressDestinations).toHaveBeenCalledWith('token-tenant-1', 'tenant-1', null, expect.any(AbortSignal));
    expect(screen.queryByRole('button', { name: /approve|dismiss/i })).toBeNull();
  });

  it('shows a proof outage and retries, without treating an empty response as proof of enablement', async () => {
    signIn();
    const user = userEvent.setup();
    const retry = deferred<PlatformEgressDestinationPage>();
    api.listPlatformEgressDestinations.mockRejectedValueOnce(
      new GatewayError(503, { code: 'egress_proof_unavailable' }, 'Unavailable'))
      .mockReturnValueOnce(retry.promise);
    renderPage();
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Couldn’t verify provider host requests');
    expect(screen.queryByText(emptyQueue)).toBeNull();
    const action = screen.getByRole('button', { name: 'Retry' });
    action.focus();
    await user.keyboard('{Enter}');
    expect(document.activeElement).toBe(action);
    expect(action.getAttribute('aria-disabled')).toBe('true');
    await act(async () => retry.resolve(page([])));
    const status = await screen.findByText(emptyQueue);
    expect(status.getAttribute('role')).toBe('status');
    expect(document.activeElement).toBe(status);
    expect(api.listPlatformEgressDestinations.mock.calls.map((call) => call[2])).toEqual([null, null]);
  });

  it('follows empty pages and keeps earlier rows when an append fails and retries', async () => {
    signIn();
    api.listPlatformEgressDestinations
      .mockResolvedValueOnce(page(['1'], 'first'))
      .mockResolvedValueOnce(page([], 'second'))
      .mockRejectedValueOnce(new GatewayError(503, {}, 'Unavailable'))
      .mockResolvedValueOnce(page(['2']));
    renderPage();
    const more = () => screen.getByRole('button', { name: 'Load more' });
    await screen.findByRole('rowheader', { name: 'host-1.example.test' });
    fireEvent.click(more());
    await waitFor(() => expect(more().getAttribute('aria-disabled')).not.toBe('true'));
    fireEvent.click(more());
    await screen.findByRole('alert');
    expect(screen.getByRole('rowheader', { name: 'host-1.example.test' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByRole('rowheader', { name: 'host-2.example.test' });
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
    expect(api.listPlatformEgressDestinations.mock.calls.map((call) => call[2])).toEqual([null, 'first', 'second', 'second']);
  });

  it('keeps keyboard focus through load, error, and retry after an empty first page', async () => {
    signIn();
    const user = userEvent.setup();
    const append = deferred<PlatformEgressDestinationPage>();
    const retry = deferred<PlatformEgressDestinationPage>();
    api.listPlatformEgressDestinations
      .mockResolvedValueOnce(page([], 'first'))
      .mockReturnValueOnce(append.promise)
      .mockReturnValueOnce(retry.promise);
    renderPage();
    await screen.findByText('No requests were returned on this page.');
    expect(screen.queryByText(emptyQueue)).toBeNull();
    expect(screen.queryByRole('table', { name: queueTitle })).toBeNull();

    screen.getByRole('button', { name: 'Refresh provider host requests' }).focus();
    await user.tab();
    const action = screen.getByRole('button', { name: 'Load more' });
    expect(document.activeElement).toBe(action);
    await user.keyboard('{Enter}');
    expect(document.activeElement).toBe(action);
    expect(action.getAttribute('aria-disabled')).toBe('true');

    await act(async () => append.reject(new GatewayError(503, {}, 'Unavailable')));
    expect(screen.getByRole('button', { name: 'Retry' })).toBe(action);
    expect(document.activeElement).toBe(action);
    await user.keyboard('{Enter}');
    expect(document.activeElement).toBe(action);
    expect(action.getAttribute('aria-disabled')).toBe('true');

    await act(async () => retry.resolve(page(['1'])));
    expect(screen.queryByRole('button', { name: /load more|retry/i })).toBeNull();
    expect(document.activeElement).toBe(screen.getByText('Provider host requests shown: 1.'));
    expect(api.listPlatformEgressDestinations.mock.calls.map((call) => call[2])).toEqual([null, 'first', 'first']);
  });

  it.each(['session-success', 'session-error', 'tenant-change', 'refresh'] as const)('discards a late append: %s', async (scenario) => {
    signIn();
    const { promise, resolve, reject } = deferred<PlatformEgressDestinationPage>();
    api.listPlatformEgressDestinations.mockResolvedValueOnce(page(['1'], 'next'))
      .mockReturnValueOnce(promise).mockResolvedValueOnce(page(['2']));
    const view = renderPage();
    await screen.findByRole('rowheader', { name: 'host-1.example.test' });
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    if (scenario === 'refresh') {
      fireEvent.click(screen.getByRole('button', { name: 'Refresh provider host requests' }));
    } else {
      if (scenario === 'tenant-change')
        auth.authorizationContext = { issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-2' };
      else
        signIn();
      view.rerender(
        <MemoryRouter initialEntries={['/platform-settings']}>
          <Routes>
            <Route path="/platform-settings" element={<PlatformSettingsPage />} />
            <Route path="*" element={null} />
          </Routes>
          <CurrentPath />
        </MemoryRouter>,
      );
    }
    expect(screen.queryByText('host-1.example.test')).toBeNull();
    await screen.findByRole('rowheader', { name: 'host-2.example.test' });
    await act(async () => {
      if (scenario === 'session-error') reject(new GatewayError(503, {}, 'Unavailable'));
      else resolve(page(['3'], 'stale'));
    });
    expect(screen.queryByText('host-3.example.test')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
    expect(api.listPlatformEgressDestinations.mock.calls.map((call) => call[2])).toEqual([null, 'next', null]);
  });

  it('clears the queue on 403 but keeps provider settings and the component report', async () => {
    signIn();
    api.listPlatformEgressDestinations.mockResolvedValueOnce(page(['1'], 'next'))
      .mockRejectedValueOnce(new GatewayError(403, {}, 'Forbidden'));
    renderPage();
    await screen.findByRole('rowheader', { name: 'host-1.example.test' });
    const action = screen.getByRole('button', { name: 'Load more' });
    action.focus();
    fireEvent.click(action);
    await screen.findByText('Only platform administrators can view provider host requests.');
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Refresh provider host requests' }));
    expect(screen.queryByRole('table', { name: queueTitle })).toBeNull();
    expect(screen.getByRole('heading', { name: 'Model providers' })).toBeTruthy();
    expect(screen.getByRole('table', { name: 'Platform component areas' })).toBeTruthy();
  });
});

describe('PlatformSettingsPage', () => {
  it('renders every area with its reported state, without inventing one', async () => {
    api.getPlatformComponents.mockResolvedValue({
      observedAt: '2026-01-01T00:00:00Z',
      areas: AREAS.map((name) => name === 'sandbox'
        ? area(name, {
          readiness: 'ready', reason: null,
          active: [{
            implementationId: 'aca', displayName: 'Container sandbox', version: '1.0', hosting: 'hosted', layer: null,
            meterSource: null, order: null, readiness: 'degraded', reason: 'readinessTimedOut', observedAt: '2026-01-01T00:00:00Z',
          }],
          available: [{ implementationId: 'local', displayName: 'Local sandbox', version: '0.1', enabled: false }],
        })
        : name === 'canvas' ? area(name, { owner: 'none', readiness: 'notAvailable', reason: 'noBackendOwner' }) : area(name)),
    });
    renderPage();
    const table = await screen.findByRole('table', { name: 'Platform component areas' });
    // One header row plus one row per area.
    expect(within(table).getAllByRole('row')).toHaveLength(17);
    expect(within(table).getByRole('rowheader', { name: /Network policy/ })).toBeTruthy();
    expect(within(table).getAllByText('Status not reported')).toHaveLength(14);
    expect(within(table).getByText('Ready')).toBeTruthy();
    expect(within(table).getByText('Degraded')).toBeTruthy();
    expect(within(table).getByText('The readiness check timed out.')).toBeTruthy();
    expect(within(table).getByText('Not available')).toBeTruthy();
    expect(within(table).getByText('No service owns this area yet.')).toBeTruthy();
    expect(within(table).getByText(/turned off/)).toBeTruthy();
    // The report is read with an unbound token.
    expect(calls.bindings).toEqual([null]);
  });

  it('shows admin-only copy for a 403', async () => {
    api.getPlatformComponents.mockRejectedValue(new GatewayError(403, {}, 'Forbidden'));
    renderPage();
    expect(await screen.findByText('Only tenant or platform administrators can view platform components.')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('reads the report again on Refresh', async () => {
    api.getPlatformComponents.mockResolvedValue({ observedAt: '2026-01-01T00:00:00Z', areas: [area('sessions')] });
    renderPage();
    await screen.findByRole('table');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(api.getPlatformComponents).toHaveBeenCalledTimes(2));
  });
});
