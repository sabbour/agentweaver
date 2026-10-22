import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { PlatformComponentArea, PlatformComponentAreaReport } from './contracts';
import { PlatformSettingsPage } from './PlatformSettingsPage';

const api = vi.hoisted(() => ({ getPlatformComponents: vi.fn() }));
const calls = vi.hoisted(() => ({ bindings: [] as unknown[] }));
// One stable apiCall, like the real provider. It records the project binding it was asked for.
const auth = vi.hoisted(() => ({
  apiCall: (operation: (token: string, tenant: string) => unknown, binding: unknown) => {
    calls.bindings.push(binding);
    return operation('token', 'tenant-1');
  },
}));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({ ...(await importActual<typeof import('./api')>()), gatewayClient: api }));
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
  vi.clearAllMocks();
  calls.bindings = [];
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
    render(<PlatformSettingsPage />);
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
    render(<PlatformSettingsPage />);
    expect(await screen.findByText('Only tenant or platform administrators can view platform components.')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('reads the report again on Refresh', async () => {
    api.getPlatformComponents.mockResolvedValue({ observedAt: '2026-01-01T00:00:00Z', areas: [area('sessions')] });
    render(<PlatformSettingsPage />);
    await screen.findByRole('table');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(api.getPlatformComponents).toHaveBeenCalledTimes(2));
  });
});
