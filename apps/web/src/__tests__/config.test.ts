import { AgentweaverApiClient, ApiError } from '../api/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
// The `/api` base-path convention: API_URL is the ORIGIN ONLY (no `/api` suffix). The API
// client owns the single `/api` prefix for XHR endpoints, while the Entra redirect
// endpoint lives at the origin root (`/auth/entra/authorize`). These tests lock in that
// convention for both localhost dev (absolute origin) and the deployed gateway (same origin,
// empty API_URL) so a regression can never reintroduce the sign-in "unauthorized" bug.

afterEach(() => {
  FakeBroadcastChannel.reset();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.resetModules();
  delete (window as unknown as { __AGENTWEAVER_CONFIG__?: unknown }).__AGENTWEAVER_CONFIG__;
});

class FakeBroadcastChannel {
  static instances: FakeBroadcastChannel[] = [];

  readonly name: string;
  private listeners: Array<(event: MessageEvent) => void> = [];

  constructor(name: string) {
    this.name = name;
    FakeBroadcastChannel.instances.push(this);
  }

  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) {
    this.listeners.push(listener);
  }

  postMessage(message: unknown) {
    for (const instance of FakeBroadcastChannel.instances) {
      if (instance !== this && instance.name === this.name) {
        instance.dispatch(message);
      }
    }
  }

  dispatch(message: unknown) {
    for (const listener of this.listeners) {
      listener(new MessageEvent('message', { data: message }));
    }
  }

  close() {}

  static reset() {
    FakeBroadcastChannel.instances = [];
  }
}

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

  describe('cross-tab session auth', () => {
    it('restores a requested token into sessionStorage without using localStorage', async () => {
      vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
      const localStorageSet = vi.spyOn(window.localStorage, 'setItem');
      const cfg = await loadConfigWith('');
      const peer = new FakeBroadcastChannel('agentweaver.session-auth');
      peer.addEventListener('message', (event) => {
        const request = event.data as { type: string; requestId: string };
        if (request.type === 'request') {
          peer.postMessage({
            type: 'response',
            requestId: request.requestId,
            token: 'peer-token',
            login: 'peer-login',
          });
        }
      });

      await expect(cfg.requestSessionAuthFromPeer()).resolves.toBe(true);

      expect(sessionStorage.getItem(cfg.SESSION_TOKEN_STORAGE_KEY)).toBe('peer-token');
      expect(sessionStorage.getItem(cfg.SESSION_LOGIN_STORAGE_KEY)).toBe('peer-login');
      expect(localStorageSet).not.toHaveBeenCalledWith(cfg.SESSION_TOKEN_STORAGE_KEY, expect.anything());
    });

    it('does not return the token that a requester already had rejected', async () => {
      vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
      const cfg = await loadConfigWith('');
      cfg.setSessionAuth('stale-token', 'member');
      const peer = new FakeBroadcastChannel('agentweaver.session-auth');
      const received = vi.fn();
      peer.addEventListener('message', received);

      peer.postMessage({ type: 'request', requestId: 'request-1', rejectedToken: 'stale-token' });
      await Promise.resolve();

      expect(received).not.toHaveBeenCalled();
    });

    it('clears this tab when sign-out is broadcast by another tab', async () => {
      vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
      const cfg = await loadConfigWith('');
      cfg.setSessionAuth('session-token', 'member');
      const invalid = vi.fn();
      window.addEventListener(cfg.SESSION_AUTH_INVALID_EVENT, invalid);
      const peer = new FakeBroadcastChannel('agentweaver.session-auth');

      peer.postMessage({ type: 'clear' });

      expect(cfg.getSessionToken()).toBeNull();
      expect(cfg.getSessionLogin()).toBeNull();
      expect(invalid).toHaveBeenCalledTimes(1);
      window.removeEventListener(cfg.SESSION_AUTH_INVALID_EVENT, invalid);
    });

    it('notifies a signed-out tab when another tab finishes authentication', async () => {
      vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
      const cfg = await loadConfigWith('');
      const available = vi.fn();
      window.addEventListener(cfg.SESSION_AUTH_AVAILABLE_EVENT, available);
      const peer = new FakeBroadcastChannel('agentweaver.session-auth');

      peer.postMessage({ type: 'available' });

      expect(available).toHaveBeenCalledTimes(1);
      window.removeEventListener(cfg.SESSION_AUTH_AVAILABLE_EVENT, available);
    });
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
