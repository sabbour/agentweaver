import { Button } from '@fluentui/react-components';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient, GatewayError } from './api';
import type { RunEgressPage, RunEgressRecord } from './contracts';
import { ErrorNotice, Loading, Panel } from './pageParts';
import { errorCode, errorMessage } from './errors';

const PAGE_SIZE = 50;
const COVERAGE_NOTICE = 'Coverage unavailable. This view shows stored records only.';

interface ConfirmedScope {
  key: string;
}

interface PanelState {
  routeScope: string;
  confirmedScope: ConfirmedScope | null;
  items: RunEgressRecord[];
  nextCursor: string | null;
  loaded: boolean;
  needsSignIn: boolean;
  loading: boolean;
  loadingMore: boolean;
  error: unknown;
  failedCursor: string | null;
}

function initialState(routeScope: string): PanelState {
  return {
    routeScope,
    confirmedScope: null,
    items: [],
    nextCursor: null,
    loaded: false,
    needsSignIn: false,
    loading: true,
    loadingMore: false,
    error: null,
    failedCursor: null,
  };
}

function scopeKey(actor: string, tenantId: string, projectId: string, runId: string): string {
  return JSON.stringify([actor, tenantId, projectId, runId]);
}

function terminalAccessError(reason: unknown): boolean {
  return reason instanceof GatewayError && (
    [401, 403, 404].includes(reason.status) ||
    reason.code === 'authorization_context_binding_mismatch' ||
    reason.code === 'authorization_context_actor_mismatch' ||
    reason.code === 'authorization_context_tenant_mismatch'
  );
}

function retryableError(reason: unknown): boolean {
  return !(reason instanceof GatewayError) ||
    reason.status === 408 ||
    reason.status === 429 ||
    reason.status >= 500;
}

/** Reads stored run egress with one exact project session and confirmed tenant per page. */
export function RunEgressPanel({ projectId, runId }: { projectId: string; runId: string }) {
  const {
    session,
    authSessionGeneration,
    unboundSessionGeneration,
    authorizationContext,
    apiCall,
    authorize,
    ensureSession,
    busy,
  } = useAuth();
  const intendedTenantId = authorizationContext?.tenantId ?? null;
  const intendedActor = authorizationContext?.actorId && authorizationContext.issuer
    ? JSON.stringify([authorizationContext.issuer, authorizationContext.actorId])
    : null;
  const routeScope = useMemo(
    () => JSON.stringify([
      projectId,
      runId,
      session?.accessToken ?? null,
      authSessionGeneration,
      unboundSessionGeneration,
      authorizationContext?.issuer ?? null,
      authorizationContext?.actorId ?? null,
      intendedTenantId,
    ]),
    [
      authSessionGeneration,
      authorizationContext?.actorId,
      authorizationContext?.issuer,
      intendedTenantId,
      projectId,
      runId,
      session?.accessToken,
      unboundSessionGeneration,
    ],
  );
  const [state, setState] = useState(() => initialState(routeScope));
  const stateRef = useRef(state);
  stateRef.current = state;
  const currentRouteScope = useRef(routeScope);
  currentRouteScope.current = routeScope;
  const currentViewScope = useRef(JSON.stringify([projectId, runId]));
  currentViewScope.current = JSON.stringify([projectId, runId]);
  const requestGeneration = useRef(0);
  const requestController = useRef<AbortController | null>(null);
  const startedRouteScope = useRef<string | null>(null);
  const appliedRouteScope = useRef(routeScope);
  const mounted = useRef(true);
  const signInPending = useRef(false);
  const projectBinding = useMemo(() => ({ projectId }), [projectId]);

  useLayoutEffect(() => {
    if (appliedRouteScope.current === routeScope) return;
    appliedRouteScope.current = routeScope;
    requestController.current?.abort();
    requestGeneration.current += 1;
    setState(initialState(routeScope));
  }, [routeScope]);

  const isCurrent = useCallback((generation: number, requestedRouteScope: string) =>
    requestGeneration.current === generation &&
    currentRouteScope.current === requestedRouteScope,
  []);

  const readPage = useCallback(async (
    cursor: string | null,
    existingScope: ConfirmedScope | null,
    generation: number,
    requestedRouteScope: string,
    signal: AbortSignal,
  ): Promise<
    | { page: RunEgressPage; scope: ConfirmedScope; tenantChanged: false }
    | { scope: ConfirmedScope; tenantChanged: true }
  > => {
    const currentRequest = () => isCurrent(generation, requestedRouteScope) && !signal.aborted;
    const staleRequest = () => new DOMException('The request is no longer current.', 'AbortError');
    return apiCall(async (token) => {
      if (!currentRequest()) throw staleRequest();
      const context = await gatewayClient.getAuthorizationContext(
        token,
        intendedTenantId ?? undefined,
        signal,
      );
      if (!currentRequest()) throw staleRequest();
      if (context.boundProjectId !== projectId || context.boundRunId !== null)
        throw new GatewayError(
          502,
          { code: 'authorization_context_binding_mismatch' },
          'The Projects owner returned authorization context for a different project.',
        );
      if (!context.tenantId ||
          (intendedTenantId !== null && context.tenantId !== intendedTenantId))
        throw new GatewayError(
          502,
          { code: 'authorization_context_tenant_mismatch' },
          'The Projects owner returned authorization context for a different tenant.',
        );
      const actor = JSON.stringify([context.issuer, context.actorId]);
      if (intendedActor !== null && actor !== intendedActor)
        throw new GatewayError(
          502,
          { code: 'authorization_context_actor_mismatch' },
          'The Projects owner returned authorization context for a different actor.',
        );
      if (!currentRequest()) throw staleRequest();
      const scope: ConfirmedScope = {
        key: scopeKey(actor, context.tenantId, projectId, runId),
      };
      const tenantChanged = existingScope !== null && existingScope.key !== scope.key;
      if (tenantChanged)
        return { scope, tenantChanged: true };
      return {
        page: await gatewayClient.readRunEgress(
          token,
          runId,
          { limit: PAGE_SIZE, cursor, signal },
          context.tenantId,
        ),
        scope,
        tenantChanged: false,
      };
    }, projectBinding);
  }, [
    apiCall,
    intendedActor,
    intendedTenantId,
    isCurrent,
    projectBinding,
    projectId,
    runId,
  ]);

  const load = useCallback(async (cursor: string | null = null) => {
    if (!mounted.current ||
        signInPending.current ||
        currentRouteScope.current !== routeScope) return;
    requestController.current?.abort();
    const controller = new AbortController();
    requestController.current = controller;
    const generation = ++requestGeneration.current;
    const requestedRouteScope = routeScope;
    if (cursor === null) startedRouteScope.current = requestedRouteScope;
    const existingScope = cursor === null ? null : stateRef.current.confirmedScope;
    setState((current) => {
      if (!isCurrent(generation, requestedRouteScope)) return current;
      return {
        ...(current.routeScope === routeScope ? current : initialState(routeScope)),
        loading: cursor === null,
        loadingMore: cursor !== null,
        error: null,
        failedCursor: cursor,
        needsSignIn: false,
        ...(cursor === null
          ? { items: [], nextCursor: null, loaded: false, confirmedScope: null }
          : {}),
      };
    });
    try {
      if (!await ensureSession(projectBinding, false)) {
        if (isCurrent(generation, requestedRouteScope))
          setState((current) => isCurrent(generation, requestedRouteScope)
            ? ({
                ...current,
                needsSignIn: true,
                loading: false,
                loadingMore: false,
                items: [],
                nextCursor: null,
                loaded: false,
                confirmedScope: null,
                failedCursor: null,
              })
            : current);
        return;
      }
      if (!isCurrent(generation, requestedRouteScope) || controller.signal.aborted) return;
      const result = await readPage(
        cursor,
        existingScope,
        generation,
        requestedRouteScope,
        controller.signal,
      );
      if (!isCurrent(generation, requestedRouteScope)) return;
      if (result.tenantChanged) {
        setState(initialState(routeScope));
        void loadRef.current(null);
        return;
      }
      setState((current) => {
        if (!isCurrent(generation, requestedRouteScope)) return current;
        const sameConfirmedScope = cursor === null || current.confirmedScope?.key === result.scope.key;
        if (!sameConfirmedScope) return initialState(routeScope);
        return {
          ...current,
          routeScope,
          confirmedScope: result.scope,
          items: cursor === null ? result.page.items : current.items.concat(result.page.items),
          nextCursor: result.page.nextCursor,
          loaded: true,
          loading: false,
          loadingMore: false,
          error: null,
          failedCursor: null,
        };
      });
    } catch (reason) {
      if (!isCurrent(generation, requestedRouteScope)) return;
      if (reason instanceof GatewayError &&
          reason.status === 400 &&
          reason.code === 'run_egress_cursor_invalid' &&
          cursor !== null) {
        void loadRef.current(null);
        return;
      }
      setState((current) => {
        if (!isCurrent(generation, requestedRouteScope)) return current;
        return {
          ...current,
          loading: false,
          loadingMore: false,
          error: reason,
          needsSignIn: reason instanceof GatewayError && reason.status === 401,
          ...(terminalAccessError(reason)
            ? {
                items: [],
                nextCursor: null,
                loaded: false,
                confirmedScope: null,
                failedCursor: null,
              }
            : {}),
        };
      });
    }
  }, [ensureSession, isCurrent, projectBinding, readPage, routeScope]);
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    // Strict Mode replays effects in development. One route scope receives one first-page read.
    if (busy || startedRouteScope.current === routeScope) return;
    void loadRef.current(null);
  }, [busy, load, routeScope]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      startedRouteScope.current = null;
      requestController.current?.abort();
      requestGeneration.current += 1;
    };
  }, []);

  const visible = state.routeScope === routeScope ? state : initialState(routeScope);
  const signIn = async () => {
    const requestedViewScope = JSON.stringify([projectId, runId]);
    signInPending.current = true;
    try {
      await authorize(projectBinding, undefined, false);
    } finally {
      signInPending.current = false;
    }
    if (!mounted.current || currentViewScope.current !== requestedViewScope) return;
    await loadRef.current(null);
  };

  return (
    <Panel title="Egress" description="Stored outbound network flows for this run, in server order.">
      {visible.needsSignIn && (
        <div className="v1-stack">
          <p>Sign in for this project to read stored egress records.</p>
          <Button appearance="primary" disabled={busy} onClick={() => void signIn()}>
            Sign in for this project
          </Button>
        </div>
      )}
      {!visible.needsSignIn && visible.error != null && (
        <div className="v1-stack">
          <ErrorNotice code={errorCode(visible.error)}>{errorMessage(visible.error)}</ErrorNotice>
          {retryableError(visible.error) && (
            <Button
              appearance="secondary"
              disabled={visible.loading || visible.loadingMore}
              onClick={() => void load(visible.failedCursor)}
            >
              Retry
            </Button>
          )}
        </div>
      )}
      {visible.needsSignIn ? null : visible.loading ? (
        <Loading label="Loading stored egress" />
      ) : (
        <>
          {visible.loaded && <p className="v1-muted">{COVERAGE_NOTICE}</p>}
          {visible.loaded && visible.items.length === 0 && <p className="v1-muted">No stored records</p>}
          {visible.items.length > 0 && (
            <div
              className="v1-table-wrap v1-egress-table-wrap"
              role="region"
              aria-label="Stored run egress records"
              tabIndex={0}
            >
              <table className="v1-table" aria-label="Stored run egress">
                <thead>
                  <tr>
                    <th>Host</th>
                    <th>Address</th>
                    <th>Port</th>
                    <th>Verdict</th>
                    <th>First seen</th>
                    <th>Last seen</th>
                    <th>Count</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.items.map((item, index) => (
                    <tr key={`${item.host ?? ''}:${item.address ?? ''}:${item.port}:${item.verdict}:${item.firstSeen}:${index}`}>
                      <td>{item.host ?? '—'}</td>
                      <td>{item.address ?? '—'}</td>
                      <td>{item.port}</td>
                      <td>{item.verdict}</td>
                      <td className="v1-egress-timestamp"><time dateTime={item.firstSeen}>{item.firstSeen}</time></td>
                      <td className="v1-egress-timestamp"><time dateTime={item.lastSeen}>{item.lastSeen}</time></td>
                      <td>{item.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {visible.error == null && visible.nextCursor !== null && (
            <Button
              appearance="secondary"
              disabled={visible.loadingMore}
              onClick={() => void load(visible.nextCursor)}
            >
              {visible.loadingMore ? 'Loading…' : 'Load more'}
            </Button>
          )}
        </>
      )}
    </Panel>
  );
}
