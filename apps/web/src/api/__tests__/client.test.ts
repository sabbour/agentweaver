import { AgentweaverApiClient, ApiError } from '../client';
import type { SkillProvenance } from '../types';
import { afterEach, describe, expect, it, vi } from 'vitest';

function serverSkill(provenance: string) {
  return {
    id: 'skill-1',
    name: 'system-design',
    description: 'Design distributed systems.',
    provenance,
    source_repository: null,
    source_location: null,
    status: 'active',
    content_hash: 'abc123',
    resource_count: 0,
    assigned_agents: [],
    created_at: '2026-07-16T00:00:00Z',
    updated_at: '2026-07-16T00:00:00Z',
  };
}

function serverSkillDetail(provenance: string) {
  return {
    ...serverSkill(provenance),
    instructions: 'Document architecture decisions.',
    resources: [],
  };
}

describe('AgentweaverApiClient skill catalog contract', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('deserializes the server built-in provenance as the typed catalog value', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => JSON.stringify([serverSkill('built-in')]),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    const skills = await client.listSkills('project-1');
    const provenance: SkillProvenance = skills[0]!.provenance;

    expect(provenance).toBe('built-in');
  });

  it('rejects list and detail responses with unknown provenance values', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        text: async () => JSON.stringify([serverSkill('external-import')]),
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        text: async () => JSON.stringify(serverSkillDetail('external-import')),
      });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    await expect(client.listSkills('project-1')).rejects.toThrow('Invalid skill list response.');
    await expect(client.getSkill('project-1', 'skill-1')).rejects.toThrow('Invalid skill detail response.');
  });
});

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

  it('preserves structured non-2xx response bodies for typed feature error handling', () => {
    const payload = {
      blueprint_id: 'blueprint-software-development',
      blueprint_version: 'version-1',
      digest: 'preview-1',
      can_apply: false,
      errors: ['A confirmed team is required.'],
      assignments: [],
    };

    const error = new ApiError(422, JSON.stringify(payload));

    expect(error.payload).toEqual(payload);
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

    it('uses the backend skill-default routes and sends blueprint_id with the exact preview digest', async () => {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          text: async () => JSON.stringify({
            blueprint_id: 'blueprint-software-development',
            blueprint_version: 'version-1',
            digest: 'preview-1',
            can_apply: true,
            errors: [],
            assignments: [],
          }),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          text: async () => JSON.stringify({ outcome: 'applied', errors: [], preview: null }),
        });
      vi.stubGlobal('fetch', fetchMock);
      const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

      await client.previewBlueprintSkillDefaults('project/1', 'blueprint-software-development');
      await client.applyBlueprintSkillDefaults('project/1', 'blueprint-software-development', 'preview-1');

      expect(fetchMock.mock.calls[0][0]).toBe('https://api.example.test/api/projects/project%2F1/skill-defaults/preview');
      expect(fetchMock.mock.calls[0][1].method).toBe('POST');
      expect(fetchMock.mock.calls[0][1].body).toBe('{"blueprint_id":"blueprint-software-development"}');
      expect(fetchMock.mock.calls[1][0]).toBe('https://api.example.test/api/projects/project%2F1/skill-defaults/apply');
      expect(fetchMock.mock.calls[1][1].body).toBe('{"blueprint_id":"blueprint-software-development","digest":"preview-1"}');
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
