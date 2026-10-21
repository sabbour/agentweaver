/**
 * Data adapters for the shared run list. `useBrowserRunList` reads runs the user
 * opened in this browser. `useGatewayRunList` reads the project run list
 * (`GET /api/v1/projects/{projectId}/runs`, gap G2) and returns the same
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
  const [generation, setGeneration] = useState(0);
  const remove = useCallback((item: RunListItem) => {
    if (!tenantId) return;
    forgetRun(tenantId, item.projectId, item.runId);
    setGeneration((value) => value + 1);
  }, [tenantId]);
  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const items = useMemo(() => {
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
  const stateKey = state?.join(',') ?? '';
  const scope = JSON.stringify([session?.accessToken ? 'signed-in' : null, projectId, stateKey]);
  const [result, setResult] = useState<{
    scope: string; items: RunListItem[]; nextCursor: string | null; error: unknown; loaded: boolean;
  }>({ scope: '', items: [], nextCursor: null, error: null, loaded: false });
  const [generation, setGeneration] = useState(0);
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  const readPage = useCallback(async (cursor: string | null): Promise<ProjectRunPage> => apiCall(
    (token, tenant) => gatewayClient.listProjectRuns(token, projectId, {
      state: stateKey ? stateKey.split(',') as ProjectRunStateFilter[] : undefined,
      cursor,
      pageSize: GATEWAY_RUN_PAGE_SIZE,
    }, tenant),
    null,
  ), [apiCall, projectId, stateKey]);

  useEffect(() => {
    if (!projectId) return undefined;
    let active = true;
    const timer = window.setTimeout(() => {
      void readPage(null).then((page) => {
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
  const loadMore = useCallback(() => {
    if (!nextCursor) return;
    void readPage(nextCursor).then((page) => {
      if (scopeRef.current !== scope) return;
      setResult((previous) => previous.scope !== scope || previous.nextCursor !== nextCursor ? previous : {
        ...previous,
        items: [...previous.items, ...page.items.map((run) => gatewayRunListItem(projectId, run))],
        nextCursor: page.nextCursor,
        error: null,
      });
    }, (reason: unknown) => {
      if (scopeRef.current === scope) setResult((previous) => ({ ...previous, error: reason }));
    });
  }, [nextCursor, projectId, readPage, scope]);

  return {
    items: current ? result.items : [],
    reload,
    stateSource: 'service',
    loading: Boolean(projectId) && !(current && result.loaded),
    error: current ? result.error : null,
    loadMore: nextCursor ? loadMore : undefined,
  };
}