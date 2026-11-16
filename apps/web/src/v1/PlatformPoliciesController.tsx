import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { GatewayError } from './api';
import { useAuth } from './AuthContext';
import type { PlatformPolicyIdentity, PlatformPolicyRefreshResult } from './AuthContext';
import { PLATFORM_POLICY_SCOPE_ID } from './platformPolicyContracts';
import type {
  PlatformPolicyRevisionPage,
  PlatformPolicyRevisionReadResult,
  PlatformPolicyRevisionSummary,
} from './platformPolicyContracts';

type PolicyPageStatus = 'idle' | 'checking' | 'needsTenant' | 'ready' | 'denied' | 'unavailable';

interface PolicyRevisionSelection {
  policyId: string;
  snapshotRevision: number;
  revision: PlatformPolicyRevisionSummary;
}

type RetryRead =
  | { kind: 'revision'; selection: PolicyRevisionSelection }
  | { kind: 'continuation'; cursor: string; previousPage: PlatformPolicyRevisionPage };

interface ExpectedRefreshChange {
  previousAuthSessionGeneration: number;
  previousUnboundSessionGeneration: number;
  authSessionGeneration: number;
  unboundSessionGeneration: number;
  tenantId: string;
  issuer: string | null;
  actorId: string | null;
  contextTenantId: string | null;
}

export interface PlatformPoliciesControllerValue {
  tenantId: string;
  setTenantId: (tenantId: string) => void;
  status: PolicyPageStatus;
  authorized: boolean;
  history: PlatformPolicyRevisionPage | null;
  historyLoading: boolean;
  historyError: string | null;
  detail: PlatformPolicyRevisionReadResult | null;
  detailLoading: boolean;
  detailError: string | null;
  retry: () => void;
  viewRevision: (revision: PlatformPolicyRevisionSummary) => void;
  loadMore: () => void;
}

const PlatformPoliciesControllerContext = createContext<PlatformPoliciesControllerValue | null>(null);

function sameIdentity(left: PlatformPolicyIdentity, right: PlatformPolicyIdentity): boolean {
  return left.issuer === right.issuer &&
    left.actorId === right.actorId &&
    left.tenantId === right.tenantId;
}

function sameLineage(left: PlatformPolicyRevisionPage, right: PlatformPolicyRevisionPage): boolean {
  return left.scopeType === right.scopeType &&
    left.scopeId === right.scopeId &&
    left.policyId === right.policyId &&
    left.snapshotRevision === right.snapshotRevision;
}

function validateFirstPage(page: PlatformPolicyRevisionPage): void {
  if (page.scopeType !== 'platform' ||
      page.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      ((page.policyId === null) !== (page.snapshotRevision === null)))
    throw new Error('The saved Platform Policy snapshot is unavailable.');
  if (page.policyId === null) {
    if (page.items.length !== 0 || page.nextCursor !== null)
      throw new Error('The saved Platform Policy snapshot is unavailable.');
    return;
  }
  if (page.snapshotRevision === null ||
      !page.items.some((item) => item.revision === page.snapshotRevision))
    throw new Error('The saved Platform Policy snapshot is unavailable.');
}

function verifyRevision(
  result: PlatformPolicyRevisionReadResult,
  selection: PolicyRevisionSelection,
): void {
  if (result.scopeType !== 'platform' ||
      result.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      result.policyId !== selection.policyId ||
      result.revision !== selection.revision.revision ||
      result.contentSha256 !== selection.revision.contentSha256 ||
      result.artifact.contentSha256 !== selection.revision.contentSha256)
    throw new Error('The selected Platform Policy revision is unavailable.');
}

function safeFailure(reason: unknown): { status: PolicyPageStatus; message: string } {
  if (reason instanceof GatewayError && reason.status === 403)
    return { status: 'denied', message: 'You do not have permission to read Platform Policies.' };
  return { status: 'unavailable', message: 'Platform Policies could not load. Try again.' };
}

export function PlatformPoliciesControllerProvider({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const {
    establishPlatformPolicyAccess,
    getPlatformPolicyRevisionHistory,
    readPlatformPolicyRevision,
    refreshPlatformPolicySession,
  } = auth;
  const location = useLocation();
  const [tenantId, setTenantIdState] = useState(auth.authorizationContext?.tenantId ?? '');
  const [status, setStatus] = useState<PolicyPageStatus>('idle');
  const [authorized, setAuthorizedState] = useState(false);
  const [history, setHistoryState] = useState<PlatformPolicyRevisionPage | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [detail, setDetail] = useState<PlatformPolicyRevisionReadResult | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [visible, setVisible] = useState(() => document.visibilityState !== 'hidden');
  const requestGenerationRef = useRef(0);
  const identityEpochRef = useRef(0);
  const currentRequestRef = useRef<AbortController | null>(null);
  const policyIdentityRef = useRef<PlatformPolicyIdentity | null>(null);
  const historyRef = useRef<PlatformPolicyRevisionPage | null>(null);
  const tenantRef = useRef(tenantId);
  const tenantWasSelectedRef = useRef(false);
  const authorizedRef = useRef(false);
  const statusRef = useRef(status);
  const lastAuthSignatureRef = useRef('');
  const expectedRefreshChangeRef = useRef<ExpectedRefreshChange | null>(null);
  const latestAuthRef = useRef(auth);
  const lastActiveContextTenantRef = useRef(auth.authorizationContext?.tenantId ?? null);
  const lastActiveContextSessionGenerationRef = useRef(auth.authSessionGeneration);
  const focusAfterHideRef = useRef(false);
  const runCheckRef = useRef<((
    requestedTenant: string,
    expectedIdentity?: PlatformPolicyIdentity | null,
    retryCount?: number,
    retryRead?: RetryRead,
  ) => Promise<void>) | null>(null);

  useEffect(() => {
    latestAuthRef.current = auth;
  }, [auth]);

  const capturePolicyLinkFocus = useCallback(() => {
    const activeElement = document.activeElement;
    if (activeElement instanceof HTMLAnchorElement &&
        activeElement.getAttribute('href') === '/policies')
      focusAfterHideRef.current = true;
  }, []);

  const setAuthorized = useCallback((value: boolean) => {
    if (!value) capturePolicyLinkFocus();
    authorizedRef.current = value;
    setAuthorizedState(value);
  }, [capturePolicyLinkFocus]);

  const setHistory = useCallback((value: PlatformPolicyRevisionPage | null) => {
    historyRef.current = value;
    setHistoryState(value);
  }, []);

  const setPageStatus = useCallback((value: PolicyPageStatus) => {
    statusRef.current = value;
    setStatus(value);
  }, []);

  const clearPolicyState = useCallback((nextStatus: PolicyPageStatus) => {
    capturePolicyLinkFocus();
    identityEpochRef.current += 1;
    requestGenerationRef.current += 1;
    currentRequestRef.current?.abort();
    currentRequestRef.current = null;
    policyIdentityRef.current = null;
    setAuthorized(false);
    setHistory(null);
    setHistoryLoading(false);
    setHistoryError(null);
    setDetail(null);
    setDetailLoading(false);
    setDetailError(null);
    setPageStatus(nextStatus);
  }, [capturePolicyLinkFocus, setAuthorized, setHistory, setPageStatus]);

  const updateTenantId = useCallback((value: string, userSelected: boolean) => {
    const changed = tenantRef.current.trim() !== value.trim();
    tenantRef.current = value;
    setTenantIdState(value);
    if (userSelected && changed) tenantWasSelectedRef.current = true;
    if (changed)
      clearPolicyState(value.trim() ? 'idle' : 'needsTenant');
  }, [clearPolicyState]);

  const setTenantId = useCallback((value: string) => {
    updateTenantId(value, true);
  }, [updateTenantId]);

  const expectPolicyRefreshChange = useCallback((selectedTenant: string): ExpectedRefreshChange => {
    const current = latestAuthRef.current;
    const expected: ExpectedRefreshChange = {
      previousAuthSessionGeneration: current.authSessionGeneration,
      previousUnboundSessionGeneration: current.unboundSessionGeneration,
      authSessionGeneration: current.authSessionGeneration +
        (current.session?.binding === null ? 1 : 0),
      unboundSessionGeneration: current.unboundSessionGeneration + 1,
      tenantId: selectedTenant,
      issuer: current.authorizationContext?.issuer ?? null,
      actorId: current.authorizationContext?.actorId ?? null,
      contextTenantId: current.authorizationContext?.tenantId ?? null,
    };
    expectedRefreshChangeRef.current = expected;
    return expected;
  }, []);

  const isCurrentRecovery = useCallback((
    identityEpoch: number,
    requestGeneration: number,
    selectedTenant: string,
    expected: ExpectedRefreshChange,
    result: PlatformPolicyRefreshResult,
  ): boolean => {
    const current = latestAuthRef.current;
    const sameIdentity = !current.authorizationContext ||
      (current.authorizationContext.issuer === expected.issuer &&
       current.authorizationContext.actorId === expected.actorId &&
       current.authorizationContext.tenantId === expected.contextTenantId);
    const isCurrent = identityEpochRef.current === identityEpoch &&
      requestGenerationRef.current === requestGeneration &&
      tenantRef.current.trim() === selectedTenant &&
      result.authSessionGeneration === expected.authSessionGeneration &&
      result.unboundSessionGeneration === expected.unboundSessionGeneration &&
      (current.authSessionGeneration === expected.previousAuthSessionGeneration ||
       current.authSessionGeneration === expected.authSessionGeneration) &&
      (current.unboundSessionGeneration === expected.previousUnboundSessionGeneration ||
       current.unboundSessionGeneration === expected.unboundSessionGeneration) &&
      sameIdentity;
    if (!isCurrent && expectedRefreshChangeRef.current === expected)
      expectedRefreshChangeRef.current = null;
    return isCurrent;
  }, []);

  const isCurrentRequest = useCallback((
    identityEpoch: number,
    requestGeneration: number,
    controller: AbortController,
  ) => identityEpoch === identityEpochRef.current &&
    requestGeneration === requestGenerationRef.current &&
    currentRequestRef.current === controller &&
    !controller.signal.aborted, []);

  const runCheck = useCallback(async (
    requestedTenant: string,
    expectedIdentity: PlatformPolicyIdentity | null = null,
    retryCount = 0,
    retryRead?: RetryRead,
  ): Promise<void> => {
    const selectedTenant = requestedTenant.trim();
    updateTenantId(selectedTenant, false);
    if (!selectedTenant) {
      clearPolicyState('needsTenant');
      return;
    }
    if (statusRef.current === 'checking' &&
        tenantRef.current === selectedTenant &&
        currentRequestRef.current)
      return;

    currentRequestRef.current?.abort();
    const controller = new AbortController();
    currentRequestRef.current = controller;
    const identityEpoch = ++identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    policyIdentityRef.current = null;
    setAuthorized(false);
    setHistory(null);
    setHistoryLoading(false);
    setHistoryError(null);
    setDetail(null);
    setDetailLoading(false);
    setDetailError(null);
    setPageStatus('checking');

    let resolvedIdentity = expectedIdentity;
    let historyAuthorized = false;
    try {
      const identity = await establishPlatformPolicyAccess(selectedTenant, controller.signal);
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      if (expectedIdentity && !sameIdentity(identity, expectedIdentity))
        throw new GatewayError(403, { code: 'policy_identity_changed' },
          'Platform Policy access was denied.');
      resolvedIdentity = identity;

      const firstPage = await getPlatformPolicyRevisionHistory(
        selectedTenant,
        { signal: controller.signal },
      );
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      validateFirstPage(firstPage);
      policyIdentityRef.current = identity;
      setHistory(firstPage);
      setAuthorized(true);
      setPageStatus('ready');
      historyAuthorized = true;

      if (retryRead?.kind === 'revision') {
        if (firstPage.policyId !== retryRead.selection.policyId ||
            firstPage.snapshotRevision !== retryRead.selection.snapshotRevision)
          throw new Error('The selected Platform Policy revision is unavailable.');
        setDetailLoading(true);
        const result = await readPlatformPolicyRevision(
          selectedTenant,
          retryRead.selection.revision.revision,
          controller.signal,
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        verifyRevision(result, retryRead.selection);
        setDetail(result);
        setDetailLoading(false);
      } else if (retryRead?.kind === 'continuation') {
        const page = await getPlatformPolicyRevisionHistory(
          selectedTenant,
          { cursor: retryRead.cursor, signal: controller.signal },
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (!sameLineage(page, retryRead.previousPage))
          throw new Error('The Platform Policy history changed. Reload the page.');
        const seen = new Set(retryRead.previousPage.items.map((item) => item.revision));
        if (page.items.some((item) => seen.has(item.revision)))
          throw new Error('The Platform Policy history is unavailable.');
        setHistory({
          ...retryRead.previousPage,
          items: [...retryRead.previousPage.items, ...page.items],
          nextCursor: page.nextCursor,
        });
      }
    } catch (reason) {
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      if (reason instanceof GatewayError &&
          reason.status === 401 &&
          reason.code === 'platform_policy_request_failed' &&
          retryCount === 0) {
        clearPolicyState('checking');
        const recoveryIdentityEpoch = identityEpochRef.current;
        const recoveryRequestGeneration = requestGenerationRef.current;
        const recoveryTenant = tenantRef.current.trim();
        const expectedRefresh = expectPolicyRefreshChange(selectedTenant);
        const refreshResult = await refreshPlatformPolicySession();
        if (!isCurrentRecovery(
          recoveryIdentityEpoch,
          recoveryRequestGeneration,
          recoveryTenant,
          expectedRefresh,
          refreshResult,
        )) return;
        if (refreshResult.refreshed) {
          await runCheckRef.current?.(selectedTenant, resolvedIdentity, 1, retryRead);
          return;
        }
      }
      if (historyAuthorized &&
          retryRead?.kind === 'revision' &&
          !(reason instanceof GatewayError && (reason.status === 401 || reason.status === 403))) {
        setDetailError('The selected Platform Policy revision is unavailable. Try again.');
        return;
      }
      const failure = safeFailure(reason);
      clearPolicyState(failure.status);
      if (retryRead?.kind === 'revision' && failure.status !== 'denied')
        setDetailError('The selected Platform Policy revision is unavailable. Try again.');
    } finally {
      if (currentRequestRef.current === controller) {
        currentRequestRef.current = null;
        setDetailLoading(false);
      }
    }
  }, [
    establishPlatformPolicyAccess,
    getPlatformPolicyRevisionHistory,
    readPlatformPolicyRevision,
    refreshPlatformPolicySession,
    clearPolicyState,
    isCurrentRequest,
    expectPolicyRefreshChange,
    isCurrentRecovery,
    setAuthorized,
    setHistory,
    setPageStatus,
    updateTenantId,
  ]);

  useEffect(() => {
    runCheckRef.current = runCheck;
  }, [runCheck]);

  const retry = useCallback(() => {
    void runCheck(tenantRef.current);
  }, [runCheck]);

  const viewRevision = useCallback((revision: PlatformPolicyRevisionSummary) => {
    const currentPage = historyRef.current;
    const identity = policyIdentityRef.current;
    if (!authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !currentPage?.policyId ||
        currentPage.snapshotRevision === null ||
        !identity ||
        !currentPage.items.some((item) =>
          item.revision === revision.revision && item.contentSha256 === revision.contentSha256))
      return;

    const selection: PolicyRevisionSelection = {
      policyId: currentPage.policyId,
      snapshotRevision: currentPage.snapshotRevision,
      revision,
    };
    const controller = new AbortController();
    currentRequestRef.current?.abort();
    currentRequestRef.current = controller;
    const identityEpoch = identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    setDetail(null);
    setDetailError(null);
    setDetailLoading(true);
    void (async () => {
      try {
        const result = await readPlatformPolicyRevision(
          identity.tenantId,
          revision.revision,
          controller.signal,
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        verifyRevision(result, selection);
        setDetail(result);
      } catch (reason) {
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          clearPolicyState('denied');
        } else if (reason instanceof GatewayError &&
            reason.status === 401 &&
            reason.code === 'platform_policy_request_failed') {
          clearPolicyState('checking');
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            identity.tenantId,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed)
            await runCheckRef.current?.(identity.tenantId, identity, 1, { kind: 'revision', selection });
          else {
            setPageStatus('unavailable');
            setDetailError('The selected Platform Policy revision is unavailable. Try again.');
          }
        } else {
          setDetailError('The selected Platform Policy revision is unavailable. Try again.');
        }
      } finally {
        if (currentRequestRef.current === controller) {
          currentRequestRef.current = null;
          setDetailLoading(false);
        }
      }
    })();
  }, [
    clearPolicyState,
    expectPolicyRefreshChange,
    isCurrentRecovery,
    isCurrentRequest,
    readPlatformPolicyRevision,
    refreshPlatformPolicySession,
    setPageStatus,
  ]);

  const loadMore = useCallback(() => {
    const currentPage = historyRef.current;
    const identity = policyIdentityRef.current;
    if (!authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !currentPage?.nextCursor ||
        !currentPage.policyId ||
        currentPage.snapshotRevision === null ||
        !identity ||
        currentRequestRef.current)
      return;
    const cursor = currentPage.nextCursor;
    const controller = new AbortController();
    currentRequestRef.current = controller;
    const identityEpoch = identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    setHistoryLoading(true);
    setHistoryError(null);
    void (async () => {
      try {
        const page = await getPlatformPolicyRevisionHistory(
          identity.tenantId,
          { cursor, signal: controller.signal },
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (!sameLineage(page, currentPage))
          throw new Error('The Platform Policy history changed. Reload the page.');
        const seen = new Set(currentPage.items.map((item) => item.revision));
        if (page.items.some((item) => seen.has(item.revision)))
          throw new Error('The Platform Policy history is unavailable.');
        setHistory({
          ...currentPage,
          items: [...currentPage.items, ...page.items],
          nextCursor: page.nextCursor,
        });
      } catch (reason) {
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          clearPolicyState('denied');
          return;
        }
        if (reason instanceof GatewayError &&
            reason.status === 401 &&
            reason.code === 'platform_policy_request_failed') {
          clearPolicyState('checking');
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            identity.tenantId,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed) {
            await runCheckRef.current?.(identity.tenantId, identity, 1, {
              kind: 'continuation',
              cursor,
              previousPage: currentPage,
            });
            return;
          }
        }
        if (reason instanceof TypeError) {
          setHistoryError('Additional Platform Policy revisions could not load. Try again.');
          return;
        }
        clearPolicyState('unavailable');
      } finally {
        if (currentRequestRef.current === controller) {
          currentRequestRef.current = null;
          setHistoryLoading(false);
        }
      }
    })();
  }, [
    getPlatformPolicyRevisionHistory,
    expectPolicyRefreshChange,
    refreshPlatformPolicySession,
    clearPolicyState,
    isCurrentRecovery,
    isCurrentRequest,
    setHistory,
  ]);

  const authSignature = JSON.stringify([
    auth.authSessionGeneration,
    auth.unboundSessionGeneration,
    Boolean(auth.session),
    auth.authorizationContext?.issuer ?? '',
    auth.authorizationContext?.actorId ?? '',
    auth.authorizationContext?.tenantId ?? '',
    auth.authorizationContext?.membershipRevision ?? 0,
  ]);

  useEffect(() => {
    const activeTenant = auth.authorizationContext?.tenantId ?? null;
    if (lastActiveContextSessionGenerationRef.current !== auth.authSessionGeneration) {
      lastActiveContextSessionGenerationRef.current = auth.authSessionGeneration;
      lastActiveContextTenantRef.current = activeTenant;
      if (activeTenant && !tenantWasSelectedRef.current)
        updateTenantId(activeTenant, false);
      return;
    }
    if (!activeTenant) return;
    const previousTenant = lastActiveContextTenantRef.current;
    lastActiveContextTenantRef.current = activeTenant;
    if (previousTenant && previousTenant !== activeTenant)
      updateTenantId(activeTenant, false);
    else if (!previousTenant && !tenantWasSelectedRef.current)
      updateTenantId(activeTenant, false);
  }, [
    auth.authSessionGeneration,
    auth.authorizationContext?.tenantId,
    updateTenantId,
  ]);

  useEffect(() => {
    if (!lastAuthSignatureRef.current) {
      lastAuthSignatureRef.current = authSignature;
      const selectedTenant = tenantRef.current.trim() || auth.authorizationContext?.tenantId || '';
      if (auth.session && selectedTenant && location.pathname !== '/policies')
        void runCheck(selectedTenant);
      return;
    }
    if (lastAuthSignatureRef.current === authSignature) return;
    lastAuthSignatureRef.current = authSignature;
    const expectedRefresh = expectedRefreshChangeRef.current;
    const refreshNotification =
      expectedRefresh &&
      expectedRefresh.authSessionGeneration === auth.authSessionGeneration &&
      expectedRefresh.unboundSessionGeneration === auth.unboundSessionGeneration &&
      expectedRefresh.tenantId === tenantRef.current.trim() &&
      (!auth.authorizationContext ||
        (expectedRefresh.issuer === auth.authorizationContext.issuer &&
         expectedRefresh.actorId === auth.authorizationContext.actorId &&
         expectedRefresh.contextTenantId === auth.authorizationContext.tenantId));
    if (refreshNotification) {
      expectedRefreshChangeRef.current = null;
      return;
    }
    expectedRefreshChangeRef.current = null;
    const selectedTenant = tenantRef.current.trim() || auth.authorizationContext?.tenantId || '';
    clearPolicyState(auth.session ? 'checking' : 'idle');
    if (auth.session && selectedTenant)
      void runCheck(selectedTenant);
  }, [
    auth.authSessionGeneration,
    auth.authorizationContext,
    auth.authorizationContext?.actorId,
    auth.authorizationContext?.issuer,
    auth.authorizationContext?.membershipRevision,
    auth.authorizationContext?.tenantId,
    auth.session,
    authSignature,
    auth.unboundSessionGeneration,
    clearPolicyState,
    location.pathname,
    runCheck,
  ]);

  useEffect(() => {
    if (location.pathname !== '/policies') return;
    if (tenantRef.current)
      void runCheck(tenantRef.current);
    else
      clearPolicyState('needsTenant');
  }, [clearPolicyState, location.pathname, runCheck]);

  useEffect(() => {
    const onVisibilityChange = () => {
      const nextVisible = document.visibilityState !== 'hidden';
      setVisible(nextVisible);
      if (nextVisible) {
        const selectedTenant = tenantRef.current.trim();
        if (selectedTenant) void runCheck(selectedTenant);
      }
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, [runCheck]);

  useEffect(() => {
    if (!visible) return;
    const timer = window.setInterval(() => {
      const selectedTenant = tenantRef.current.trim();
      if (selectedTenant) void runCheck(selectedTenant);
    }, 60_000);
    return () => window.clearInterval(timer);
  }, [runCheck, visible]);

  useEffect(() => {
    if (!focusAfterHideRef.current || authorized) return;
    focusAfterHideRef.current = false;
    window.setTimeout(() => {
      const retry = document.querySelector<HTMLButtonElement>('[data-platform-policies-retry]');
      const target = retry && !retry.disabled
        ? retry
        : document.querySelector<HTMLElement>('[data-platform-policies-heading]') ??
        document.querySelector<HTMLElement>('main[aria-label="Main content"]');
      target?.focus();
    }, 0);
  }, [authorized, status]);

  const value = useMemo<PlatformPoliciesControllerValue>(() => ({
    tenantId,
    setTenantId,
    status,
    authorized,
    history,
    historyLoading,
    historyError,
    detail,
    detailLoading,
    detailError,
    retry,
    viewRevision,
    loadMore,
  }), [
    authorized,
    detail,
    detailError,
    detailLoading,
    history,
    historyError,
    historyLoading,
    loadMore,
    retry,
    setTenantId,
    status,
    tenantId,
    viewRevision,
  ]);

  return (
    <PlatformPoliciesControllerContext.Provider value={value}>
      {children}
    </PlatformPoliciesControllerContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function usePlatformPoliciesController(): PlatformPoliciesControllerValue {
  const value = useContext(PlatformPoliciesControllerContext);
  if (!value) throw new Error('Platform Policies requires its application controller.');
  return value;
}
