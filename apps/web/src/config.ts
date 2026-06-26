type RuntimeConfig = {
  API_URL?: string;
};

declare global {
  interface Window {
    __AGENTWEAVER_CONFIG__?: RuntimeConfig;
  }
}

const runtimeConfig = typeof window !== 'undefined' ? window.__AGENTWEAVER_CONFIG__ : undefined;

export const API_URL = runtimeConfig?.API_URL || import.meta.env.VITE_API_URL || 'http://localhost:5000';

export const SESSION_TOKEN_STORAGE_KEY = 'agentweaver.sessionToken';
export const SESSION_LOGIN_STORAGE_KEY = 'agentweaver.sessionLogin';

export function getSessionToken(): string | null {
  try {
    return sessionStorage.getItem(SESSION_TOKEN_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function getSessionLogin(): string | null {
  try {
    return sessionStorage.getItem(SESSION_LOGIN_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function setSessionAuth(token: string, login?: string | null): void {
  try {
    sessionStorage.setItem(SESSION_TOKEN_STORAGE_KEY, token);
    if (login) sessionStorage.setItem(SESSION_LOGIN_STORAGE_KEY, login);
  } catch {
    // Session storage can be unavailable in private/embedded contexts.
  }
}

export function bindSessionLogin(login: string | null | undefined): void {
  if (!login || !getSessionToken()) return;
  try {
    sessionStorage.setItem(SESSION_LOGIN_STORAGE_KEY, login);
  } catch {
    // Best-effort only; API calls still rely on the httpOnly cookie/session token.
  }
}

export function clearSessionAuth(): void {
  try {
    sessionStorage.removeItem(SESSION_TOKEN_STORAGE_KEY);
    sessionStorage.removeItem(SESSION_LOGIN_STORAGE_KEY);
  } catch {
    // Nothing to clear.
  }
}

export async function captureSessionAuthFromUrl(): Promise<void> {
  const params = new URLSearchParams(window.location.search);
  const auth = params.get('auth');
  const code = params.get('code');

  const stripAuthParams = () => {
    params.delete('code');
    params.delete('auth');
    // Remove legacy raw-token params that must never appear in URLs going forward.
    params.delete('session_token');
    params.delete('sessionToken');
    params.delete('login');
    params.delete('github_login');
    // Normalize double slashes to prevent SecurityError on history.replaceState
    const pathname = window.location.pathname.replace(/\/\/+/g, '/') || '/';
    const next = `${pathname}${params.toString() ? `?${params}` : ''}${window.location.hash}`;
    window.history.replaceState({}, document.title, next);
  };

  if (auth !== 'success' || !code) {
    // Nothing to exchange; still strip any stale auth params present.
    if (params.has('code') || params.has('auth') || params.has('session_token') || params.has('sessionToken')) {
      stripAuthParams();
    }
    return;
  }

  try {
    const response = await fetch(`${API_URL}/auth/session/exchange`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ code }),
    });
    if (response.ok) {
      const data = await response.json() as { session_token: string; login: string };
      setSessionAuth(data.session_token, data.login);
    }
    // On failure (e.g. 400 invalid_code) leave unauthenticated — do not throw.
  } catch {
    // Network errors — leave unauthenticated silently.
  } finally {
    stripAuthParams();
  }
}
