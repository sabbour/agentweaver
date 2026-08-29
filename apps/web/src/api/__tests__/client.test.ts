import { AgentweaverApiClient, ApiError } from '../client';
import {
  GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT,
  GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE,
} from '../githubConnectionRequirement';
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

  it('reads the redacted project Copilot connection state from its scoped endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => JSON.stringify({ status: 'connected', github_login: 'octocat' }),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    const connection = await client.getProjectCopilotConnection('project/1');

    expect(connection).toEqual({ status: 'connected', github_login: 'octocat' });
    expect(fetchMock.mock.calls[0][0])
      .toBe('https://api.example.test/api/projects/project%2F1/github/copilot/connection');
    expect(fetchMock.mock.calls[0][1].method).toBe('GET');
  });

  it('broadcasts the shared Copilot connection action for every typed requirement response', async () => {
    const requirement = {
      code: 'github_copilot_connection_required',
      message: GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE,
      action: { type: 'connect_project_copilot_app', project_id: 'project-1' },
    };
    const received = vi.fn();
    window.addEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      text: async () => JSON.stringify(requirement),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    await expect(client.getProject('project-1')).rejects.toMatchObject({ status: 401, payload: requirement });

    expect(received).toHaveBeenCalledTimes(1);
    expect((received.mock.calls[0]![0] as CustomEvent).detail).toEqual(requirement);
    window.removeEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
  });

  it('broadcasts the shared Copilot connection action when an in-place retry is fenced', async () => {
    const requirement = {
      code: 'github_copilot_connection_required',
      message: GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE,
      action: { type: 'connect_project_copilot_app', project_id: 'project-1' },
    };
    const received = vi.fn();
    window.addEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      text: async () => JSON.stringify(requirement),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    await expect(client.retryRun('run/1')).rejects.toMatchObject({ status: 409, payload: requirement });

    expect(fetchMock.mock.calls[0][0])
      .toBe('https://api.example.test/api/runs/run%2F1/retry');
    expect((received.mock.calls[0]![0] as CustomEvent).detail).toEqual(requirement);
    window.removeEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
  });

  it('does not broadcast a connection action for an untyped 401 response', async () => {
    const received = vi.fn();
    window.addEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      text: async () => JSON.stringify({ error: 'Unauthorized.' }),
    }));
    const client = new AgentweaverApiClient('https://api.example.test', 'session-token');

    await expect(client.getProject('project-1')).rejects.toMatchObject({ status: 401 });

    expect(received).not.toHaveBeenCalled();
    window.removeEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, received);
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
