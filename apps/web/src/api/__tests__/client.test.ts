import { AgentweaverApiClient, ApiError } from '../client';
import { afterEach, describe, expect, it, vi } from 'vitest';
describe('AgentweaverApiClient keepalive', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('resolves relative API keepalive URLs through the configured API base', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => '',
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');
    await client.pingKeepalive('/api/runs/run-1/sandbox/preview/token/keepalive');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('https://api.example.test/api/runs/run-1/sandbox/preview/token/keepalive');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('include');
  });

  it('throws ApiError for non-2xx keepalive responses', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 404,
      text: async () => '{"error":"missing"}',
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    const promise = client.pingKeepalive('/api/runs/run-1/sandbox/preview/token/keepalive');
    await expect(promise).rejects.toBeInstanceOf(ApiError);
    await expect(promise).rejects.toMatchObject({ status: 404, body: '{"error":"missing"}' });
  });
});

// #208 point 5 regression coverage: an AbortSignal passed to a metrics-fetching client method must
// reach the underlying `fetch` call so callers (DashboardPage/OverviewPage) can actually cancel
// in-flight requests on unmount/range-change/overlapping-poll instead of the option being a no-op.
describe('AgentweaverApiClient AbortSignal plumbing', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('AgentweaverApiClient blueprint skill defaults', () => {
    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it('uses the project-scoped preview and applies the exact preview digest', async () => {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          text: async () => JSON.stringify({ digest: 'preview-1' }),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          text: async () => JSON.stringify({ digest: 'preview-1', applied: true, actions: [], blockers: [] }),
        });
      vi.stubGlobal('fetch', fetchMock);
      const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

      await client.previewBlueprintSkillDefaults('project/1');
      await client.applyBlueprintSkillDefaults('project/1', 'preview-1');

      expect(fetchMock.mock.calls[0][0]).toBe('https://api.example.test/api/projects/project%2F1/skills/defaults/preview');
      expect(fetchMock.mock.calls[0][1].method).toBe('POST');
      expect(fetchMock.mock.calls[0][1].body).toBe('{}');
      expect(fetchMock.mock.calls[1][0]).toBe('https://api.example.test/api/projects/project%2F1/skills/defaults/apply');
      expect(fetchMock.mock.calls[1][1].body).toBe('{"digest":"preview-1"}');
    });
  });

  it('forwards the signal from getProjectMetrics to fetch', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => '{}',
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');
    const controller = new AbortController();
    await client.getProjectMetrics('project-1', undefined, undefined, controller.signal);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.signal).toBe(controller.signal);
  });

  it('forwards the signal from getProjectDashboard to fetch and includes includeMetrics=false when requested', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => '{}',
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');
    const controller = new AbortController();
    await client.getProjectDashboard('project-1', { includeMetrics: false, signal: controller.signal });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('includeMetrics=false');
    expect(init.signal).toBe(controller.signal);
  });
});
