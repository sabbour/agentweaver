import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { OverviewPage } from '../pages/OverviewPage';
import { gatewayFailureFetch, gatewayFixtureFetch } from './retainedGatewayTest';

const mocks = vi.hoisted(() => ({ fetch: vi.fn(), apiCall: vi.fn() }));
vi.mock('../v1/AuthContext', () => ({ useAuth: () => ({ apiCall: mocks.apiCall }) }));
vi.mock('../v1/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../v1/api')>();
  return { ...actual, gatewayClient: new actual.AgentweaverGatewayClient('/api/v1', mocks.fetch) };
});

beforeEach(() => {
  mocks.fetch.mockImplementation(gatewayFixtureFetch);
  mocks.apiCall.mockImplementation((operation: (token: string, tenant: null) => Promise<unknown>) => operation('test-token', null));
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function renderPage() {
  return render(<AzureFluentProvider density="compact"><MemoryRouter initialEntries={['/overview']}><OverviewPage /></MemoryRouter></AzureFluentProvider>);
}
function paths(): string[] {
  return mocks.fetch.mock.calls.map(([input]) => String(input));
}

describe('retained OverviewPage v1 reads', () => {
  it('keeps all 0.x panels while reading only published paths', async () => {
    renderPage();
    await screen.findByText('Recent projects');
    await screen.findByText('sabbour/demo');
    expect(screen.getByText('AI usage & performance')).toBeDefined();
    expect(screen.getByText('Activity feed')).toBeDefined();
    expect(screen.getByText('Needs attention')).toBeDefined();
    expect(screen.getByText('1 run needs input')).toBeDefined();
    expect(screen.getByText('Active')).toBeDefined();
    expect(screen.getAllByText('Run failed', { exact: false }).length).toBeGreaterThan(0);
    expect(screen.getByText('Response duration')).toBeDefined();
    expect(screen.getByText('Time to first token (TTFT)')).toBeDefined();
    expect(screen.getByText('Health unavailable')).toBeDefined();
    expect(screen.getByText(/Cross-project in-flight, queued, and completed counts are unavailable/)).toBeDefined();
    expect(paths().map((path) => new URL(path, 'http://localhost').pathname).sort()).toEqual([
      '/api/v1/notifications', '/api/v1/projects', '/api/v1/projects/p1/backlog',
      '/api/v1/projects/p1/configuration', '/api/v1/projects/p1/metrics',
      '/api/v1/projects/p1/metrics', '/api/v1/projects/p1/runs', '/api/v1/projects/p1/usage',
    ].sort());
    expect(paths().every((path) => !path.includes('/overview') && !path.includes('/dashboard'))).toBe(true);
    expect(paths().filter((path) => path.includes('/metrics?')).every((path) => new URL(path, 'http://localhost').searchParams.get('bucket') === 'day')).toBe(true);
    expect(screen.getByRole('link', { name: /View logs/ }).getAttribute('href')).toBe('/projects/p1/orchestrations');
  });

  it('shows unavailable states when optional Overview reads fail', async () => {
    mocks.fetch.mockImplementation(gatewayFailureFetch([
      '/api/v1/notifications',
      '/api/v1/projects/p1/metrics',
      '/api/v1/projects/p1/usage',
    ]));
    renderPage();
    await screen.findByText('Recent projects');
    await screen.findByText('Notifications are unavailable from the Gateway.');
    expect(screen.getByText('Run metrics are unavailable from the Gateway.')).toBeDefined();
    expect(screen.getAllByText('Model usage is unavailable from the Gateway.')).toHaveLength(2);
    expect(screen.queryByText('No completed runs in this range.')).toBeNull();
    expect(screen.queryByText('No usage reported yet.')).toBeNull();
    expect(screen.queryByText('Nothing needs attention.')).toBeNull();
  });

  it('reads the selected range through the v1 metric and usage routes', async () => {
    renderPage();
    await screen.findByText('sabbour/demo');
    const before = paths().filter((path) => path.includes('/metrics?')).at(-1);
    fireEvent.change(screen.getByLabelText('AI usage time range'), { target: { value: '7d' } });
    await waitFor(() => expect(paths().filter((path) => path.includes('/metrics?')).length).toBeGreaterThan(2));
    expect(paths().filter((path) => path.includes('/metrics?')).at(-1)).not.toBe(before);
    expect(paths().at(-1)).not.toContain('/api/overview');
  });
});
