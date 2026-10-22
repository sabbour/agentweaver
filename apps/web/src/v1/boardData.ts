/**
 * Data for the project Board: the backlog graph, the synced repository issues
 * that enrich task cards, the column grouping, and the capture and edit writes.
 * Every call uses the unbound project session, because backlog reads and writes
 * are project-scoped and a run-bound session is rejected for issue reads.
 */
import { useCallback, useEffect, useState } from 'react';
import { GatewayError, gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type {
  BacklogGraph,
  BacklogTaskSnapshot,
  ProjectIssuePage,
  ProjectIssueSummary,
} from './contracts';
import { errorMessage } from './errors';

/** Board column IDs. They match the 0.x column IDs so styles and anchors stay the same. */
export type BoardColumnId = 'backlog' | 'ready' | 'active' | 'done';

/** One rendered board column. */
export interface BoardColumn {
  id: BoardColumnId;
  label: string;
  description: string;
  tasks: BacklogTaskSnapshot[];
}

/** Column order and copy. Human Review and Problems are left out because no backlog data feeds them. */
export const BOARD_COLUMNS: ReadonlyArray<Omit<BoardColumn, 'tasks'>> = [
  { id: 'backlog', label: 'Backlog', description: 'Captured work not yet committed to an agent run.' },
  { id: 'ready', label: 'Ready', description: 'Committed tasks that wait for a run to claim them.' },
  { id: 'active', label: 'Active', description: 'Tasks that a run has claimed.' },
  { id: 'done', label: 'Done', description: 'Tasks whose work, or whose linked issue, is closed.' },
];

/** Plain copy shown where 0.x let users drag tasks between columns. */
export const MOVE_UNAVAILABLE_COPY = 'Moving tasks between columns is not available yet.';

/** Issue pages read per board load. 100 issues per page keeps one load to at most 1000 issues. */
const ISSUE_PAGE_SIZE = 100;
const ISSUE_PAGE_LIMIT = 10;

/**
 * Picks the column for one task. Archived tasks are not shown. A closed task
 * goes to Done whatever its queue state, because closing ends the work. Open
 * tasks follow their queue state, and `claimed` shows as Active.
 */
export function boardColumnFor(task: BacklogTaskSnapshot): BoardColumnId | null {
  if (task.isArchived) return null;
  if (task.lifecycle === 'closed') return 'done';
  if (task.state === 'claimed') return 'active';
  return task.state === 'ready' ? 'ready' : 'backlog';
}

/** Groups the graph's tasks into the fixed board columns, keeping the graph's task order. */
export function boardColumns(graph: BacklogGraph): BoardColumn[] {
  const columns = BOARD_COLUMNS.map((column) => ({ ...column, tasks: [] as BacklogTaskSnapshot[] }));
  for (const task of graph.tasks) {
    const id = boardColumnFor(task);
    // A null column means the task is hidden, for example because it is archived.
    if (id) columns.find((column) => column.id === id)?.tasks.push(task);
  }
  return columns;
}

/**
 * Lists the titles of tasks that must finish before `taskId` can start. A
 * prerequisite missing from the graph is shown by its count only, so the user
 * never sees a raw task ID.
 */
export function prerequisiteTitles(graph: BacklogGraph, taskId: string): { titles: string[]; unknown: number } {
  const titles: string[] = [];
  let unknown = 0;
  for (const edge of graph.dependencies) {
    if (edge.task.taskId !== taskId) continue;
    const prerequisite = graph.tasks.find((task) => task.reference.taskId === edge.prerequisite.taskId);
    if (prerequisite) titles.push(prerequisite.title);
    else unknown += 1;
  }
  return { titles, unknown };
}

/**
 * Creates the client-chosen ID for a new task. The service accepts IDs that
 * start with a letter or digit and use only letters, digits, `-`, `_`, `.`
 * and `:`. A UUID matches that rule. A fallback covers browsers without
 * `crypto.randomUUID`, such as pages served over plain HTTP.
 */
export function newBacklogTaskId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return `task-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Codes of the reasons in a rejected backlog write, normalized to lowercase without separators. */
function backlogIssueCodes(error: unknown): string[] {
  if (!(error instanceof GatewayError) || !Array.isArray(error.problem?.issues)) return [];
  return error.problem.issues
    .map((issue: unknown) => (issue && typeof issue === 'object' ? (issue as { code?: unknown }).code : undefined))
    .filter((code): code is string => typeof code === 'string')
    .map((code) => code.toLowerCase().replace(/[^a-z]/g, ''));
}

/** True when the write failed only because another write changed the graph or task first. */
export function isStaleRevision(error: unknown): boolean {
  if (!(error instanceof GatewayError) || error.status !== 409) return false;
  const codes = backlogIssueCodes(error);
  return codes.length > 0 && codes.every((code) => code === 'stalegraphrevision' || code === 'staletaskrevision');
}

/**
 * Turns a Board failure into plain copy. 403 explains the missing access for
 * the action. 409 explains the common conflicts. Other failures keep the
 * status and message, which helps when someone reports a problem.
 */
export function boardErrorMessage(error: unknown, action: 'read' | 'capture' | 'edit'): string {
  if (error instanceof GatewayError && error.status === 403) {
    return action === 'read'
      ? "You don't have permission to view this project's board."
      : "You don't have permission to change tasks in this project.";
  }
  if (error instanceof GatewayError && error.status === 409) {
    const codes = backlogIssueCodes(error);
    if (codes.includes('tasknoteditable'))
      return 'This task can no longer be edited. It may be archived or waiting for an automation.';
    if (codes.includes('duplicaterepositoryissue'))
      return 'Another task already tracks that repository issue.';
    if (isStaleRevision(error))
      return 'The board changed while you were working. It has been reloaded; try again.';
  }
  if (error instanceof GatewayError && error.status === 404 && action === 'edit')
    return 'This task no longer exists. The board has been reloaded.';
  return errorMessage(error);
}

/** Board load state for one project. */
export interface ProjectBoardData {
  graph: BacklogGraph | null;
  /** Synced issues keyed by the task that tracks them. */
  issues: ReadonlyMap<string, ProjectIssueSummary>;
  loading: boolean;
  /** Backlog read failure. The board can't render without the graph. */
  error: unknown;
  /** Issue read failure. Cards still render, without issue details. */
  issuesError: unknown;
  /** True when the issue list had more pages than one load reads. */
  issuesTruncated: boolean;
  reload: () => void;
  /** Shows a graph returned by a successful write without another read. */
  replaceGraph: (graph: BacklogGraph) => void;
}

interface BoardState {
  /** The project this state belongs to. State for another project is ignored. */
  projectId: string;
  graph: BacklogGraph | null;
  issues: ReadonlyMap<string, ProjectIssueSummary>;
  error: unknown;
  issuesError: unknown;
  issuesTruncated: boolean;
  settled: boolean;
}

const EMPTY_ISSUES: ReadonlyMap<string, ProjectIssueSummary> = new Map();

/**
 * Loads the backlog graph and every synced issue page for one project. The
 * graph and the issues load in parallel. An issue failure does not hide the
 * board, because issues only add details to cards. Responses for an older
 * project or an older reload are dropped.
 */
export function useProjectBoard(projectId: string, reloadKey?: unknown): ProjectBoardData {
  const { apiCall } = useAuth();
  const [state, setState] = useState<BoardState>({
    projectId: '', graph: null, issues: EMPTY_ISSUES, error: null, issuesError: null, issuesTruncated: false, settled: false,
  });
  // Bumped by `reload` to read again.
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    if (!projectId) return;
    // Cleared on cleanup so a late response for an old project or reload is dropped.
    let active = true;
    const timer = window.setTimeout(() => {
      const graphRead = apiCall((token, tenant) => gatewayClient.readProjectBacklog(token, projectId, tenant), null);
      // Read issue pages one after another, because each page needs the cursor of
      // the one before it. Stop at the page limit so a huge project can't stall the board.
      const issueRead = (async () => {
        const byTask = new Map<string, ProjectIssueSummary>();
        let cursor: string | null = null;
        for (let page = 0; page < ISSUE_PAGE_LIMIT; page += 1) {
          const current: string | null = cursor;
          const result: ProjectIssuePage = await apiCall((token, tenant) => gatewayClient.listProjectIssues(
            token, projectId, { state: 'all', cursor: current, pageSize: ISSUE_PAGE_SIZE }, tenant), null);
          for (const issue of result.items) byTask.set(issue.taskId, issue);
          cursor = result.nextCursor;
          if (!cursor) return { byTask, truncated: false };
        }
        return { byTask, truncated: true };
      })();
      void Promise.allSettled([graphRead, issueRead]).then(([graph, issues]) => {
        if (!active) return;
        setState((previous) => {
          // On a failed reload, keep the last good graph for this project and show the error with it.
          const sameProject = previous.projectId === projectId;
          return {
            projectId,
            graph: graph.status === 'fulfilled' ? graph.value : sameProject ? previous.graph : null,
            issues: issues.status === 'fulfilled' ? issues.value.byTask : sameProject ? previous.issues : EMPTY_ISSUES,
            error: graph.status === 'rejected' ? graph.reason : null,
            issuesError: issues.status === 'rejected' ? issues.reason : null,
            issuesTruncated: issues.status === 'fulfilled' ? issues.value.truncated : false,
            settled: true,
          };
        });
      });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, generation, reloadKey]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const replaceGraph = useCallback((graph: BacklogGraph) => {
    // Accept only a graph for the current project; a late write result for another project is ignored.
    setState((previous) => (graph.projectId === projectId
      ? { ...previous, projectId, graph, error: null, settled: true }
      : previous));
  }, [projectId]);

  const current = state.projectId === projectId;
  return {
    graph: current ? state.graph : null,
    issues: current ? state.issues : EMPTY_ISSUES,
    loading: !!projectId && (!current || !state.settled),
    error: current ? state.error : null,
    issuesError: current ? state.issuesError : null,
    issuesTruncated: current ? state.issuesTruncated : false,
    reload,
    replaceGraph,
  };
}

type ApiCall = ReturnType<typeof useAuth>['apiCall'];

/**
 * Captures one titled task into the project backlog and returns the new graph.
 * The write needs the current graph revision. When the caller has none, the
 * graph is read first. If another write lands in between, the service rejects
 * the stale revision; the graph is read again and the same request is sent
 * once more with the same task ID, so the task can't be added twice.
 */
export async function captureTask(
  apiCall: ApiCall,
  projectId: string,
  title: string,
  knownRevision?: number,
): Promise<BacklogGraph> {
  const taskId = newBacklogTaskId();
  const send = (expectedGraphRevision: number) => apiCall((token, tenant) => gatewayClient.captureBacklogTask(
    token, projectId, { taskId, content: { title }, expectedGraphRevision }, tenant), null);
  const readRevision = async () => (await apiCall(
    (token, tenant) => gatewayClient.readProjectBacklog(token, projectId, tenant), null)).revision;
  const revision = knownRevision ?? await readRevision();
  try {
    return await send(revision);
  } catch (error) {
    if (!isStaleRevision(error)) throw error;
    return await send(await readRevision());
  }
}

/**
 * Replaces a task's title and description. Lifecycle and the linked issue are
 * sent back unchanged, because the write replaces the whole content and the
 * Board does not edit them. Both revisions come from the graph the user saw,
 * so a change made by someone else in between is rejected, not overwritten.
 */
export function editTaskContent(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  content: { title: string; description: string | null },
): Promise<BacklogGraph> {
  return apiCall((token, tenant) => gatewayClient.editBacklogTaskContent(token, graph.projectId, task.reference.taskId, {
    content: {
      title: content.title,
      description: content.description,
      lifecycle: task.lifecycle,
      repositoryIssue: task.repositoryIssue ?? null,
    },
    expectedGraphRevision: graph.revision,
    expectedTaskRevision: task.revision,
  }, tenant), null);
}
