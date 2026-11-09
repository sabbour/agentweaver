import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { DashboardPage } from './DashboardPage';
import { gatewayFixtureFetch } from '../__tests__/retainedGatewayTest';

const mocks = vi.hoisted(() => ({ fetch: vi.fn(), apiCall: vi.fn() }));
vi.mock('./AuthContext', () => ({ useAuth: () => ({ apiCall: mocks.apiCall }) }));
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
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
    await screen.findByRole('heading', { name: 'Recent runs' });
    const summary = screen.getByLabelText('Project summary');
    expect(screen.getByRole('link', { name: 'Projects' }).getAttribute('href')).toBe('/projects');
    expect(within(summary).getByText('Active runs')).toBeDefined();
    expect(within(summary).getByText('Runs, last 7 days')).toBeDefined();
    expect(within(summary).getByText('Completed tasks')).toBeDefined();
    expect(within(summary).getByText('Run success')).toBeDefined();
    expect(screen.getByText('Model and agent metrics')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.getByText('gpt-5')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Setup checks' })).toBeDefined();
    expect(screen.getByRole('link', { name: 'Choose a model' }).getAttribute('href')).toBe('/projects/p1/settings?section=models');
    expect(screen.getAllByText('Ada Lovelace').length).toBeGreaterThan(0);
    expect(screen.getByRole('link', { name: 'Open Team' }).getAttribute('href')).toBe('/projects/p1/team');
    expect(screen.getByRole('table', { name: 'Agent usage' })).toBeDefined();
    expect(screen.getByRole('link', { name: 'Open run Run failed' }).getAttribute('href')).toBe('/projects/p1/runs/r1');
    expect(screen.getByText('Agent scores and response times are not available yet.')).toBeDefined();
    expect(paths().map((path) => new URL(path, 'http://localhost').pathname).sort()).toEqual([
      '/api/v1/projects/p1', '/api/v1/projects/p1/configuration', '/api/v1/projects/p1/runs',
      '/api/v1/projects/p1/metrics',
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
    expect(new URL(paths().filter((path) => path.includes('/metrics?')).at(-1)!, 'http://localhost').searchParams.get('from')).toBe(initial.sort().at(-1));
    expect(paths().filter((path) => path.includes('/usage?')).length).toBe(2);
  });

  it('keeps selected-range metrics and usage when the independent weekly count fails', async () => {
    mocks.fetch.mockImplementation(async (input) => {
      const url = new URL(String(input), 'http://localhost');
      const duration = Date.parse(url.searchParams.get('to') ?? '') - Date.parse(url.searchParams.get('from') ?? '');
      if (url.pathname.endsWith('/metrics') && duration < 8 * 24 * 60 * 60 * 1000) {
        return new Response(JSON.stringify({ detail: 'Weekly metrics unavailable' }), {
          status: 503,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return gatewayFixtureFetch(input);
    });
    renderPage();
    await screen.findByText('gpt-5');
    expect(screen.getByRole('table', { name: 'Agent usage' })).toBeDefined();
    expect(screen.getByText(/Weekly run count unavailable:/)).toBeDefined();
    expect(screen.getByRole('img', { name: 'Created and completed runs over the selected range' })).toBeDefined();
  });

  it('does not show old-range snapshots under a new range after its reads fail', async () => {
    let failSevenDayReads = false;
    mocks.fetch.mockImplementation(async (input) => {
      const url = new URL(String(input), 'http://localhost');
      const duration = Date.parse(url.searchParams.get('to') ?? '') - Date.parse(url.searchParams.get('from') ?? '');
      if (failSevenDayReads && duration < 8 * 24 * 60 * 60 * 1000 &&
          (url.pathname.endsWith('/metrics') || url.pathname.endsWith('/usage'))) {
        return new Response(JSON.stringify({ detail: 'Selected range unavailable' }), {
          status: 503,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return gatewayFixtureFetch(input);
    });
    renderPage();
    await screen.findByText('gpt-5');
    failSevenDayReads = true;
    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '7d' } });

    await screen.findByText('Dashboard data did not load');
    expect(screen.queryByText('gpt-5')).toBeNull();
    expect(screen.queryByRole('img', { name: 'Created and completed runs over the selected range' })).toBeNull();
    expect(screen.getByText('No run data for this range. Start or complete a run to add data.')).toBeDefined();
  });

  it('guides archived projects to settings instead of offering run actions', async () => {
    mocks.fetch.mockImplementation(async (input) => {
      const response = await gatewayFixtureFetch(input);
      const url = new URL(String(input), 'http://localhost');
      if (url.pathname !== '/api/v1/projects/p1') return response;
      const project = await response.json() as { state: string };
      project.state = 'archived';
      return new Response(JSON.stringify(project), { headers: { 'Content-Type': 'application/json' } });
    });
    renderPage();
    await screen.findByText('This project is archived');
    const settingsLinks = screen.getAllByRole('link', { name: 'Open Project settings' });
    expect(settingsLinks.length).toBeGreaterThan(0);
    expect(settingsLinks.every((link) => link.getAttribute('href') === '/projects/p1/settings?section=danger')).toBe(true);
    expect(screen.queryByRole('link', { name: 'Open Board' })).toBeNull();
  });

  it('does not repeat the primary action as a secondary action for a project without runs', async () => {
    mocks.fetch.mockImplementation(async (input) => {
      const url = new URL(String(input), 'http://localhost');
      const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
      if (url.pathname === '/api/v1/projects/p1/runs') return json({ projectId: 'p1', items: [], nextCursor: null });
      if (url.pathname === '/api/v1/projects/p1/metrics') {
        const now = new Date().toISOString();
        return json({
          projectId: 'p1', from: now, to: now, activeRuns: 0, blockedRuns: 0,
          runsInRange: 0, runsTotal: 0, completedRuns: 0, succeededRuns: 0, series: [],
        });
      }
      if (url.pathname === '/api/v1/projects/p1/usage') {
        const now = new Date().toISOString();
        return json({ projectId: 'p1', from: now, to: now, usageByModel: [], usageByAgent: [] });
      }
      return gatewayFixtureFetch(input);
    });
    renderPage();
    await screen.findByText('This project has no run activity.');
    expect(screen.getByText('Agent scores are not available yet. Open Flow to review agent runs.')).toBeDefined();
    expect(screen.queryByText(/Gateway/)).toBeNull();
    const actions = screen.getByLabelText('Project actions');
    const labels = within(actions).getAllByRole('link').map((link) => link.textContent);
    expect(labels.length).toBeGreaterThan(1);
    expect(new Set(labels).size).toBe(labels.length);
    const startTask = within(actions).getByRole('link', { name: 'Start task' });
    expect(startTask.getAttribute('href')).toBe('/projects/p1/orchestrations');
  });

  it('uses singular wording when one run is active', async () => {
    mocks.fetch.mockImplementation(async (input) => {
      const response = await gatewayFixtureFetch(input);
      const url = new URL(String(input), 'http://localhost');
      if (!url.pathname.endsWith('/metrics')) return response;
      const metrics = await response.json() as { activeRuns: number };
      return new Response(JSON.stringify({ ...metrics, activeRuns: 1 }), {
        headers: { 'Content-Type': 'application/json' },
      });
    });
    renderPage();

    expect(await screen.findByText('1 run is active. Active agent count is unavailable.')).toBeDefined();
    expect(screen.queryByText(/1 run are active/)).toBeNull();
  });
});
