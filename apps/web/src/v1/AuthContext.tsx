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
  exchangeAuthorizationCode,
  isConsentPrompt,
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
import type { BrokerConsentPrompt, BrokerTokenResponse } from './contracts';
import { GatewayError } from './api';

interface AuthSession {
  accessToken: string;
  refreshToken?: string;
  expiresAt: number;
  binding: RunBinding | null;
}

interface AuthContextValue {
  session: AuthSession | null;
  busy: boolean;
  consent: BrokerConsentPrompt | null;
  error: string | null;
  configurationError: string | null;
  authorize: (binding?: RunBinding | null) => Promise<void>;
  decideConsent: (approve: boolean) => Promise<void>;
  signOut: () => void;
  apiCall: <T>(
    operation: (accessToken: string) => Promise<T>,
    binding?: RunBinding | null,
  ) => Promise<T>;
}

const AuthContext = createContext<AuthContextValue | null>(null);
const AUTH_TIMEOUT_MS = 10 * 60 * 1000;

function sameBinding(left: RunBinding | null, right: RunBinding | null): boolean {
  return left?.projectId === right?.projectId && left?.runId === right?.runId;
}

function transactionFor(binding: RunBinding | null): AuthorizationTransaction {
  return {
    state: randomState(),
    verifier: randomVerifier(),
    redirectUri: defaultAuthorizationConfig().redirectUri,
    binding,
  };
}

function sessionFromToken(token: BrokerTokenResponse, binding: RunBinding | null): AuthSession {
  return {
    accessToken: token.access_token,
    refreshToken: token.refresh_token,
    expiresAt: Date.now() + token.expires_in * 1000,
    binding,
  };
}

function cleanCallbackUrl(): void {
  const url = new URL(window.location.href);
  url.searchParams.delete('code');
  url.searchParams.delete('state');
  url.searchParams.delete('error');
  url.searchParams.delete('error_description');
  window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSessionState] = useState<AuthSession | null>(null);
  const [busy, setBusy] = useState(false);
  const [consent, setConsent] = useState<BrokerConsentPrompt | null>(null);
  const [error, setError] = useState<string | null>(null);
  const sessionRef = useRef<AuthSession | null>(null);
  const refreshPromiseRef = useRef<{
    session: AuthSession;
    promise: Promise<AuthSession | null>;
  } | null>(null);
  const transactionRef = useRef<AuthorizationTransaction | null>(null);
  const pollAbortRef = useRef<AbortController | null>(null);

  const configurationError = useMemo(() => {
    const missing = missingAuthConfiguration();
    return missing.length
      ? `Configure ${missing.join(', ')} before signing in.`
      : null;
  }, []);

  const setSession = useCallback((next: AuthSession | null) => {
    sessionRef.current = next;
    setSessionState(next);
  }, []);

  const finishCode = useCallback(async (code: string, transaction: AuthorizationTransaction) => {
    const token = await exchangeAuthorizationCode(
      defaultAuthorizationConfig(),
      transaction,
      code,
    );
    clearAuthorizationTransaction();
    transactionRef.current = null;
    setConsent(null);
    setError(null);
    setSession(sessionFromToken(token, transaction.binding));
  }, [setSession]);

  const consumeAuthorizationResponse = useCallback(async (
    responseUrl: string,
    transaction: AuthorizationTransaction,
  ): Promise<boolean> => {
    const result = parseAuthorizationResult(responseUrl, transaction.state);
    if (!result) return false;
    if ('error' in result) {
      throw new Error(result.description ?? `Identity Broker authorization failed: ${result.error}.`);
    }
    await finishCode(result.code, transaction);
    return true;
  }, [finishCode]);

  useEffect(() => {
    const current = new URL(window.location.href);
    if (!current.searchParams.has('code') && !current.searchParams.has('error')) return;
    let transaction: AuthorizationTransaction | null = transactionRef.current;
    try {
      transaction ??= readAuthorizationTransaction();
    } catch {
      transaction = null;
    }
    cleanCallbackUrl();
    if (!transaction) {
      setError('The sign-in response has no matching in-progress PKCE transaction. Start sign-in again.');
      return;
    }
    setBusy(true);
    void consumeAuthorizationResponse(current.toString(), transaction)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Sign-in failed.'))
      .finally(() => setBusy(false));
  }, [consumeAuthorizationResponse, setBusy]);

  useEffect(() => () => pollAbortRef.current?.abort(), []);

  const authorize = useCallback(async (
    requestedBinding: RunBinding | null = null,
    popupOverride?: Window | null,
  ) => {
    if (configurationError) {
      setError(configurationError);
      return;
    }
    const config = defaultAuthorizationConfig();
    setError(null);
    setConsent(null);
    setSession(null);
    setBusy(true);
    pollAbortRef.current?.abort();

    let popup: Window | null = null;
    try {
      popup = popupOverride === undefined
        ? window.open('about:blank', 'agentweaver-identity', 'popup,width=560,height=720')
        : popupOverride;
      const transaction = transactionFor(requestedBinding);
      transactionRef.current = transaction;
      storeAuthorizationTransaction(transaction);
      const challenge = await pkceChallenge(transaction.verifier);
      const authorizeUrl = buildAuthorizeUrl(config, transaction, challenge);
      if (popup) popup.location.replace(authorizeUrl);
      else {
        window.location.assign(authorizeUrl);
        return;
      }

      const startedAt = Date.now();
      while (Date.now() - startedAt < AUTH_TIMEOUT_MS) {
        if (popup.closed) throw new Error('Identity Broker sign-in was closed before it completed.');
        const abortController = new AbortController();
        pollAbortRef.current = abortController;
        const timeout = window.setTimeout(() => abortController.abort(), 2500);
        try {
          const response = await fetch(authorizeUrl, {
            credentials: 'include',
            cache: 'no-store',
            redirect: 'follow',
            signal: abortController.signal,
            headers: { Accept: 'application/json' },
          });
          if (await consumeAuthorizationResponse(response.url, transaction)) {
            popup.close();
            return;
          }
          if (!response.ok) {
            const problem = await response.json().catch(() => null) as {
              error?: string;
              error_description?: string;
            } | null;
            throw new Error(
              problem?.error_description ??
                (problem?.error
                  ? `Identity Broker authorization failed: ${problem.error}.`
                  : `Identity Broker authorization failed with HTTP ${response.status}.`),
            );
          }
          const result: unknown = await response.json().catch(() => null);
          if (isConsentPrompt(result)) {
            if (result.client_id !== config.clientId ||
                result.requested_scopes.some((scope) => !config.scopes.includes(scope))) {
              throw new Error('The Broker consent response does not match this registered client and its configured scopes.');
            }
            popup.close();
            setConsent(result);
            return;
          }
          const body = result as { error?: string; error_description?: string } | null;
          if (body?.error)
            throw new Error(body.error_description ?? `Identity Broker authorization failed: ${body.error}.`);
        } catch (reason) {
          if (reason instanceof Error && !/aborted|load failed|failed to fetch/i.test(reason.message))
            throw reason;
        } finally {
          window.clearTimeout(timeout);
          if (pollAbortRef.current === abortController) pollAbortRef.current = null;
        }
        await new Promise((resolve) => window.setTimeout(resolve, 500));
      }
      throw new Error('Identity Broker sign-in timed out. You can try again.');
    } catch (reason) {
      popup?.close();
      setError(reason instanceof Error ? reason.message : 'Identity Broker sign-in failed.');
      clearAuthorizationTransaction();
      transactionRef.current = null;
    } finally {
      setBusy(false);
    }
  }, [configurationError, consumeAuthorizationResponse, setSession]);

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
      if (await consumeAuthorizationResponse(response.url, transaction)) {
        popup?.close();
        return;
      }
      if (!approve) {
        throw new Error('Consent was declined. No Agentweaver session was created.');
      }
      if (!popup)
        throw new Error('Allow the Identity Broker sign-in popup to continue.');
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

  const refresh = useCallback((current: AuthSession): Promise<AuthSession | null> => {
    const latest = sessionRef.current;
    if (latest !== current) {
      return Promise.resolve(latest && sameBinding(latest.binding, current.binding) ? latest : null);
    }
    if (refreshPromiseRef.current?.session === current)
      return refreshPromiseRef.current.promise;
    const refreshToken = current.refreshToken;
    if (!refreshToken) {
      setSession(null);
      setError('Your Broker session expired. Sign in again to continue.');
      return Promise.resolve(null);
    }
    const promise = (async (): Promise<AuthSession | null> => {
      try {
        const refreshed = await refreshBrokerToken(defaultAuthorizationConfig(), refreshToken);
        const next = sessionFromToken(refreshed, current.binding);
        if (!next.refreshToken) next.refreshToken = refreshToken;
        if (sessionRef.current !== current) {
          const replacement = sessionRef.current;
          return replacement && sameBinding(replacement.binding, current.binding) ? replacement : null;
        }
        setSession(next);
        return next;
      } catch (reason) {
        if (sessionRef.current !== current) {
          const replacement = sessionRef.current;
          return replacement && sameBinding(replacement.binding, current.binding) ? replacement : null;
        }
        setSession(null);
        setError(reason instanceof Error ? reason.message : 'Your Broker session expired. Sign in again.');
        return null;
      }
    })();
    const activeRefresh = { session: current, promise };
    refreshPromiseRef.current = activeRefresh;
    void promise.finally(() => {
      if (refreshPromiseRef.current === activeRefresh)
        refreshPromiseRef.current = null;
    });
    return promise;
  }, [setSession]);

  useEffect(() => {
    if (!session) return;
    const delay = Math.max(0, session.expiresAt - Date.now() - 30_000);
    const timer = window.setTimeout(() => { void refresh(session); }, delay);
    return () => window.clearTimeout(timer);
  }, [refresh, session]);

  const apiCall = useCallback(async <T,>(
    operation: (accessToken: string) => Promise<T>,
    requestedBinding: RunBinding | null = null,
  ): Promise<T> => {
    let current = sessionRef.current;
    if (!current || !sameBinding(current.binding, requestedBinding)) {
      throw new GatewayError(
        401,
        { code: 'unauthorized' },
        requestedBinding
          ? 'Sign in for this exact project and run before continuing.'
          : 'Sign in through the Identity Broker to continue.',
      );
    }
    if (current.expiresAt <= Date.now() + 5_000) {
      current = await refresh(current);
      if (!current) throw new GatewayError(401, { code: 'unauthorized' }, 'Your Broker session expired.');
    }
    try {
      return await operation(current.accessToken);
    } catch (reason) {
      if (!(reason instanceof GatewayError) || reason.status !== 401) throw reason;
      current = await refresh(current);
      if (!current) throw reason;
      return await operation(current.accessToken);
    }
  }, [refresh]);

  const signOut = useCallback(() => {
    pollAbortRef.current?.abort();
    clearAuthorizationTransaction();
    transactionRef.current = null;
    setSession(null);
    setConsent(null);
    setError(null);
  }, [setSession]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    busy,
    consent,
    error,
    configurationError,
    authorize,
    decideConsent,
    signOut,
    apiCall,
  }), [apiCall, authorize, busy, configurationError, consent, decideConsent, error, session, signOut]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used within AuthProvider.');
  return value;
}
