/**
 * Data adapters for the shared run list. They read the project run list from
 * the service and return one `RunListData` shape, so every page that shows
 * runs (Orchestrations, Sessions, Dashboard, Overview, Memories) uses the same
 * rows and paging.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { GatewayError, gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type { ProjectRunPage, ProjectRunStateFilter, ProjectRunSummary, ProjectSummary } from './contracts';
import { errorMessage } from './errors';

/** Page size the service uses when the client does not send one. */
export const DEFAULT_RUN_PAGE_SIZE = 25;

/** One row in a run list. */
export interface RunListItem {
  projectId: string;
  runId: string;
  /** Text shown for the run: title, then goal, then root session ID, then run ID. */
  label: string;
  executionState: string | null;
  /** Time shown in the row and the meaning of that time. */
  time: { label: string; value: string };
}

/** Rows, request state, and paging for one run list. */
export interface RunListData {
  items: RunListItem[];
  /** True while the first page loads, or reloads after Refresh. */
  loading: boolean;
  /** Last request failure, or null. Rows from earlier pages stay in `items`. */
  error: unknown;
  /** True when the service has more rows after the loaded ones. */
  hasMore: boolean;
  /** True while the next page loads. */
  loadingMore: boolean;
  /** Loads the next page and adds its rows. Does nothing when there is no next page. */
  loadMore: () => void;
  /** Reads the first page again and drops any extra pages. */
  reload: () => void;
}

/** Run list that spans projects, with names for the project column. */
export interface CrossProjectRunListData extends RunListData {
  projectNames: ReadonlyMap<string, string>;
  /** Names of projects whose runs could not be read, for example without read access. */
  unreadableProjects: string[];
}

/** Options for one project's run list. */
export interface ProjectRunListOptions {
  /** States to include. Omit for every state. `open` and `terminal` are accepted. */
  states?: ProjectRunStateFilter[];
  /** Rows per page, 1 to 100. */
  pageSize?: number;
}

/**
 * Returns the text shown for a run. The order is title, goal, root session ID,
 * then run ID. Blank values are skipped so a row never shows empty text.
 */
export function runDisplayLabel(run: Pick<ProjectRunSummary, 'runId' | 'title' | 'goal' | 'rootSessionId'>): string {
  for (const value of [run.title, run.goal, run.rootSessionId]) {
    const text = value?.trim();
    if (text) return text;
  }
  return run.runId;
}

/** Converts one service run summary to a list row. */
export function toRunListItem(projectId: string, run: ProjectRunSummary): RunListItem {
  return {
    projectId,
    runId: run.runId,
    label: runDisplayLabel(run),
    executionState: run.state,
    time: { label: 'Started', value: run.createdAt },
  };
}

/**
 * Formats a run list failure for display. A 403 means the user cannot read
 * runs in the project, so it gets a plain message instead of the raw error.
 */
export function runListErrorMessage(error: unknown): string {
  if (error instanceof GatewayError && error.status === 403) return 'You do not have permission to read runs in this project.';
  return errorMessage(error);
}

/** Time of a row in milliseconds, for sorting. An unreadable time sorts last. */
function rowTime(item: RunListItem): number {
  const parsed = Date.parse(item.time.value);
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
}

/**
 * Adds new rows to existing rows. A run already in the list keeps one row with
 * the newer data, because a run can move between pages while the user pages.
 * When `sort` is set, rows are sorted newest first.
 */
export function mergeRunItems(current: RunListItem[], added: RunListItem[], sort = false): RunListItem[] {
  const byKey = new Map(current.map((item) => [`${item.projectId}/${item.runId}`, item]));
  for (const item of added) byKey.set(`${item.projectId}/${item.runId}`, item);
  const merged = [...byKey.values()];
  if (sort) merged.sort((a, b) => rowTime(b) - rowTime(a));
  return merged;
}

interface ListState {
  /** Request key the rows belong to. Rows for another key are not shown. */
  key: string;
  items: RunListItem[];
  /** Next cursor per project. A missing or null entry means no more pages. */
  cursors: Record<string, string | null>;
  unreadable: string[];
  error: unknown;
}

const EMPTY_STATE: ListState = { key: '', items: [], cursors: {}, unreadable: [], error: null };

/**
 * Shared paging engine for one or more projects. `projects` is the list of
 * project IDs to read. `fetchPage` reads one page for one project. A change to
 * `key` or `trigger`, or a call to `reload`, reads the first page of every
 * project again.
 */
function usePagedRunList(
  key: string | null,
  projects: readonly string[],
  trigger: unknown,
  fetchPage: (projectId: string, cursor: string | null) => Promise<ProjectRunPage>,
  projectName: (projectId: string) => string,
): RunListData & { unreadableProjects: string[] } {
  const [list, setList] = useState<ListState>(EMPTY_STATE);
  const [pending, setPending] = useState<'first' | 'more' | null>(null);
  const [generation, setGeneration] = useState(0);
  // Each first-page request gets a new number. A response whose number is not
  // the latest belongs to an old project, filter, or refresh and is dropped.
  const latest = useRef(0);
  const sort = projects.length > 1;

  useEffect(() => {
    // No key means the caller has nothing to read yet, for example while the
    // project list loads.
    if (key === null) return;
    const request = ++latest.current;
    // Start in a zero-delay timer so state updates stay out of the effect body.
    const timer = window.setTimeout(() => {
      setPending('first');
      void Promise.allSettled(projects.map((projectId) => fetchPage(projectId, null))).then((results) => {
        if (request !== latest.current) return;
        const cursors: Record<string, string | null> = {};
        const unreadable: string[] = [];
        let items: RunListItem[] = [];
        let firstError: unknown = null;
        results.forEach((result, index) => {
          const projectId = projects[index];
          if (result.status === 'fulfilled') {
            items = mergeRunItems(items, result.value.items.map((run) => toRunListItem(projectId, run)), sort);
            cursors[projectId] = result.value.nextCursor ?? null;
          } else {
            unreadable.push(projectName(projectId));
            firstError ??= result.reason;
          }
        });
        // Report an error only when nothing could be read. When some projects
        // fail and others succeed, the caller shows `unreadableProjects`.
        const error = results.length > 0 && unreadable.length === results.length ? firstError : null;
        setList((current) => {
          // A failed refresh keeps the rows already on screen for the same key.
          if (error && current.key === key) return { ...current, error };
          return { key, items, cursors, unreadable, error };
        });
        setPending(null);
      });
    }, 0);
    return () => window.clearTimeout(timer);
  }, [key, projects, trigger, fetchPage, projectName, sort, generation]);

  const listMatches = key !== null && list.key === key;
  // Projects that still have a next page, with their cursors.
  const openCursors = useMemo(
    () => (listMatches
      ? Object.entries(list.cursors).filter((entry): entry is [string, string] => typeof entry[1] === 'string')
      : []),
    [listMatches, list.cursors],
  );

  const loadMore = useCallback(() => {
    // Ignore the click while another request runs or when there is no next page.
    if (pending || openCursors.length === 0) return;
    const request = latest.current;
    setPending('more');
    void Promise.allSettled(openCursors.map(([projectId, cursor]) => fetchPage(projectId, cursor))).then((results) => {
      // A refresh or a filter change started after this click; drop the result.
      if (request !== latest.current) return;
      setList((current) => {
        const cursors = { ...current.cursors };
        let items = current.items;
        let error: unknown = null;
        results.forEach((result, index) => {
          const [projectId] = openCursors[index];
          if (result.status === 'fulfilled') {
            items = mergeRunItems(items, result.value.items.map((run) => toRunListItem(projectId, run)), sort);
            cursors[projectId] = result.value.nextCursor ?? null;
          } else {
            // Keep the cursor so the next click tries this page again.
            error ??= result.reason;
          }
        });
        return { ...current, items, cursors, error };
      });
      setPending(null);
    });
  }, [pending, openCursors, fetchPage, sort]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);

  return {
    items: listMatches ? list.items : [],
    // Before the first response for this key arrives, the list is loading.
    loading: key !== null && (pending === 'first' || !listMatches),
    error: listMatches ? list.error : null,
    hasMore: openCursors.length > 0,
    loadingMore: pending === 'more',
    loadMore,
    reload,
    unreadableProjects: listMatches ? list.unreadable : [],
  };
}

/**
 * Reads one project's runs from the service, newest first, with paging. The
 * call uses the unbound project token. An empty project ID reads nothing.
 */
export function useProjectRunList(projectId: string, options: ProjectRunListOptions = {}): RunListData {
  const { apiCall } = useAuth();
  // Join the states into a string so a new array with the same values does not
  // start a new request.
  const statesKey = (options.states ?? []).join(',');
  const pageSize = options.pageSize ?? DEFAULT_RUN_PAGE_SIZE;
  const key = projectId ? JSON.stringify([projectId, statesKey, pageSize]) : null;
  const projects = useMemo(() => (projectId ? [projectId] : []), [projectId]);
  const fetchPage = useCallback((id: string, cursor: string | null) => {
    const states = statesKey ? statesKey.split(',') as ProjectRunStateFilter[] : undefined;
    // Send the cursor back unchanged with the same states and page size.
    return apiCall((token, tenant) => gatewayClient.listProjectRuns(token, id, { states, cursor, pageSize }, tenant), null);
  }, [apiCall, statesKey, pageSize]);
  const projectName = useCallback((id: string) => id, []);
  return usePagedRunList(key, projects, null, fetchPage, projectName);
}

/**
 * Reads the newest runs of every project in `projects` and merges them newest
 * first. Load more reads the next page of each project that has one. Pass
 * null while the project list loads. Each new `projects` array reads again,
 * so refreshing the project list also refreshes the runs.
 */
export function useCrossProjectRunList(projects: ProjectSummary[] | null, pageSize = 10): CrossProjectRunListData {
  const { apiCall } = useAuth();
  // Join the IDs into a string so the ID array keeps its identity while the
  // same projects are listed.
  const idsKey = (projects ?? []).map((project) => project.projectId).join('\n');
  const ids = useMemo(() => (idsKey ? idsKey.split('\n') : []), [idsKey]);
  const key = projects ? JSON.stringify([idsKey, pageSize]) : null;
  const projectNames = useMemo(
    () => new Map((projects ?? []).map((project) => [project.projectId, project.name])) as ReadonlyMap<string, string>,
    [projects],
  );
  const fetchPage = useCallback(
    (projectId: string, cursor: string | null) =>
      apiCall((token, tenant) => gatewayClient.listProjectRuns(token, projectId, { cursor, pageSize }, tenant), null),
    [apiCall, pageSize],
  );
  const projectName = useCallback((id: string) => projectNames.get(id) ?? id, [projectNames]);
  return { ...usePagedRunList(key, ids, projects, fetchPage, projectName), projectNames };
}
