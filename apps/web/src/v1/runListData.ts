/**
 * Data adapters for the shared run list. `useBrowserRunList` reads runs the user
 * opened in this browser. `useGatewayRunList` reads the project run list
 * (`GET /api/v1/projects/{projectId}/runs`) and returns the same
 * `RunListData` shape, so callers keep the same layout.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectRunPage, ProjectRunStateFilter, ProjectRunSummary } from './contracts';
import { forgetRun, readRecentRuns } from './recentRuns';

/** One row in a run list, independent of where the data came from. */
export interface RunListItem {
  projectId: string;
  runId: string;
  executionState: string | null;
  /** Time shown in the row and the meaning of that time. */
  time: { label: string; value: string };
  /** Run title stored by the start request. Null or absent shows the run ID. */
  title?: string | null;
  /** Extra short facts shown under the title, such as the pull request state. */
  notes?: string[];
}

/** Rows plus how to remove one, for the active data adapter. */
export interface RunListData {
  items: RunListItem[];
  /** Removes a row from the source when the source supports it. */
  remove?: (item: RunListItem) => void;
  /** Reads the source again. */
  reload: () => void;
  /** `browser` rows carry the state seen when last opened; `service` rows carry the current state. Defaults to `browser`. */
  stateSource?: 'browser' | 'service';
  /** True until the first read finishes. */
  loading?: boolean;
  /** Last read failure, if any. */
  error?: unknown;
  /** Reads the next page when the source has one. */
  loadMore?: () => void;
}

/**
 * Reads the browser-local run list for the signed-in tenant. Pass a project ID
 * to limit rows to one project.
 */
export function useBrowserRunList(projectId?: string): RunListData {
  const { authorizationContext } = useAuth();
  const tenantId = authorizationContext?.tenantId;
  // localStorage does not notify React of changes, so a generation counter
  // forces the list to be read again after remove or reload.
  const [generation, setGeneration] = useState(0);
  const remove = useCallback((item: RunListItem) => {
    // Without a tenant there is no storage key, so there is nothing to remove.
    if (!tenantId) return;
    forgetRun(tenantId, item.projectId, item.runId);
    setGeneration((value) => value + 1);
  }, [tenantId]);
  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const items = useMemo(() => {
    // `generation < 0` is never true. Reading `generation` here makes it a real
    // dependency, so the memo re-reads storage when it changes.
    if (!tenantId || generation < 0) return [];
    return readRecentRuns(tenantId, projectId).map((run) => ({
      projectId: run.projectId,
      runId: run.runId,
      executionState: run.executionState,
      time: { label: 'Last opened', value: run.lastOpenedAt },
    }));
  }, [tenantId, projectId, generation]);
  return { items, remove, reload };
}

/** Maps one Gateway run summary to a run list row. */
export function gatewayRunListItem(projectId: string, run: ProjectRunSummary): RunListItem {
  return {
    projectId,
    runId: run.runId,
    executionState: run.state,
    title: run.title,
    // A finished run shows when it ended. A run still going shows its last update.
    time: run.completedAt
      ? { label: 'Ended', value: run.completedAt }
      : { label: 'Updated', value: run.updatedAt },
  };
}

/** Page size for Gateway run list reads; the Gateway accepts 1–100. */
export const GATEWAY_RUN_PAGE_SIZE = 100;

/**
 * Reads one project's runs from the Gateway with the unbound session, newest
 * first. The first page loads on mount and on `reload`; `loadMore` appends the
 * next page while the Gateway returns a cursor. An empty `projectId` reads nothing.
 */
export function useGatewayRunList(projectId: string, state?: ProjectRunStateFilter[]): RunListData {
  const { session, apiCall } = useAuth();
  // Join the state filter into a string so it is a stable dependency even if the
  // caller passes a new array each render.
  const stateKey = state?.join(',') ?? '';
  // Only "signed in or not" is part of the scope, not the token itself. A token
  // refresh then keeps the loaded pages instead of resetting the list.
  const scope = JSON.stringify([session?.accessToken ? 'signed-in' : null, projectId, stateKey]);
  const [result, setResult] = useState<{
    scope: string; items: RunListItem[]; nextCursor: string | null; error: unknown; loaded: boolean;
  }>({ scope: '', items: [], nextCursor: null, error: null, loaded: false });
  // Bumping the generation reloads the first page.
  const [generation, setGeneration] = useState(0);
  // Latest scope, read by async callbacks to detect stale responses.
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  /** Reads one page. A null cursor reads the first page. */
  const readPage = useCallback(async (cursor: string | null): Promise<ProjectRunPage> => apiCall(
    (token, tenant) => gatewayClient.listProjectRuns(token, projectId, {
      // Turn the joined key back into the filter list; an empty key means no filter.
      state: stateKey ? stateKey.split(',') as ProjectRunStateFilter[] : undefined,
      cursor,
      pageSize: GATEWAY_RUN_PAGE_SIZE,
    }, tenant),
    // `null` binding: the project run list uses the unbound session token.
    null,
  ), [apiCall, projectId, stateKey]);

  useEffect(() => {
    if (!projectId) return undefined;
    // `active` turns false on cleanup so a late first page is ignored.
    let active = true;
    const timer = window.setTimeout(() => {
      void readPage(null).then((page) => {
        // The first page replaces the whole list, dropping any pages loaded before.
        if (active && scopeRef.current === scope) setResult({
          scope, items: page.items.map((run) => gatewayRunListItem(projectId, run)), nextCursor: page.nextCursor, error: null, loaded: true,
        });
      }, (reason: unknown) => {
        if (active && scopeRef.current === scope)
          setResult({ scope, items: [], nextCursor: null, error: reason, loaded: true });
      });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [projectId, readPage, scope, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const current = result.scope === scope;
  const nextCursor = current ? result.nextCursor : null;
  /** Appends the next page to the list. */
  const loadMore = useCallback(() => {
    if (!nextCursor) return;
    void readPage(nextCursor).then((page) => {
      // Ignore the page if the project, filter, or sign-in changed meanwhile.
      if (scopeRef.current !== scope) return;
      // Append only if the list is still at the same cursor. If a reload or another
      // loadMore already moved it, appending would add duplicate or out-of-order rows.
      setResult((previous) => previous.scope !== scope || previous.nextCursor !== nextCursor ? previous : {
        ...previous,
        items: [...previous.items, ...page.items.map((run) => gatewayRunListItem(projectId, run))],
        nextCursor: page.nextCursor,
        error: null,
      });
    }, (reason: unknown) => {
      // A failed next page keeps the rows already shown and only records the error.
      if (scopeRef.current === scope) setResult((previous) => ({ ...previous, error: reason }));
    });
  }, [nextCursor, projectId, readPage, scope]);

  return {
    items: current ? result.items : [],
    reload,
    stateSource: 'service',
    loading: Boolean(projectId) && !(current && result.loaded),
    error: current ? result.error : null,
    // Only offer "load more" while the Gateway reports another page.
    loadMore: nextCursor ? loadMore : undefined,
  };
}