// Resolves the API host at runtime and synchronizes short-lived auth state between open tabs.
/** Runtime deployment settings that may be injected by the web host before the app bundle runs. */
type RuntimeConfig = {
  API_URL?: string;
};

declare global {
  /** Browser field populated by the host's runtime-config script. */
  interface Window {
    __AGENTWEAVER_CONFIG__?: RuntimeConfig;
  }
}

const runtimeConfig = typeof window !== 'undefined' ? window.__AGENTWEAVER_CONFIG__ : undefined;

// API_URL is the API ORIGIN only (no `/api` suffix). The API client (client.ts request())
// and the raw fetch call sites own the single `/api` prefix for XHR endpoints, while the
// Browser-redirect endpoints (`/auth/entra/*`) live at the origin root.
//
// A runtime-config value of "" is VALID and means "same origin as the served app" (used on
// the deployed gateway where the frontend and API share a host). Because "" is falsy, we
// must check for a defined string rather than rely on `||` truthiness — otherwise an empty
// deployed value would incorrectly fall through to the localhost dev default.
function resolveApiUrl(): string {
  if (runtimeConfig && typeof runtimeConfig.API_URL === 'string') return runtimeConfig.API_URL;
  if (import.meta.env.VITE_API_URL) return import.meta.env.VITE_API_URL;
  return 'http://localhost:5000';
}

/** API origin used by HTTP clients; API routes add their own `/api` prefix. */
export const API_URL = resolveApiUrl();

// External integrations need an absolute URL. When API_URL is the deployed same-origin sentinel
// (""), use the browser origin rather than producing a relative path.
/** Resolves the public API host, using the browser origin for an empty deployment value and removing one trailing slash. */
export function resolvePublicApiOrigin(apiUrl = API_URL): string {
  return (apiUrl || (typeof window !== 'undefined' ? window.location.origin : '')).replace(/\/$/, '');
}

/** Public MCP endpoint derived from the resolved API origin. */
export const MCP_URL = `${resolvePublicApiOrigin()}/mcp`;
/** Root-level Entra browser redirect endpoint; unlike API calls it has no `/api` prefix. */
export const ENTRA_AUTHORIZE_URL = `${API_URL.replace(/\/$/, '')}/auth/entra/authorize`;

/** Session-scoped storage key for the short-lived session token. */
export const SESSION_TOKEN_STORAGE_KEY = 'agentweaver.sessionToken';
/** Session-scoped storage key for the optional GitHub login associated with the token. */
export const SESSION_LOGIN_STORAGE_KEY = 'agentweaver.sessionLogin';
/** Same-tab event emitted when another tab makes valid session credentials available. */
export const SESSION_AUTH_AVAILABLE_EVENT = 'agentweaver:session-auth-available';
/** Same-tab event emitted when session credentials are cleared or rejected. */
export const SESSION_AUTH_INVALID_EVENT = 'agentweaver:session-auth-invalid';

const SESSION_AUTH_CHANNEL_NAME = 'agentweaver.session-auth';
const SESSION_AUTH_REQUEST_TIMEOUT_MS = 300;

/** Non-durable protocol messages used to synchronize session credentials between same-origin tabs. */
type SessionAuthMessage =
  | { type: 'request'; requestId: string; rejectedToken?: string }
  | { type: 'response'; requestId: string; token: string; login: string | null }
  | { type: 'available' }
  | { type: 'clear' };

/** Resolver and timeout retained while a peer tab answers a credential request. */
type PendingSessionAuthRequest = {
  resolve: (restored: boolean) => void;
  timeoutId: number;
};

const pendingSessionAuthRequests = new Map<string, PendingSessionAuthRequest>();
let sessionAuthChannel: BroadcastChannel | null | undefined;

// SECURITY (accepted residual risk, tracked separately): the session token is stored
// only in sessionStorage and is therefore readable by any
// same-origin script. There is no confirmed XSS sink in this app today (LLM/tool
// output is escaped/sanitized — see .security findings-frontend-web.md, Alert 1),
// but this remains a JS-readable secret and would become higher severity the moment
// an XSS vector is introduced elsewhere. Full remediation (migrating to a
// short-lived HttpOnly/Secure/SameSite session cookie, which also requires adding
// CSRF protection since cookies are attached automatically) is a larger auth-flow
// change tracked as a follow-up, not attempted in this pass. In the meantime, the
// CSP `script-src 'self'` (no `unsafe-inline`/`unsafe-eval`) added alongside this
// comment narrows the practical avenues for third-party script injection. Same-origin
// tabs transfer the token transiently through BroadcastChannel only in response to a
// nonce-bearing request; the token is never copied to localStorage, a JS-readable
// cookie, URLs, or durable cross-tab storage.
/** Opens the shared tab channel once and caches `null` when the browser blocks or lacks it. */
function getSessionAuthChannel(): BroadcastChannel | null {
  if (sessionAuthChannel !== undefined) return sessionAuthChannel;
  if (typeof BroadcastChannel === 'undefined') {
    sessionAuthChannel = null;
    return null;
  }

  try {
    sessionAuthChannel = new BroadcastChannel(SESSION_AUTH_CHANNEL_NAME);
    sessionAuthChannel.addEventListener('message', handleSessionAuthMessage);
  } catch {
    sessionAuthChannel = null;
  }
  return sessionAuthChannel;
}

/**
 * Handles same-origin tab messages. A peer only receives a token in reply to a request,
 * and a request naming a rejected token is not answered with that same credential.
 */
function handleSessionAuthMessage(event: MessageEvent<SessionAuthMessage>): void {
  const message = event.data;
  if (!message || typeof message !== 'object') return;

  if (message.type === 'request') {
    const token = getSessionToken();
    if (!token || token === message.rejectedToken) return;
    getSessionAuthChannel()?.postMessage({
      type: 'response',
      requestId: message.requestId,
      token,
      login: getSessionLogin(),
    } satisfies SessionAuthMessage);
    return;
  }

  if (message.type === 'response') {
    const pending = pendingSessionAuthRequests.get(message.requestId);
    if (!pending || !message.token) return;
    pendingSessionAuthRequests.delete(message.requestId);
    window.clearTimeout(pending.timeoutId);
    storeSessionAuth(message.token, message.login);
    pending.resolve(true);
    return;
  }

  if (message.type === 'available') {
    window.dispatchEvent(new Event(SESSION_AUTH_AVAILABLE_EVENT));
    return;
  }

  if (message.type === 'clear') {
    clearLocalSessionAuth();
    window.dispatchEvent(new Event(SESSION_AUTH_INVALID_EVENT));
  }
}

/** Stores credentials in this tab's session storage, replacing the optional login when absent. */
function storeSessionAuth(token: string, login?: string | null): void {
  sessionStorage.setItem(SESSION_TOKEN_STORAGE_KEY, token);
  if (login) sessionStorage.setItem(SESSION_LOGIN_STORAGE_KEY, login);
  else sessionStorage.removeItem(SESSION_LOGIN_STORAGE_KEY);
}

/** Removes this tab's session token and associated login without notifying peer tabs. */
function clearLocalSessionAuth(): void {
  sessionStorage.removeItem(SESSION_TOKEN_STORAGE_KEY);
  sessionStorage.removeItem(SESSION_LOGIN_STORAGE_KEY);
}

getSessionAuthChannel();

/** Reads the current tab's token; storage access failures are treated as no active session. */
export function getSessionToken(): string | null {
  try {
    return sessionStorage.getItem(SESSION_TOKEN_STORAGE_KEY);
  } catch {
    return null;
  }
}

/** Reads the optional current-tab login; storage access failures return `null`. */
export function getSessionLogin(): string | null {
  try {
    return sessionStorage.getItem(SESSION_LOGIN_STORAGE_KEY);
  } catch {
    return null;
  }
}

/** Saves credentials locally and announces availability to other tabs; blocked storage is ignored. */
export function setSessionAuth(token: string, login?: string | null): void {
  try {
    storeSessionAuth(token, login);
    getSessionAuthChannel()?.postMessage({ type: 'available' } satisfies SessionAuthMessage);
  } catch {
    // Session storage can be unavailable in private/embedded contexts.
  }
}

/** Updates the stored login only when both a login and a session token are present. */
export function bindSessionLogin(login: string | null | undefined): void {
  if (!login || !getSessionToken()) return;
  try {
    sessionStorage.setItem(SESSION_LOGIN_STORAGE_KEY, login);
  } catch {
    // Best-effort only; API calls still rely on the httpOnly cookie/session token.
  }
}

/** Clears this tab's credentials and optionally asks peer tabs to clear their own copies. */
export function clearSessionAuth(notifyPeers = false): void {
  try {
    clearLocalSessionAuth();
    if (notifyPeers) {
      getSessionAuthChannel()?.postMessage({ type: 'clear' } satisfies SessionAuthMessage);
    }
  } catch {
    // Nothing to clear.
  }
}

/** Notifies listeners in this tab that the current credentials are no longer valid. */
export function notifySessionAuthInvalid(): void {
  window.dispatchEvent(new Event(SESSION_AUTH_INVALID_EVENT));
}

/**
 * Requests a usable token from another open tab and resolves false if no peer answers promptly.
 * The rejected token is sent as an exclusion so peers do not return the same invalid credential.
 */
export function requestSessionAuthFromPeer(rejectedToken?: string): Promise<boolean> {
  const channel = getSessionAuthChannel();
  if (!channel) return Promise.resolve(false);

  const requestId = crypto.randomUUID();
  return new Promise<boolean>((resolve) => {
    const timeoutId = window.setTimeout(() => {
      pendingSessionAuthRequests.delete(requestId);
      resolve(false);
    }, SESSION_AUTH_REQUEST_TIMEOUT_MS);
    pendingSessionAuthRequests.set(requestId, { resolve, timeoutId });
    channel.postMessage({ type: 'request', requestId, rejectedToken } satisfies SessionAuthMessage);
  });
}

/**
 * Exchanges a one-time callback code for a session token, then strips auth material from browser history.
 * Rejected exchanges and network failures leave the tab unauthenticated without throwing.
 */
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
    const response = await fetch(`${API_URL.replace(/\/$/, '')}/api/auth/session/exchange`, {
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
