import { Button } from '@fluentui/react-components';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type { PlatformEgressDestination } from './contracts';
import { ErrorNotice, Loading } from './pageParts';

const TITLE = 'Platform-wide provider host requests';
const DESCRIPTION = 'Requests from all tenants that Environment received. A request does not grant network access.';

export function PlatformProviderHostRequests() {
  const { session, authorizationContext, authSessionEpoch, authSessionGeneration, unboundSessionGeneration } = useAuth();
  const heading = useRef<HTMLSpanElement>(null);
  const removedFocusedControl = useRef(false);
  const previousSession = useRef({ epoch: authSessionEpoch, generation: authSessionGeneration });
  const recordFocusedRemoval = useCallback(() => { removedFocusedControl.current = true; }, []);
  const context = session && !session.binding && authorizationContext?.tenantId ? authorizationContext : null;
  const key = context && JSON.stringify([
    context.issuer, context.actorId, context.tenantId, authSessionEpoch, authSessionGeneration, unboundSessionGeneration,
  ]);

  useLayoutEffect(() => {
    const previous = previousSession.current;
    previousSession.current = { epoch: authSessionEpoch, generation: authSessionGeneration };
    const renewed = previous.epoch === authSessionEpoch && previous.generation !== authSessionGeneration;
    if (renewed && removedFocusedControl.current && document.activeElement === document.body)
      heading.current?.focus();
    removedFocusedControl.current = false;
  }, [authSessionEpoch, authSessionGeneration, key]);

  return (
    <PageSection title={<span ref={heading} tabIndex={-1}>{TITLE}</span>} description={DESCRIPTION}>
      {session && context
        ? <HostRequestList key={key} session={session} tenantId={context.tenantId} onFocusedRemoval={recordFocusedRemoval} />
        : <p role="status">Use a platform sign-in with a confirmed tenant to view provider host requests.</p>}
    </PageSection>
  );
}

function HostRequestList({ session, tenantId, onFocusedRemoval }: {
  session: NonNullable<ReturnType<typeof useAuth>['session']>;
  tenantId: string;
  onFocusedRemoval: () => void;
}) {
  const { apiCall, isActiveSession } = useAuth();
  const [rows, setRows] = useState<PlatformEgressDestination[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<{ message: string; retryCursor: string | null; denied: boolean } | null>(null);
  const request = useRef<AbortController | null>(null);
  const pageAction = useRef<HTMLButtonElement>(null);
  const refreshAction = useRef<HTMLButtonElement>(null);
  const resultsStatus = useRef<HTMLDivElement>(null);

  const load = useCallback(async (cursor: string | null) => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    const { signal } = controller;
    const isCurrent = () => !signal.aborted && isActiveSession(session);
    setLoading(true);
    if (cursor === null) {
      setRows([]);
      setNextCursor(null);
    }

    try {
      const page = await apiCall((token, liveTenant) => {
        if (!isCurrent() || token !== session.accessToken) return Promise.resolve(null);
        if (liveTenant !== tenantId)
          throw new GatewayError(403, { code: 'authorization_context_tenant_mismatch' }, 'The confirmed tenant changed.');
        return gatewayClient.listPlatformEgressDestinations(token, tenantId, cursor, signal)
          .catch((reason: unknown) => {
            // A queue 401 must not refresh the shared session and restart the queue.
            if (reason instanceof GatewayError && reason.status === 401) return reason;
            throw reason;
          });
      }, null);
      if (!isCurrent() || !page) return;
      if (page instanceof GatewayError) throw page;
      if (page.nextCursor === null && document.activeElement === pageAction.current)
        resultsStatus.current?.focus();
      setFailure(null);
      setRows((prior) => cursor === null ? page.destinations : [...prior, ...page.destinations]);
      setNextCursor(page.nextCursor);
    } catch (reason) {
      if (!isCurrent()) return;
      const status = reason instanceof GatewayError ? reason.status : null;
      const tenantChanged = reason instanceof GatewayError && reason.code === 'authorization_context_tenant_mismatch';
      const denied = status === 401 || status === 403;
      if (denied) {
        if (document.activeElement === pageAction.current) refreshAction.current?.focus();
        setRows([]);
        setNextCursor(null);
      }
      setFailure({
        message: tenantChanged ? 'Confirm your tenant to view provider host requests.'
          : status === 401 ? 'Sign in to view provider host requests.'
          : status === 403 ? 'Only platform administrators can view provider host requests.'
          : 'Couldn’t verify provider host requests',
        retryCursor: cursor,
        denied,
      });
    } finally {
      if (isCurrent()) setLoading(false);
    }
  }, [apiCall, isActiveSession, session, tenantId]);

  useLayoutEffect(() => () => {
    const focused = document.activeElement;
    if (focused && (focused === pageAction.current || focused === refreshAction.current))
      onFocusedRemoval();
    request.current?.abort();
  }, [onFocusedRemoval]);
  useEffect(() => {
    const timer = window.setTimeout(() => void load(null), 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  return (
    <>
      <div className="v1-actions">
        <Button ref={refreshAction} aria-label="Refresh provider host requests" onClick={() => void load(null)}>Refresh</Button>
      </div>
      {loading && <Loading label="Loading provider host requests" />}
      {!loading && failure && <ErrorNotice>{failure.message}</ErrorNotice>}
      <div ref={resultsStatus} role="status" tabIndex={-1}>
        {!loading && !failure && (rows.length > 0 ? `Provider host requests shown: ${rows.length}.`
          : nextCursor === null ? 'No provider host requests were returned.'
          : 'No requests were returned on this page.')}
      </div>
      {rows.length > 0 && (
        <div className="v1-table-wrap">
          <table className="v1-table" aria-label={TITLE}>
            <thead>
              <tr>
                {['Host', 'Port', 'Scope', 'Tenant ID', 'Project ID', 'Provider ID', 'Revision', 'Requested at']
                  .map((label) => <th key={label} scope="col">{label}</th>)}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.destinationId}>
                  <th scope="row">{row.host}</th>
                  <td>{row.port}</td>
                  <td>{row.scope === 'project' ? 'Project' : 'Platform'}</td>
                  <td>{row.tenantId}</td>
                  <td>{row.projectId ?? 'Not applicable'}</td>
                  <td>{row.providerId}</td>
                  <td>{row.providerRevision}</td>
                  <td><time dateTime={row.requestedAt}>{new Date(row.requestedAt).toLocaleString()}</time></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {(failure ? !failure.denied : nextCursor !== null) && (
        <Button ref={pageAction} disabledFocusable={loading}
          onClick={() => { if (!loading) void load(failure ? failure.retryCursor : nextCursor); }}>
          {failure ? 'Retry' : 'Load more'}
        </Button>
      )}
    </>
  );
}
