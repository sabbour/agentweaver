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

  describe('AgentweaverApiClient GitHub webhook provisioning contract', () => {
    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it('posts to the project webhook provisioning endpoint', async () => {
      const response = {
        hook_id: 42,
        created: true,
        repository: 'octocat/demo',
        payload_url: 'https://api.example.test/api/projects/project%2F1/webhooks/github',
      };
      const fetchMock = vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        text: async () => JSON.stringify(response),
      });
      vi.stubGlobal('fetch', fetchMock);
      const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

      await expect(client.autoCreateProjectWebhook('project/1')).resolves.toEqual(response);
      expect(fetchMock).toHaveBeenCalledWith(
        'https://api.example.test/api/projects/project%2F1/webhooks/github/provision',
        expect.objectContaining({
          method: 'POST',
          body: '{}',
        }),
      );
    });
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

describe('AgentweaverApiClient project GitHub identity', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('uses the project GitHub identity endpoint when switching linked accounts', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 204,
      text: async () => '',
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    await client.setProjectGitHubIdentityOverride('project/1', 'altcat');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('https://api.example.test/api/projects/project%2F1/github-identity');
    expect(init.method).toBe('PUT');
    expect(init.body).toBe('{"github_login":"altcat"}');
  });

  it('uses the project GitHub identity endpoint when refreshing the selected account', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => JSON.stringify({
        project_id: 'project/1',
        project_override_login: 'altcat',
        effective_login: 'altcat',
        resolution_source: 'project_override',
      }),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    const identity = await client.getProjectGitHubIdentity('project/1');

    expect(fetchMock.mock.calls[0][0]).toBe('https://api.example.test/api/projects/project%2F1/github-identity');
    expect(fetchMock.mock.calls[0][1].method).toBe('GET');
    expect(identity.effective_login).toBe('altcat');
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

    it('preserves the backend 422 invalid and 409 stale apply contracts', async () => {
      const invalid = {
        outcome: 'invalid',
        errors: ['A confirmed team is required.'],
        preview: null,
      };
      const stale = {
        outcome: 'stale',
        errors: ['Preview digest is stale.'],
        preview: null,
      };
      const fetchMock = vi.fn()
        .mockResolvedValueOnce({
          ok: false,
          status: 422,
          text: async () => JSON.stringify(invalid),
        })
        .mockResolvedValueOnce({
          ok: false,
          status: 409,
          text: async () => JSON.stringify(stale),
        });
      vi.stubGlobal('fetch', fetchMock);
      const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

      await expect(client.applyBlueprintSkillDefaults('project-1', 'blueprint-1', 'digest-1'))
        .rejects.toMatchObject({ status: 422, payload: invalid });
      await expect(client.applyBlueprintSkillDefaults('project-1', 'blueprint-1', 'digest-2'))
        .rejects.toMatchObject({ status: 409, payload: stale });
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
