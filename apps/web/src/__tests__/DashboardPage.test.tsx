import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { DashboardPage } from '../pages/DashboardPage';
import { gatewayFixtureFetch } from './retainedGatewayTest';

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
  return render(<AzureFluentProvider density="compact"><MemoryRouter initialEntries={['/projects/p1']}><Routes><Route path="/projects/:projectId" element={<DashboardPage />} /></Routes></MemoryRouter></AzureFluentProvider>);
}
function paths(): string[] {
  return mocks.fetch.mock.calls.map(([input]) => String(input));
}

describe('retained DashboardPage v1 reads', () => {
  it('preserves the 0.x dashboard panels without calling its old dashboard path', async () => {
    renderPage();
    await screen.findByText('Runs over time');
    const summary = screen.getByLabelText('Project summary');
    expect(within(summary).getByText('Active runs')).toBeDefined();
    expect(within(summary).getByText('Runs, last 7 days')).toBeDefined();
    expect(within(summary).getByText('Completed tasks')).toBeDefined();
    expect(within(summary).getByText('Run success')).toBeDefined();
    expect(screen.getByText('Model and agent metrics')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.getByText('gpt-5')).toBeDefined();
    expect(screen.getByText(/Agent leaderboard and latency percentiles are unavailable/)).toBeDefined();
    expect(paths().map((path) => new URL(path, 'http://localhost').pathname).sort()).toEqual([
      '/api/v1/projects/p1', '/api/v1/projects/p1/metrics',
      '/api/v1/projects/p1/metrics', '/api/v1/projects/p1/usage',
    ].sort());
    expect(paths().every((path) => !path.includes('/dashboard'))).toBe(true);
    expect(paths().filter((path) => path.includes('/metrics?')).every((path) => new URL(path, 'http://localhost').searchParams.get('bucket') === 'day')).toBe(true);
  });

  it('refreshes selected-range metrics and usage without losing weekly totals', async () => {
    renderPage();
    await screen.findByText('Runs over time');
    const initial = paths().filter((path) => path.includes('/metrics?')).map((path) => new URL(path, 'http://localhost').searchParams.get('from'));
    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '7d' } });
    await waitFor(() => expect(paths().filter((path) => path.includes('/metrics?')).length).toBe(3));
    expect(new URL(paths().filter((path) => path.includes('/metrics?')).at(-1)!, 'http://localhost').searchParams.get('from')).toBe(initial[1]);
    expect(paths().filter((path) => path.includes('/usage?')).length).toBe(2);
  });
});
