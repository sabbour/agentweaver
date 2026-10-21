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
const AUTH_TIMEOUT_MS = 10 * 60 * 1000;
const SILENT_AUTH_TIMEOUT_MS = 15 * 1000;
const SIGNED_IN_BEFORE_STORAGE_KEY = 'agentweaver.auth.signed-in-before';

function hasSignedInBefore(): boolean {
  try {
    return sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

function rememberSignIn(): void {
  try {
    sessionStorage.setItem(SIGNED_IN_BEFORE_STORAGE_KEY, '1');
  } catch {
    // This optional marker must not prevent an in-memory sign-in.
  }
}

function forgetSignIn(): void {
  try {
    sessionStorage.removeItem(SIGNED_IN_BEFORE_STORAGE_KEY);
  } catch {
    // If session storage is unavailable, reads also fail closed and cannot start silent sign-in.
  }
}

function hasIdentityCallback(): boolean {
  return window.location.pathname === '/auth/callback' ||
    typeof window.__AGENTWEAVER_IDENTITY_CALLBACK__ === 'string';
}

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
    return null;
  }
}

/** Map key for the session held for one binding; the unbound session uses the empty key. */
function bindingKey(binding: RunBinding | null): string {
  return binding ? JSON.stringify([binding.projectId, binding.runId]) : '';
}

/** How a sign-in result is stored: replace all sessions, or add one and optionally activate it. */
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
  const [restoringSession, setRestoringSession] = useState(
    () => hasSignedInBefore() && !hasIdentityCallback(),
  );
  const [consent, setConsent] = useState<BrokerConsentPrompt | null>(null);
  const [error, setError] = useState<string | null>(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    return typeof callbackSearch === 'string' &&
      !parseAuthorizationCallbackParameters(callbackSearch)
      ? 'The sign-in response is invalid. Start sign-in again.'
      : null;
  });
  const sessionRef = useRef<AuthSession | null>(null);
  // Every session the page holds, keyed by bindingKey(). The unbound session and each
  // run-bound session live side by side, so opening a run does not sign out the user.
  const sessionsRef = useRef(new Map<string, AuthSession>());
  // Silent sign-ins in flight, keyed by binding. Callers asking for the same binding
  // share one attempt instead of opening several hidden frames.
  const ensureRef = useRef(new Map<string, Promise<boolean>>());
  const sessionGenerationRef = useRef(0);
  const authorizationContextRequestRef = useRef(0);
  const authorizationContextRef = useRef<{
    value: ConfirmedAuthorizationContext;
    accessToken: string;
    binding: RunBinding | null;
    sessionGeneration: number;
  } | null>(null);
  // One token refresh in flight per binding, so parallel calls do not spend the same
  // refresh token twice.
  const refreshPromisesRef = useRef(new Map<string, {
    session: AuthSession;
    promise: Promise<AuthSession | null>;
  }>());
  const transactionRef = useRef<AuthorizationTransaction | null>(null);
  const pollAbortRef = useRef<AbortController | null>(null);
  const authorizationFrameRef = useRef<HTMLIFrameElement | null>(null);
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
    // A different token or binding means a new identity for the page. Bump the generation
    // and drop the cached tenant context so no answer from the old session is reused.
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
    // Forget every held session and every pending silent sign-in. Used for sign-out
    // and for a fresh unbound sign-in.
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

  /** Exchanges the authorization code for tokens and stores the new session using `mode`. */
  const finishCode = useCallback(async (code: string, transaction: AuthorizationTransaction, mode: SessionMode) => {
    const token = await exchangeAuthorizationCode(
      defaultAuthorizationConfig(),
      transaction,
      code,
    );
    // The PKCE transaction is single use. Clear it before storing the session so a
    // replayed callback cannot exchange the same code again.
    clearAuthorizationTransaction();
    transactionRef.current = null;
    setConsent(null);
    setError(null);
    rememberSignIn();
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
    const result = parseAuthorizationResult(responseUrl, transaction.state);
    if (!result) return false;
    if ('error' in result) {
      throw new Error(result.description ?? `Sign-in failed: ${result.error}.`);
    }
    await finishCode(result.code, transaction, mode);
    return true;
  }, [finishCode]);

  useEffect(() => {
    const callbackSearch = window.__AGENTWEAVER_IDENTITY_CALLBACK__;
    if (typeof callbackSearch !== 'string') return;
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    const callback = parseAuthorizationCallbackParameters(callbackSearch);
    if (!callback) return;
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
    const responseUrl = new URL('/auth/callback', window.location.origin);
    responseUrl.searchParams.set('state', callback.state);
    if ('code' in callback) responseUrl.searchParams.set('code', callback.code);
    else {
      responseUrl.searchParams.set('error', callback.error);
      if (callback.error_description)
        responseUrl.searchParams.set('error_description', callback.error_description);
    }
    setBusy(true);
    void consumeAuthorizationResponse(responseUrl.toString(), transaction)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Sign-in failed.'))
      .finally(() => setBusy(false));
  }, [consumeAuthorizationResponse, setBusy]);

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
    // Without a valid client configuration no sign-in can work. A background run-bound
    // attempt fails quietly; a user-started attempt shows the configuration error.
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
    // A run-bound sign-in while already signed in keeps the other sessions. A sign-in with
    // no binding, or with no session yet, replaces everything.
    const mode: SessionMode = rebind ?? (requestedBinding && sessionRef.current ? 'activate' : 'replace');
    // Background run-bound attempts do not touch the page's error, consent, or busy state,
    // so they never interrupt what the user is looking at.
    if (!rebind) {
      setError(null);
      setConsent(null);
      setBusy(true);
    }
    if (mode === 'replace') setSession(null);
    // Abort any older consent poll so only this attempt is waiting on the Broker.
    pollAbortRef.current?.abort();

    let popup: Window | null = null;
    let iframe: HTMLIFrameElement | null = null;
    let callbackWindow: Window | null = null;
    let popupCallbackUrl: string | null = null;
    let popupCallbackError: Error | null = null;
    let popupCallbackReceived = false;
    let popupMessageHandler: ((event: MessageEvent<unknown>) => void) | null = null;
    try {
      // Silent sign-in uses a hidden frame, never a popup. A caller may pass its own popup
      // (opened during a click so the browser allows it).
      popup = silent
        ? null
        : popupOverride === undefined
          ? window.open('about:blank', 'agentweaver-identity', 'popup,width=560,height=720')
          : popupOverride;
      const transaction = transactionFor(requestedBinding);
      transactionRef.current = transaction;
      storeAuthorizationTransaction(transaction);
      const challenge = await pkceChallenge(transaction.verifier);
      const authorizeUrl = silent
        ? buildAuthorizeUrl(config, transaction, challenge, 'none')
        : buildAuthorizeUrl(config, transaction, challenge);
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
        // No popup (blocked or none given). Fall back to a full-page redirect; the result
        // arrives through the callback page on reload.
        window.location.assign(authorizeUrl);
        return false;
      }
      const callbackOrigin = new URL(transaction.redirectUri).origin;
      // Accept a callback message only from the window we opened, from the callback origin,
      // for the current transaction, and only once. Anything else is ignored.
      popupMessageHandler = (event) => {
        if (event.source !== callbackWindow ||
            event.origin !== callbackOrigin ||
            transactionRef.current !== transaction ||
            popupCallbackReceived)
          return;

        const message = parseAuthorizationCallbackMessage(event.data);
        if (!message) return;
        popupCallbackReceived = true;
        try {
          const responseUrl = new URL(transaction.redirectUri);
          responseUrl.searchParams.set('state', message.state);
          if ('code' in message) responseUrl.searchParams.set('code', message.code);
          else {
            responseUrl.searchParams.set('error', message.error);
            if (message.error_description)
              responseUrl.searchParams.set('error_description', message.error_description);
          }
          // Validate `state` here so a forged message fails before the loop uses it.
          parseAuthorizationResult(responseUrl.toString(), transaction.state);
          popupCallbackUrl = responseUrl.toString();
        } catch (reason) {
          popupCallbackError = reason instanceof Error
            ? reason
            : new Error('The sign-in response is invalid.');
        }
        // Stop the current consent poll so the loop below picks up the result at once.
        pollAbortRef.current?.abort();
      };
      window.addEventListener('message', popupMessageHandler);
      const startedAt = Date.now();
      // Wait for the callback. Silent sign-in has a short limit because the Broker answers
      // at once or not at all; interactive sign-in gives the user time to finish.
      while (Date.now() - startedAt < (silent ? SILENT_AUTH_TIMEOUT_MS : AUTH_TIMEOUT_MS)) {
        // A newer sign-in replaced this one.
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
        if (silent) {
          await new Promise((resolve) => window.setTimeout(resolve, 50));
          continue;
        }
        // Interactive only: ask the Broker whether it needs a consent decision first.
        // Each poll is cut off after 2.5 seconds so a slow answer does not block the loop.
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
          // A redirect means the Broker is sending the browser on to the callback, so there is
          // nothing to read here. Any other answer is an error or a consent prompt.
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
            // Show consent only if it is for this client and asks for no extra scopes.
            if (isConsentPrompt(result)) {
              if (result.client_id !== config.clientId ||
                  result.requested_scopes.some((scope) => !config.scopes.includes(scope))) {
                throw new Error('The Broker consent response does not match this registered client and its configured scopes.');
              }
              popup?.close();
              setConsent(result);
              return false;
            }
            const body = result as { error?: string; error_description?: string } | null;
            if (body?.error)
              throw new Error(body.error_description ?? `Sign-in failed: ${body.error}.`);
          }
        } catch (reason) {
          // Aborts and network blips are expected while polling; try again. Real errors stop sign-in.
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
      // How a failure is reported depends on who started the attempt.
      if (rebind) {
        // The held sessions stay usable; the page offers a manual sign-in instead.
      } else if (silent) {
        // Silent restore failed: forget the 'was signed in' marker and show the sign-in page
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
      // Background attempts never owned the restoring or busy flags, so leave them alone.
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

  /** Returns a session for `binding`, reusing a held one or trying one silent sign-in. */
  const ensureSession = useCallback((binding: RunBinding | null, activate = true): Promise<boolean> => {
    const key = bindingKey(binding);
    const held = sessionsRef.current.get(key);
    // Already held: no network call. Switch to it if the caller wants it active.
    if (held) {
      if (activate && sessionRef.current !== held) setActiveSession(held);
      return Promise.resolve(true);
    }
    // Share an attempt that is already running for this binding.
    const pending = ensureRef.current.get(key);
    if (pending) return pending;
    // Silent attempt that keeps the other held sessions whatever the result.
    const attempt = runAuthorization(binding, null, true, activate ? 'activate' : 'store');
    ensureRef.current.set(key, attempt);
    void attempt.finally(() => {
      // Remove only our own entry. setSession may have cleared it and a newer attempt may own the key.
      if (ensureRef.current.get(key) === attempt) ensureRef.current.delete(key);
    });
    return attempt;
  }, [runAuthorization, setActiveSession]);

  useEffect(() => {
    if (session || !restoringSession || silentAttemptedRef.current) return;
    silentAttemptedRef.current = true;
    // Restore once on page load, bound to the run in the URL if there is one.
    void runAuthorization(bindingFromCurrentRoute(), null, true);
  }, [restoringSession, runAuthorization, session]);

  const decideConsent = useCallback(async (approve: boolean) => {
    if (!consent) return;
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
      popup = approve
        ? window.open('about:blank', 'agentweaver-identity', 'popup,width=560,height=720')
        : null;
      const response = await submitBrokerConsent(defaultAuthorizationConfig(), consent, approve);
      // Same rule as runAuthorization: a run-bound consent keeps the sessions already held.
      const mode: SessionMode = transaction.binding && sessionRef.current ? 'activate' : 'replace';
      if (await consumeAuthorizationResponse(response.url, transaction, mode)) {
        popup?.close();
        return;
      }
      if (!approve) {
        throw new Error('Consent was declined. No Agentweaver session was created.');
      }
      if (!popup)
        throw new Error('Allow the sign-in popup to continue.');
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
    // Losing the unbound session means the user is signed out. Clear all and show the error.
    if (!current.binding) {
      setSession(null);
      setError(message);
      return;
    }
    // Losing a run-bound session only removes that run. Fall back to the unbound session
    // if the dropped one was active, and show an error only if nothing is left.
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
      // Remove only our own entry; a newer refresh may already own the key.
      if (refreshPromisesRef.current.get(key) === activeRefresh)
        refreshPromisesRef.current.delete(key);
    });
    return promise;
  }, [dropSession, setActiveSession]);

  useEffect(() => {
    if (!session) return;
    const delay = Math.max(0, session.expiresAt - Date.now() - 30_000);
    const timer = window.setTimeout(() => { void refresh(session); }, delay);
    return () => window.clearTimeout(timer);
  }, [refresh, session]);

  const apiCall = useCallback(async <T,>(
    operation: (accessToken: string, tenantSelector: string | null) => Promise<T>,
    requestedBinding: RunBinding | null = null,
  ): Promise<T> => {
    // Use the session held for this exact binding, never a different one. A run-bound call
    // must not fall back to the unbound token, and the reverse.
    let current = sessionsRef.current.get(bindingKey(requestedBinding)) ?? null;
    if (!current) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        requestedBinding
          ? 'Sign in for this exact project and run before continuing.'
          : 'Sign in to continue.',
      );
    }
    if (current.expiresAt <= Date.now() + 5_000) {
      current = await refresh(current);
      if (!current) throw new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.');
    }
    const invoke = (active: AuthSession) => {
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
      if (requestGeneration !== authorizationContextRequestRef.current ||
          !identity ||
          !active ||
          active.accessToken !== identity.accessToken ||
          sessionGenerationRef.current !== identity.sessionGeneration ||
          !sameBinding(active.binding, identity.binding))
        return null;
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
