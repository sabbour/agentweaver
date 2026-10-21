import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';
import {
  buildAuthorizeUrl,
  clearAuthorizationTransaction,
  defaultAuthorizationConfig,
  displayNameFromIdToken,
  exchangeAuthorizationCode,
  isConsentPrompt,
  parseAuthorizationCallbackMessage,
  parseAuthorizationCallbackParameters,
  parseAuthorizationResult,
  pkceChallenge,
  randomState,
  randomVerifier,
  readAuthorizationTransaction,
  refreshBrokerToken,
  storeAuthorizationTransaction,
  submitBrokerConsent,
} from './authProtocol';
import type { AuthorizationTransaction, RunBinding } from './authProtocol';
import { missingAuthConfiguration } from './config';
import type {
  BrokerConsentPrompt,
  BrokerTokenResponse,
  ProjectAuthorizationContextResponse,
} from './contracts';
import { gatewayClient, GatewayError } from './api';
import { clearRecentRuns } from './recentRuns';

/** In-memory bearer session bound to the project/run that initiated authorization. */
interface AuthSession {
  accessToken: string;
  refreshToken?: string;
  expiresAt: number;
  binding: RunBinding | null;
  /** Display name from the ID token, or null when the Broker sent none. Shown in the persona menu. */
  displayName: string | null;
}

/** Tenant authorization values confirmed by the Projects owner. */
type ConfirmedAuthorizationContext = Pick<
  ProjectAuthorizationContextResponse,
  'tenantId' | 'membershipRevision'
>;

/** Authentication state and operations shared with v1 pages and Gateway callers. */
interface AuthContextValue {
  session: AuthSession | null;
  authorizationContext: ConfirmedAuthorizationContext | null;
  authorizationContextLoading: boolean;
  authorizationContextError: unknown;
  busy: boolean;
  restoringSession: boolean;
  consent: BrokerConsentPrompt | null;
  error: string | null;
  configurationError: string | null;
  authorize: (binding?: RunBinding | null) => Promise<void>;
  /**
   * Makes a session for `binding` available without a prompt. It reuses a held session or
   * asks the Broker once with `prompt=none`. Returns false when the Broker needs the user;
   * the current session is kept in that case. `activate` makes it the page's active session.
   */
  ensureSession: (binding: RunBinding | null, activate?: boolean) => Promise<boolean>;
  resolveAuthorizationContext: (tenantSelector?: string) => Promise<ConfirmedAuthorizationContext | null>;
  decideConsent: (approve: boolean) => Promise<void>;
  signOut: () => void;
  apiCall: <T>(
    operation: (accessToken: string, tenantSelector: string | null) => Promise<T>,
    binding?: RunBinding | null,
  ) => Promise<T>;
}

const AuthContext = createContext<AuthContextValue | null>(null);
// How long an interactive (popup) sign-in may take before we give up.
const AUTH_TIMEOUT_MS = 10 * 60 * 1000;
// A silent sign-in needs no user action, so it should finish quickly or not at all.
const SILENT_AUTH_TIMEOUT_MS = 15 * 1000;
// Session-storage flag that says "this tab signed in before". It holds no token. After a
// reload or a full-page redirect it tells the provider to try one silent sign-in.
const SIGNED_IN_BEFORE_STORAGE_KEY = 'agentweaver.auth.signed-in-before';

/** True when this tab signed in earlier, so a silent restore after reload is worth trying. */
function hasSignedInBefore(): boolean {
  try {
    return sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY) === '1';
  } catch {
    // Storage can be blocked. Treat that as "never signed in" so no silent attempt starts.
    return false;
  }
}

/** Sets the "signed in before" flag after a successful sign-in. */
function rememberSignIn(): void {
  try {
    sessionStorage.setItem(SIGNED_IN_BEFORE_STORAGE_KEY, '1');
  } catch {
    // This optional marker must not prevent an in-memory sign-in.
  }
}

/** Clears the flag on sign-out or when a silent restore fails, so we do not retry forever. */
function forgetSignIn(): void {
  try {
    sessionStorage.removeItem(SIGNED_IN_BEFORE_STORAGE_KEY);
  } catch {
    // If session storage is unavailable, reads also fail closed and cannot start silent sign-in.
  }
}

/**
 * True when this page load is the Broker redirect back to us. In that case the callback
 * handler finishes sign-in, so the silent restore must not also start.
 */
function hasIdentityCallback(): boolean {
  return window.location.pathname === '/auth/callback' ||
    typeof window.__AGENTWEAVER_IDENTITY_CALLBACK__ === 'string';
}

/**
 * Reads the project/run binding from the current URL (a run or orchestration page).
 * A silent restore on such a page asks for a token bound to that run, because the
 * run page cannot use an unbound token. Other pages get an unbound token.
 */
function bindingFromCurrentRoute(): RunBinding | null {
  const match = /^\/projects\/([^/]+)\/(?:runs|orchestrations)\/([^/]+)(?:\/|$)/
    .exec(window.location.pathname);
  if (!match) return null;
  try {
    return {
      projectId: decodeURIComponent(match[1]),
      runId: decodeURIComponent(match[2]),
    };
  } catch {
    // A malformed percent-escape cannot name a real run; fall back to an unbound token.
    return null;
  }
}

/** Map key for the session held for one binding; the unbound session uses the empty key. */
function bindingKey(binding: RunBinding | null): string {
  return binding ? JSON.stringify([binding.projectId, binding.runId]) : '';
}

/**
 * How a sign-in result is stored:
 * - `replace`: drop every held session and keep only the new one (a fresh sign-in).
 * - `activate`: add the new session next to the held ones and make it the active one.
 * - `store`: add the new session but keep the current active session.
 */
type SessionMode = 'replace' | 'activate' | 'store';

/** True when two optional authorization requests target the same project and run. */
function sameBinding(left: RunBinding | null, right: RunBinding | null): boolean {
  return left?.projectId === right?.projectId && left?.runId === right?.runId;
}

/** Ensures owner-returned context is bound to exactly the requested run scope. */
function contextMatchesBinding(
  context: ProjectAuthorizationContextResponse,
  binding: RunBinding | null,
): boolean {
  return binding
    ? context.boundProjectId === binding.projectId && context.boundRunId === binding.runId
    : context.boundProjectId === null && context.boundRunId === null;
}

/** Starts a new PKCE transaction with fresh state and verifier for the requested scope. */
function transactionFor(binding: RunBinding | null): AuthorizationTransaction {
  return {
    state: randomState(),
    verifier: randomVerifier(),
    redirectUri: defaultAuthorizationConfig().redirectUri,
    binding,
  };
}

/** Converts a Broker token response into the session state held only in memory. */
function sessionFromToken(token: BrokerTokenResponse, binding: RunBinding | null): AuthSession {
  return {
    accessToken: token.access_token,
    refreshToken: token.refresh_token,
    expiresAt: Date.now() + token.expires_in * 1000,
    binding,
    displayName: displayNameFromIdToken(token.id_token),
  };
}

/**
 * Owns the v1 Broker sessions, PKCE sign-in, one-time silent restoration, consent,
 * refresh, and tenant context.
 * Access tokens stay in memory. The provider holds at most one session per binding
 * (the unbound session plus run-bound sessions); `session` is the active one. Each
 * Gateway call uses the session held for its exact project/run binding and may retry
 * once after a 401 using token refresh.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSessionState] = useState<AuthSession | null>(null);
  const [authorizationContext, setAuthorizationContextState] =
    useState<ConfirmedAuthorizationContext | null>(null);
  const [authorizationContextLoading, setAuthorizationContextLoading] = useState(false);
  const [authorizationContextError, setAuthorizationContextError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  // Start in "restoring" when the tab signed in before and this load is not the Broker
  // callback. Pages wait on this flag instead of showing the sign-in screen too early.
  const [restoringSession, setRestoringSession] = useState(
    () => hasSignedInBefore() && !hasIdentityCallback(),
  );
  const [consent, setConsent] = useState<BrokerConsentPrompt | null>(null);
  // A redirect callback with unreadable parameters is reported right away.
  const [error, setError] = useState<string | null>(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    return typeof callbackSearch === 'string' &&
      !parseAuthorizationCallbackParameters(callbackSearch)
      ? 'The sign-in response is invalid. Start sign-in again.'
      : null;
  });
  // The active session, readable synchronously from callbacks without waiting for a render.
  const sessionRef = useRef<AuthSession | null>(null);
  // Every held session, keyed by binding. The unbound session uses the empty key.
  const sessionsRef = useRef(new Map<string, AuthSession>());
  // One in-flight silent sign-in per binding, so parallel callers share the same attempt.
  const ensureRef = useRef(new Map<string, Promise<boolean>>());
  // Bumped whenever the active session changes. Late results from an older session are ignored.
  const sessionGenerationRef = useRef(0);
  // Bumped on each tenant-context request. Only the newest request may update state.
  const authorizationContextRequestRef = useRef(0);
  // The confirmed tenant, plus the exact token, binding and generation it was confirmed for.
  const authorizationContextRef = useRef<{
    value: ConfirmedAuthorizationContext;
    accessToken: string;
    binding: RunBinding | null;
    sessionGeneration: number;
  } | null>(null);
  // One in-flight token refresh per binding, tagged with the session it refreshes.
  const refreshPromisesRef = useRef(new Map<string, {
    session: AuthSession;
    promise: Promise<AuthSession | null>;
  }>());
  // The PKCE transaction in progress. A newer sign-in replaces it, which cancels the older loop.
  const transactionRef = useRef<AuthorizationTransaction | null>(null);
  const pollAbortRef = useRef<AbortController | null>(null);
  // Hidden iframe used by silent sign-in, so sign-out can remove it.
  const authorizationFrameRef = useRef<HTMLIFrameElement | null>(null);
  // The reload restore runs at most once per page load.
  const silentAttemptedRef = useRef(false);

  const configurationError = useMemo(() => {
    const missing = missingAuthConfiguration();
    return missing.length
      ? `Configure ${missing.join(', ')} before signing in.`
      : null;
  }, []);

  /** Makes `next` the active session without changing the held sessions. */
  const setActiveSession = useCallback((next: AuthSession | null) => {
    const current = sessionRef.current;
    // A different token or binding means the confirmed tenant no longer applies.
    // Bump the generations so in-flight tenant requests are ignored, and clear the tenant.
    // Activating the same session again keeps the tenant.
    if (current?.accessToken !== next?.accessToken || !sameBinding(current?.binding ?? null, next?.binding ?? null)) {
      sessionGenerationRef.current += 1;
      authorizationContextRequestRef.current += 1;
      authorizationContextRef.current = null;
      setAuthorizationContextState(null);
      setAuthorizationContextLoading(false);
      setAuthorizationContextError(null);
    }
    sessionRef.current = next;
    setSessionState(next);
  }, []);

  /** Replaces every held session with `next` (or none) and activates it. */
  const setSession = useCallback((next: AuthSession | null) => {
    // Forget pending silent attempts too; they belong to the sessions being dropped.
    sessionsRef.current.clear();
    ensureRef.current.clear();
    if (next) sessionsRef.current.set(bindingKey(next.binding), next);
    setActiveSession(next);
  }, [setActiveSession]);

  /** Stores a new session for its binding; `mode` decides whether others are kept and it becomes active. */
  const storeSession = useCallback((stored: AuthSession, mode: SessionMode) => {
    if (mode === 'replace') {
      setSession(stored);
      return;
    }
    // Sessions kept side by side belong to the same signed-in person. A run-bound token
    // without an ID token keeps the name the active session already shows.
    const next = !stored.displayName && sessionRef.current?.displayName
      ? { ...stored, displayName: sessionRef.current.displayName }
      : stored;
    // A new token for the same binding replaces the old one in the map.
    sessionsRef.current.set(bindingKey(next.binding), next);
    // In `store` mode we still activate when the active session is already bound to this
    // same run: the page is using that run, so it must switch to the newer token.
    if (mode === 'activate' || sessionRef.current?.binding && sameBinding(sessionRef.current.binding, next.binding))
      setActiveSession(next);
  }, [setActiveSession, setSession]);

  /** Swaps the authorization code for tokens and stores the new session. */
  const finishCode = useCallback(async (code: string, transaction: AuthorizationTransaction, mode: SessionMode) => {
    const token = await exchangeAuthorizationCode(
      defaultAuthorizationConfig(),
      transaction,
      code,
    );
    // The transaction is single use. Clear it before storing so a replayed callback fails.
    clearAuthorizationTransaction();
    transactionRef.current = null;
    setConsent(null);
    setError(null);
    rememberSignIn();
    // The token is bound to the binding the transaction was started for, not the current URL.
    storeSession(sessionFromToken(token, transaction.binding), mode);
  }, [storeSession]);

  /**
   * Reads a Broker redirect URL for this transaction. Returns false when the URL holds no
   * result, throws when the Broker reported an error, and stores the session otherwise.
   */
  const consumeAuthorizationResponse = useCallback(async (
    responseUrl: string,
    transaction: AuthorizationTransaction,
    mode: SessionMode = 'replace',
  ): Promise<boolean> => {
    // The parser also checks that `state` matches this transaction.
    const result = parseAuthorizationResult(responseUrl, transaction.state);
    if (!result) return false;
    if ('error' in result) {
      throw new Error(result.description ?? `Sign-in failed: ${result.error}.`);
    }
    await finishCode(result.code, transaction, mode);
    return true;
  }, [finishCode]);

  // Full-page redirect sign-in: the Broker sent the browser back with a code or an error.
  // The boot script saved the callback query in a global; finish the sign-in from it here.
  useEffect(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    if (typeof callbackSearch !== 'string') return;
    // Consume the global once so a re-render or remount cannot replay the code.
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    const callback = parseAuthorizationCallbackParameters(callbackSearch);
    // Invalid parameters were already reported by the initial `error` state.
    if (!callback) return;
    // After a full-page redirect the in-memory transaction is gone, so read the stored one.
    let transaction: AuthorizationTransaction | null = transactionRef.current;
    try {
      transaction ??= readAuthorizationTransaction();
    } catch {
      transaction = null;
    }
    if (!transaction) {
      setError('The sign-in response has no matching in-progress PKCE transaction. Start sign-in again.');
      return;
    }
    // Rebuild a callback URL so the same parser and state check as the popup path apply.
    const responseUrl = new URL('/auth/callback', window.location.origin);
    responseUrl.searchParams.set('state', callback.state);
    if ('code' in callback) responseUrl.searchParams.set('code', callback.code);
    else {
      responseUrl.searchParams.set('error', callback.error);
      if (callback.error_description)
        responseUrl.searchParams.set('error_description', callback.error_description);
    }
    setBusy(true);
    // Default `replace` mode: a redirect sign-in starts a fresh set of sessions.
    void consumeAuthorizationResponse(responseUrl.toString(), transaction)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Sign-in failed.'))
      .finally(() => setBusy(false));
  }, [consumeAuthorizationResponse, setBusy]);

  // Stop any pending Broker poll when the provider unmounts.
  useEffect(() => () => pollAbortRef.current?.abort(), []);

  /**
   * Runs one Broker authorization. `silent` uses a hidden frame with `prompt=none`.
   * `rebind` adds a run-bound session next to the held ones; it keeps the current
   * session and the sign-in marker when the Broker needs the user. Returns true when
   * a session was stored.
   */
  const runAuthorization = useCallback(async (
    requestedBinding: RunBinding | null,
    popupOverride: Window | null | undefined,
    silent: boolean,
    rebind: SessionMode | null = null,
  ): Promise<boolean> => {
    // Missing client settings: sign-in cannot work. A background rebind fails quietly;
    // otherwise show the error and stop the reload restore so the page does not wait.
    if (configurationError) {
      if (rebind) return false;
      setError(configurationError);
      if (silent) {
        forgetSignIn();
        setRestoringSession(false);
      }
      return false;
    }
    const config = defaultAuthorizationConfig();
    // Pick how the result is stored. A caller-supplied rebind mode wins. Otherwise a
    // run-bound sign-in while already signed in adds a session next to the unbound one,
    // and any other sign-in replaces all sessions.
    const mode: SessionMode = rebind ?? (requestedBinding && sessionRef.current ? 'activate' : 'replace');
    // A rebind runs in the background, so it must not touch page-level error/consent/busy UI.
    if (!rebind) {
      setError(null);
      setConsent(null);
      setBusy(true);
    }
    // Drop old tokens before a replacing sign-in so no call uses them during the flow.
    if (mode === 'replace') setSession(null);
    // Only one Broker poll may run at a time.
    pollAbortRef.current?.abort();

    let popup: Window | null = null;
    let iframe: HTMLIFrameElement | null = null;
    let callbackWindow: Window | null = null;
    let popupCallbackUrl: string | null = null;
    let popupCallbackError: Error | null = null;
    let popupCallbackReceived = false;
    let popupMessageHandler: ((event: MessageEvent<unknown>) => void) | null = null;
    try {
      // Open the popup before any `await`. Browsers only allow popups inside the click
      // handler. A caller may pass its own popup (or null to force a full-page redirect).
      popup = silent
        ? null
        : popupOverride === undefined
          ? window.open('about:blank', 'agentweaver-identity', 'popup,width=560,height=720')
          : popupOverride;
      // Setting transactionRef cancels any older sign-in loop (it checks this ref each turn).
      // The transaction is also saved to session storage so a full-page redirect can finish it.
      const transaction = transactionFor(requestedBinding);
      transactionRef.current = transaction;
      storeAuthorizationTransaction(transaction);
      const challenge = await pkceChallenge(transaction.verifier);
      // `prompt=none` tells the Broker to answer at once: a code if the user still has a
      // Broker session, or an error if it would need to show a page.
      const authorizeUrl = silent
        ? buildAuthorizeUrl(config, transaction, challenge, 'none')
        : buildAuthorizeUrl(config, transaction, challenge);
      // Three ways to reach the Broker: a hidden iframe (silent), a popup, or, when the
      // popup was blocked, a full-page redirect that is finished by the callback effect.
      if (silent) {
        iframe = document.createElement('iframe');
        iframe.title = 'Silent sign-in';
        iframe.setAttribute('aria-hidden', 'true');
        iframe.tabIndex = -1;
        iframe.style.display = 'none';
        iframe.src = authorizeUrl;
        document.body.append(iframe);
        authorizationFrameRef.current = iframe;
        callbackWindow = iframe.contentWindow;
        if (!callbackWindow) throw new Error('The browser could not start silent sign-in.');
      } else if (popup) {
        popup.location.replace(authorizeUrl);
        callbackWindow = popup;
      }
      else {
        window.location.assign(authorizeUrl);
        return false;
      }
      // The callback page in the popup or iframe posts the result back with postMessage.
      const callbackOrigin = new URL(transaction.redirectUri).origin;
      popupMessageHandler = (event) => {
        // Accept only one message, from our own window, from our callback origin, and only
        // while this transaction is still the current one. Anything else is ignored.
        if (event.source !== callbackWindow ||
            event.origin !== callbackOrigin ||
            transactionRef.current !== transaction ||
            popupCallbackReceived)
          return;

        const message = parseAuthorizationCallbackMessage(event.data);
        if (!message) return;
        popupCallbackReceived = true;
        // Validate the result here (including `state`), but finish it in the wait loop below
        // so token exchange errors go through the same error handling.
        try {
          const responseUrl = new URL(transaction.redirectUri);
          responseUrl.searchParams.set('state', message.state);
          if ('code' in message) responseUrl.searchParams.set('code', message.code);
          else {
            responseUrl.searchParams.set('error', message.error);
            if (message.error_description)
              responseUrl.searchParams.set('error_description', message.error_description);
          }
          parseAuthorizationResult(responseUrl.toString(), transaction.state);
          popupCallbackUrl = responseUrl.toString();
        } catch (reason) {
          popupCallbackError = reason instanceof Error
            ? reason
            : new Error('The sign-in response is invalid.');
        }
        // Wake the wait loop now instead of after the current poll finishes.
        pollAbortRef.current?.abort();
      };
      window.addEventListener('message', popupMessageHandler);
      // Wait loop: check for a posted result, a closed popup, or (popup only) a consent page.
      const startedAt = Date.now();
      while (Date.now() - startedAt < (silent ? SILENT_AUTH_TIMEOUT_MS : AUTH_TIMEOUT_MS)) {
        // A newer sign-in or sign-out replaced this transaction.
        if (transactionRef.current !== transaction)
          throw new Error('Sign-in was cancelled.');
        if (popupCallbackError) throw popupCallbackError;
        if (popupCallbackUrl) {
          if (!await consumeAuthorizationResponse(popupCallbackUrl, transaction, mode))
            throw new Error('The sign-in response is invalid.');
          popup?.close();
          return true;
        }
        if (!silent && popup?.closed)
          throw new Error('The sign-in window closed before sign-in finished.');
        // Silent sign-in only waits for the iframe message; it never polls the Broker.
        if (silent) {
          await new Promise((resolve) => window.setTimeout(resolve, 50));
          continue;
        }
        // Popup sign-in: poll the authorize URL with the Broker cookie. A redirect means the
        // user is still signing in. A JSON body can be a consent prompt or a Broker error.
        const abortController = new AbortController();
        pollAbortRef.current = abortController;
        const timeout = window.setTimeout(() => abortController.abort(), 2500);
        try {
          const response = await fetch(authorizeUrl, {
            credentials: 'include',
            cache: 'no-store',
            redirect: 'manual',
            signal: abortController.signal,
            headers: { Accept: 'application/json' },
          });
          if (response.type !== 'opaqueredirect') {
            if (!response.ok) {
              const problem = await response.json().catch(() => null) as {
                error?: string;
                error_description?: string;
              } | null;
              throw new Error(
                problem?.error_description ??
                  (problem?.error
                    ? `Sign-in failed: ${problem.error}.`
                    : `Sign-in failed with HTTP ${response.status}.`),
              );
            }
            const result: unknown = await response.json().catch(() => null);
            if (isConsentPrompt(result)) {
              // Refuse a consent prompt for another client or for scopes we never asked for.
              if (result.client_id !== config.clientId ||
                  result.requested_scopes.some((scope) => !config.scopes.includes(scope))) {
                throw new Error('The Broker consent response does not match this registered client and its configured scopes.');
              }
              // Hand off to the in-app consent dialog; `decideConsent` continues from here.
              popup?.close();
              setConsent(result);
              return false;
            }
            const body = result as { error?: string; error_description?: string } | null;
            if (body?.error)
              throw new Error(body.error_description ?? `Sign-in failed: ${body.error}.`);
          }
        } catch (reason) {
          // Aborts and network blips are normal while polling; keep waiting. Real errors stop.
          if (reason instanceof Error && !/aborted|load failed|failed to fetch/i.test(reason.message))
            throw reason;
        } finally {
          window.clearTimeout(timeout);
          if (pollAbortRef.current === abortController) pollAbortRef.current = null;
        }
        await new Promise((resolve) => window.setTimeout(resolve, 500));
      }
      throw new Error('Sign-in timed out. You can try again.');
    } catch (reason) {
      popup?.close();
      if (rebind) {
        // The held sessions stay usable; the page offers a manual sign-in instead.
      } else if (silent) {
        // The reload restore failed (for example the Broker session ended). Clear the
        // flag so we do not retry on every load, and show the normal sign-in screen
        // without an error.
        forgetSignIn();
        setError(null);
      } else {
        setError(reason instanceof Error ? reason.message : 'Sign-in failed.');
      }
      clearAuthorizationTransaction();
      transactionRef.current = null;
      return false;
    } finally {
      if (popupMessageHandler) window.removeEventListener('message', popupMessageHandler);
      if (authorizationFrameRef.current === iframe)
        authorizationFrameRef.current = null;
      iframe?.remove();
      // Only the reload restore owns the `restoringSession` flag; rebinds leave it alone.
      if (silent && !rebind) setRestoringSession(false);
      if (!rebind) setBusy(false);
    }
  }, [configurationError, consumeAuthorizationResponse, setSession]);

  /** Interactive sign-in for the unbound session or a project/run binding. */
  const authorize = useCallback(async (
    requestedBinding: RunBinding | null = null,
    popupOverride?: Window | null,
  ) => {
    await runAuthorization(requestedBinding, popupOverride, false);
  }, [runAuthorization]);

  const ensureSession = useCallback((binding: RunBinding | null, activate = true): Promise<boolean> => {
    const key = bindingKey(binding);
    // Already holding a token for this exact binding: reuse it, no Broker call.
    const held = sessionsRef.current.get(key);
    if (held) {
      if (activate && sessionRef.current !== held) setActiveSession(held);
      return Promise.resolve(true);
    }
    // A silent attempt for this binding is already running: share it instead of starting another.
    const pending = ensureRef.current.get(key);
    if (pending) return pending;
    // Ask the Broker silently for a token bound to this binding. On failure the current
    // session is kept and the caller shows a manual sign-in.
    const attempt = runAuthorization(binding, null, true, activate ? 'activate' : 'store');
    ensureRef.current.set(key, attempt);
    // Clear the entry when done, unless sign-out or a newer attempt already replaced it.
    void attempt.finally(() => {
      if (ensureRef.current.get(key) === attempt) ensureRef.current.delete(key);
    });
    return attempt;
  }, [runAuthorization, setActiveSession]);

  // Reload restore: tokens live only in memory, so a reload loses them. If this tab signed
  // in before, try one silent sign-in for the page's binding (run-bound on a run page,
  // unbound elsewhere). This is not a rebind, so the result replaces all sessions.
  useEffect(() => {
    if (session || !restoringSession || silentAttemptedRef.current) return;
    silentAttemptedRef.current = true;
    void runAuthorization(bindingFromCurrentRoute(), null, true);
  }, [restoringSession, runAuthorization, session]);

  /** Sends the user's consent decision to the Broker and finishes or cancels sign-in. */
  const decideConsent = useCallback(async (approve: boolean) => {
    if (!consent) return;
    // The consent belongs to the sign-in transaction that triggered it.
    const transaction = transactionRef.current ?? readAuthorizationTransaction();
    if (!transaction) {
      setConsent(null);
      setError('The consent request expired. Start sign-in again.');
      return;
    }
    setBusy(true);
    setError(null);
    let popup: Window | null = null;
    try {
      // Open the popup now, while still inside the click, in case sign-in must continue.
      popup = approve
        ? window.open('about:blank', 'agentweaver-identity', 'popup,width=560,height=720')
        : null;
      const response = await submitBrokerConsent(defaultAuthorizationConfig(), consent, approve);
      // Same rule as runAuthorization: a run-bound sign-in while signed in adds a session.
      const mode: SessionMode = transaction.binding && sessionRef.current ? 'activate' : 'replace';
      // The Broker may answer the consent with a code right away.
      if (await consumeAuthorizationResponse(response.url, transaction, mode)) {
        popup?.close();
        return;
      }
      if (!approve) {
        throw new Error('Consent was declined. No Agentweaver session was created.');
      }
      if (!popup)
        throw new Error('Allow the sign-in popup to continue.');
      // Otherwise restart the interactive sign-in in the popup for the same binding.
      await authorize(transaction.binding, popup);
    } catch (reason) {
      popup?.close();
      setError(reason instanceof Error ? reason.message : 'Broker consent failed.');
      if (!approve) clearAuthorizationTransaction();
    } finally {
      if (!approve) setConsent(null);
      setBusy(false);
    }
  }, [authorize, consent, consumeAuthorizationResponse]);

  /** Drops one held session after a failed refresh; the unbound session falls back to sign-in. */
  const dropSession = useCallback((current: AuthSession, message: string) => {
    // Losing the unbound session means the user is signed out: drop everything.
    if (!current.binding) {
      setSession(null);
      setError(message);
      return;
    }
    // Losing a run-bound session only drops that run's token. If it was active, fall back
    // to the unbound session. Show the error only when no session is left.
    sessionsRef.current.delete(bindingKey(current.binding));
    if (sessionRef.current === current) setActiveSession(sessionsRef.current.get('') ?? null);
    if (!sessionRef.current) setError(message);
  }, [setActiveSession, setSession]);

  /**
   * Refreshes one held session's token. Returns the new session, the newer session that
   * already replaced it, or null when the session was dropped.
   */
  const refresh = useCallback((current: AuthSession): Promise<AuthSession | null> => {
    const key = bindingKey(current.binding);
    // Someone already replaced this session (refresh or new sign-in): use the newer one.
    const latest = sessionsRef.current.get(key) ?? null;
    if (latest !== current) return Promise.resolve(latest);
    // Share an in-flight refresh so parallel 401s do not spend the refresh token twice.
    const inFlight = refreshPromisesRef.current.get(key);
    if (inFlight?.session === current) return inFlight.promise;
    const refreshToken = current.refreshToken;
    if (!refreshToken) {
      dropSession(current, 'Your sign-in expired. Sign in again to continue.');
      return Promise.resolve(null);
    }
    const promise = (async (): Promise<AuthSession | null> => {
      try {
        const refreshed = await refreshBrokerToken(defaultAuthorizationConfig(), refreshToken);
        // The refreshed token keeps the same binding as the session it replaces.
        const next = sessionFromToken(refreshed, current.binding);
        // Keep the old refresh token if the Broker did not rotate it.
        if (!next.refreshToken) next.refreshToken = refreshToken;
        // A refresh response may omit the ID token; the person has not changed, so keep the name.
        if (!next.displayName) next.displayName = current.displayName;
        // Sign-out or a new sign-in happened while we waited: do not put the old session back.
        if (sessionsRef.current.get(key) !== current) return sessionsRef.current.get(key) ?? null;
        sessionsRef.current.set(key, next);
        if (sessionRef.current === current) setActiveSession(next);
        return next;
      } catch (reason) {
        if (sessionsRef.current.get(key) !== current) return sessionsRef.current.get(key) ?? null;
        dropSession(current, reason instanceof Error ? reason.message : 'Your sign-in expired. Sign in again.');
        return null;
      }
    })();
    const activeRefresh = { session: current, promise };
    refreshPromisesRef.current.set(key, activeRefresh);
    void promise.finally(() => {
      if (refreshPromisesRef.current.get(key) === activeRefresh)
        refreshPromisesRef.current.delete(key);
    });
    return promise;
  }, [dropSession, setActiveSession]);

  // Refresh the active session about 30 seconds before it expires.
  useEffect(() => {
    if (!session) return;
    const delay = Math.max(0, session.expiresAt - Date.now() - 30_000);
    const timer = window.setTimeout(() => { void refresh(session); }, delay);
    return () => window.clearTimeout(timer);
  }, [refresh, session]);

  /**
   * Runs one Gateway call with the token held for `requestedBinding`. Run-bound calls use
   * only the token bound to that exact run, never the unbound token or another run's token.
   */
  const apiCall = useCallback(async <T,>(
    operation: (accessToken: string, tenantSelector: string | null) => Promise<T>,
    requestedBinding: RunBinding | null = null,
  ): Promise<T> => {
    let current = sessionsRef.current.get(bindingKey(requestedBinding)) ?? null;
    // No token for this binding: fail like the Gateway would, so callers handle it the same way.
    if (!current) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        requestedBinding
          ? 'Sign in for this exact project and run before continuing.'
          : 'Sign in to continue.',
      );
    }
    // Refresh first if the token is about to expire, to avoid a predictable 401.
    if (current.expiresAt <= Date.now() + 5_000) {
      current = await refresh(current);
      if (!current) throw new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.');
    }
    const invoke = (active: AuthSession) => {
      // Send the confirmed tenant only if it was confirmed for this exact token, session
      // generation and binding. Otherwise send no tenant and let the Gateway pick the default.
      const confirmed = authorizationContextRef.current;
      const tenantSelector =
        confirmed &&
        confirmed.accessToken === active.accessToken &&
        confirmed.sessionGeneration === sessionGenerationRef.current &&
        sameBinding(confirmed.binding, active.binding) &&
        sameBinding(active.binding, requestedBinding)
          ? confirmed.value.tenantId
          : null;
      return operation(active.accessToken, tenantSelector);
    };
    // On a 401, refresh once and retry once. A second failure goes to the caller.
    try {
      return await invoke(current);
    } catch (reason) {
      if (!(reason instanceof GatewayError) || reason.status !== 401) throw reason;
      current = await refresh(current);
      if (!current) throw reason;
      return await invoke(current);
    }
  }, [refresh]);

  const resolveAuthorizationContext = useCallback(async (
    tenantSelector?: string,
  ): Promise<ConfirmedAuthorizationContext | null> => {
    const requestGeneration = ++authorizationContextRequestRef.current;
    const startingSession = sessionRef.current;
    authorizationContextRef.current = null;
    setAuthorizationContextState(null);
    setAuthorizationContextLoading(true);
    setAuthorizationContextError(null);
    if (!startingSession) {
      const reason = new GatewayError(401, { code: 'unauthorized' }, 'Sign in to resolve a tenant context.');
      setAuthorizationContextError(reason);
      setAuthorizationContextLoading(false);
      return null;
    }

    const requestIdentity: { current: {
      accessToken: string;
      binding: RunBinding | null;
      sessionGeneration: number;
    } | null } = { current: null };
    try {
      const result = await apiCall(
        (accessToken) => {
          const active = sessionRef.current;
          // The active session changed between the request start and this call: stop.
          if (!active ||
              active.accessToken !== accessToken ||
              !sameBinding(active.binding, startingSession.binding))
            throw new GatewayError(401, { code: 'authorization_context_stale' }, 'The sign-in session changed while resolving tenant context.');
          requestIdentity.current = {
            accessToken,
            binding: active.binding,
            sessionGeneration: sessionGenerationRef.current,
          };
          return gatewayClient.getAuthorizationContext(accessToken, tenantSelector);
        },
        startingSession.binding,
      );
      const identity = requestIdentity.current;
      const active = sessionRef.current;
      // Ignore the answer if a newer request started or the session changed while waiting.
      if (requestGeneration !== authorizationContextRequestRef.current ||
          !identity ||
          !active ||
          active.accessToken !== identity.accessToken ||
          sessionGenerationRef.current !== identity.sessionGeneration ||
          !sameBinding(active.binding, identity.binding))
        return null;
      // The owner must confirm the same binding the token carries (run-bound or unbound).
      if (!contextMatchesBinding(result, identity.binding))
        throw new GatewayError(
          502,
          { code: 'authorization_context_binding_mismatch' },
          'The Projects owner returned authorization context for a different binding.',
        );

      const confirmedContext: ConfirmedAuthorizationContext = {
        tenantId: result.tenantId,
        membershipRevision: result.membershipRevision,
      };
      authorizationContextRef.current = {
        value: confirmedContext,
        accessToken: identity.accessToken,
        binding: identity.binding,
        sessionGeneration: identity.sessionGeneration,
      };
      setAuthorizationContextState(confirmedContext);
      return confirmedContext;
    } catch (reason) {
      if (requestGeneration === authorizationContextRequestRef.current) {
        authorizationContextRef.current = null;
        setAuthorizationContextState(null);
        setAuthorizationContextError(reason);
      }
      return null;
    } finally {
      if (requestGeneration === authorizationContextRequestRef.current)
        setAuthorizationContextLoading(false);
    }
  }, [apiCall]);

  useEffect(() => {
    if (!session) return;
    const bootstrap = window.setTimeout(() => {
      void resolveAuthorizationContext();
    }, 0);
    return () => window.clearTimeout(bootstrap);
  }, [resolveAuthorizationContext, session]);

  /** Clears every token, any sign-in in progress, the restore flag, and the recent-runs list. */
  const signOut = useCallback(() => {
    pollAbortRef.current?.abort();
    authorizationFrameRef.current?.remove();
    authorizationFrameRef.current = null;
    clearAuthorizationTransaction();
    transactionRef.current = null;
    setSession(null);
    setConsent(null);
    setError(null);
    forgetSignIn();
    // Recent runs belong to the signed-in user; do not show them to the next one.
    clearRecentRuns();
    setRestoringSession(false);
  }, [setSession]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    busy,
    restoringSession,
    consent,
    error,
    configurationError,
    authorize,
    ensureSession,
    decideConsent,
    signOut,
    apiCall,
    authorizationContext,
    authorizationContextLoading,
    authorizationContextError,
    resolveAuthorizationContext,
  }), [
    apiCall,
    authorizationContext,
    authorizationContextError,
    authorizationContextLoading,
    authorize,
    busy,
    configurationError,
    consent,
    decideConsent,
    ensureSession,
    error,
    resolveAuthorizationContext,
    session,
    signOut,
    restoringSession,
  ]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Reads the active authentication context and throws if no provider is mounted. */
// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used within AuthProvider.');
  return value;
}
