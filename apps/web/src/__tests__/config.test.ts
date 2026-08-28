import { AgentweaverApiClient, ApiError } from '../api/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
// The `/api` base-path convention: API_URL is the ORIGIN ONLY (no `/api` suffix). The API
// client owns the single `/api` prefix for XHR endpoints, while the Entra redirect
// endpoint lives at the origin root (`/auth/entra/authorize`). These tests lock in that
// convention for both localhost dev (absolute origin) and the deployed gateway (same origin,
// empty API_URL) so a regression can never reintroduce the sign-in "unauthorized" bug.

afterEach(() => {
  vi.restoreAllMocks();
  vi.resetModules();
  delete (window as unknown as { __AGENTWEAVER_CONFIG__?: unknown }).__AGENTWEAVER_CONFIG__;
});

async function loadConfigWith(apiUrl: string | undefined) {
  vi.resetModules();
  if (apiUrl === undefined) {
    delete (window as unknown as { __AGENTWEAVER_CONFIG__?: unknown }).__AGENTWEAVER_CONFIG__;
  } else {
    (window as unknown as { __AGENTWEAVER_CONFIG__?: { API_URL?: string } }).__AGENTWEAVER_CONFIG__ = { API_URL: apiUrl };
  }
  return import('../config');
}

describe('config ENTRA_AUTHORIZE_URL (origin root, never /api)', () => {
  it('resolves to <origin>/auth/entra/authorize for an absolute origin (localhost dev)', async () => {
    const cfg = await loadConfigWith('http://localhost:5000');
    expect(cfg.API_URL).toBe('http://localhost:5000');
    expect(cfg.ENTRA_AUTHORIZE_URL).toBe('http://localhost:5000/auth/entra/authorize');
  });

  it('resolves to same-origin /auth/entra/authorize when API_URL is "" (deployed gateway)', async () => {
    const cfg = await loadConfigWith('');
    // Empty string is a VALID value meaning "same origin" — it must NOT fall through to a default.
    expect(cfg.API_URL).toBe('');
    expect(cfg.ENTRA_AUTHORIZE_URL).toBe('/auth/entra/authorize');
  });

  it('treats an empty runtime API_URL as same-origin, not as unset (no localhost fallback)', async () => {
    const cfg = await loadConfigWith('');
    expect(cfg.API_URL).not.toBe('http://localhost:5000');
  });
});

describe('ApiClient request() single /api prefix', () => {
  function spyFetch() {
    return vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}', { status: 200 }));
  }

  it('prepends exactly one /api for an absolute origin baseUrl (localhost dev)', async () => {
    const fetchSpy = spyFetch();
    const client = new AgentweaverApiClient('http://localhost:5000', () => null);
    await client.getRun('abc');
    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5000/api/runs/abc',
      expect.anything(),
    );
  });

  it('yields a single same-origin /api prefix when baseUrl is "" (deployed gateway)', async () => {
    const fetchSpy = spyFetch();
    const client = new AgentweaverApiClient('', () => null);
    await client.getRun('abc');
    expect(fetchSpy).toHaveBeenCalledWith('/api/runs/abc', expect.anything());
    // Must never double-prefix to /api/api/... under the same-origin convention.
    const calledUrl = String(fetchSpy.mock.calls[0][0]);
    expect(calledUrl).not.toContain('/api/api/');
  });

  it('posts relative keepalive URLs to the configured API origin', async () => {
    const fetchSpy = spyFetch();
    const client = new AgentweaverApiClient('http://localhost:5000', () => null);
    await client.pingKeepalive('/api/runs/r1/sandbox/keepalive');
    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5000/api/runs/r1/sandbox/keepalive',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('throws ApiError when keepalive returns a non-OK response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('expired', { status: 410 }));
    const client = new AgentweaverApiClient('http://localhost:5000', () => null);
    await expect(client.pingKeepalive('/api/keepalive')).rejects.toBeInstanceOf(ApiError);
  });
});
