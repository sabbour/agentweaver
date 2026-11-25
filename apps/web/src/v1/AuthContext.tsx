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
import type {
  AuthorizationCallbackMessage,
  AuthorizationBinding,
  AuthorizationTransaction,
} from './authProtocol';
import { missingAuthConfiguration } from './config';
import type {
  BrokerConsentPrompt,
  BrokerTokenResponse,
  ProjectAuthorizationContextResponse,
  ProjectSummary,
} from './contracts';
import { gatewayClient, GatewayError } from './api';
import { clearRecentRuns } from './recentRuns';
import {
  createProjectCredentialOperationStore,
  type ProjectCredentialOperationStore,
} from './projectCredentialOperations';
import type {
  PlatformPolicyArtifactRequest,
  PlatformPolicyRevisionPage,
  PlatformPolicyRevisionReadResult,
  PlatformPolicyValidationResult,
} from './platformPolicyContracts';

/** In-memory bearer session bound to the project scope that initiated authorization. */
interface AuthSession {
  accessToken: string;
  refreshToken?: string;
  expiresAt: number;
  binding: AuthorizationBinding | null;
  /** Display name from the ID token, or null when the Broker sent none. Shown in the persona menu. */
  displayName: string | null;
}

/** Tenant authorization values confirmed by the Projects owner. */
type ConfirmedAuthorizationContext = Pick<
  ProjectAuthorizationContextResponse,
  'issuer' | 'actorId' | 'tenantId' | 'membershipRevision'
>;

/** Identity fields safe to share with the read-only Platform Policies controller. */
export type PlatformPolicyIdentity = Pick<
  ConfirmedAuthorizationContext,
  'issuer' | 'actorId' | 'tenantId'
>;

export interface ProjectPolicyAccess {
  identity: PlatformPolicyIdentity;
  projects: ProjectSummary[];
  projectId: string | null;
}

export interface PlatformPolicyRefreshResult {
  refreshed: boolean;
  authSessionGeneration: number;
  unboundSessionGeneration: number;
}

/** Authentication state and operations shared with v1 pages and Gateway callers. */
interface AuthContextValue {
  session: AuthSession | null;
  authSessionGeneration: number;
  authSessionEpoch: number;
  isActiveSession: (session: AuthSession) => boolean;
  unboundSessionGeneration: number;
  authorizationContext: ConfirmedAuthorizationContext | null;
  authorizationContextLoading: boolean;
  authorizationContextError: unknown;
  getAuthorizationContextError?: () => unknown;
  busy: boolean;
  restoringSession: boolean;
  consent: BrokerConsentPrompt | null;
  error: string | null;
  configurationError: string | null;
  /** `activate` defaults to true; false stores a project session without changing the active session. */
  authorize: (binding?: AuthorizationBinding | null, popupOverride?: Window | null, activate?: boolean) => Promise<void>;
  /**
   * Makes a session for `binding` available without a prompt. It reuses a held session or
   * asks the Broker once with `prompt=none`. Returns false when the Broker needs the user;
   * the current session is kept in that case. `activate` makes it the page's active session.
   */
  ensureSession: (binding: AuthorizationBinding | null, activate?: boolean) => Promise<boolean>;
  resolveAuthorizationContext: (tenantSelector?: string) => Promise<ConfirmedAuthorizationContext | null>;
  decideConsent: (approve: boolean) => Promise<void>;
  signOut: () => void;
  apiCall: <T>(
    operation: (accessToken: string, tenantSelector: string | null) => Promise<T>,
    binding?: AuthorizationBinding | null,
  ) => Promise<T>;
  apiCallOnce: <T>(
    operation: (accessToken: string, tenantSelector: string) => Promise<T>,
    binding: AuthorizationBinding,
    expectedIdentity?: {
      tenantId: string;
      actorId: string | null;
      issuer: string | null;
    },
  ) => Promise<T>;
  projectCredentialOperations?: ProjectCredentialOperationStore;
  establishPlatformPolicyAccess: (
    tenantSelector: string,
    signal?: AbortSignal,
  ) => Promise<PlatformPolicyIdentity>;
  getPlatformPolicyRevisionHistory: (
    tenantSelector: string,
    options?: { cursor?: string | null; signal?: AbortSignal },
  ) => Promise<PlatformPolicyRevisionPage>;
  readPlatformPolicyRevision: (
    tenantSelector: string,
    revision: number,
    signal?: AbortSignal,
  ) => Promise<PlatformPolicyRevisionReadResult>;
  validatePlatformPolicyArtifact: (
    tenantSelector: string,
    artifact: PlatformPolicyArtifactRequest,
    signal?: AbortSignal,
  ) => Promise<PlatformPolicyValidationResult>;
  establishProjectPolicyAccess: (
    tenantSelector: string,
    projectId: string | null,
    signal?: AbortSignal,
  ) => Promise<ProjectPolicyAccess>;
  validateProjectPolicyArtifact: (
    tenantSelector: string,
    projectId: string,
    artifact: PlatformPolicyArtifactRequest,
    signal?: AbortSignal,
  ) => Promise<PlatformPolicyValidationResult>;
  refreshPlatformPolicySession: () => Promise<PlatformPolicyRefreshResult>;
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

/** Map key for the session held for one binding; the unbound session uses the empty key. */
function bindingKey(binding: AuthorizationBinding | null): string {
  return binding ? JSON.stringify([binding.projectId, binding.runId ?? null]) : '';
}

/** How a sign-in result is stored: replace all sessions, or add one and optionally activate it. */
type SessionMode = 'replace' | 'activate' | 'store';

/** True when two optional authorization requests target the same project and run. */
function sameBinding(left: AuthorizationBinding | null, right: AuthorizationBinding | null): boolean {
  return left?.projectId === right?.projectId && left?.runId === right?.runId;
}

/** Ensures owner-returned context matches the requested project or project/run scope. */
function contextMatchesBinding(
  context: ProjectAuthorizationContextResponse,
  binding: AuthorizationBinding | null,
): boolean {
  if (!binding)
    return context.boundProjectId === null && context.boundRunId === null;
  return context.boundProjectId === binding.projectId &&
    context.boundRunId === (binding.runId ?? null);
}

/** Starts a new PKCE transaction with fresh state and verifier for the requested scope. */
function transactionFor(binding: AuthorizationBinding | null): AuthorizationTransaction {
  return {
    state: randomState(),
    verifier: randomVerifier(),
    redirectUri: defaultAuthorizationConfig().redirectUri,
    binding,
  };
}

/** Converts a Broker token response into the session state held only in memory. */
function sessionFromToken(token: BrokerTokenResponse, binding: AuthorizationBinding | null): AuthSession {
  return {
    accessToken: token.access_token,
    refreshToken: token.refresh_token,
    expiresAt: Date.now() + token.expires_in * 1000,
    binding,
    displayName: displayNameFromIdToken(token.id_token),
  };
}

interface PlatformPolicyAccessSnapshot {
  identity: PlatformPolicyIdentity;
  authorizationContext: ProjectAuthorizationContextResponse;
  activeSession: AuthSession;
  unboundSession: AuthSession;
  authSessionGeneration: number;
  unboundSessionGeneration: number;
}

interface PlatformPolicySilentAttempt {
  controller: AbortController;
  iframe: HTMLIFrameElement | null;
  messageHandler: ((event: MessageEvent<unknown>) => void) | null;
  waiters: number;
}

/**
 * Owns the v1 Broker sessions, PKCE sign-in, one-time silent restoration, consent,
 * refresh, and tenant context.
 * Access tokens stay in memory. The provider holds at most one session per binding
 * (the unbound session plus project-bound sessions); `session` is the active one. Each
 * Gateway call uses the session held for its exact binding and may retry
 * once after a 401 using token refresh.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSessionState] = useState<AuthSession | null>(null);
  const [authSessionGeneration, setAuthSessionGeneration] = useState(0);
  const [authSessionEpoch, setAuthSessionEpoch] = useState(0);
  const [unboundSessionGeneration, setUnboundSessionGeneration] = useState(0);
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
  // project binding live side by side.
  const sessionsRef = useRef(new Map<string, AuthSession>());
  // Silent sign-ins in flight, keyed by binding. Callers asking for the same binding
  // share one attempt instead of opening several hidden frames.
  const ensureRef = useRef(new Map<string, Promise<boolean>>());
  const sessionGenerationRef = useRef(0);
  const unboundSessionGenerationRef = useRef(0);
  const authorizationContextRequestRef = useRef(0);
  const contextBootstrapRequestGenerationRef = useRef(0);
  const authorizationContextRef = useRef<{
    value: ConfirmedAuthorizationContext;
    accessToken: string;
    binding: AuthorizationBinding | null;
    sessionGeneration: number;
  } | null>(null);
  const authorizationContextErrorRef = useRef<unknown>(null);
  // One token refresh in flight per binding, so parallel calls do not spend the same
  // refresh token twice.
  const refreshPromisesRef = useRef(new Map<string, {
    session: AuthSession;
    promise: Promise<AuthSession>;
  }>());
  const transactionRef = useRef<AuthorizationTransaction | null>(null);
  const consentSessionModeRef = useRef<SessionMode | null>(null);
  const pollAbortRef = useRef<AbortController | null>(null);
  const authorizationFrameRef = useRef<HTMLIFrameElement | null>(null);
  const silentAttemptedRef = useRef(false);
  const policyAccessRef = useRef<PlatformPolicyAccessSnapshot | null>(null);
  const policyCandidateRef = useRef<{
    identity: PlatformPolicyIdentity;
    binding: AuthorizationBinding | null;
  } | null>(null);
  const policySilentAttemptRef = useRef<PlatformPolicySilentAttempt | null>(null);
  const policySilentPromiseRef = useRef<Promise<boolean> | null>(null);
  const projectCredentialOperations = useMemo(
    () => createProjectCredentialOperationStore(),
    [],
  );

  const configurationError = useMemo(() => {
    const missing = missingAuthConfiguration();
    return missing.length
      ? `Configure ${missing.join(', ')} before signing in.`
      : null;
  }, []);

  const notifyUnboundSessionChange = useCallback(() => {
    const next = ++unboundSessionGenerationRef.current;
    setUnboundSessionGeneration(next);
    policyAccessRef.current = null;
  }, []);

  const cancelPolicyAuthorization = useCallback(() => {
    const attempt = policySilentAttemptRef.current;
    if (!attempt) return;
    attempt.controller.abort();
    if (attempt.messageHandler)
      window.removeEventListener('message', attempt.messageHandler);
    attempt.iframe?.remove();
    policySilentAttemptRef.current = null;
    policySilentPromiseRef.current = null;
  }, []);

  /** Makes `next` the active session without changing the held sessions. */
  const setActiveSession = useCallback((next: AuthSession | null, refreshed = false) => {
    const current = sessionRef.current;
    // Refresh keeps the activation epoch; replacing or switching sessions changes it.
    if (current !== next && !refreshed)
      setAuthSessionEpoch((epoch) => epoch + 1);
    // A different token or binding means a new identity for the page. Bump the generation
    // and drop the cached tenant context so no answer from the old session is reused.
    if (current?.accessToken !== next?.accessToken || !sameBinding(current?.binding ?? null, next?.binding ?? null)) {
      cancelPolicyAuthorization();
      if (!sameBinding(current?.binding ?? null, next?.binding ?? null))
        policyCandidateRef.current = null;
      sessionGenerationRef.current += 1;
      setAuthSessionGeneration(sessionGenerationRef.current);
      authorizationContextRequestRef.current += 1;
      contextBootstrapRequestGenerationRef.current = authorizationContextRequestRef.current;
      authorizationContextRef.current = null;
      authorizationContextErrorRef.current = null;
      policyAccessRef.current = null;
      setAuthorizationContextState(null);
      setAuthorizationContextLoading(false);
      setAuthorizationContextError(null);
    }
    sessionRef.current = next;
    setSessionState(next);
  }, [cancelPolicyAuthorization]);
  const isActiveSession = useCallback((expected: AuthSession) => sessionRef.current === expected, []);

  /** Replaces every held session with `next` (or none) and activates it. */
  const setSession = useCallback((next: AuthSession | null) => {
    // Forget every held session and every pending silent sign-in. Used for sign-out
    // and for a fresh unbound sign-in.
    cancelPolicyAuthorization();
    const hadUnboundSession = sessionsRef.current.has('');
    sessionsRef.current.clear();
    if (hadUnboundSession) notifyUnboundSessionChange();
    ensureRef.current.clear();
    consentSessionModeRef.current = null;
    if (next) {
      sessionsRef.current.set(bindingKey(next.binding), next);
      if (!next.binding) notifyUnboundSessionChange();
    }
    policyAccessRef.current = null;
    policyCandidateRef.current = null;
    setActiveSession(next);
  }, [cancelPolicyAuthorization, notifyUnboundSessionChange, setActiveSession]);

  /** Stores a new session for its binding; `mode` decides whether others are kept and it becomes active. */
  const storeSession = useCallback((stored: AuthSession, mode: SessionMode) => {
    if (mode === 'replace') {
      setSession(stored);
      return;
    }
    // Sessions kept side by side belong to the same signed-in person. A bound token
    // without an ID token keeps the name the active session already shows.
    const next = !stored.displayName && sessionRef.current?.displayName
      ? { ...stored, displayName: sessionRef.current.displayName }
      : stored;
    // A new token for the same binding replaces the old one in the map.
    sessionsRef.current.set(bindingKey(next.binding), next);
    if (!next.binding) notifyUnboundSessionChange();
    // In `store` mode we still activate when the active session is already bound to this
    // same run: the page is using that run, so it must switch to the newer token.
    if (mode === 'activate' || sessionRef.current?.binding && sameBinding(sessionRef.current.binding, next.binding))
      setActiveSession(next);
  }, [notifyUnboundSessionChange, setActiveSession, setSession]);

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
   * `rebind` adds a bound session next to the held ones; it keeps the current
   * session and the sign-in marker when the Broker needs the user. Returns true when
   * a session was stored.
   */
  const runAuthorization = useCallback(async (
    requestedBinding: AuthorizationBinding | null,
    popupOverride: Window | null | undefined,
    silent: boolean,
    rebind: SessionMode | null = null,
  ): Promise<boolean> => {
    // Without a valid client configuration no sign-in can work. A background bound
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
    // A bound sign-in while already signed in keeps the other sessions. A sign-in with
    // no binding, or with no session yet, replaces everything.
    const mode: SessionMode = rebind ?? (requestedBinding && sessionRef.current ? 'activate' : 'replace');
    // Background bound attempts do not touch the page's error, consent, or busy state,
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
          consentSessionModeRef.current = null;
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
              consentSessionModeRef.current = mode;
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

  /** Gets an unbound token without taking ownership of the interactive sign-in flow. */
  const ensurePlatformPolicySession = useCallback((signal?: AbortSignal): Promise<boolean> => {
    const waitForCaller = (
      promise: Promise<boolean>,
      attempt: PlatformPolicySilentAttempt | null = null,
    ): Promise<boolean> => {
      if (attempt) attempt.waiters += 1;
      const release = (cancelled: boolean) => {
        if (!attempt) return;
        attempt.waiters -= 1;
        if (cancelled && attempt.waiters === 0 &&
            policySilentAttemptRef.current === attempt)
          attempt.controller.abort();
      };
      if (!signal) return attempt
        ? promise.finally(() => release(false))
        : promise;
      if (signal.aborted) {
        release(true);
        return Promise.resolve(false);
      }
      return new Promise((resolve) => {
        let settled = false;
        const finish = (result: boolean, cancelled = false) => {
          if (settled) return;
          settled = true;
          signal.removeEventListener('abort', onAbort);
          release(cancelled);
          resolve(result);
        };
        const onAbort = () => finish(false, true);
        signal.addEventListener('abort', onAbort, { once: true });
        void promise.then(finish, () => finish(false));
        if (signal.aborted) finish(false);
      });
    };
    if (sessionsRef.current.has('')) return waitForCaller(Promise.resolve(true));
    if (signal?.aborted || configurationError) return Promise.resolve(false);
    if (policySilentPromiseRef.current)
      return waitForCaller(policySilentPromiseRef.current, policySilentAttemptRef.current);

    const config = defaultAuthorizationConfig();
    const transaction = transactionFor(null);
    const attempt: PlatformPolicySilentAttempt = {
      controller: new AbortController(),
      iframe: null,
      messageHandler: null,
      waiters: 0,
    };
    const activeGeneration = sessionGenerationRef.current;
    policySilentAttemptRef.current = attempt;

    const promiseRef: { current: Promise<boolean> | null } = { current: null };
    const promise = (async () => {
      let timeout: number | undefined;
      try {
        const challenge = await pkceChallenge(transaction.verifier);
        if (attempt.controller.signal.aborted) return false;
        const authorizeUrl = buildAuthorizeUrl(config, transaction, challenge, 'none');
        const iframe = document.createElement('iframe');
        attempt.iframe = iframe;
        iframe.title = 'Platform Policy sign-in';
        iframe.setAttribute('aria-hidden', 'true');
        iframe.tabIndex = -1;
        iframe.style.display = 'none';
        iframe.src = authorizeUrl;
        document.body.append(iframe);
        const callbackWindow = iframe.contentWindow;
        if (!callbackWindow) return false;

        const callbackOrigin = new URL(transaction.redirectUri).origin;
        const callback = await new Promise<AuthorizationCallbackMessage>((resolve, reject) => {
          const handler = (event: MessageEvent<unknown>) => {
            if (event.source !== callbackWindow ||
                event.origin !== callbackOrigin ||
                policySilentAttemptRef.current !== attempt)
              return;
            const message = parseAuthorizationCallbackMessage(event.data);
            if (!message || message.state !== transaction.state) return;
            resolve(message);
          };
          attempt.messageHandler = handler;
          window.addEventListener('message', handler);
          timeout = window.setTimeout(
            () => reject(new Error('Silent sign-in timed out.')),
            SILENT_AUTH_TIMEOUT_MS,
          );
          attempt.controller.signal.addEventListener(
            'abort',
            () => reject(new DOMException('Silent sign-in was cancelled.', 'AbortError')),
            { once: true },
          );
        });
        if (!('code' in callback) ||
            attempt.controller.signal.aborted ||
            policySilentAttemptRef.current !== attempt ||
            sessionGenerationRef.current !== activeGeneration)
          return false;

        const responseUrl = new URL(transaction.redirectUri);
        responseUrl.searchParams.set('state', callback.state);
        responseUrl.searchParams.set('code', callback.code);
        const result = parseAuthorizationResult(responseUrl.toString(), transaction.state);
        if (!result || 'error' in result) return false;
        const token = await exchangeAuthorizationCode(config, transaction, result.code);
        if (attempt.controller.signal.aborted ||
            policySilentAttemptRef.current !== attempt ||
            sessionGenerationRef.current !== activeGeneration)
          return false;
        const stored = sessionFromToken(token, null);
        if (!sessionsRef.current.has('')) storeSession(stored, 'store');
        return true;
      } catch {
        return false;
      } finally {
        if (timeout !== undefined) window.clearTimeout(timeout);
        if (attempt.messageHandler)
          window.removeEventListener('message', attempt.messageHandler);
        attempt.iframe?.remove();
        if (policySilentAttemptRef.current === attempt)
          policySilentAttemptRef.current = null;
        if (policySilentPromiseRef.current === promiseRef.current)
          policySilentPromiseRef.current = null;
      }
    })();
    promiseRef.current = promise;
    policySilentPromiseRef.current = promise;
    return waitForCaller(promise, attempt);
  }, [configurationError, storeSession]);

  /** Interactive sign-in for the unbound session or an exact project binding. */
  const authorize = useCallback(async (
    requestedBinding: AuthorizationBinding | null = null,
    popupOverride?: Window | null,
    activate = true,
  ) => {
    await runAuthorization(requestedBinding, popupOverride, false, activate ? null : 'store');
  }, [runAuthorization]);

  /** Returns a session for `binding`, reusing a held one or trying one silent sign-in. */
  const ensureSession = useCallback((binding: AuthorizationBinding | null, activate = true): Promise<boolean> => {
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
    // Restore the unbound session once on page load. The shell needs it on every page, and
    // a run page then gets its own run session silently (or asks the user for it).
    void runAuthorization(null, null, true);
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
      // A bound consent keeps the sessions already held.
      const mode = consentSessionModeRef.current
        ?? (transaction.binding && sessionRef.current ? 'activate' : 'replace');
      if (await consumeAuthorizationResponse(response.url, transaction, mode)) {
        consentSessionModeRef.current = null;
        popup?.close();
        return;
      }
      if (!approve) {
        throw new Error('Consent was declined. No Agentweaver session was created.');
      }
      if (!popup)
        throw new Error('Allow the sign-in popup to continue.');
      await authorize(transaction.binding, popup, mode !== 'store');
    } catch (reason) {
      popup?.close();
      setError(reason instanceof Error ? reason.message : 'Broker consent failed.');
      if (!approve) clearAuthorizationTransaction();
    } finally {
      if (!approve) {
        consentSessionModeRef.current = null;
        setConsent(null);
      }
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
    // Losing a bound session only removes that binding. Fall back to the unbound session
    // if the dropped one was active, and show an error only if nothing is left.
    sessionsRef.current.delete(bindingKey(current.binding));
    if (sessionRef.current === current) setActiveSession(sessionsRef.current.get('') ?? null);
    if (!sessionRef.current) setError(message);
  }, [setActiveSession, setSession]);

  /** Shares the Broker refresh itself; each caller decides what a failure removes. */
  const refreshSession = useCallback((current: AuthSession): Promise<AuthSession> => {
    const key = bindingKey(current.binding);
    const latest = sessionsRef.current.get(key) ?? null;
    if (latest !== current) {
      return latest
        ? Promise.resolve(latest)
        : Promise.reject(new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.'));
    }
    // Share an in-flight refresh so parallel 401s do not spend the refresh token twice.
    const inFlight = refreshPromisesRef.current.get(key);
    if (inFlight?.session === current) return inFlight.promise;
    const refreshToken = current.refreshToken;
    if (!refreshToken)
      return Promise.reject(new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.'));
    const promise = refreshBrokerToken(defaultAuthorizationConfig(), refreshToken).then((refreshed) => {
      const next = sessionFromToken(refreshed, current.binding);
      if (!next.refreshToken) next.refreshToken = refreshToken;
      if (!next.displayName) next.displayName = current.displayName;
      if (sessionsRef.current.get(key) !== current)
        return sessionsRef.current.get(key) ?? Promise.reject(
          new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.'),
        );
      sessionsRef.current.set(key, next);
      if (!next.binding) notifyUnboundSessionChange();
      if (sessionRef.current === current) setActiveSession(next, true);
      return next;
    });
    const activeRefresh = { session: current, promise };
    refreshPromisesRef.current.set(key, activeRefresh);
    const clearRefresh = () => {
      // Remove only our own entry; a newer refresh may already own the key.
      if (refreshPromisesRef.current.get(key) === activeRefresh)
        refreshPromisesRef.current.delete(key);
    };
    void promise.then(clearRefresh, clearRefresh);
    return promise;
  }, [notifyUnboundSessionChange, setActiveSession]);

  /** Preserves the established failure behavior for existing authentication callers. */
  const refresh = useCallback(async (current: AuthSession): Promise<AuthSession | null> => {
    try {
      return await refreshSession(current);
    } catch (reason) {
      if (sessionsRef.current.get(bindingKey(current.binding)) === current)
        dropSession(current, reason instanceof Error ? reason.message : 'Your sign-in expired. Sign in again.');
      return sessionsRef.current.get(bindingKey(current.binding)) ?? null;
    }
  }, [dropSession, refreshSession]);

  /** A Policy read can discard only its unbound session after a refresh failure. */
  const refreshPlatformPolicySession = useCallback(async (): Promise<PlatformPolicyRefreshResult> => {
    const result = (refreshed: boolean): PlatformPolicyRefreshResult => ({
      refreshed,
      authSessionGeneration: sessionGenerationRef.current,
      unboundSessionGeneration: unboundSessionGenerationRef.current,
    });
    const current = sessionsRef.current.get('') ?? null;
    if (!current) return result(false);
    try {
      await refreshSession(current);
      return result(true);
    } catch {
      if (sessionsRef.current.get('') === current) {
        sessionsRef.current.delete('');
        notifyUnboundSessionChange();
        if (sessionRef.current === current) setActiveSession(null);
      }
      return result(false);
    }
  }, [notifyUnboundSessionChange, refreshSession, setActiveSession]);

  useEffect(() => {
    if (!session) return;
    const delay = Math.max(0, session.expiresAt - Date.now() - 30_000);
    const timer = window.setTimeout(() => { void refresh(session); }, delay);
    return () => window.clearTimeout(timer);
  }, [refresh, session]);

  const apiCall = useCallback(async <T,>(
    operation: (accessToken: string, tenantSelector: string | null) => Promise<T>,
    requestedBinding: AuthorizationBinding | null = null,
  ): Promise<T> => {
    // Use the session held for this exact binding, never a different one.
    let current = sessionsRef.current.get(bindingKey(requestedBinding)) ?? null;
    if (!current) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        requestedBinding
          ? requestedBinding.runId
            ? 'Sign in for this exact project and run before continuing.'
            : 'Sign in for this exact project before continuing.'
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
    authorizationContextErrorRef.current = null;
    setAuthorizationContextState(null);
    setAuthorizationContextLoading(true);
    setAuthorizationContextError(null);
    if (!startingSession) {
      const reason = new GatewayError(401, { code: 'unauthorized' }, 'Sign in to resolve a tenant context.');
      authorizationContextErrorRef.current = reason;
      setAuthorizationContextError(reason);
      setAuthorizationContextLoading(false);
      return null;
    }

    const requestIdentity: { current: {
      accessToken: string;
      binding: AuthorizationBinding | null;
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
        issuer: result.issuer,
        actorId: result.actorId,
        tenantId: result.tenantId,
        membershipRevision: result.membershipRevision,
      };
      authorizationContextRef.current = {
        value: confirmedContext,
        accessToken: identity.accessToken,
        binding: identity.binding,
        sessionGeneration: identity.sessionGeneration,
      };
      projectCredentialOperations.retainIdentity({
        issuer: confirmedContext.issuer,
        actorId: confirmedContext.actorId,
        tenantId: confirmedContext.tenantId,
      });
      setAuthorizationContextState(confirmedContext);
      return confirmedContext;
    } catch (reason) {
      if (requestGeneration === authorizationContextRequestRef.current) {
        authorizationContextRef.current = null;
        authorizationContextErrorRef.current = reason;
        setAuthorizationContextState(null);
        setAuthorizationContextError(reason);
      }
      return null;
    } finally {
      if (requestGeneration === authorizationContextRequestRef.current)
        setAuthorizationContextLoading(false);
    }
  }, [apiCall, projectCredentialOperations]);

  const apiCallOnce = useCallback(async <T,>(
    operation: (accessToken: string, tenantSelector: string) => Promise<T>,
    requestedBinding: AuthorizationBinding,
    expectedIdentity?: {
      tenantId: string;
      actorId: string | null;
      issuer: string | null;
    },
  ): Promise<T> => {
    if (!requestedBinding?.projectId || requestedBinding.runId) {
      throw new GatewayError(
        403,
        { code: 'forbidden' },
        'A project-only session is required for this action.',
      );
    }
    let current = sessionsRef.current.get(bindingKey(requestedBinding)) ?? null;
    if (!current ||
        !sameBinding(current.binding, requestedBinding) ||
        current.binding?.runId) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        'Sign in for this exact project before continuing.',
      );
    }
    if (current.expiresAt <= Date.now() + 5_000) {
      current = await refresh(current);
      if (!current) {
        throw new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.');
      }
    }

    const existingConfirmed = authorizationContextRef.current;
    const needsContextConfirmation =
      !existingConfirmed ||
      existingConfirmed.accessToken !== current.accessToken ||
      existingConfirmed.sessionGeneration !== sessionGenerationRef.current ||
      !sameBinding(existingConfirmed.binding, current.binding) ||
      !sameBinding(current.binding, requestedBinding);

    if (needsContextConfirmation) {
      if (sessionRef.current !== current) {
        throw new GatewayError(
          401,
          { code: 'unauthorized' },
          'Your project session changed before the request was sent.',
        );
      }
      const targetTenant = expectedIdentity?.tenantId ?? existingConfirmed?.value.tenantId;
      const reconfirmed = await resolveAuthorizationContext(targetTenant);
      if (!reconfirmed) {
        const contextError = authorizationContextErrorRef.current;
        if (contextError instanceof GatewayError) throw contextError;
        throw new GatewayError(
          401,
          { code: 'unauthorized' },
          'Could not confirm project authorization context.',
        );
      }
    }

    const active = sessionsRef.current.get(bindingKey(requestedBinding)) ?? null;
    const confirmed = authorizationContextRef.current;
    if (!active ||
        active.accessToken !== current.accessToken ||
        !sameBinding(active.binding, requestedBinding) ||
        active.binding?.runId ||
        sessionRef.current !== active ||
        !confirmed ||
        confirmed.accessToken !== active.accessToken ||
        confirmed.sessionGeneration !== sessionGenerationRef.current ||
        !sameBinding(confirmed.binding, active.binding) ||
        !confirmed.value.tenantId) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        'Your project session changed before the request was sent.',
      );
    }
    if (expectedIdentity && (
      confirmed.value.tenantId !== expectedIdentity.tenantId ||
      (expectedIdentity.actorId !== null && confirmed.value.actorId !== expectedIdentity.actorId) ||
      (expectedIdentity.issuer !== null && confirmed.value.issuer !== expectedIdentity.issuer)
    )) {
      throw new GatewayError(
        403,
        { code: 'forbidden' },
        'Your signed-in account or tenant changed before the request was sent.',
      );
    }

    return await operation(active.accessToken, confirmed.value.tenantId);
  }, [refresh, resolveAuthorizationContext]);

  const establishPlatformPolicyAccess = useCallback(async (
    tenantSelector: string,
    signal?: AbortSignal,
  ): Promise<PlatformPolicyIdentity> => {
    if (!tenantSelector ||
        tenantSelector.length > 256 ||
        /[\r\n]/.test(tenantSelector) ||
        tenantSelector.trim() !== tenantSelector)
      throw new GatewayError(400, { code: 'tenant_selector_required' },
        'Choose a tenant before reading Platform Policies.');
    if (signal?.aborted) throw new DOMException('Policy check was cancelled.', 'AbortError');

    const active = sessionRef.current;
    if (!active)
      throw new GatewayError(401, { code: 'unauthorized' }, 'Sign in to continue.');
    const activeGeneration = sessionGenerationRef.current;
    const cached = authorizationContextRef.current;
    let candidate: PlatformPolicyIdentity;
    if (cached &&
        cached.accessToken === active.accessToken &&
        cached.sessionGeneration === activeGeneration &&
        sameBinding(cached.binding, active.binding) &&
        cached.value.tenantId === tenantSelector) {
      candidate = {
        issuer: cached.value.issuer,
        actorId: cached.value.actorId,
        tenantId: cached.value.tenantId,
      };
    } else {
      const activeContext = await gatewayClient.getAuthorizationContext(
        active.accessToken,
        tenantSelector,
        signal,
      );
      if (signal?.aborted) throw new DOMException('Policy check was cancelled.', 'AbortError');
      if (sessionRef.current !== active || sessionGenerationRef.current !== activeGeneration)
        throw new GatewayError(401, { code: 'authorization_context_stale' },
          'The sign-in session changed while checking Platform Policy access.');
      if (!contextMatchesBinding(activeContext, active.binding) ||
          activeContext.tenantId !== tenantSelector)
        throw new GatewayError(403, { code: 'policy_context_mismatch' },
          'Platform Policy access was denied.');
      candidate = {
        issuer: activeContext.issuer,
        actorId: activeContext.actorId,
        tenantId: activeContext.tenantId,
      };
    }

    const previousCandidate = policyCandidateRef.current;
    if (previousCandidate &&
        sameBinding(previousCandidate.binding, active.binding) &&
        previousCandidate.identity.tenantId === tenantSelector &&
        (previousCandidate.identity.issuer !== candidate.issuer ||
         previousCandidate.identity.actorId !== candidate.actorId))
      throw new GatewayError(403, { code: 'policy_identity_changed' },
        'Platform Policy access was denied.');
    policyCandidateRef.current = {
      identity: candidate,
      binding: active.binding,
    };

    if (!await ensurePlatformPolicySession(signal)) {
      if (signal?.aborted) throw new DOMException('Policy check was cancelled.', 'AbortError');
      throw new GatewayError(401, { code: 'policy_sign_in_required' },
        'Sign in again to check Platform Policy access.');
    }
    const unbound = sessionsRef.current.get('') ?? null;
    if (!unbound || unbound.expiresAt <= Date.now() + 5_000)
      throw new GatewayError(401, { code: 'platform_policy_request_failed' },
        'Sign in again to check Platform Policy access.');
    const unboundGeneration = unboundSessionGenerationRef.current;
    let unboundContext: ProjectAuthorizationContextResponse;
    try {
      unboundContext = await gatewayClient.getAuthorizationContext(
        unbound.accessToken,
        tenantSelector,
        signal,
      );
    } catch (reason) {
      if (reason instanceof GatewayError && reason.status === 401)
        throw new GatewayError(401, { code: 'platform_policy_request_failed' },
          'Platform Policy data could not be loaded.');
      throw reason;
    }
    if (signal?.aborted) throw new DOMException('Policy check was cancelled.', 'AbortError');
    if (sessionRef.current !== active ||
        sessionGenerationRef.current !== activeGeneration ||
        sessionsRef.current.get('') !== unbound ||
        unboundSessionGenerationRef.current !== unboundGeneration)
      throw new GatewayError(401, { code: 'authorization_context_stale' },
        'The sign-in session changed while checking Platform Policy access.');
    if (unboundContext.boundProjectId !== null ||
        unboundContext.boundRunId !== null ||
        unboundContext.issuer !== candidate.issuer ||
        unboundContext.actorId !== candidate.actorId ||
        unboundContext.tenantId !== candidate.tenantId)
      throw new GatewayError(403, { code: 'policy_identity_mismatch' },
        'Platform Policy access was denied.');

    const identity = {
      issuer: candidate.issuer,
      actorId: candidate.actorId,
      tenantId: candidate.tenantId,
    };
    policyAccessRef.current = {
      identity,
      authorizationContext: unboundContext,
      activeSession: active,
      unboundSession: unbound,
      authSessionGeneration: activeGeneration,
      unboundSessionGeneration: unboundGeneration,
    };
    return identity;
  }, [ensurePlatformPolicySession]);

  const isCurrentPlatformPolicyAccess = useCallback((
    access: PlatformPolicyAccessSnapshot,
    tenantSelector: string,
  ): boolean => policyAccessRef.current === access &&
    access.identity.tenantId === tenantSelector &&
    sessionRef.current === access.activeSession &&
    sessionGenerationRef.current === access.authSessionGeneration &&
    sessionsRef.current.get('') === access.unboundSession &&
    unboundSessionGenerationRef.current === access.unboundSessionGeneration, []);

  const requirePlatformPolicyAccess = useCallback((tenantSelector: string): PlatformPolicyAccessSnapshot => {
    const access = policyAccessRef.current;
    if (access && isCurrentPlatformPolicyAccess(access, tenantSelector))
      return access;
    throw new GatewayError(401, { code: 'policy_access_expired' },
      'Platform Policy access must be checked again.');
  }, [isCurrentPlatformPolicyAccess]);

  const getPlatformPolicyRevisionHistory = useCallback(async (
    tenantSelector: string,
    options: { cursor?: string | null; signal?: AbortSignal } = {},
  ): Promise<PlatformPolicyRevisionPage> => {
    const access = requirePlatformPolicyAccess(tenantSelector);
    try {
      const page = await gatewayClient.getPlatformPolicyRevisionHistory(
        access.unboundSession.accessToken,
        tenantSelector,
        options,
      );
      if (!isCurrentPlatformPolicyAccess(access, tenantSelector))
        throw new GatewayError(401, { code: 'policy_access_expired' },
          'Platform Policy access must be checked again.');
      return page;
    } catch (reason) {
      if (reason instanceof GatewayError &&
          reason.status === 403 &&
          policyAccessRef.current === access)
        policyAccessRef.current = null;
      throw reason;
    }
  }, [isCurrentPlatformPolicyAccess, requirePlatformPolicyAccess]);

  const readPlatformPolicyRevision = useCallback(async (
    tenantSelector: string,
    revision: number,
    signal?: AbortSignal,
  ): Promise<PlatformPolicyRevisionReadResult> => {
    const access = requirePlatformPolicyAccess(tenantSelector);
    try {
      const result = await gatewayClient.readPlatformPolicyRevision(
        access.unboundSession.accessToken,
        revision,
        tenantSelector,
        signal,
      );
      if (!isCurrentPlatformPolicyAccess(access, tenantSelector))
        throw new GatewayError(401, { code: 'policy_access_expired' },
          'Platform Policy access must be checked again.');
      return result;
    } catch (reason) {
      if (reason instanceof GatewayError &&
          reason.status === 403 &&
          policyAccessRef.current === access)
        policyAccessRef.current = null;
      throw reason;
    }
  }, [isCurrentPlatformPolicyAccess, requirePlatformPolicyAccess]);

  const establishProjectPolicyAccess = useCallback(async (
    tenantSelector: string,
    selectedProjectId: string | null,
    signal?: AbortSignal,
  ): Promise<ProjectPolicyAccess> => {
    const identity = await establishPlatformPolicyAccess(tenantSelector, signal);
    const access = requirePlatformPolicyAccess(tenantSelector);
    const projects = await gatewayClient.listProjects(
      access.unboundSession.accessToken,
      tenantSelector,
    );
    if (!Array.isArray(projects) || projects.some((project) =>
      !project || typeof project !== 'object' ||
      typeof project.projectId !== 'string' ||
      !Array.isArray(project.effectiveRoles)))
      throw new GatewayError(502, { code: 'project_policy_response_invalid' },
        'Project Policy access could not be checked.');
    if (!isCurrentPlatformPolicyAccess(access, tenantSelector))
      throw new GatewayError(401, { code: 'policy_access_expired' },
        'Project Policy access must be checked again.');

    const writeProjectsFor = (projectId: string) =>
      access.authorizationContext.effectiveAuthority.some((grant) =>
        grant.permissions.some((permission) => permission.permission === 'writeProjects') &&
        ((grant.resourceType === 'project' && grant.resourceId === projectId) ||
         (grant.resourceType === 'tenant' && grant.resourceId === tenantSelector)));
    const canAuthor = (project: ProjectSummary) =>
      writeProjectsFor(project.projectId) &&
      project.effectiveRoles.some((role) =>
        (role.resourceType === 'project' &&
          role.resourceId === project.projectId &&
          role.role === 'owner') ||
        (role.resourceType === 'tenant' &&
          role.resourceId === tenantSelector &&
          role.role === 'tenantAdmin'));
    const eligible = projects.filter(canAuthor);
    if (selectedProjectId) {
      const visible = projects.find((project) => project.projectId === selectedProjectId);
      if (!visible)
        throw new GatewayError(404, { code: 'project_policy_not_found' },
          'The selected Project is unavailable.');
      if (!canAuthor(visible))
        throw new GatewayError(403, { code: 'project_policy_access_denied' },
          'Project Policy access was denied.');
    }
    return { identity, projects: eligible, projectId: selectedProjectId };
  }, [
    establishPlatformPolicyAccess,
    isCurrentPlatformPolicyAccess,
    requirePlatformPolicyAccess,
  ]);

  const validatePlatformPolicyArtifact = useCallback(async (
    tenantSelector: string,
    artifact: PlatformPolicyArtifactRequest,
    signal?: AbortSignal,
  ): Promise<PlatformPolicyValidationResult> => {
    const access = requirePlatformPolicyAccess(tenantSelector);
    try {
      const result = await gatewayClient.validatePlatformPolicyArtifact(
        access.unboundSession.accessToken,
        tenantSelector,
        artifact,
        signal,
      );
      if (!isCurrentPlatformPolicyAccess(access, tenantSelector))
        throw new GatewayError(401, { code: 'policy_access_expired' },
          'Platform Policy access must be checked again.');
      return result;
    } catch (reason) {
      if (reason instanceof GatewayError &&
          reason.status === 403 &&
          policyAccessRef.current === access)
        policyAccessRef.current = null;
      throw reason;
    }
  }, [isCurrentPlatformPolicyAccess, requirePlatformPolicyAccess]);

  const validateProjectPolicyArtifact = useCallback(async (
    tenantSelector: string,
    projectId: string,
    artifact: PlatformPolicyArtifactRequest,
    signal?: AbortSignal,
  ): Promise<PlatformPolicyValidationResult> => {
    const access = requirePlatformPolicyAccess(tenantSelector);
    try {
      const result = await gatewayClient.validateProjectPolicyArtifact(
        access.unboundSession.accessToken,
        tenantSelector,
        projectId,
        artifact,
        signal,
      );
      if (!isCurrentPlatformPolicyAccess(access, tenantSelector))
        throw new GatewayError(401, { code: 'policy_access_expired' },
          'Project Policy access must be checked again.');
      return result;
    } catch (reason) {
      if (reason instanceof GatewayError &&
          (reason.status === 403 || reason.status === 404) &&
          policyAccessRef.current === access)
        policyAccessRef.current = null;
      throw reason;
    }
  }, [isCurrentPlatformPolicyAccess, requirePlatformPolicyAccess]);

  useEffect(() => () => cancelPolicyAuthorization(), [cancelPolicyAuthorization]);

  useEffect(() => {
    if (!session) return;
    const requestGeneration = contextBootstrapRequestGenerationRef.current;
    const bootstrap = window.setTimeout(() => {
      // A page's explicit tenant selection takes priority over this default lookup.
      if (requestGeneration === authorizationContextRequestRef.current)
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
    projectCredentialOperations.clear();
    setConsent(null);
    setError(null);
    forgetSignIn();
    clearRecentRuns();
    setRestoringSession(false);
  }, [projectCredentialOperations, setSession]);

  const getAuthorizationContextError = useCallback(() => authorizationContextErrorRef.current, []);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    authSessionGeneration,
    authSessionEpoch,
    isActiveSession,
    unboundSessionGeneration,
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
    apiCallOnce,
    projectCredentialOperations,
    establishPlatformPolicyAccess,
    getPlatformPolicyRevisionHistory,
    readPlatformPolicyRevision,
    validatePlatformPolicyArtifact,
    establishProjectPolicyAccess,
    validateProjectPolicyArtifact,
    refreshPlatformPolicySession,
    authorizationContext,
    authorizationContextLoading,
    authorizationContextError,
    getAuthorizationContextError,
    resolveAuthorizationContext,
  }), [
    apiCall,
    apiCallOnce,
    projectCredentialOperations,
    authSessionGeneration,
    authSessionEpoch,
    authorizationContext,
    authorizationContextError,
    authorizationContextLoading,
    getAuthorizationContextError,
    authorize,
    busy,
    configurationError,
    consent,
    decideConsent,
    ensureSession,
    establishPlatformPolicyAccess,
    error,
    getPlatformPolicyRevisionHistory,
    isActiveSession,
    readPlatformPolicyRevision,
    validatePlatformPolicyArtifact,
    establishProjectPolicyAccess,
    validateProjectPolicyArtifact,
    resolveAuthorizationContext,
    refreshPlatformPolicySession,
    session,
    signOut,
    unboundSessionGeneration,
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
